import json
import pytest
from decimal import Decimal
from unittest.mock import MagicMock, patch
from botocore.exceptions import ClientError


def _post_event(body: dict) -> dict:
    return {"httpMethod": "POST", "path": "/leaderboard", "body": json.dumps(body), "queryStringParameters": {}, "pathParameters": {}}


def _get_event(params: dict = None) -> dict:
    return {"httpMethod": "GET", "path": "/leaderboard", "body": None, "queryStringParameters": params or {}, "pathParameters": {}}


def _make_table_mock(query_items=None):
    table = MagicMock()
    table.put_item.return_value = {}
    table.query.return_value = {"Items": query_items or []}
    return table


# ---------------------------------------------------------------------------
# submit_race_result
# ---------------------------------------------------------------------------

class TestSubmitRaceResult:
    def test_valid_body_returns_201_with_entry_id(self):
        from backend.leaderboard import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event({"player_name": "Alice", "character": "Lambda", "track": "us-east-1", "time_ms": 120000}), None)
        assert response["statusCode"] == 201
        body = json.loads(response["body"])
        assert "entryId" in body
        assert "playerId" in body

    @pytest.mark.parametrize("missing_field", ["player_name", "character", "track", "time_ms"])
    def test_missing_required_field_returns_400(self, missing_field):
        from backend.leaderboard import handler as module
        body = {"player_name": "Alice", "character": "Lambda", "track": "us-east-1", "time_ms": 120000}
        del body[missing_field]
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event(body), None)
        assert response["statusCode"] == 400

    def test_invalid_character_returns_400(self):
        from backend.leaderboard import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event({"player_name": "Alice", "character": "NotAService", "track": "us-east-1", "time_ms": 120000}), None)
        assert response["statusCode"] == 400
        assert "character" in json.loads(response["body"])["error"].lower()

    @pytest.mark.parametrize("bad_time", ["not_a_number", -1, 0])
    def test_invalid_time_ms_returns_400(self, bad_time):
        from backend.leaderboard import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_post_event({"player_name": "Alice", "character": "EC2", "track": "us-east-1", "time_ms": bad_time}), None)
        assert response["statusCode"] == 400

    def test_all_valid_characters_accepted(self):
        from backend.leaderboard import handler as module
        for char in ["EC2", "Lambda", "S3", "DynamoDB", "CloudFront", "SageMaker", "Bedrock"]:
            with patch.object(module, "table", _make_table_mock()):
                response = module.handler(_post_event({"player_name": "X", "character": char, "track": "t", "time_ms": 1000}), None)
            assert response["statusCode"] == 201, f"Expected 201 for character {char}"

    def test_dynamodb_error_returns_500(self):
        from backend.leaderboard import handler as module
        table = _make_table_mock()
        table.put_item.side_effect = ClientError({"Error": {"Code": "ProvisionedThroughputExceededException", "Message": ""}}, "PutItem")
        with patch.object(module, "table", table):
            response = module.handler(_post_event({"player_name": "Alice", "character": "S3", "track": "t", "time_ms": 1000}), None)
        assert response["statusCode"] == 500

    def test_invalid_json_body_returns_400(self):
        from backend.leaderboard import handler as module
        event = {"httpMethod": "POST", "path": "/leaderboard", "body": "bad json {", "queryStringParameters": {}, "pathParameters": {}}
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(event, None)
        assert response["statusCode"] == 400


# ---------------------------------------------------------------------------
# get_track_leaderboard
# ---------------------------------------------------------------------------

class TestGetTrackLeaderboard:
    def _entry(self, rank=1) -> dict:
        return {"entryId": "abc", "playerId": "p1", "playerName": "Alice", "character": "Lambda",
                "trackId": "us-east-1", "timeMs": Decimal(120000), "powerupsUsed": [], "timestamp": Decimal(1000)}

    def test_returns_entries_for_track(self):
        from backend.leaderboard import handler as module
        table = _make_table_mock(query_items=[self._entry()])
        with patch.object(module, "table", table):
            response = module.handler(_get_event({"track": "us-east-1"}), None)
        assert response["statusCode"] == 200
        body = json.loads(response["body"])
        assert body["track"] == "us-east-1"
        assert body["count"] == 1
        assert body["entries"][0]["rank"] == 1

    def test_missing_track_param_returns_400(self):
        from backend.leaderboard import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_get_event({}), None)
        assert response["statusCode"] == 400

    def test_queries_track_time_index(self):
        from backend.leaderboard import handler as module
        table = _make_table_mock()
        with patch.object(module, "table", table):
            module.handler(_get_event({"track": "us-east-1"}), None)
        call_kwargs = table.query.call_args[1]
        assert call_kwargs["IndexName"] == "TrackTimeIndex"

    def test_dynamodb_error_returns_500(self):
        from backend.leaderboard import handler as module
        table = _make_table_mock()
        table.query.side_effect = ClientError({"Error": {"Code": "InternalServerError", "Message": ""}}, "Query")
        with patch.object(module, "table", table):
            response = module.handler(_get_event({"track": "us-east-1"}), None)
        assert response["statusCode"] == 500


# ---------------------------------------------------------------------------
# get_global_leaderboard
# ---------------------------------------------------------------------------

class TestGetGlobalLeaderboard:
    def test_queries_global_time_index(self):
        from backend.leaderboard import handler as module
        table = _make_table_mock()
        event = {"httpMethod": "GET", "path": "/leaderboard/global", "body": None, "queryStringParameters": {}, "pathParameters": {}}
        with patch.object(module, "table", table):
            response = module.handler(event, None)
        assert response["statusCode"] == 200
        call_kwargs = table.query.call_args[1]
        assert call_kwargs["IndexName"] == "GlobalTimeIndex"
        assert json.loads(response["body"])["scope"] == "global"


# ---------------------------------------------------------------------------
# CORS preflight + 404
# ---------------------------------------------------------------------------

class TestRouting:
    def test_options_returns_200(self):
        from backend.leaderboard import handler as module
        event = {"httpMethod": "OPTIONS", "path": "/leaderboard", "body": None, "queryStringParameters": {}, "pathParameters": {}}
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(event, None)
        assert response["statusCode"] == 200

    def test_unknown_route_returns_404(self):
        from backend.leaderboard import handler as module
        event = {"httpMethod": "DELETE", "path": "/leaderboard", "body": None, "queryStringParameters": {}, "pathParameters": {}}
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(event, None)
        assert response["statusCode"] == 404

    def test_cors_headers_on_all_responses(self):
        from backend.leaderboard import handler as module
        with patch.object(module, "table", _make_table_mock()):
            response = module.handler(_get_event({}), None)
        assert response["headers"]["Access-Control-Allow-Origin"] == "*"
