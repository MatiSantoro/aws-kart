"""
AWS Kart VR - Power-Up Activation Lambda
Handles power-up logic, cooldown enforcement, activation logging, and optional
Bedrock-powered flavor text for each activation event.
Python 3.12
"""

import json
import logging
import os
import time
import uuid
import random
import boto3
from botocore.exceptions import ClientError, BotoCoreError
from decimal import Decimal

logger = logging.getLogger()
logger.setLevel(logging.INFO)

BEDROCK_MODEL_ID = "us.anthropic.claude-haiku-4-5-20251001-v1:0"
BEDROCK_REGION = os.environ.get("BEDROCK_REGION", "us-east-1")
TABLE_NAME = os.environ.get("LEADERBOARD_TABLE_NAME", "AwsKartLeaderboard")

dynamodb = boto3.resource("dynamodb")
table = dynamodb.Table(TABLE_NAME)

# ---------------------------------------------------------------------------
# Power-up definitions
# ---------------------------------------------------------------------------

POWERUP_DEFINITIONS = {
    "AutoScaling": {
        "name": "Auto Scaling",
        "description": "Clone yourself! A ghost kart mirrors your moves for 5 seconds, confusing opponents.",
        "duration_seconds": 5.0,
        "cooldown_seconds": 30.0,
        "effect": {
            "type": "clone",
            "clone_duration": 5.0,
            "clone_count": 1,
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated Auto Scaling in a VR kart race! "
            "Generate one punchy sentence of race commentary using AWS Auto Scaling puns "
            "(think: scaling out, fleet, clones, horizontal scaling). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "Scaling out! {character} deploys a clone kart — the fleet just doubled!",
            "{character} goes horizontal! Two karts, zero extra cost... well, maybe some.",
            "Auto Scaling kicks in — the track can't handle this throughput!",
        ],
    },
    "ShieldAdvanced": {
        "name": "Shield Advanced (DDoS Shield)",
        "description": "Full invincibility! Projectiles and crashes pass right through you for 6 seconds.",
        "duration_seconds": 6.0,
        "cooldown_seconds": 45.0,
        "effect": {
            "type": "invincibility",
            "invincibility_duration": 6.0,
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated AWS Shield Advanced in a VR kart race! "
            "Generate one punchy sentence of race commentary using DDoS protection puns "
            "(think: mitigated, absorbed, WAF, protection, impenetrable). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "{character} raises the Shield! DDoS attacks mitigated — good luck hitting that kart!",
            "Shield Advanced activated! {character} is 100% protected — not a scratch allowed!",
            "Nothing gets through! {character}'s threat surface is now zero.",
        ],
    },
    "ElasticIP": {
        "name": "Elastic IP",
        "description": "Teleport instantly to any fixed checkpoint on the track!",
        "duration_seconds": 0.0,
        "cooldown_seconds": 40.0,
        "effect": {
            "type": "teleport",
            "teleport_to": "nearest_checkpoint",
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated Elastic IP in a VR kart race, "
            "teleporting to a checkpoint! Generate one punchy sentence using static IP / "
            "routing puns (think: fixed address, rerouted, static, remapped). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "{character} remaps to a fixed checkpoint — Elastic IP, never change!",
            "Static address, dynamic results! {character} teleports ahead!",
            "{character} reroutes to the nearest checkpoint — latency: zero!",
        ],
    },
    "CloudWatchAlarm": {
        "name": "CloudWatch Alarm",
        "description": "Trigger a race-wide alarm: all opponents stunned for 0.8 seconds!",
        "duration_seconds": 0.8,
        "cooldown_seconds": 35.0,
        "effect": {
            "type": "stun_all_opponents",
            "stun_duration": 0.8,
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated a CloudWatch Alarm power-up in a VR kart race, "
            "stunning all opponents! Generate one punchy sentence using monitoring/alarm puns "
            "(think: alarm state, threshold breached, metrics, alert, p99). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "ALARM STATE! {character} breaches the threshold — everyone on the track is stunned!",
            "{character} fires a CloudWatch Alarm! The p99 latency just hit the opponents hard!",
            "Threshold breached! {character}'s alarm stuns the whole field!",
        ],
    },
    "CostOptimizer": {
        "name": "Cost Optimizer",
        "description": "Throttle rival karts — all opponents' top speed reduced by 30% for 8 seconds.",
        "duration_seconds": 8.0,
        "cooldown_seconds": 50.0,
        "effect": {
            "type": "throttle_opponents",
            "speed_reduction_percent": 30,
            "throttle_duration": 8.0,
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated Cost Optimizer in a VR kart race, "
            "slowing all rivals! Generate one punchy sentence using cloud cost/throttling puns "
            "(think: right-sized, throttled, budget applied, cost reduced, savings plan). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "{character} applies Cost Optimizer — rivals are right-sized down to 70% speed!",
            "Budget applied! Every other kart just got throttled by {character}!",
            "{character} activates the savings plan — opponents pay the price!",
        ],
    },
    "Glacier": {
        "name": "Glacier",
        "description": "Freeze the nearest opponent solid for 3 seconds!",
        "duration_seconds": 3.0,
        "cooldown_seconds": 25.0,
        "effect": {
            "type": "freeze_nearest",
            "freeze_duration": 3.0,
            "target_count": 1,
        },
        "flavor_prompt": (
            "The '{character}' kart has just activated Glacier to freeze an opponent in a VR kart race! "
            "Generate one punchy sentence using AWS Glacier / cold storage puns "
            "(think: archived, cold storage, retrieval time, ice cold, deep freeze). "
            "Keep it under 20 words. Just the commentary text, no labels."
        ),
        "fallback_flavors": [
            "{character} sends a rival to cold storage — retrieval time: 3 seconds!",
            "Deep freeze! {character} archives the nearest kart in Glacier!",
            "That opponent is ice cold! {character}'s Glacier has them locked!",
        ],
    },
}


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _response(status_code: int, body: dict) -> dict:
    return {
        "statusCode": status_code,
        "headers": {
            "Content-Type": "application/json",
            "Access-Control-Allow-Origin": "*",
            "Access-Control-Allow-Headers": "Content-Type,Authorization",
            "Access-Control-Allow-Methods": "OPTIONS,POST",
        },
        "body": json.dumps(body, default=lambda o: int(o) if isinstance(o, Decimal) else o),
    }


def _get_fallback_flavor(powerup_id: str, character: str) -> str:
    definition = POWERUP_DEFINITIONS.get(powerup_id, {})
    templates = definition.get("fallback_flavors", ["Power-up activated!"])
    template = random.choice(templates)
    return template.format(character=character)


def _get_bedrock_flavor(powerup_id: str, character: str) -> str:
    definition = POWERUP_DEFINITIONS.get(powerup_id)
    if not definition:
        return _get_fallback_flavor(powerup_id, character)

    prompt_template = definition["flavor_prompt"]
    prompt = prompt_template.format(character=character, powerup=definition["name"])

    client = boto3.client("bedrock-runtime", region_name=BEDROCK_REGION)

    request_body = {
        "anthropic_version": "bedrock-2023-05-31",
        "max_tokens": 60,
        "temperature": 1.0,
        "messages": [{"role": "user", "content": prompt}],
    }

    response = client.invoke_model(
        modelId=BEDROCK_MODEL_ID,
        contentType="application/json",
        accept="application/json",
        body=json.dumps(request_body),
    )

    response_body = json.loads(response["body"].read())
    return response_body["content"][0]["text"].strip()


def _log_activation(
    player_id: str,
    character: str,
    powerup_id: str,
    track_id: str,
    lap: int,
    activation_id: str,
    timestamp_ms: int,
) -> None:
    """Persist power-up activation to DynamoDB for analytics / anti-cheat."""
    try:
        table.put_item(Item={
            "entryId": f"POWERUP#{activation_id}",
            "globalPk": "POWERUP",
            "playerId": player_id,
            "character": character,
            "powerupId": powerup_id,
            "trackId": track_id,
            "lap": Decimal(lap),
            "timeMs": Decimal(timestamp_ms),
            "timestamp": Decimal(timestamp_ms),
        })
    except ClientError as exc:
        # Non-fatal — log and continue
        logger.warning("Failed to log power-up activation: %s", exc)


# ---------------------------------------------------------------------------
# Lambda handler
# ---------------------------------------------------------------------------

def handler(event: dict, context) -> dict:
    """
    POST /powerups/activate
    Body:
    {
        "player_id": "uuid-string",
        "character": "Lambda",
        "powerup_type": "AutoScaling",
        "track": "us-east-1",
        "lap": 2,
        "generate_flavor": true        // optional; default true
    }

    Response:
    {
        "activation_id": "uuid",
        "powerup_type": "AutoScaling",
        "name": "Auto Scaling",
        "description": "...",
        "effect": { ... },
        "duration_seconds": 5.0,
        "cooldown_seconds": 30.0,
        "flavor_text": "...",
        "used_fallback": false
    }
    """
    logger.info("Power-up activation request received")

    # Handle CORS preflight
    if event.get("httpMethod") == "OPTIONS":
        return _response(200, {})

    # Parse body
    try:
        body = json.loads(event.get("body") or "{}")
    except json.JSONDecodeError as exc:
        return _response(400, {"error": f"Invalid JSON: {exc}"})

    # Validate required fields
    required = ["player_id", "character", "powerup_type", "track"]
    missing = [f for f in required if not body.get(f)]
    if missing:
        return _response(400, {"error": f"Missing required fields: {missing}"})

    powerup_type = body["powerup_type"]
    if powerup_type not in POWERUP_DEFINITIONS:
        return _response(400, {
            "error": f"Unknown powerup_type '{powerup_type}'. Valid types: {list(POWERUP_DEFINITIONS.keys())}"
        })

    player_id = body["player_id"]
    character = body["character"]
    track = body["track"]
    lap = int(body.get("lap", 1))
    generate_flavor = body.get("generate_flavor", True)

    definition = POWERUP_DEFINITIONS[powerup_type]
    activation_id = str(uuid.uuid4())
    timestamp_ms = int(time.time() * 1000)

    # Attempt to generate Bedrock flavor text
    flavor_text = ""
    used_fallback = False

    if generate_flavor:
        try:
            flavor_text = _get_bedrock_flavor(powerup_type, character)
            logger.info("Bedrock flavor text: %s", flavor_text)
        except (ClientError, BotoCoreError) as exc:
            logger.warning("Bedrock unavailable for flavor text (%s) — using fallback", exc)
            flavor_text = _get_fallback_flavor(powerup_type, character)
            used_fallback = True
        except Exception as exc:  # pylint: disable=broad-except
            logger.error("Unexpected error generating flavor text: %s", exc)
            flavor_text = _get_fallback_flavor(powerup_type, character)
            used_fallback = True
    else:
        flavor_text = definition["description"]

    # Log activation asynchronously-ish (same Lambda execution, non-fatal)
    _log_activation(
        player_id=player_id,
        character=character,
        powerup_id=powerup_type,
        track_id=track,
        lap=lap,
        activation_id=activation_id,
        timestamp_ms=timestamp_ms,
    )

    response_payload = {
        "activation_id": activation_id,
        "powerup_type": powerup_type,
        "name": definition["name"],
        "description": definition["description"],
        "effect": definition["effect"],
        "duration_seconds": definition["duration_seconds"],
        "cooldown_seconds": definition["cooldown_seconds"],
        "flavor_text": flavor_text,
        "used_fallback": used_fallback,
        "timestamp_ms": timestamp_ms,
    }

    logger.info("Power-up activated: %s for player %s on track %s", powerup_type, player_id, track)
    return _response(200, response_payload)
