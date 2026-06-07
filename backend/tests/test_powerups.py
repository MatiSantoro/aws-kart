import json
import pytest
from unittest.mock import MagicMock, patch
from botocore.exceptions import ClientError, BotoCoreError


def _make_bedrock_response(text: str) -> dict:
    body = MagicMock()
    body.read.return_value = json.dumps({"content": [{"text": text}]})
    return {"body": body}


def _valid_body(**overrides) -> dict:
    base = {"player_id": "player-001", "character": "Lambda", "powerup_type": "Glacier", "track": "us-east-1", "lap": 1}
    base.update(overrides)
    return base


def _post_event(body: dict) -> dict:
    return {"httpMethod": "POST", "path": "/powerups/activate", "body": json.dumps(body)}


def _make_table_mock():
    t = MagicMock()
    t.put_item.return_value = {}
    return t


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

class TestValidation:
    @pytest.mark.parametrize("missing", ["player_id", "character", "powerup_type", "track"])
    def test_missing_required_field_returns_400(self, missing):
        from backend.powerups import handler as module
        body = _valid_body()
        del body[missing]
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event(body), None)
        assert response["statusCode"] == 400
        assert missing in json.loads(response["body"])["error"]

    def test_unknown_powerup_type_returns_400(self):
        from backend.powerups import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event(_valid_body(powerup_type="NotReal")), None)
        assert response["statusCode"] == 400

    def test_invalid_json_returns_400(self):
        from backend.powerups import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler({"httpMethod": "POST", "body": "bad {"}, None)
        assert response["statusCode"] == 400

    def test_options_returns_200(self):
        from backend.powerups import handler as module
        response = module.handler({"httpMethod": "OPTIONS"}, None)
        assert response["statusCode"] == 200


# ---------------------------------------------------------------------------
# All 6 power-up types accepted
# ---------------------------------------------------------------------------

class TestAllPowerUpTypes:
    @pytest.mark.parametrize("powerup_type", [
        "AutoScaling", "ShieldAdvanced", "ElasticIP",
        "CloudWatchAlarm", "CostOptimizer", "Glacier",
    ])
    def test_all_types_return_200(self, powerup_type):
        from backend.powerups import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Flavor!")
        with patch.object(module, "table", _make_table_mock()), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = _make_table_mock()
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body(powerup_type=powerup_type)), None)
        assert response["statusCode"] == 200, f"Expected 200 for {powerup_type}"
        body = json.loads(response["body"])
        assert body["powerup_type"] == powerup_type


# ---------------------------------------------------------------------------
# Response shape
# ---------------------------------------------------------------------------

class TestResponseShape:
    def test_successful_activation_has_required_fields(self):
        from backend.powerups import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Ice cold!")
        with patch.object(module, "table", _make_table_mock()), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = _make_table_mock()
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body()), None)
        body = json.loads(response["body"])
        for field in ["activation_id", "powerup_type", "name", "description", "effect", "duration_seconds", "cooldown_seconds", "flavor_text", "used_fallback", "timestamp_ms"]:
            assert field in body, f"Missing field: {field}"

    def test_effect_has_type_field(self):
        from backend.powerups import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Frozen!")
        with patch.object(module, "table", _make_table_mock()), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = _make_table_mock()
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body(powerup_type="Glacier")), None)
        effect = json.loads(response["body"])["effect"]
        assert effect["type"] == "freeze_nearest"


# ---------------------------------------------------------------------------
# Bedrock fallback behaviour
# ---------------------------------------------------------------------------

class TestBedrockFallback:
    def test_bedrock_client_error_returns_200_with_fallback(self):
        from backend.powerups import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.side_effect = ClientError(
            {"Error": {"Code": "ThrottlingException", "Message": ""}}, "InvokeModel"
        )
        with patch.object(module, "table", _make_table_mock()), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = _make_table_mock()
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body()), None)
        assert response["statusCode"] == 200
        body = json.loads(response["body"])
        assert body["used_fallback"] is True
        assert len(body["flavor_text"]) > 0

    def test_generate_flavor_false_skips_bedrock(self):
        from backend.powerups import handler as module
        mock_client = MagicMock()
        with patch.object(module, "table", _make_table_mock()), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = _make_table_mock()
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body(generate_flavor=False)), None)
        mock_client.invoke_model.assert_not_called()
        assert response["statusCode"] == 200


# ---------------------------------------------------------------------------
# DynamoDB logging — non-fatal on failure
# ---------------------------------------------------------------------------

class TestDynamoDBLogging:
    def test_dynamodb_failure_does_not_fail_request(self):
        from backend.powerups import handler as module
        table = _make_table_mock()
        table.put_item.side_effect = ClientError({"Error": {"Code": "InternalServerError", "Message": ""}}, "PutItem")
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Zap!")
        with patch.object(module, "table", table), \
             patch.object(module, "boto3") as mock_boto3:
            mock_boto3.resource.return_value.Table.return_value = table
            mock_boto3.client.return_value = mock_client
            response = module.handler(_post_event(_valid_body()), None)
        assert response["statusCode"] == 200
