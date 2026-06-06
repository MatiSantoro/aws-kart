#!/usr/bin/env node
/**
 * AWS Kart VR — CDK App Entry Point
 * Instantiates the AwsKartStack in us-east-1.
 */
import "source-map-support/register";
import * as cdk from "aws-cdk-lib";
import { AwsKartStack } from "../lib/aws-kart-stack";

const app = new cdk.App();

new AwsKartStack(app, "AwsKartStack", {
  env: {
    account: process.env.CDK_DEFAULT_ACCOUNT,
    region: "us-east-1",
  },
  description:
    "AWS Kart VR — Meta Quest racing game backend: DynamoDB leaderboard, " +
    "Bedrock AI commentator, API Gateway + Lambda.",
  tags: {
    Project: "AwsKartVR",
    Environment: "production",
    ManagedBy: "CDK",
  },
});

app.synth();
