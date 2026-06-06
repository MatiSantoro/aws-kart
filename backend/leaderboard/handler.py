"""
AWS Kart VR - Leaderboard Lambda
Manages race results in DynamoDB with GSI support for per-track and global queries.
Python 3.12
"""

import json
import logging
import os
import time
import uuid
from decimal import Decimal
from typing import Any

import boto3
from boto3.dynamodb.conditions import Key
from botocore.exceptions import ClientError

logger = logging.getLogger()
logger.setLevel(logging.INFO)

TABLE_NAME = os.environ.get("LEADERBOARD_TABLE_NAME", "AwsKartLeaderboard")
TRACK_TIME_INDEX = "TrackTimeIndex"       # GSI: trackId (pk) + timeMs (sk) — per-track top times
PLAYER_TIME_INDEX = "PlayerTimeIndex"     # GSI: playerId (pk) + timeMs (sk) — per-player history
GLOBAL_INDEX = "GlobalTimeIndex"          # GSI: globalPk (pk, constant "GLOBAL") + timeMs (sk)

dynamodb = boto3.resource("dynamodb")
table = dynamodb.Table(TABLE_NAME)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _response(status_code: int, body: Any) -> dict:
    return {
        "statusCode": status_code,
        "headers": {
            "Content-Type": "application/json",
            "Access-Control-Allow-Origin": "*",
            "Access-Control-Allow-Headers": "Content-Type,Authorization",
            "Access-Control-Allow-Methods": "OPTIONS,GET,POST",
        },
        "body": json.dumps(body, default=_decimal_default),
    }


def _decimal_default(obj):
    """JSON serializer for Decimal types returned by DynamoDB."""
    if isinstance(obj, Decimal):
        return int(obj) if obj % 1 == 0 else float(obj)
    raise TypeError(f"Object of type {type(obj)} is not JSON serializable")


def _parse_body(event: dict) -> tuple[dict, str | None]:
    """Parse and validate request body. Returns (body_dict, error_message)."""
    raw = event.get("body") or "{}"
    try:
        body = json.loads(raw)
        return body, None
    except json.JSONDecodeError as exc:
        return {}, f"Invalid JSON: {exc}"


def _format_entry(item: dict) -> dict:
    """Normalise a DynamoDB item into a clean API response dict."""
    return {
        "entryId": item.get("entryId", ""),
        "playerId": item.get("playerId", ""),
        "playerName": item.get("playerName", "Anonymous"),
        "character": item.get("character", "Unknown"),
        "trackId": item.get("trackId", ""),
        "timeMs": int(item.get("timeMs", 0)),
        "powerupsUsed": item.get("powerupsUsed", []),
        "timestamp": item.get("timestamp", 0),
        "rank": item.get("rank"),
    }


# ---------------------------------------------------------------------------
# Core CRUD operations
# ---------------------------------------------------------------------------

def submit_race_result(body: dict) -> dict:
    """
    POST /leaderboard
    Required fields: player_name, character, track, time_ms
    Optional fields: player_id, powerups_used
    """
    required = ["player_name", "character", "track", "time_ms"]
    missing = [f for f in required if f not in body]
    if missing:
        return _response(400, {"error": f"Missing required fields: {missing}"})

    try:
        time_ms = int(body["time_ms"])
        if time_ms <= 0:
            raise ValueError("time_ms must be positive")
    except (ValueError, TypeError) as exc:
        return _response(400, {"error": f"Invalid time_ms: {exc}"})

    valid_characters = {"EC2", "Lambda", "S3", "DynamoDB", "CloudFront", "SageMaker", "Bedrock"}
    character = body["character"]
    if character not in valid_characters:
        return _response(400, {
            "error": f"Invalid character '{character}'. Must be one of: {sorted(valid_characters)}"
        })

    entry_id = str(uuid.uuid4())
    player_id = body.get("player_id") or str(uuid.uuid4())
    track_id = body["track"]
    powerups_used = body.get("powerups_used", [])
    timestamp = int(time.time() * 1000)

    item = {
        # Primary key: entryId (allows multiple entries per player/track)
        "entryId": entry_id,
        # GSI keys
        "trackId": track_id,           # TrackTimeIndex pk
        "timeMs": Decimal(time_ms),    # TrackTimeIndex sk, GlobalTimeIndex sk
        "playerId": player_id,         # PlayerTimeIndex pk
        "globalPk": "GLOBAL",          # GlobalTimeIndex pk (constant)
        # Attributes
        "playerName": body["player_name"],
        "character": character,
        "powerupsUsed": powerups_used,
        "timestamp": Decimal(timestamp),
    }

    try:
        table.put_item(Item=item)
        logger.info("Leaderboard entry saved: %s / track=%s time=%d", entry_id, track_id, time_ms)
    except ClientError as exc:
        logger.error("DynamoDB put_item failed: %s", exc)
        return _response(500, {"error": "Failed to save race result"})

    return _response(201, {
        "entryId": entry_id,
        "playerId": player_id,
        "message": "Race result saved successfully",
    })


def get_track_leaderboard(query_params: dict) -> dict:
    """
    GET /leaderboard?track=us-east-1&limit=10
    Returns top times for a specific track, sorted ascending by timeMs.
    """
    track_id = query_params.get("track")
    if not track_id:
        return _response(400, {"error": "Missing required query parameter: track"})

    try:
        limit = min(int(query_params.get("limit", 10)), 100)
    except (ValueError, TypeError):
        limit = 10

    try:
        result = table.query(
            IndexName=TRACK_TIME_INDEX,
            KeyConditionExpression=Key("trackId").eq(track_id),
            ScanIndexForward=True,   # ascending timeMs = fastest first
            Limit=limit,
        )
        items = result.get("Items", [])
    except ClientError as exc:
        logger.error("DynamoDB query (track leaderboard) failed: %s", exc)
        return _response(500, {"error": "Failed to retrieve leaderboard"})

    entries = []
    for rank, item in enumerate(items, start=1):
        entry = _format_entry(item)
        entry["rank"] = rank
        entries.append(entry)

    return _response(200, {
        "track": track_id,
        "count": len(entries),
        "entries": entries,
    })


def get_global_leaderboard(query_params: dict) -> dict:
    """
    GET /leaderboard/global?limit=20
    Returns fastest overall times across all tracks.
    """
    try:
        limit = min(int(query_params.get("limit", 20)), 100)
    except (ValueError, TypeError):
        limit = 20

    try:
        result = table.query(
            IndexName=GLOBAL_INDEX,
            KeyConditionExpression=Key("globalPk").eq("GLOBAL"),
            ScanIndexForward=True,   # fastest times first
            Limit=limit,
        )
        items = result.get("Items", [])
    except ClientError as exc:
        logger.error("DynamoDB query (global leaderboard) failed: %s", exc)
        return _response(500, {"error": "Failed to retrieve global leaderboard"})

    entries = []
    for rank, item in enumerate(items, start=1):
        entry = _format_entry(item)
        entry["rank"] = rank
        entries.append(entry)

    return _response(200, {
        "scope": "global",
        "count": len(entries),
        "entries": entries,
    })


def get_player_history(player_id: str, query_params: dict) -> dict:
    """
    GET /leaderboard/player/{playerId}?limit=20
    Returns all race entries for a specific player.
    """
    try:
        limit = min(int(query_params.get("limit", 20)), 100)
    except (ValueError, TypeError):
        limit = 20

    try:
        result = table.query(
            IndexName=PLAYER_TIME_INDEX,
            KeyConditionExpression=Key("playerId").eq(player_id),
            ScanIndexForward=True,
            Limit=limit,
        )
        items = result.get("Items", [])
    except ClientError as exc:
        logger.error("DynamoDB query (player history) failed: %s", exc)
        return _response(500, {"error": "Failed to retrieve player history"})

    entries = [_format_entry(item) for item in items]

    return _response(200, {
        "playerId": player_id,
        "count": len(entries),
        "entries": entries,
    })


# ---------------------------------------------------------------------------
# Lambda handler
# ---------------------------------------------------------------------------

def handler(event: dict, context) -> dict:
    """
    API Gateway proxy integration handler.

    Routes:
      POST /leaderboard               — submit race result
      GET  /leaderboard               — get track leaderboard (?track=us-east-1&limit=10)
      GET  /leaderboard/global        — global top times
      GET  /leaderboard/player/{id}   — player race history
    """
    logger.info("Leaderboard event: method=%s path=%s",
                event.get("httpMethod"), event.get("path"))

    http_method = event.get("httpMethod", "GET").upper()
    path = event.get("path", "/leaderboard")
    query_params = event.get("queryStringParameters") or {}
    path_params = event.get("pathParameters") or {}

    # Handle CORS preflight
    if http_method == "OPTIONS":
        return _response(200, {})

    # POST /leaderboard — submit result
    if http_method == "POST" and path == "/leaderboard":
        body, error = _parse_body(event)
        if error:
            return _response(400, {"error": error})
        return submit_race_result(body)

    # GET /leaderboard/global
    if http_method == "GET" and path == "/leaderboard/global":
        return get_global_leaderboard(query_params)

    # GET /leaderboard/player/{playerId}
    if http_method == "GET" and "/leaderboard/player/" in path:
        player_id = path_params.get("playerId") or path.split("/leaderboard/player/")[-1]
        if not player_id:
            return _response(400, {"error": "Missing playerId in path"})
        return get_player_history(player_id, query_params)

    # GET /leaderboard?track=...
    if http_method == "GET" and path == "/leaderboard":
        return get_track_leaderboard(query_params)

    return _response(404, {"error": f"Route not found: {http_method} {path}"})
