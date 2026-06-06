using UnityEngine;
using AwsKart.Game;

namespace AwsKart.VR
{
    /// <summary>
    /// Meta Quest VR kart controller using OVRInput.
    ///
    /// Controls:
    ///   - Left thumbstick X-axis  → steering
    ///   - Right trigger            → accelerate
    ///   - Left trigger             → brake / reverse
    ///   - B button                 → activate power-up
    ///   - Head yaw (HMD)           → look-ahead steering assist
    ///
    /// Requires:
    ///   - Rigidbody on this GameObject or parent
    ///   - Four WheelColliders assigned (frontLeft, frontRight, rearLeft, rearRight)
    ///   - PowerUpSystem component on the same kart
    ///   - OVR Camera Rig in scene (Meta XR SDK)
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class VRKartController : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Inspector configuration
        // -------------------------------------------------------------------------

        [Header("Wheel Colliders")]
        [SerializeField] private WheelCollider wheelFrontLeft;
        [SerializeField] private WheelCollider wheelFrontRight;
        [SerializeField] private WheelCollider wheelRearLeft;
        [SerializeField] private WheelCollider wheelRearRight;

        [Header("Wheel Meshes (optional visual)")]
        [SerializeField] private Transform meshFrontLeft;
        [SerializeField] private Transform meshFrontRight;
        [SerializeField] private Transform meshRearLeft;
        [SerializeField] private Transform meshRearRight;

        [Header("Character Stats")]
        [Tooltip("Assigned from the character select screen. Determines speed/handling curves.")]
        [SerializeField] private KartCharacter character;

        [Header("Physics Tuning")]
        [Tooltip("Base max motor torque (Nm) before character stat scaling.")]
        [SerializeField] private float baseMotorTorque = 2000f;

        [Tooltip("Base braking torque applied to all wheels.")]
        [SerializeField] private float brakeTorque = 4000f;

        [Tooltip("Maximum front wheel steering angle in degrees.")]
        [SerializeField] private float maxSteeringAngle = 35f;

        [Tooltip("Fraction of head yaw (0-1) mixed into steering as look-ahead assist.")]
        [SerializeField, Range(0f, 0.5f)] private float headSteeringAssistFraction = 0.25f;

        [Tooltip("Speed (m/s) above which the kart automatically applies downforce.")]
        [SerializeField] private float downforceThreshold = 15f;

        [Tooltip("Downforce multiplier at max speed.")]
        [SerializeField] private float downforceAmount = 800f;

        [Header("Anti-Rollover")]
        [SerializeField] private float antiRollStiffness = 5000f;

        [Header("Haptics")]
        [Tooltip("Haptic intensity on collision (0-1).")]
        [SerializeField, Range(0f, 1f)] private float collisionHapticIntensity = 0.8f;

        [Tooltip("Haptic intensity on power-up activation (0-1).")]
        [SerializeField, Range(0f, 1f)] private float powerupHapticIntensity = 0.5f;

        [Tooltip("Haptic duration in seconds.")]
        [SerializeField] private float hapticDuration = 0.2f;

        // -------------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------------

        private Rigidbody _rb;
        private PowerUpSystem _powerUpSystem;

        private float _currentMotorTorque;
        private float _currentSteeringAngle;
        private bool _isFrozen = false;
        private bool _wasAcceleratingLastFrame = false;

        // Head tracking
        private Transform _hmdTransform;
        private float _hmdYawAtRaceStart;
        private bool _hmdCalibrated = false;

        // Speed limiting based on character stats and throttle effects
        private float _maxSpeedMetersPerSecond = 40f;

        // -------------------------------------------------------------------------
        // Lifecycle
        // -------------------------------------------------------------------------

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _powerUpSystem = GetComponent<PowerUpSystem>();

            _rb.centerOfMass = new Vector3(0f, -0.5f, 0f); // lower CoM for stability
        }

        private void Start()
        {
            if (character != null)
            {
                _maxSpeedMetersPerSecond = character.MaxSpeedMetersPerSecond(50f);
                _currentMotorTorque = character.AccelerationMultiplier(baseMotorTorque);
            }
            else
            {
                _maxSpeedMetersPerSecond = 40f;
                _currentMotorTorque = baseMotorTorque;
            }

            // Find HMD transform from OVR Camera Rig
            var cameraRig = FindFirstObjectByType<OVRCameraRig>();
            if (cameraRig != null)
                _hmdTransform = cameraRig.centerEyeAnchor;
            else
                _hmdTransform = Camera.main != null ? Camera.main.transform : transform;

            // Subscribe to power-up events for haptics
            if (_powerUpSystem != null)
            {
                _powerUpSystem.OnPowerUpActivated.AddListener((_) => TriggerPowerUpHaptics());
            }
        }

        private void FixedUpdate()
        {
            if (_isFrozen) return;

            ReadInputAndDrive();
            ApplyDownforce();
            ApplyAntiRollover();
            UpdateWheelMeshes();
            EnforceSpeedLimit();
        }

        // -------------------------------------------------------------------------
        // Input reading (OVRInput)
        // -------------------------------------------------------------------------

        private void ReadInputAndDrive()
        {
            // --- Throttle (right trigger: 0-1) ---
            float throttle = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);

            // --- Brake (left trigger: 0-1) ---
            float brake = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);

            // --- Steering (left thumbstick X: -1 to 1) ---
            float stickX = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick).x;

            // Head-look steering assist: mix a fraction of HMD yaw delta into steering
            float headAssist = GetHeadYawSteering();
            float steering = Mathf.Clamp(stickX + headAssist * headSteeringAssistFraction, -1f, 1f);

            // --- Power-up activation (B button, right controller) ---
            if (OVRInput.GetDown(OVRInput.Button.Two) && _powerUpSystem != null)
            {
                _powerUpSystem.TryActivatePowerUp();
                TriggerPowerUpHaptics();
            }

            // --- Calibrate HMD yaw on first significant head movement ---
            if (!_hmdCalibrated && Mathf.Abs(stickX) < 0.1f && Mathf.Abs(throttle) > 0.1f)
            {
                _hmdYawAtRaceStart = _hmdTransform.eulerAngles.y;
                _hmdCalibrated = true;
            }

            // --- Apply physics ---
            ApplyMotorTorque(throttle, brake);
            ApplyBraking(brake);
            ApplySteering(steering);
        }

        private float GetHeadYawSteering()
        {
            if (_hmdTransform == null || !_hmdCalibrated) return 0f;
            float hmdYaw = _hmdTransform.eulerAngles.y;
            float delta = Mathf.DeltaAngle(_hmdYawAtRaceStart, hmdYaw);
            // Normalise: ±maxSteeringAngle maps to ±1
            return Mathf.Clamp(delta / maxSteeringAngle, -1f, 1f);
        }

        private void ApplyMotorTorque(float throttle, float brake)
        {
            float speedMultiplier = _powerUpSystem != null
                ? _powerUpSystem.GetSpeedMultiplier()
                : 1f;

            float effectiveTorque = _currentMotorTorque * throttle * speedMultiplier;

            // Only drive rear wheels for a rear-wheel-drive kart feel
            if (wheelRearLeft != null)  wheelRearLeft.motorTorque = effectiveTorque;
            if (wheelRearRight != null) wheelRearRight.motorTorque = effectiveTorque;
        }

        private void ApplyBraking(float brake)
        {
            float brakeForce = brakeTorque * brake;
            if (wheelFrontLeft != null)  wheelFrontLeft.brakeTorque = brakeForce;
            if (wheelFrontRight != null) wheelFrontRight.brakeTorque = brakeForce;
            if (wheelRearLeft != null)   wheelRearLeft.brakeTorque = brakeForce * 0.6f;
            if (wheelRearRight != null)  wheelRearRight.brakeTorque = brakeForce * 0.6f;
        }

        private void ApplySteering(float steerInput)
        {
            // Apply Ackermann-approximate steering: inside wheel steers more sharply
            float targetAngle = steerInput * maxSteeringAngle;

            float characterHandling = character != null ? character.ClampedHandling : 0.5f;
            // Smoother at lower handling, more responsive at higher handling
            float lerpSpeed = Mathf.Lerp(2f, 8f, characterHandling);

            _currentSteeringAngle = Mathf.Lerp(
                _currentSteeringAngle, targetAngle, Time.fixedDeltaTime * lerpSpeed);

            if (wheelFrontLeft != null)  wheelFrontLeft.steerAngle = _currentSteeringAngle;
            if (wheelFrontRight != null) wheelFrontRight.steerAngle = _currentSteeringAngle;
        }

        private void ApplyDownforce()
        {
            float speed = _rb.linearVelocity.magnitude;
            if (speed > downforceThreshold)
            {
                float excess = speed - downforceThreshold;
                _rb.AddForce(-transform.up * downforceAmount * excess * Time.fixedDeltaTime,
                             ForceMode.Force);
            }
        }

        private void ApplyAntiRollover()
        {
            ApplyAntiRollBar(wheelFrontLeft, wheelFrontRight);
            ApplyAntiRollBar(wheelRearLeft, wheelRearRight);
        }

        private void ApplyAntiRollBar(WheelCollider left, WheelCollider right)
        {
            if (left == null || right == null) return;

            left.GetGroundHit(out WheelHit hitL);
            right.GetGroundHit(out WheelHit hitR);

            bool groundedL = left.isGrounded;
            bool groundedR = right.isGrounded;

            float travelL = groundedL
                ? (-left.transform.InverseTransformPoint(hitL.point).y - left.radius) / left.suspensionDistance
                : 1f;
            float travelR = groundedR
                ? (-right.transform.InverseTransformPoint(hitR.point).y - right.radius) / right.suspensionDistance
                : 1f;

            float antiRollForce = (travelL - travelR) * antiRollStiffness;

            if (groundedL)
                _rb.AddForceAtPosition(left.transform.up * -antiRollForce, left.transform.position);
            if (groundedR)
                _rb.AddForceAtPosition(right.transform.up * antiRollForce, right.transform.position);
        }

        private void EnforceSpeedLimit()
        {
            float speedMultiplier = _powerUpSystem != null
                ? _powerUpSystem.GetSpeedMultiplier()
                : 1f;
            float limit = _maxSpeedMetersPerSecond * speedMultiplier;

            Vector3 flatVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            if (flatVelocity.magnitude > limit)
            {
                Vector3 clamped = flatVelocity.normalized * limit;
                _rb.linearVelocity = new Vector3(clamped.x, _rb.linearVelocity.y, clamped.z);
            }
        }

        // -------------------------------------------------------------------------
        // Wheel mesh sync
        // -------------------------------------------------------------------------

        private void UpdateWheelMeshes()
        {
            UpdateWheelMesh(wheelFrontLeft,  meshFrontLeft);
            UpdateWheelMesh(wheelFrontRight, meshFrontRight);
            UpdateWheelMesh(wheelRearLeft,   meshRearLeft);
            UpdateWheelMesh(wheelRearRight,  meshRearRight);
        }

        private static void UpdateWheelMesh(WheelCollider collider, Transform mesh)
        {
            if (collider == null || mesh == null) return;
            collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
            mesh.SetPositionAndRotation(pos, rot);
        }

        // -------------------------------------------------------------------------
        // Collision handling
        // -------------------------------------------------------------------------

        private void OnCollisionEnter(Collision collision)
        {
            float impactMagnitude = collision.relativeVelocity.magnitude;
            if (impactMagnitude < 3f) return; // ignore minor bumps

            // Haptic feedback on collision
            float hapticStrength = Mathf.Clamp01(impactMagnitude / 20f) * collisionHapticIntensity;
            OVRInput.SetControllerVibration(hapticStrength, hapticStrength, OVRInput.Controller.RTouch);
            OVRInput.SetControllerVibration(hapticStrength, hapticStrength, OVRInput.Controller.LTouch);

            // Cancel haptics after duration
            Invoke(nameof(StopHaptics), hapticDuration);

            // Notify race manager for commentary
            if (impactMagnitude > 6f && RaceManager.Instance != null)
            {
                RaceManager.Instance.NotifyPlayerCrash();
            }
        }

        private void StopHaptics()
        {
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        }

        private void TriggerPowerUpHaptics()
        {
            OVRInput.SetControllerVibration(
                powerupHapticIntensity, powerupHapticIntensity, OVRInput.Controller.RTouch);
            OVRInput.SetControllerVibration(
                powerupHapticIntensity, powerupHapticIntensity, OVRInput.Controller.LTouch);
            Invoke(nameof(StopHaptics), hapticDuration * 2f);
        }

        // -------------------------------------------------------------------------
        // Public control methods
        // -------------------------------------------------------------------------

        /// <summary>Freeze or unfreeze the kart (used by stun / Glacier effects).</summary>
        public void SetFrozen(bool frozen)
        {
            _isFrozen = frozen;
            if (frozen)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;

                // Stop all wheels
                foreach (var wheel in new[] { wheelFrontLeft, wheelFrontRight, wheelRearLeft, wheelRearRight })
                {
                    if (wheel == null) continue;
                    wheel.motorTorque = 0f;
                    wheel.brakeTorque = brakeTorque * 5f;
                }
            }
            else
            {
                foreach (var wheel in new[] { wheelFrontLeft, wheelFrontRight, wheelRearLeft, wheelRearRight })
                {
                    if (wheel == null) continue;
                    wheel.brakeTorque = 0f;
                }
            }
        }

        /// <summary>Assign a character at runtime (from character select screen).</summary>
        public void SetCharacter(KartCharacter kartCharacter)
        {
            character = kartCharacter;
            if (character != null)
            {
                _maxSpeedMetersPerSecond = character.MaxSpeedMetersPerSecond(50f);
                _currentMotorTorque = character.AccelerationMultiplier(baseMotorTorque);
            }
        }

        /// <summary>Current speed in m/s.</summary>
        public float CurrentSpeedMetersPerSecond => _rb.linearVelocity.magnitude;

        /// <summary>Current speed in km/h for HUD display.</summary>
        public float CurrentSpeedKmh => CurrentSpeedMetersPerSecond * 3.6f;

        public bool IsFrozen => _isFrozen;
    }
}
