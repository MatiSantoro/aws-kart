using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using AwsKart.Network;

namespace AwsKart.Game
{
    /// <summary>
    /// Manages power-up collection and activation for a kart in the race.
    ///
    /// Attach to the kart GameObject. Requires:
    ///   - A trigger Collider for pickup detection
    ///   - ApiClient in the scene (singleton)
    ///   - RaceManager (singleton)
    ///
    /// Power-up types match the backend POWERUP_DEFINITIONS keys:
    ///   AutoScaling, ShieldAdvanced, ElasticIP, CloudWatchAlarm,
    ///   CostOptimizer, Glacier
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PowerUpSystem : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Inspector configuration
        // -------------------------------------------------------------------------

        [Header("Settings")]
        [Tooltip("Tag on pickup GameObjects (trigger colliders).")]
        [SerializeField] private string pickupTag = "PowerUpPickup";

        [Tooltip("If true, pressing the activate button calls backend for flavor text.")]
        [SerializeField] private bool requestBedrockFlavor = true;

        [Header("Events")]
        [Tooltip("Fired when a power-up is collected. Passes power-up type string.")]
        public UnityEvent<string> OnPowerUpCollected;

        [Tooltip("Fired when a power-up is successfully activated. Passes PowerUpResult.")]
        public UnityEvent<PowerUpResult> OnPowerUpActivated;

        [Tooltip("Fired when a power-up's effect ends.")]
        public UnityEvent<string> OnPowerUpExpired;

        [Header("Audio / Haptics")]
        [SerializeField] private AudioClip collectSfx;
        [SerializeField] private AudioClip activateSfx;
        private AudioSource _audioSource;

        // -------------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------------

        private string _heldPowerUpType = null;
        private bool _isActivating = false;

        // Cooldown tracking: powerUpType -> Time.time when it becomes available again
        private readonly Dictionary<string, float> _cooldownEnds = new();

        // Active effect tracking
        private string _activeEffectType = null;
        private Coroutine _activeEffectCoroutine = null;

        // References
        private KartCharacter _character;
        private string _playerId;
        private string _currentTrack = "us-east-1";

        // External references to apply local effects
        private Rigidbody _rb;

        // Original physics values for restoring after effects
        private float _originalMaxSpeed;
        private float _originalAcceleration;

        // -------------------------------------------------------------------------
        // Lifecycle
        // -------------------------------------------------------------------------

        private void Awake()
        {
            _rb = GetComponentInParent<Rigidbody>() ?? GetComponent<Rigidbody>();
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();

            _playerId = SystemInfo.deviceUniqueIdentifier;
        }

        private void Start()
        {
            // Grab character reference from race manager if available
            if (RaceManager.Instance != null)
            {
                _currentTrack = RaceManager.Instance.TrackId;
            }
        }

        /// <summary>Set the character so power-up effects can scale with character stats.</summary>
        public void Initialise(KartCharacter character, string playerId = null)
        {
            _character = character;
            if (playerId != null) _playerId = playerId;
        }

        // -------------------------------------------------------------------------
        // Pickup detection
        // -------------------------------------------------------------------------

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(pickupTag)) return;
            if (_heldPowerUpType != null) return; // already holding one

            // Get the power-up type from the pickup object
            var pickup = other.GetComponent<PowerUpPickupMarker>();
            string powerUpType = pickup != null ? pickup.PowerUpType : GetRandomPowerUpType();

            CollectPowerUp(powerUpType);

            // Disable the pickup so other karts can't grab it
            other.gameObject.SetActive(false);
        }

        private void CollectPowerUp(string powerUpType)
        {
            _heldPowerUpType = powerUpType;
            Debug.Log($"[PowerUpSystem] Collected: {powerUpType}");

            if (collectSfx != null) _audioSource.PlayOneShot(collectSfx);
            OnPowerUpCollected?.Invoke(powerUpType);
        }

        // -------------------------------------------------------------------------
        // Activation — called by VRKartController when B button pressed
        // -------------------------------------------------------------------------

        /// <summary>
        /// Attempt to activate the currently held power-up.
        /// Called from VRKartController on B button press.
        /// </summary>
        public void TryActivatePowerUp()
        {
            if (_heldPowerUpType == null)
            {
                Debug.Log("[PowerUpSystem] No power-up held.");
                return;
            }

            if (_isActivating)
            {
                Debug.Log("[PowerUpSystem] Already activating a power-up.");
                return;
            }

            if (IsOnCooldown(_heldPowerUpType))
            {
                float remaining = _cooldownEnds[_heldPowerUpType] - Time.time;
                Debug.Log($"[PowerUpSystem] {_heldPowerUpType} on cooldown for {remaining:F1}s.");
                return;
            }

            StartCoroutine(ActivatePowerUpCoroutine(_heldPowerUpType));
            _heldPowerUpType = null;
        }

        private IEnumerator ActivatePowerUpCoroutine(string powerUpType)
        {
            _isActivating = true;

            int currentLap = RaceManager.Instance != null ? RaceManager.Instance.CurrentLap : 1;
            string character = _character != null ? _character.characterName : "EC2";

            PowerUpResult result = default;
            bool responseReceived = false;

            // Fire and forget to backend — non-blocking
            ApiClient.Instance.ActivatePowerUp(
                playerId: _playerId,
                character: character,
                powerUpType: powerUpType,
                track: _currentTrack,
                lap: currentLap,
                onComplete: (r) =>
                {
                    result = r;
                    responseReceived = true;
                }
            );

            // Apply local effect immediately without waiting for backend
            ApplyLocalEffect(powerUpType);

            // Set cooldown based on built-in definition
            float cooldown = GetDefaultCooldown(powerUpType);
            _cooldownEnds[powerUpType] = Time.time + cooldown;

            if (activateSfx != null) _audioSource.PlayOneShot(activateSfx);

            // Wait up to 5 seconds for backend response (for flavor text only)
            float waitStart = Time.time;
            while (!responseReceived && Time.time - waitStart < 5f)
            {
                yield return null;
            }

            if (responseReceived)
            {
                OnPowerUpActivated?.Invoke(result);
            }
            else
            {
                // Fire event with a local-only result so UI still updates
                var localResult = new PowerUpResult
                {
                    powerup_type = powerUpType,
                    name = powerUpType,
                    flavor_text = $"{character} activates {powerUpType}!",
                    used_fallback = true,
                };
                OnPowerUpActivated?.Invoke(localResult);
            }

            _isActivating = false;
        }

        // -------------------------------------------------------------------------
        // Local effect application
        // -------------------------------------------------------------------------

        private void ApplyLocalEffect(string powerUpType)
        {
            // Cancel any existing timed effect
            if (_activeEffectCoroutine != null)
            {
                StopCoroutine(_activeEffectCoroutine);
                RestoreKartStats();
            }

            _activeEffectType = powerUpType;

            switch (powerUpType)
            {
                case "AutoScaling":
                    _activeEffectCoroutine = StartCoroutine(AutoScalingEffect());
                    break;
                case "ShieldAdvanced":
                    _activeEffectCoroutine = StartCoroutine(ShieldEffect(6f));
                    break;
                case "ElasticIP":
                    ApplyElasticIPTeleport();
                    _activeEffectType = null;
                    break;
                case "CloudWatchAlarm":
                    _activeEffectCoroutine = StartCoroutine(CloudWatchAlarmEffect());
                    break;
                case "CostOptimizer":
                    _activeEffectCoroutine = StartCoroutine(CostOptimizerEffect(8f));
                    break;
                case "Glacier":
                    _activeEffectCoroutine = StartCoroutine(GlacierEffect());
                    break;
                default:
                    Debug.LogWarning($"[PowerUpSystem] Unknown power-up type: {powerUpType}");
                    break;
            }
        }

        private IEnumerator AutoScalingEffect()
        {
            // Spawn a ghost clone that mirrors this kart's transform
            GameObject clone = Instantiate(gameObject, transform.position, transform.rotation);
            var cloneRb = clone.GetComponent<Rigidbody>();
            if (cloneRb != null) cloneRb.isKinematic = true;

            // Remove clone's power-up system so it doesn't interfere
            var clonePowerUp = clone.GetComponent<PowerUpSystem>();
            if (clonePowerUp != null) Destroy(clonePowerUp);

            float elapsed = 0f;
            while (elapsed < 5f)
            {
                if (clone != null)
                {
                    clone.transform.position = transform.position - transform.right * 2f;
                    clone.transform.rotation = transform.rotation;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (clone != null) Destroy(clone);
            ExpireEffect("AutoScaling");
        }

        private IEnumerator ShieldEffect(float duration)
        {
            // Tag this kart as invincible — the collision system checks this tag
            gameObject.layer = LayerMask.NameToLayer("Invincible");
            var renderer = GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                // Pulse the material color to indicate invincibility
                Color originalColor = renderer.material.color;
                float elapsed = 0f;
                while (elapsed < duration)
                {
                    float t = Mathf.PingPong(elapsed * 4f, 1f);
                    renderer.material.color = Color.Lerp(originalColor, Color.cyan, t * 0.5f);
                    elapsed += Time.deltaTime;
                    yield return null;
                }
                renderer.material.color = originalColor;
            }
            else
            {
                yield return new WaitForSeconds(duration);
            }

            gameObject.layer = LayerMask.NameToLayer("Default");
            ExpireEffect("ShieldAdvanced");
        }

        private void ApplyElasticIPTeleport()
        {
            if (RaceManager.Instance == null) return;
            Transform checkpoint = RaceManager.Instance.GetNearestCheckpoint(transform.position);
            if (checkpoint == null) return;

            // Teleport kart to checkpoint position
            if (_rb != null) _rb.linearVelocity = Vector3.zero;
            transform.position = checkpoint.position + Vector3.up * 0.5f;
            transform.rotation = checkpoint.rotation;

            Debug.Log($"[PowerUpSystem] Elastic IP: teleported to checkpoint {checkpoint.name}");
        }

        private IEnumerator CloudWatchAlarmEffect()
        {
            // Notify RaceManager to stun all opponents
            if (RaceManager.Instance != null)
            {
                RaceManager.Instance.StunAllOpponents(this, 0.8f);
            }
            yield return new WaitForSeconds(0.1f);
            ExpireEffect("CloudWatchAlarm");
        }

        private IEnumerator CostOptimizerEffect(float duration)
        {
            // Tell RaceManager to throttle all opponent karts
            if (RaceManager.Instance != null)
            {
                RaceManager.Instance.ThrottleOpponents(this, 0.30f, duration);
            }
            yield return new WaitForSeconds(duration);
            ExpireEffect("CostOptimizer");
        }

        private IEnumerator GlacierEffect()
        {
            // Find nearest opponent kart and freeze it
            GameObject nearest = FindNearestOpponentKart();
            if (nearest != null)
            {
                var opponentRb = nearest.GetComponent<Rigidbody>();
                var opponentController = nearest.GetComponent<AwsKart.VR.VRKartController>();

                if (opponentController != null) opponentController.SetFrozen(true);
                if (opponentRb != null)
                {
                    Vector3 savedVelocity = opponentRb.linearVelocity;
                    opponentRb.linearVelocity = Vector3.zero;
                    opponentRb.constraints = RigidbodyConstraints.FreezeAll;

                    yield return new WaitForSeconds(3f);

                    opponentRb.constraints = RigidbodyConstraints.None |
                                            RigidbodyConstraints.FreezeRotationX |
                                            RigidbodyConstraints.FreezeRotationZ;
                    opponentRb.linearVelocity = savedVelocity * 0.3f;
                }
                else
                {
                    yield return new WaitForSeconds(3f);
                }

                if (opponentController != null) opponentController.SetFrozen(false);
            }
            else
            {
                yield return new WaitForSeconds(3f);
            }

            ExpireEffect("Glacier");
        }

        // -------------------------------------------------------------------------
        // Throttle / Freeze — called by opponents' power-up effects via RaceManager
        // -------------------------------------------------------------------------

        private bool _isThrottled = false;
        private float _throttleReduction = 0f;

        public void ApplyThrottle(float reductionPercent, float duration)
        {
            StartCoroutine(ThrottleCoroutine(reductionPercent, duration));
        }

        private IEnumerator ThrottleCoroutine(float reductionPercent, float duration)
        {
            _isThrottled = true;
            _throttleReduction = reductionPercent;
            yield return new WaitForSeconds(duration);
            _isThrottled = false;
            _throttleReduction = 0f;
        }

        /// <summary>Current speed multiplier accounting for throttle effects (1.0 = full speed).</summary>
        public float GetSpeedMultiplier()
            => _isThrottled ? (1f - _throttleReduction) : 1f;

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private void ExpireEffect(string powerUpType)
        {
            _activeEffectType = null;
            _activeEffectCoroutine = null;
            OnPowerUpExpired?.Invoke(powerUpType);
        }

        private void RestoreKartStats()
        {
            // Reset any in-progress throttle or modifier effects
            _isThrottled = false;
            _throttleReduction = 0f;
        }

        private bool IsOnCooldown(string powerUpType)
        {
            return _cooldownEnds.TryGetValue(powerUpType, out float end) && Time.time < end;
        }

        private float GetDefaultCooldown(string powerUpType)
        {
            return powerUpType switch
            {
                "AutoScaling" => 30f,
                "ShieldAdvanced" => 45f,
                "ElasticIP" => 40f,
                "CloudWatchAlarm" => 35f,
                "CostOptimizer" => 50f,
                "Glacier" => 25f,
                _ => 30f
            };
        }

        private static string GetRandomPowerUpType()
        {
            string[] types =
            {
                "AutoScaling", "ShieldAdvanced", "ElasticIP",
                "CloudWatchAlarm", "CostOptimizer", "Glacier"
            };
            return types[UnityEngine.Random.Range(0, types.Length)];
        }

        private GameObject FindNearestOpponentKart()
        {
            GameObject[] karts = GameObject.FindGameObjectsWithTag("Kart");
            GameObject nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var kart in karts)
            {
                if (kart == gameObject) continue;
                float dist = Vector3.Distance(transform.position, kart.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = kart;
                }
            }
            return nearest;
        }

        // -------------------------------------------------------------------------
        // Public state accessors
        // -------------------------------------------------------------------------

        /// <summary>Whether the player currently holds a power-up ready to fire.</summary>
        public bool HasPowerUp => _heldPowerUpType != null;

        /// <summary>The type of the currently held power-up, or null.</summary>
        public string HeldPowerUpType => _heldPowerUpType;

        /// <summary>Whether a timed effect is currently active on this kart.</summary>
        public bool HasActiveEffect => _activeEffectType != null;

        public string ActiveEffectType => _activeEffectType;
    }

    /// <summary>
    /// Marker component placed on power-up pickup objects in the scene.
    /// Set PowerUpType in the Inspector to one of the valid types.
    /// </summary>
    public class PowerUpPickupMarker : MonoBehaviour
    {
        [Tooltip("Must match a key in the backend POWERUP_DEFINITIONS. " +
                 "Valid: AutoScaling, ShieldAdvanced, ElasticIP, CloudWatchAlarm, CostOptimizer, Glacier")]
        public string PowerUpType = "AutoScaling";
    }
}
