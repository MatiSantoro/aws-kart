import * as path from "path";
import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as apigateway from "aws-cdk-lib/aws-apigateway";
import * as iam from "aws-cdk-lib/aws-iam";
import * as logs from "aws-cdk-lib/aws-logs";
import * as cloudwatch from "aws-cdk-lib/aws-cloudwatch";
import * as cw_actions from "aws-cdk-lib/aws-cloudwatch-actions";
import * as sns from "aws-cdk-lib/aws-sns";

export class AwsKartStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // -------------------------------------------------------------------------
    // DynamoDB — AwsKartLeaderboard
    // -------------------------------------------------------------------------

    const table = new dynamodb.Table(this, "LeaderboardTable", {
      tableName: "AwsKartLeaderboard",
      partitionKey: { name: "entryId", type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      pointInTimeRecovery: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // Top times per track — sorted ascending by timeMs (fastest first)
    table.addGlobalSecondaryIndex({
      indexName: "TrackTimeIndex",
      partitionKey: { name: "trackId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "timeMs", type: dynamodb.AttributeType.NUMBER },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Per-player race history — sorted ascending by timeMs
    table.addGlobalSecondaryIndex({
      indexName: "PlayerTimeIndex",
      partitionKey: { name: "playerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "timeMs", type: dynamodb.AttributeType.NUMBER },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Global leaderboard across all tracks — PK is always the constant "GLOBAL"
    table.addGlobalSecondaryIndex({
      indexName: "GlobalTimeIndex",
      partitionKey: { name: "globalPk", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "timeMs", type: dynamodb.AttributeType.NUMBER },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // -------------------------------------------------------------------------
    // Shared Bedrock permission (commentator + powerups Lambdas)
    // -------------------------------------------------------------------------

    const bedrockInvokePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ["bedrock:InvokeModel"],
      resources: [
        // Foundation model (underlying)
        `arn:aws:bedrock:*::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0`,
        // Cross-region inference profile (required for on-demand invocation of Haiku 4.5)
        `arn:aws:bedrock:${this.region}:${this.account}:inference-profile/us.anthropic.claude-haiku-4-5-20251001-v1:0`,
      ],
    });

    // -------------------------------------------------------------------------
    // Lambda — shared defaults
    // -------------------------------------------------------------------------

    const commonProps: Pick<
      lambda.FunctionProps,
      "runtime" | "memorySize" | "timeout" | "logRetention"
    > = {
      runtime: lambda.Runtime.PYTHON_3_12,
      memorySize: 512,
      timeout: cdk.Duration.seconds(30),
      logRetention: logs.RetentionDays.ONE_MONTH,
    };

    // -------------------------------------------------------------------------
    // Lambda — Commentator  (Bedrock → AI race commentary)
    // -------------------------------------------------------------------------

    const commentatorFn = new lambda.Function(this, "CommentatorFunction", {
      ...commonProps,
      functionName: "AwsKart-Commentator",
      description:
        "Generates real-time AI race commentary via Amazon Bedrock (Claude Haiku 4.5).",
      code: lambda.Code.fromAsset(
        path.join(__dirname, "../../backend/commentator")
      ),
      handler: "handler.handler",
      environment: {
        BEDROCK_REGION: this.region,
      },
    });

    commentatorFn.addToRolePolicy(bedrockInvokePolicy);
    // Provisioned concurrency (1 instance) can be added here once the account's
    // Lambda concurrency limit is raised above 10 via a Service Quotas increase request.

    // -------------------------------------------------------------------------
    // Lambda — Leaderboard  (DynamoDB CRUD)
    // -------------------------------------------------------------------------

    const leaderboardFn = new lambda.Function(this, "LeaderboardFunction", {
      ...commonProps,
      functionName: "AwsKart-Leaderboard",
      description: "Race result submission and leaderboard queries via DynamoDB.",
      code: lambda.Code.fromAsset(
        path.join(__dirname, "../../backend/leaderboard")
      ),
      handler: "handler.handler",
      environment: {
        LEADERBOARD_TABLE_NAME: table.tableName,
      },
    });

    table.grantReadWriteData(leaderboardFn);

    // -------------------------------------------------------------------------
    // Lambda — Power-ups  (DynamoDB logging + Bedrock flavor text)
    // -------------------------------------------------------------------------

    const powerupsFn = new lambda.Function(this, "PowerupsFunction", {
      ...commonProps,
      functionName: "AwsKart-Powerups",
      description:
        "Power-up activation validation, DynamoDB logging, and Bedrock flavor text.",
      code: lambda.Code.fromAsset(
        path.join(__dirname, "../../backend/powerups")
      ),
      handler: "handler.handler",
      environment: {
        BEDROCK_REGION: this.region,
        LEADERBOARD_TABLE_NAME: table.tableName,
      },
    });

    powerupsFn.addToRolePolicy(bedrockInvokePolicy);
    table.grantReadWriteData(powerupsFn);

    // -------------------------------------------------------------------------
    // API Gateway — REST API  (stage: prod)
    // -------------------------------------------------------------------------

    const api = new apigateway.RestApi(this, "AwsKartApi", {
      restApiName: "AwsKartAPI",
      description:
        "AWS Kart VR — commentary, leaderboard, and power-up endpoints.",
      deployOptions: {
        stageName: "prod",
        throttlingBurstLimit: 100,
        throttlingRateLimit: 50,
        loggingLevel: apigateway.MethodLoggingLevel.ERROR,
        dataTraceEnabled: false,
      },
      defaultCorsPreflightOptions: {
        allowOrigins: apigateway.Cors.ALL_ORIGINS,
        allowMethods: apigateway.Cors.ALL_METHODS,
        allowHeaders: ["Content-Type", "Authorization", "X-Amz-Date", "x-api-key"],
      },
    });

    // API key + usage plan — prevents unauthenticated abuse of Bedrock endpoints
    const apiKey = new apigateway.ApiKey(this, "AwsKartApiKey", {
      apiKeyName: "AwsKartApiKey",
      description: "API key for Meta Quest game client",
    });

    const usagePlan = new apigateway.UsagePlan(this, "AwsKartUsagePlan", {
      name: "AwsKartUsagePlan",
      throttle: { burstLimit: 50, rateLimit: 20 },
      apiStages: [{ api, stage: api.deploymentStage }],
    });
    usagePlan.addApiKey(apiKey);

    const methodOptions: apigateway.MethodOptions = { apiKeyRequired: true };

    const commentatorIntegration = new apigateway.LambdaIntegration(
      commentatorFn
    );
    const leaderboardIntegration = new apigateway.LambdaIntegration(
      leaderboardFn
    );
    const powerupsIntegration = new apigateway.LambdaIntegration(powerupsFn);

    // POST /commentary
    api.root
      .addResource("commentary")
      .addMethod("POST", commentatorIntegration, methodOptions);

    // POST /leaderboard  — submit race result
    // GET  /leaderboard  — top times for a track (?track=us-east-1&limit=10)
    const leaderboardResource = api.root.addResource("leaderboard");
    leaderboardResource.addMethod("POST", leaderboardIntegration, methodOptions);
    leaderboardResource.addMethod("GET", leaderboardIntegration, methodOptions);

    // GET /leaderboard/global
    leaderboardResource
      .addResource("global")
      .addMethod("GET", leaderboardIntegration, methodOptions);

    // GET /leaderboard/player/{playerId}
    leaderboardResource
      .addResource("player")
      .addResource("{playerId}")
      .addMethod("GET", leaderboardIntegration, methodOptions);

    // POST /powerups/activate
    api.root
      .addResource("powerups")
      .addResource("activate")
      .addMethod("POST", powerupsIntegration, methodOptions);

    // -------------------------------------------------------------------------
    // CloudWatch alarms
    // -------------------------------------------------------------------------

    const alarmTopic = new sns.Topic(this, "AwsKartAlarmTopic", {
      topicName: "AwsKartAlarms",
      displayName: "AWS Kart VR — Lambda error and throttle alarms",
    });

    const snsAction = new cw_actions.SnsAction(alarmTopic);

    const makeErrorAlarm = (
      id: string,
      fn: lambda.Function,
      label: string
    ) => {
      const alarm = new cloudwatch.Alarm(this, id, {
        metric: fn.metricErrors({ period: cdk.Duration.minutes(5) }),
        threshold: 5,
        evaluationPeriods: 1,
        alarmDescription: `${label} Lambda errors > 5 in 5 minutes`,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      alarm.addAlarmAction(snsAction);
      return alarm;
    };

    makeErrorAlarm("CommentatorErrorAlarm", commentatorFn, "Commentator");
    makeErrorAlarm("LeaderboardErrorAlarm", leaderboardFn, "Leaderboard");
    makeErrorAlarm("PowerupsErrorAlarm", powerupsFn, "Powerups");

    // Throttle alarm on commentator — provisioned concurrency makes this a real signal
    const throttleAlarm = new cloudwatch.Alarm(this, "CommentatorThrottleAlarm", {
      metric: commentatorFn.metricThrottles({ period: cdk.Duration.minutes(1) }),
      threshold: 0,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      alarmDescription: "Commentator Lambda throttled — consider raising provisioned concurrency",
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    throttleAlarm.addAlarmAction(snsAction);

    // -------------------------------------------------------------------------
    // Stack outputs
    // -------------------------------------------------------------------------

    new cdk.CfnOutput(this, "ApiUrl", {
      value: api.url,
      description:
        'Paste into ApiClient.cs as API_BASE_URL (e.g. "https://xxx.execute-api.us-east-1.amazonaws.com/prod/").',
      exportName: "AwsKartApiUrl",
    });

    new cdk.CfnOutput(this, "ApiKeyId", {
      value: apiKey.keyId,
      description:
        "API key ID — retrieve the actual value with: aws apigateway get-api-key --api-key <id> --include-value --query value --output text",
    });

    new cdk.CfnOutput(this, "AlarmTopicArn", {
      value: alarmTopic.topicArn,
      description: "SNS topic for Lambda error/throttle alarms. Subscribe your email to receive alerts.",
    });

    new cdk.CfnOutput(this, "LeaderboardTableArn", {
      value: table.tableArn,
      description: "DynamoDB leaderboard table ARN.",
    });
  }
}
