#!/usr/bin/env bash
# Destroys the AwsKartStack and all associated AWS resources.
# DynamoDB table is retained (RemovalPolicy.RETAIN) — delete manually if needed.
set -euo pipefail

STACK_NAME="AwsKartStack"
REGION="us-east-1"
TABLE_NAME="AwsKartLeaderboard"

echo "==> Destroying CDK stack: $STACK_NAME in $REGION"
cd "$(dirname "$0")/../infra"
node_modules/.bin/cdk destroy "$STACK_NAME" --force

echo ""
echo "==> Stack destroyed."
echo ""
echo "NOTE: The DynamoDB table '$TABLE_NAME' was retained."
echo "      To delete it manually run:"
echo "      aws dynamodb delete-table --table-name $TABLE_NAME --region $REGION"
