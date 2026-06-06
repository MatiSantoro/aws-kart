# AWS Kart VR — Architecture Document

## 1. System Diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          Meta Quest Headset                               │
│                                                                           │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────┐  │
│  │ VRKartController│    │   RaceManager   │    │  CommentaryUI /     │  │
│  │  (OVRInput)     │    │  (lap / rank /  │    │  LeaderboardUI      │  │
│  │                 │    │   events)       │    │  (World-Space VR)   │  │
│  └────────┬────────┘    └────────┬────────┘    └──────────┬──────────┘  │
│           │                      │                         │              │
│           │    PowerUpSystem      │                         │              │
│           └──────────┬───────────┘                         │              │
│                      │                                      │              │
│                 ApiClient.cs  ◄────────────────────────────┘              │
│               (UnityWebRequest)                                            │
└──────────────────────┬───────────────────────────────────────────────────┘
                        │  HTTPS / REST
                        ▼
           ┌────────────────────────┐
           │   AWS API Gateway      │
           │   (REST API, prod)     │
           │                        │
           │  POST /commentary      │
           │  POST /leaderboard     │
           │  GET  /leaderboard     │
           │  GET  /leaderboard/    │
           │        global          │
           │  POST /powerups/       │
           │        activate        │
           └─────────┬──────────────┘
                      │
         ┌────────────┼──────────────┐
         │            │              │
         ▼            ▼              ▼
┌──────────────┐ ┌──────────┐ ┌──────────────┐
│ Commentator  │ │Leaderboard│ │  Power-ups  │
│   Lambda     │ │  Lambda   │ │   Lambda    │
│ Python 3.12  │ │Python 3.12│ │ Python 3.12 │
│ 512MB / 30s  │ │512MB / 30s│ │ 512MB / 30s │
└──────┬───────┘ └─────┬─────┘ └──────┬──────┘
       │               │               │
       ▼               ▼               ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│Amazon Bedrock│ │   DynamoDB   │ │   DynamoDB   │
│Claude 3 Haiku│ │ Leaderboard  │ │ (powerup     │
│ (commentary  │ │   Table      │ │  activation  │
│  generation) │ │  (GSI x3)    │ │  logging)    │
└──────────────┘ └──────────────┘ └──────────────┘
```

---

## 2. Meta Quest → API Gateway → Lambda → Bedrock Data Flow

### 2.1 Commentary Flow (most latency-sensitive)

```
[VR Kart Event]                        [Unity Game Thread]
     │
     │ Overtake / LapComplete / Crash / Finish
     │
     ▼
RaceManager.FireCommentaryEvent()
     │
     │ (async — does NOT block the game loop)
     ▼
ApiClient.PostCommentaryEvent()
     │
     │ UnityWebRequest coroutine, non-blocking
     │ Content-Type: application/json
     │ Timeout: 15s
     ▼
API Gateway POST /commentary
     │
     ▼
Commentator Lambda (cold start ~400ms / warm ~30ms)
     │
     ├─ Build prompt with race context
     │
     ▼
Amazon Bedrock: claude-3-haiku (anthropic.claude-3-haiku-20240307-v1:0)
     │   max_tokens=150, temperature=0.9
     │   Typical response latency: 500–1200ms
     │
     ▼
Lambda returns commentary text
     │
     ▼
API Gateway returns 200 + JSON body
     │
     ▼
ApiClient callback on Unity main thread
     │
     ▼
CommentaryUI.ShowCommentary(text)
     │
     ▼
Typewriter reveal + auto-hide after 4s
```

**Key design decision:** The Unity client fires commentary events and continues running the
race immediately. The commentary callback updates the UI whenever the response arrives — 
typically 600–1500ms after the triggering event. This ensures Bedrock latency never affects
kart physics or race logic.

### 2.2 Leaderboard Submission Flow

```
Race finishes → RaceManager.OnRaceFinished event
     │
     │ 1-second delay (player sees finish animation)
     ▼
ApiClient.PostLeaderboardEntry()
     │
     ▼
API Gateway POST /leaderboard
     │
     ▼
Leaderboard Lambda: DynamoDB PutItem
     │  (primary: entryId UUID, GSI attrs: trackId, playerId, globalPk)
     ▼
201 response → LeaderboardUI fetches GET /leaderboard?track=...
     │
     ▼
LeaderboardUI rows populated with top 10 times
```

### 2.3 Power-Up Activation Flow

```
Player collects pickup → PowerUpSystem holds powerUpType
     │
     │ B button pressed
     ▼
PowerUpSystem.TryActivatePowerUp()
     │
     ├── [Immediate] ApplyLocalEffect(powerUpType)  ← zero latency
     │       Physics/visual effect applied now
     │
     └── [Async] ApiClient.ActivatePowerUp()
             │
             ▼
         API Gateway POST /powerups/activate
             │
             ▼
         Powerups Lambda:
             ├── Validate powerup type
             ├── Log activation to DynamoDB
             └── Invoke Bedrock for flavor text (if enabled)
                     │
                     ▼
                 Return { effect, flavor_text, cooldown_seconds }
                     │
                     ▼
             PowerUpSystem callback → RaceManager.NotifyPowerUpActivated()
                     │
                     ▼
             CommentaryUI.ShowCommentary(flavor_text)
```

---

## 3. DynamoDB Table Design

### Table: `AwsKartLeaderboard`

**Primary Key:**
| Attribute | Type | Description |
|---|---|---|
| `entryId` | String (PK) | UUID — unique per race submission |

**Item attributes:**
| Attribute | Type | Description |
|---|---|---|
| `playerId` | String | Persistent player UUID (device ID or account) |
| `playerName` | String | Display name |
| `character` | String | AWS character (EC2, Lambda, etc.) |
| `trackId` | String | Track identifier (e.g. "us-east-1") |
| `timeMs` | Number | Race completion time in milliseconds |
| `globalPk` | String | Constant "GLOBAL" — enables global GSI queries |
| `powerupsUsed` | List | Array of power-up type strings used in the race |
| `timestamp` | Number | Unix timestamp (ms) of submission |

**Global Secondary Indexes:**

| GSI Name | PK | SK | Use Case |
|---|---|---|---|
| `TrackTimeIndex` | `trackId` | `timeMs` (ASC) | Top times per track |
| `PlayerTimeIndex` | `playerId` | `timeMs` (ASC) | Player race history |
| `GlobalTimeIndex` | `globalPk` (="GLOBAL") | `timeMs` (ASC) | Cross-track global rankings |

**Access patterns:**
1. Top 10 times on track "us-east-1": `Query TrackTimeIndex PK=us-east-1 Limit=10 ScanIndexForward=true`
2. Global top 20: `Query GlobalTimeIndex PK=GLOBAL Limit=20 ScanIndexForward=true`
3. Player history: `Query PlayerTimeIndex PK={playerId}`
4. Submit result: `PutItem` (always creates new entry; players can improve their rank naturally)

**Power-up activation records** are stored in the same table using a sentinel value:
- `entryId`: `POWERUP#{activationUUID}`
- `globalPk`: `POWERUP` (excluded from leaderboard GSI queries)

---

## 4. Unity Project Setup

### 4.1 Requirements

| Tool | Version |
|---|---|
| Unity Editor | 2022.3.x LTS (any 2022.3 patch) |
| Meta XR All-in-One SDK | 60.0.0 or later |
| TextMeshPro | Included in Unity (import via Package Manager) |
| Android Build Support | Unity Hub module |
| Target Device | Meta Quest 2 / Quest 3 / Quest Pro |

### 4.2 Step-by-Step Setup

**Step 1 — Install Unity 2022.3 LTS**
Download and install Unity 2022.3.x via Unity Hub. Ensure the Android Build Support module
is installed (required for Quest deployment).

**Step 2 — Open the project**
In Unity Hub, click "Open" and select the `unity-game/` folder. Unity will import all assets
on first open (~2–5 minutes).

**Step 3 — Import Meta XR SDK**
Option A (Meta store):
1. Open Package Manager (Window > Package Manager).
2. Click the `+` icon > Add package from git URL.
3. Enter: `https://npm.developer.oculus.com` as a scoped registry.
4. Search for "Meta XR All-in-One SDK" and install v60+.

Option B (Unity Asset Store):
1. Search for "Meta XR All-in-One SDK" on the Unity Asset Store.
2. Add to project and import.

**Step 4 — Configure OVR Plugin**
1. Go to **Edit > Project Settings > XR Plug-in Management**.
2. Under the **Android** tab, enable **Meta XR** (or Oculus XR Plugin).
3. Under **Meta XR > Handtracking**, enable if you want hand support.

**Step 5 — Player Settings**
1. **Edit > Project Settings > Player > Android tab:**
   - Company Name: your company
   - Bundle Identifier: `com.yourcompany.awskart`
   - Minimum API Level: Android 10.0 (API 29)
   - Target API Level: Android 12L (API 32)
2. Under **Other Settings**, enable "Custom Main Manifest" if you need permissions.

**Step 6 — Set API_BASE_URL**
After CDK deploy, copy the API Gateway URL from the stack output and paste it into:
`unity-game/Assets/Scripts/Network/ApiClient.cs` line with `API_BASE_URL`.

**Step 7 — Build and deploy**
1. Connect Meta Quest in developer mode via USB.
2. File > Build Settings > Android > select Quest as run device.
3. Click **Build and Run**.

### 4.3 OVRInput Quick Reference

| Action | OVRInput call |
|---|---|
| Right trigger (0-1) | `OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger)` |
| Left trigger (0-1) | `OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger)` |
| Left stick XY | `OVRInput.Get(OVRInput.RawAxis2D.LThumbstick)` |
| B button press | `OVRInput.GetDown(OVRInput.Button.Two)` |
| Haptic feedback | `OVRInput.SetControllerVibration(freq, amp, controller)` |

---

## 5. Bedrock Model Choice Rationale

### Why Claude 3 Haiku?

| Factor | Haiku | Sonnet | Why Haiku Wins Here |
|---|---|---|---|
| Latency | ~500–900ms | ~1200–2000ms | Race commentary must feel timely |
| Cost | ~$0.00025 / 1K input tokens | ~$0.003 / 1K input tokens | 12x cheaper; high call frequency per race |
| Quality | Good short-form wit | Better reasoning | Commentary is 1-2 sentences; Haiku excels |
| Context window | 200K tokens | 200K tokens | Equal; not a differentiator here |
| Availability | us-east-1 ✓ | us-east-1 ✓ | Both available |

**Model ID:** `anthropic.claude-3-haiku-20240307-v1:0`

**Prompt design:** The system prompt is ~450 tokens and instructs Haiku to be a race commentator
with specific AWS personality traits per character. User messages are ~50–80 tokens of race context.
Total input per call: ~500–530 tokens. At $0.00025 / 1K tokens, each commentary call costs ~$0.000125.
A full 3-lap race generates approximately 6–12 commentary events = ~$0.001–0.0015 per race.

---

## 6. Latency Considerations for In-Game Bedrock Calls

### Problem
End-to-end latency for a Bedrock call from a Meta Quest headset:

```
Quest → WiFi → Internet → API Gateway → Lambda (warm: ~20ms, cold: ~400ms)
    → Bedrock invoke → Claude 3 Haiku inference (~500–900ms)
    → Response back → Quest callback

Typical total: 600–1300ms (warm Lambda)
Worst case:    1500–2500ms (cold start + inference)
```

### Mitigations implemented

**1. Fire-and-forget pattern**
Commentary events are sent asynchronously in a coroutine that does NOT block the game loop.
The race continues at 72+ FPS regardless of API latency.

**2. Commentary cooldown**
`RaceManager.commentaryCooldown` (default 5 seconds) prevents flooding Bedrock with rapid events.
Back-to-back events are silently dropped on the client side.

**3. Pre-written fallback library**
If Bedrock fails, times out, or throttles the request, both the commentator and powerups Lambdas
fall back to a curated set of pre-written AWS-pun comments. The player always hears commentary.

**4. Short max_tokens (150)**
Keeping `max_tokens=150` bounds the maximum Bedrock response time and cost.

**5. Lambda warming**
Consider using Provisioned Concurrency on the commentator Lambda if you observe frequent cold
starts. One provisioned instance ($0.015/hr) eliminates the ~400ms cold-start penalty.

**6. Commentary queueing**
`CommentaryUI` queues up to 5 commentaries. If a delayed response arrives while a previous
commentary is still playing, it is queued — not dropped — so the player hears all important
moments in sequence.

---

## 7. How to Add a New Character

1. **Define stats** — decide topSpeed, acceleration, handling, durability (0-1), and a special ability.
2. **Add factory method** in `KartCharacter.cs`:
   ```csharp
   public static KartCharacter CreateSNS()
   {
       var c = CreateInstance<KartCharacter>();
       c.characterName = "SNS";
       // ... set stats
       return c;
   }
   ```
3. **Add to `CreateByName()`** switch expression in `KartCharacter.cs`.
4. **Update backend validation** — add `"SNS"` to the `valid_characters` set in
   `backend/leaderboard/handler.py`.
5. **Add to README** character table.
6. **Add character icon** — place `SNS.png` in `unity-game/Assets/Resources/CharacterIcons/`.
7. **Create ScriptableObject** — in Unity Editor, right-click in Assets > Create > AwsKart >
   KartCharacter, and assign the factory defaults or customise in Inspector.
8. **Add to commentary personality traits** in the system prompt in
   `backend/commentator/handler.py` (SYSTEM_PROMPT section at the bottom).

---

## 8. How to Add a New Power-Up

1. **Add to `POWERUP_DEFINITIONS`** in `backend/powerups/handler.py`:
   ```python
   "NewPowerUp": {
       "name": "New Power-Up Name",
       "description": "What it does.",
       "duration_seconds": 5.0,
       "cooldown_seconds": 30.0,
       "effect": { "type": "new_effect_type", ... },
       "flavor_prompt": "...",
       "fallback_flavors": ["...", "..."],
   }
   ```
2. **Add local effect** in `PowerUpSystem.cs` — add a `case "NewPowerUp":` branch in
   `ApplyLocalEffect()` and write the corresponding effect coroutine.
3. **Add cooldown default** in `PowerUpSystem.GetDefaultCooldown()`.
4. **Add pickup prefab** — create a scene pickup object with `PowerUpPickupMarker` component
   and set `PowerUpType = "NewPowerUp"`.
5. **Update README** power-ups table.

---

## 9. Infrastructure Costs (Estimate)

Assumptions: 1000 races/month, 3 laps each, ~10 commentary events per race.

| Service | Usage | Estimated Cost/Month |
|---|---|---|
| Lambda (3 functions) | ~60K invocations @ 512MB / avg 2s | ~$0.10 |
| API Gateway | ~60K requests | ~$0.02 |
| DynamoDB | ~1000 writes + 2000 reads (on-demand) | ~$0.003 |
| Bedrock Claude 3 Haiku | 10K calls × 530 tokens input + 50 output | ~$1.50 |
| CloudWatch Logs | ~500MB/month | ~$0.25 |
| **Total** | | **~$2/month for 1000 races** |

Bedrock is the dominant cost. For higher player counts, consider batching commentary events
client-side and reducing calls per race, or caching frequently-used commentary templates.
