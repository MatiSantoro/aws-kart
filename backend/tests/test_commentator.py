import json
import pytest
from unittest.mock import MagicMock, patch
from botocore.exceptions import ClientError


def _make_bedrock_response(text: str) -> dict:
    body = MagicMock()
    body.read.return_value = json.dumps({"content": [{"text": text}]})
    return {"body": body}


def _api_event(body: dict) -> dict:
    return {"body": json.dumps(body)}


# ---------------------------------------------------------------------------
# Helpers / build_commentary_prompt
# ---------------------------------------------------------------------------

class TestBuildCommentaryPrompt:
    def test_overtake_includes_both_characters(self):
        from backend.commentator.handler import build_commentary_prompt
        prompt = build_commentary_prompt({"event_type": "overtake", "player": "Lambda", "target": "EC2", "lap": 2, "position": 1, "track": "us-east-1"})
        assert "Lambda" in prompt
        assert "EC2" in prompt

    def test_position_suffix(self):
        from backend.commentator.handler import build_commentary_prompt
        assert "1st" in build_commentary_prompt({"event_type": "finish", "player": "S3", "position": 1, "track": "t", "race_time_ms": 1000})
        assert "2nd" in build_commentary_prompt({"event_type": "finish", "player": "S3", "position": 2, "track": "t", "race_time_ms": 1000})
        assert "4th" in build_commentary_prompt({"event_type": "finish", "player": "S3", "position": 4, "track": "t", "race_time_ms": 1000})

    def test_unknown_event_type_returns_generic(self):
        from backend.commentator.handler import build_commentary_prompt
        prompt = build_commentary_prompt({"event_type": "unknown_xyz", "player": "EC2", "track": "us-east-1"})
        assert "AWS puns" in prompt


# ---------------------------------------------------------------------------
# Fallback comments
# ---------------------------------------------------------------------------

class TestGetFallbackComment:
    def test_returns_string_for_all_event_types(self):
        from backend.commentator.handler import get_fallback_comment
        for event_type in ["lap_complete", "overtake", "powerup_used", "crash", "finish", "race_start", "new_lap_record"]:
            result = get_fallback_comment({"event_type": event_type, "player": "Lambda", "target": "EC2", "lap": 1, "position": 1, "track": "us-east-1"})
            assert isinstance(result, str)
            assert len(result) > 0

    def test_formats_player_name(self):
        from backend.commentator.handler import get_fallback_comment
        result = get_fallback_comment({"event_type": "lap_complete", "player": "DynamoDB", "lap": 2, "position": 1, "track": "us-east-1"})
        assert "DynamoDB" in result

    def test_unknown_type_falls_back_to_powerup_used(self):
        from backend.commentator.handler import get_fallback_comment
        result = get_fallback_comment({"event_type": "not_a_type", "player": "S3"})
        assert isinstance(result, str)


# ---------------------------------------------------------------------------
# Handler — routing and validation
# ---------------------------------------------------------------------------

class TestHandler:
    def test_missing_event_type_returns_400(self):
        from backend.commentator.handler import handler
        response = handler(_api_event({"player": "Lambda"}), None)
        assert response["statusCode"] == 400
        assert "event_type" in json.loads(response["body"])["error"]

    def test_invalid_event_type_returns_400(self):
        from backend.commentator.handler import handler
        response = handler(_api_event({"event_type": "banana"}), None)
        assert response["statusCode"] == 400

    def test_invalid_json_body_returns_400(self):
        from backend.commentator.handler import handler
        response = handler({"body": "not json {"}, None)
        assert response["statusCode"] == 400

    @pytest.mark.parametrize("event_type", [
        "lap_complete", "overtake", "powerup_used", "crash",
        "finish", "race_start", "new_lap_record",
    ])
    def test_all_valid_event_types_return_200_with_bedrock(self, event_type):
        from backend.commentator import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Great move!")
        with patch.object(module, "boto3") as mock_boto3:
            mock_boto3.client.return_value = mock_client
            response = module.handler(_api_event({"event_type": event_type, "player": "Lambda", "target": "EC2", "lap": 1, "position": 1, "track": "us-east-1"}), None)
        assert response["statusCode"] == 200
        body = json.loads(response["body"])
        assert body["commentary"] == "Great move!"
        assert body["used_fallback"] is False

    def test_bedrock_client_error_triggers_fallback(self):
        from backend.commentator import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.side_effect = ClientError(
            {"Error": {"Code": "ThrottlingException", "Message": "Rate exceeded"}}, "InvokeModel"
        )
        with patch.object(module, "boto3") as mock_boto3:
            mock_boto3.client.return_value = mock_client
            response = module.handler(_api_event({"event_type": "overtake", "player": "Lambda", "target": "EC2", "lap": 1, "position": 1, "track": "us-east-1"}), None)
        assert response["statusCode"] == 200
        body = json.loads(response["body"])
        assert body["used_fallback"] is True
        assert len(body["commentary"]) > 0

    def test_bedrock_unexpected_error_triggers_fallback(self):
        from backend.commentator import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.side_effect = RuntimeError("unexpected")
        with patch.object(module, "boto3") as mock_boto3:
            mock_boto3.client.return_value = mock_client
            response = module.handler(_api_event({"event_type": "crash", "player": "EC2", "lap": 1, "position": 2, "track": "us-east-1"}), None)
        assert response["statusCode"] == 200
        assert json.loads(response["body"])["used_fallback"] is True

    def test_direct_event_without_body_wrapper(self):
        from backend.commentator import handler as module
        mock_client = MagicMock()
        mock_client.invoke_model.return_value = _make_bedrock_response("Wow!")
        with patch.object(module, "boto3") as mock_boto3:
            mock_boto3.client.return_value = mock_client
            response = module.handler({"event_type": "race_start", "player": "CloudFront", "track": "eu-west-1"}, None)
        assert response["statusCode"] == 200

    def test_cors_headers_present(self):
        from backend.commentator.handler import handler
        response = handler(_api_event({"event_type": "banana"}), None)
        assert response["headers"]["Access-Control-Allow-Origin"] == "*"
