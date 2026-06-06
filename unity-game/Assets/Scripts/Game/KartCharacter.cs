using UnityEngine;

namespace AwsKart.Game
{
    /// <summary>
    /// ScriptableObject defining an AWS service kart character's stats,
    /// visual identity, and special ability. Create instances via
    /// Assets > Create > AwsKart > KartCharacter, or use the static factory methods.
    /// </summary>
    [CreateAssetMenu(fileName = "NewKartCharacter", menuName = "AwsKart/KartCharacter")]
    public class KartCharacter : ScriptableObject
    {
        // -------------------------------------------------------------------------
        // Identity
        // -------------------------------------------------------------------------

        [Header("Identity")]
        [Tooltip("The AWS service name displayed in-game (e.g. 'Lambda', 'EC2').")]
        public string characterName;

        [Tooltip("Short description of the real AWS service shown on the character select screen.")]
        [TextArea(2, 4)]
        public string awsServiceDescription;

        // -------------------------------------------------------------------------
        // Stats (0.0 - 1.0 normalized; higher = better)
        // -------------------------------------------------------------------------

        [Header("Stats (0.0 – 1.0)")]
        [Range(0f, 1f)]
        [Tooltip("Maximum kart speed on a straight section.")]
        public float topSpeed;

        [Range(0f, 1f)]
        [Tooltip("How quickly the kart reaches top speed from a standstill.")]
        public float acceleration;

        [Range(0f, 1f)]
        [Tooltip("Cornering ability and steering responsiveness.")]
        public float handling;

        [Range(0f, 1f)]
        [Tooltip("How well the kart withstands collisions; affects spin-out chance.")]
        public float durability;

        // -------------------------------------------------------------------------
        // Special Ability
        // -------------------------------------------------------------------------

        [Header("Special Ability")]
        [Tooltip("Short name displayed on the HUD when the special is charged.")]
        public string specialAbilityName;

        [Tooltip("In-game tooltip describing what the special does.")]
        [TextArea(2, 4)]
        public string specialAbilityDescription;

        [Tooltip("Seconds before the special ability can be used again after activation.")]
        [Min(0f)]
        public float specialAbilityCooldown = 20f;

        // -------------------------------------------------------------------------
        // Visuals
        // -------------------------------------------------------------------------

        [Header("Visuals")]
        [Tooltip("Character portrait / icon shown in the HUD and leaderboard.")]
        public Sprite characterIcon;

        [Tooltip("Brand colour used for the kart livery and UI accents.")]
        public Color brandColor = Color.white;

        // -------------------------------------------------------------------------
        // Runtime helpers
        // -------------------------------------------------------------------------

        /// <summary>Returns the stat value clamped to [0, 1].</summary>
        public float ClampedTopSpeed => Mathf.Clamp01(topSpeed);
        public float ClampedAcceleration => Mathf.Clamp01(acceleration);
        public float ClampedHandling => Mathf.Clamp01(handling);
        public float ClampedDurability => Mathf.Clamp01(durability);

        /// <summary>
        /// Converts the normalised topSpeed stat into an actual max speed in
        /// Unity physics units/second for the kart controller.
        /// </summary>
        public float MaxSpeedMetersPerSecond(float baseMaxSpeed = 40f)
            => Mathf.Lerp(15f, baseMaxSpeed, ClampedTopSpeed);

        /// <summary>Acceleration force multiplier for the physics controller.</summary>
        public float AccelerationMultiplier(float baseForce = 3000f)
            => Mathf.Lerp(500f, baseForce, ClampedAcceleration);

        // -------------------------------------------------------------------------
        // Static factory methods — pre-configured character instances
        // These are used at runtime if no ScriptableObject assets are found.
        // -------------------------------------------------------------------------

        /// <summary>Heavy, fast top-end, slow to accelerate. Burst Mode special.</summary>
        public static KartCharacter CreateEC2()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "EC2";
            c.characterName = "EC2";
            c.awsServiceDescription =
                "Elastic Compute Cloud — the workhorse of AWS. " +
                "Configurable, powerful, and nearly unstoppable at full throttle. " +
                "Just don't expect a cold start to be quick.";
            c.topSpeed = 0.90f;
            c.acceleration = 0.35f;
            c.handling = 0.50f;
            c.durability = 0.95f;
            c.specialAbilityName = "Burst Mode";
            c.specialAbilityDescription =
                "Activates burstable instance mode — temporary +20% speed spike for 4 seconds. " +
                "Consumes accumulated CPU credits.";
            c.specialAbilityCooldown = 22f;
            c.brandColor = new Color(1.0f, 0.55f, 0.0f); // AWS Orange
            return c;
        }

        /// <summary>Lightweight, instant acceleration, poor top speed. Cold Start special.</summary>
        public static KartCharacter CreateLambda()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "Lambda";
            c.characterName = "Lambda";
            c.awsServiceDescription =
                "AWS Lambda — serverless, ephemeral, blazing fast off the line. " +
                "Invokes instantly but runs out of steam on the back straight. " +
                "Each function execution is a sprint, not a marathon.";
            c.topSpeed = 0.60f;
            c.acceleration = 1.00f;
            c.handling = 0.75f;
            c.durability = 0.30f;
            c.specialAbilityName = "Cold Start";
            c.specialAbilityDescription =
                "Instantly teleports the kart 20 metres forward as if skipping initialisation — " +
                "perfect for the race start or escaping a pack.";
            c.specialAbilityCooldown = 25f;
            c.brandColor = new Color(1.0f, 0.75f, 0.0f); // Lambda Gold
            return c;
        }

        /// <summary>Durable, steady, reliable — no standout stat. Eleven Nines special.</summary>
        public static KartCharacter CreateS3()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "S3";
            c.characterName = "S3";
            c.awsServiceDescription =
                "Simple Storage Service — the bedrock of AWS storage. " +
                "Not the fastest or most agile, but nothing breaks it. " +
                "99.999999999% durability means it shrugs off one crash, guaranteed.";
            c.topSpeed = 0.55f;
            c.acceleration = 0.45f;
            c.handling = 0.40f;
            c.durability = 1.00f;
            c.specialAbilityName = "Eleven Nines";
            c.specialAbilityDescription =
                "Activates 11-nines durability shield — the next crash or collision " +
                "is completely absorbed with no spin-out or speed loss.";
            c.specialAbilityCooldown = 30f;
            c.brandColor = new Color(0.20f, 0.60f, 0.86f); // S3 Blue
            return c;
        }

        /// <summary>Great cornering, fast drift. NoSQL Drift special.</summary>
        public static KartCharacter CreateDynamoDB()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "DynamoDB";
            c.characterName = "DynamoDB";
            c.awsServiceDescription =
                "Amazon DynamoDB — single-digit millisecond cornering. " +
                "A NoSQL drifter built for throughput and tight corners. " +
                "Gets boost from perfectly timed drifts, just like read replicas get speed from streams.";
            c.topSpeed = 0.70f;
            c.acceleration = 0.80f;
            c.handling = 1.00f;
            c.durability = 0.65f;
            c.specialAbilityName = "NoSQL Drift";
            c.specialAbilityDescription =
                "Triggers a perfect drift that restores boost meter instantly — " +
                "high-throughput cornering with zero latency penalty.";
            c.specialAbilityCooldown = 18f;
            c.brandColor = new Color(0.27f, 0.57f, 0.78f); // DynamoDB Teal-Blue
            return c;
        }

        /// <summary>Fastest straight-line speed, global edge presence. Edge Cache special.</summary>
        public static KartCharacter CreateCloudFront()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "CloudFront";
            c.characterName = "CloudFront";
            c.awsServiceDescription =
                "Amazon CloudFront — the global CDN. Unmatched on the straight, " +
                "serving content from the closest edge location at the speed of light. " +
                "Struggles on tight corners; built for long, fast circuits.";
            c.topSpeed = 1.00f;
            c.acceleration = 0.55f;
            c.handling = 0.60f;
            c.durability = 0.55f;
            c.specialAbilityName = "Edge Cache";
            c.specialAbilityDescription =
                "Triggers on long straight sections — activates edge caching mode " +
                "for an extra speed burst as if the data is pre-cached at the nearest PoP.";
            c.specialAbilityCooldown = 20f;
            c.brandColor = new Color(0.56f, 0.15f, 0.60f); // CloudFront Purple
            return c;
        }

        /// <summary>AI-assisted steering. AutoPilot special.</summary>
        public static KartCharacter CreateSageMaker()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "SageMaker";
            c.characterName = "SageMaker";
            c.awsServiceDescription =
                "Amazon SageMaker — the ML-powered all-rounder. " +
                "Steady across all metrics with an AI twist: AutoPilot finds the " +
                "optimal racing line automatically, like a trained inference endpoint.";
            c.topSpeed = 0.65f;
            c.acceleration = 0.60f;
            c.handling = 0.80f;
            c.durability = 0.60f;
            c.specialAbilityName = "AutoPilot";
            c.specialAbilityDescription =
                "AI-assisted steering takes over for 3 seconds, finding the optimal " +
                "racing line through the next corner automatically.";
            c.specialAbilityCooldown = 28f;
            c.brandColor = new Color(0.07f, 0.42f, 0.63f); // SageMaker Dark Blue
            return c;
        }

        /// <summary>Wildcard — random stat boosts each race. Foundation Model special.</summary>
        public static KartCharacter CreateBedrock()
        {
            var c = CreateInstance<KartCharacter>();
            c.name = "Bedrock";
            c.characterName = "Bedrock";
            c.awsServiceDescription =
                "Amazon Bedrock — the wildcard foundation model racer. " +
                "Stats are randomly sampled from the Foundation Model pool each race. " +
                "You might get a monster, you might get a minnow. Embrace uncertainty.";
            c.topSpeed = 0.75f;
            c.acceleration = 0.75f;
            c.handling = 0.70f;
            c.durability = 0.70f;
            c.specialAbilityName = "Foundation Model";
            c.specialAbilityDescription =
                "Randomly samples a new stat profile from the model pool — " +
                "all four stats are re-rolled mid-race. High risk, high reward.";
            c.specialAbilityCooldown = 35f;
            c.brandColor = new Color(0.85f, 0.20f, 0.20f); // Bedrock Red
            return c;
        }

        /// <summary>
        /// Returns a pre-configured character by name.
        /// Returns null if name is not recognised.
        /// </summary>
        public static KartCharacter CreateByName(string name)
        {
            return name switch
            {
                "EC2" => CreateEC2(),
                "Lambda" => CreateLambda(),
                "S3" => CreateS3(),
                "DynamoDB" => CreateDynamoDB(),
                "CloudFront" => CreateCloudFront(),
                "SageMaker" => CreateSageMaker(),
                "Bedrock" => CreateBedrock(),
                _ => null
            };
        }

        /// <summary>Returns a random stat boost for Bedrock's Foundation Model ability.</summary>
        public void RandomiseBedrockStats()
        {
            if (characterName != "Bedrock") return;
            topSpeed = Random.Range(0.4f, 1.0f);
            acceleration = Random.Range(0.4f, 1.0f);
            handling = Random.Range(0.4f, 1.0f);
            durability = Random.Range(0.4f, 1.0f);
            Debug.Log($"[Bedrock] Foundation Model re-rolled: " +
                      $"spd={topSpeed:F2} acc={acceleration:F2} hdl={handling:F2} dur={durability:F2}");
        }
    }
}
