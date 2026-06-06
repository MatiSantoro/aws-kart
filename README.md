# AWS Kart VR

A Meta Quest VR racing game in the style of Mario Kart, where every character is an AWS service. Race across cloud regions, deploy power-ups, and listen to real-time AI commentary powered by Amazon Bedrock (Claude 3 Haiku).

---

## What Is This?

AWS Kart VR is a fully immersive virtual reality kart racer built for the Meta Quest 2/3/Pro headsets. Players choose an AWS service as their character (each with stats inspired by the real service's properties), race on tracks themed after AWS regions, collect AWS-themed power-ups, and hear a Bedrock-powered AI commentator deliver witty, service-pun-filled race commentary in real time.

---

## Tech Stack

| Layer | Technology |
|---|---|
| VR Runtime | Unity 2022 LTS (2022.3.x) + Meta XR SDK 60+ |
| VR Target | Meta Quest 2 / Quest 3 / Quest Pro |
| AI Commentary | Amazon Bedrock — Claude 3 Haiku |
| Backend API | AWS API Gateway (REST) + AWS Lambda (Python 3.12) |
| Leaderboard DB | Amazon DynamoDB |
| Infrastructure | AWS CDK v2 (TypeScript) |
| Region | us-east-1 (primary) |

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        Meta Quest Headset                        │
│                                                                  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │ VRKartCtrl   │  │  RaceManager │  │   CommentaryUI / UI  │  │
│  │ (OVRInput)   │  │  (lap/pos)   │  │   LeaderboardUI      │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────┬───────────┘  │
│         │                  │                      │              │
│         └──────────────────┼──────────────────────┘              │
│                            │                                     │
│                     ApiClient.cs                                 │
│                    (UnityWebRequest)                              │
└────────────────────────────┼────────────────────────────────────┘
                             │ HTTPS
                             ▼
                  ┌──────────────────────┐
                  │   AWS API Gateway    │
                  │   (REST API)         │
                  │                      │
                  │  POST /commentary    │
                  │  POST /leaderboard   │
                  │  GET  /leaderboard   │
                  │  POST /powerups/...  │
                  └──────────┬───────────┘
                             │
              ┌──────────────┼──────────────┐
              │              │              │
              ▼              ▼              ▼
    ┌──────────────┐ ┌────────────┐ ┌─────────────┐
    │  Commentator │ │Leaderboard │ │  Power-ups  │
    │   Lambda     │ │  Lambda    │ │   Lambda    │
    │ (Python 3.12)│ │(Python 3.12│ │(Python 3.12)│
    └──────┬───────┘ └─────┬──────┘ └──────┬──────┘
           │               │               │
           ▼               ▼               │
   ┌──────────────┐ ┌────────────┐        │
   │Amazon Bedrock│ │  DynamoDB  │◄───────┘
   │ Claude 3     │ │Leaderboard │
   │ Haiku        │ │   Table    │
   └──────────────┘ └────────────┘
```

---

## Quick Start

### Backend (CDK Deploy)

Prerequisites: Node.js 18+, AWS CLI configured, CDK CLI installed globally.

```bash
cd infra
npm install
npx cdk bootstrap aws://YOUR_ACCOUNT_ID/us-east-1
npx cdk deploy
```

After deploy, note the API Gateway URL in the stack outputs. Copy it into `unity-game/Assets/Scripts/Network/ApiClient.cs` as `API_BASE_URL`.

### Unity Setup

1. Install **Unity 2022.3.x LTS** via Unity Hub.
2. Open the `unity-game/` folder as a Unity project.
3. In the Unity Package Manager, import **Meta XR All-in-One SDK** (v60+) from the Meta registry or OpenXR plugin.
4. Under **Edit > Project Settings > XR Plug-in Management**, enable **Meta XR** (Android) and set target to Quest 2/3.
5. In **Edit > Project Settings > Player**, set the Company Name and Bundle Identifier (e.g., `com.yourcompany.awskart`).
6. Build for Android (File > Build Settings > Android > Build and Run) with your Quest connected in developer mode.

---

## AWS Characters and Kart Stats

Stats are normalized 0.0 – 1.0. Higher is better.

| Character | Top Speed | Acceleration | Handling | Durability | Special Ability |
|---|---|---|---|---|---|
| **EC2** | 0.90 | 0.35 | 0.50 | 0.95 | Burst Mode — temporary 20% speed spike |
| **Lambda** | 0.60 | 1.00 | 0.75 | 0.30 | Cold Start — instant teleport boost off the line |
| **S3** | 0.55 | 0.45 | 0.40 | 1.00 | Eleven Nines — immunity to one crash |
| **DynamoDB** | 0.70 | 0.80 | 1.00 | 0.65 | NoSQL Drift — perfect drift that restores boost |
| **CloudFront** | 1.00 | 0.55 | 0.60 | 0.55 | Edge Cache — speed boost on straight sections |
| **SageMaker** | 0.65 | 0.60 | 0.80 | 0.60 | AutoPilot — AI-assisted steering for 3 seconds |
| **Bedrock** | 0.75 | 0.75 | 0.70 | 0.70 | Foundation Model — random stat boost each race |

### Character Descriptions

- **EC2 (Elastic Compute Cloud):** The heavy-hitter. Once up to speed, nearly unstoppable — but getting there takes time. Great on long tracks with few turns.
- **Lambda:** Zero to hero instantly. Unmatched off-the-line acceleration, but runs out of steam on long straights. Perfect for tight, technical tracks.
- **S3 (Simple Storage Service):** Consistent, reliable, dull. No standout stat but nothing weak either. Eleven Nines durability means one free get-out-of-jail crash.
- **DynamoDB:** The drift king. Single-digit millisecond cornering with a NoSQL drift ability that turns tight corners into speed boosts.
- **CloudFront:** Fastest racer on a straight. Edge Cache ability triggers on long straights for an extra burst. Struggles with tight corners.
- **SageMaker:** Steady all-rounder with an AI twist — AutoPilot briefly takes control and finds the optimal racing line automatically.
- **Bedrock:** Wild card. Stats are randomized each race from the Foundation Model pool — you might get a monster or a minnow. High risk, high reward.

---

## AWS-Themed Power-ups

| Power-up | Effect | Duration |
|---|---|---|
| **Auto Scaling** | Clone yourself — a ghost kart copies your moves for 5 seconds, confusing opponents | 5s |
| **Shield Advanced (DDoS Shield)** | Full invincibility — projectiles and crashes pass through you | 6s |
| **Elastic IP** | Teleport instantly to any fixed checkpoint on the track | Instant |
| **CloudWatch Alarm** | Trigger a race-wide alarm: all opponents are stunned briefly (0.8s stun) | Instant |
| **Cost Optimizer** | Throttle opponent karts — all rivals' top speed reduced by 30% for 8 seconds | 8s |
| **Glacier** | Freeze the nearest opponent solid for 3 seconds | 3s |

---

## Project Structure

```
aws-kart/
├── .gitignore
├── README.md
├── backend/
│   ├── commentator/
│   │   └── handler.py          # Bedrock-powered AI commentary Lambda
│   ├── leaderboard/
│   │   └── handler.py          # DynamoDB leaderboard CRUD Lambda
│   ├── powerups/
│   │   └── handler.py          # Power-up activation and logging Lambda
│   └── requirements.txt
├── infra/
│   ├── bin/
│   │   └── aws-kart.ts         # CDK app entry point
│   ├── lib/
│   │   └── aws-kart-stack.ts   # CDK stack (all AWS resources)
│   ├── package.json
│   └── tsconfig.json
├── unity-game/
│   └── Assets/
│       └── Scripts/
│           ├── Network/
│           │   └── ApiClient.cs
│           ├── Game/
│           │   ├── KartCharacter.cs
│           │   ├── PowerUpSystem.cs
│           │   └── RaceManager.cs
│           ├── VR/
│           │   └── VRKartController.cs
│           └── UI/
│               ├── CommentaryUI.cs
│               └── LeaderboardUI.cs
└── docs/
    └── architecture.md
```

---

## License

MIT — build your cloud kart, race your services.
