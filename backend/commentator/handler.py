"""
AWS Kart VR - AI Race Commentator Lambda
Powered by Amazon Bedrock (Claude Haiku 4.5)
Python 3.12
"""

import json
import logging
import os
import random
import boto3
from botocore.exceptions import ClientError, BotoCoreError

logger = logging.getLogger()
logger.setLevel(logging.INFO)

BEDROCK_MODEL_ID = "anthropic.claude-haiku-4-5-20251001-v1:0"
BEDROCK_REGION = os.environ.get("BEDROCK_REGION", "us-east-1")

# Fallback comments when Bedrock is unavailable
FALLBACK_COMMENTS = {
    "lap_complete": [
        "{player} completes lap {lap}! That kart is running like a well-optimised instance!",
        "Lap {lap} in the books for {player}! No cold starts on that run!",
        "{player} clears lap {lap} — consistent throughput all the way around!",
        "Another lap down for {player}! The SLA is looking good today!",
    ],
    "overtake": [
        "{player} just overtook {target}! Executed in milliseconds — pure serverless speed!",
        "{player} blows past {target}! That's horizontal scaling right there folks!",
        "Incredible! {player} spins up and overtakes {target} on the inside!",
        "{target} just got deprecated by {player} — what a move!",
        "{player} routes around {target} like a global CDN — seamless!",
    ],
    "powerup_used": [
        "{player} activates a power-up! The cloud has spoken!",
        "Power-up deployed by {player}! That's infrastructure-as-a-weapon!",
        "{player} triggers a power-up — auto-scaling the fun!",
        "Here comes {player}'s power-up — billed by the second!",
    ],
    "crash": [
        "{player} crashes! Looks like that instance just went down — initiating failover!",
        "Oh no! {player} hits the barrier! That's an unplanned downtime event!",
        "{player} spins out! Retry logic has been engaged!",
        "{player} is off the track! CloudWatch alarm triggered — severity: critical!",
    ],
    "finish": [
        "{player} crosses the finish line in position {position}! What a deployment!",
        "It's over! {player} finishes {position}! The pipeline is complete!",
        "{player} rolls across the line at position {position} — invoking the victory function!",
        "Race complete for {player}! Final report: position {position} on track {track}!",
    ],
    "race_start": [
        "And they're off on track {track}! May the best service win!",
        "Green light! The race begins on {track} — latency is everything!",
        "The race on {track} is live! 3... 2... 1... Deploy!",
    ],
    "new_lap_record": [
        "{player} sets a new lap record! That's single-digit millisecond performance!",
        "New record! {player} has achieved peak compute on lap {lap}!",
        "Incredible lap time from {player}! The benchmark has been updated!",
    ],
}

SYSTEM_PROMPT = """You are an enthusiastic, witty race commentator for AWS Kart VR — a Mario Kart-style VR racing game where every character is an AWS service. 

Your commentary must:
1. Be energetic, short (1-2 sentences max), and punchy — suitable for in-game display
2. Use natural AWS puns and service references that fit the moment organically
3. Reference the specific service's real-world traits (Lambda is fast but ephemeral, EC2 is powerful but slow to start, DynamoDB is a great at cornering/drifting, CloudFront is fastest on straights, S3 is durable, SageMaker uses AI, Bedrock is unpredictable)
4. Be suitable for all ages — exciting but clean
5. Avoid technical jargon that would confuse non-cloud players; make puns accessible
6. Vary your vocabulary; never repeat the same phrase in consecutive calls

AWS service personality traits for puns:
- EC2: instances, compute, spinning up, burst mode, heavy lifting, on-demand
- Lambda: functions, serverless, cold start, execution, milliseconds, invocations
- S3: storage, buckets, eleven nines, durable, objects, replication
- DynamoDB: NoSQL, single-digit millisecond, keys, tables, throughput, streams
- CloudFront: CDN, edge, global, cache, distribution, low latency
- SageMaker: machine learning, training, inference, models, autopilot, notebooks
- Bedrock: foundation models, generative AI, prompts, tokens, hallucinations

Power-up puns:
- Auto Scaling: scaling out, clones, horizontal scaling, fleet
- DDoS Shield / Shield Advanced: protection, mitigated, WAF, absorbed
- Elastic IP: static IP, teleport, fixed address, rerouted
- CloudWatch Alarm: alarm state, metrics, threshold, alert, monitoring
- Cost Optimizer: throttled, costs cut, budget applied, right-sized
- Glacier: frozen, archived, cold storage, ice cold, retrieval
"""

def build_commentary_prompt(event: dict) -> str:
    event_type = event.get("event_type", "unknown")
    player = event.get("player", "Unknown Racer")
    target = event.get("target", "")
    lap = event.get("lap", 1)
    position = event.get("position", 1)
    track = event.get("track", "us-east-1")
    powerup = event.get("powerup", "")
    race_time_ms = event.get("race_time_ms", 0)

    position_suffix = {1: "1st", 2: "2nd", 3: "3rd"}.get(position, f"{position}th")

    context_map = {
        "lap_complete": (
            f"The character '{player}' (an AWS service kart racer) has just completed lap {lap} "
            f"on track '{track}'. They are currently in {position_suffix} place. "
            f"Generate exciting commentary celebrating the completed lap."
        ),
        "overtake": (
            f"'{player}' (an AWS service kart racer) has just overtaken '{target}' "
            f"on lap {lap} on track '{track}'. {player} is now in {position_suffix} place. "
            f"Generate short, thrilling commentary about this overtake, using AWS puns appropriate "
            f"to both characters' services."
        ),
        "powerup_used": (
            f"'{player}' (an AWS service kart racer) has just activated the '{powerup}' power-up "
            f"on lap {lap} on track '{track}'. They are in {position_suffix} place. "
            f"Generate exciting commentary about this power-up activation."
        ),
        "crash": (
            f"'{player}' (an AWS service kart racer) has just crashed or spun out "
            f"on lap {lap} on track '{track}'. They were in {position_suffix} place. "
            f"Generate commentary that is sympathetic but funny using AWS puns about downtime or errors."
        ),
        "finish": (
            f"'{player}' (an AWS service kart racer) has just finished the race in {position_suffix} place "
            f"on track '{track}' with a total time of {race_time_ms}ms. "
            f"Generate exciting finish-line commentary using AWS puns."
        ),
        "race_start": (
            f"The race is starting on track '{track}'! All AWS service racers are on the grid. "
            f"Generate a short, exciting race-start call."
        ),
        "new_lap_record": (
            f"'{player}' (an AWS service kart racer) has just set a new lap record on track '{track}' "
            f"during lap {lap}! Generate amazed, energetic commentary using AWS performance puns."
        ),
    }

    context = context_map.get(
        event_type,
        f"Something exciting just happened with '{player}' on track '{track}'. Comment on it with AWS puns."
    )

    return (
        f"{context}\n\n"
        f"Respond with ONLY the commentary text — no quotes, no labels, no explanation. "
        f"Keep it to 1-2 sentences maximum."
    )


def get_fallback_comment(event: dict) -> str:
    event_type = event.get("event_type", "powerup_used")
    player = event.get("player", "Someone")
    target = event.get("target", "a rival")
    lap = event.get("lap", 1)
    position = event.get("position", 1)
    track = event.get("track", "us-east-1")

    bucket = FALLBACK_COMMENTS.get(event_type, FALLBACK_COMMENTS["powerup_used"])
    template = random.choice(bucket)

    position_suffix = {1: "1st", 2: "2nd", 3: "3rd"}.get(position, f"{position}th")

    return template.format(
        player=player,
        target=target,
        lap=lap,
        position=position_suffix,
        track=track,
    )


def invoke_bedrock(prompt: str) -> str:
    client = boto3.client("bedrock-runtime", region_name=BEDROCK_REGION)

    request_body = {
        "anthropic_version": "bedrock-2023-05-31",
        "max_tokens": 150,
        "temperature": 0.9,
        "top_p": 0.95,
        "system": SYSTEM_PROMPT,
        "messages": [
            {
                "role": "user",
                "content": prompt,
            }
        ],
    }

    response = client.invoke_model(
        modelId=BEDROCK_MODEL_ID,
        contentType="application/json",
        accept="application/json",
        body=json.dumps(request_body),
    )

    response_body = json.loads(response["body"].read())
    commentary = response_body["content"][0]["text"].strip()
    return commentary


def handler(event: dict, context) -> dict:
    """
    Lambda handler for AI race commentary.

    Expected input (API Gateway proxy event):
    {
        "event_type": "overtake" | "lap_complete" | "powerup_used" | "crash" | "finish" | "race_start" | "new_lap_record",
        "player": "Lambda",
        "target": "EC2",          # optional, used for overtake
        "lap": 2,
        "position": 1,
        "track": "us-east-1",
        "powerup": "Auto Scaling", # optional, used for powerup_used
        "race_time_ms": 123456     # optional, used for finish
    }
    """
    logger.info("Commentary request received: %s", json.dumps(event))

    # Handle API Gateway proxy integration
    if "body" in event:
        try:
            race_event = json.loads(event["body"] or "{}")
        except json.JSONDecodeError as exc:
            logger.error("Invalid JSON body: %s", exc)
            return _response(400, {"error": "Invalid JSON in request body"})
    else:
        race_event = event

    # Validate required fields
    event_type = race_event.get("event_type")
    if not event_type:
        return _response(400, {"error": "Missing required field: event_type"})

    valid_event_types = {
        "lap_complete", "overtake", "powerup_used", "crash",
        "finish", "race_start", "new_lap_record"
    }
    if event_type not in valid_event_types:
        return _response(400, {
            "error": f"Invalid event_type '{event_type}'. Must be one of: {sorted(valid_event_types)}"
        })

    # Try Bedrock first; fall back to pre-written comments on any failure
    used_fallback = False
    commentary = ""

    try:
        prompt = build_commentary_prompt(race_event)
        logger.info("Invoking Bedrock model %s", BEDROCK_MODEL_ID)
        commentary = invoke_bedrock(prompt)
        logger.info("Bedrock commentary generated: %s", commentary)
    except ClientError as exc:
        error_code = exc.response["Error"]["Code"]
        logger.warning("Bedrock ClientError (%s): %s — using fallback", error_code, exc)
        commentary = get_fallback_comment(race_event)
        used_fallback = True
    except BotoCoreError as exc:
        logger.warning("BotoCoreError: %s — using fallback", exc)
        commentary = get_fallback_comment(race_event)
        used_fallback = True
    except Exception as exc:  # pylint: disable=broad-except
        logger.error("Unexpected error invoking Bedrock: %s", exc)
        commentary = get_fallback_comment(race_event)
        used_fallback = True

    payload = {
        "commentary": commentary,
        "event_type": event_type,
        "player": race_event.get("player", ""),
        "used_fallback": used_fallback,
    }

    return _response(200, payload)


def _response(status_code: int, body: dict) -> dict:
    return {
        "statusCode": status_code,
        "headers": {
            "Content-Type": "application/json",
            "Access-Control-Allow-Origin": "*",
            "Access-Control-Allow-Headers": "Content-Type,X-Amz-Date,Authorization",
            "Access-Control-Allow-Methods": "OPTIONS,POST",
        },
        "body": json.dumps(body),
    }
