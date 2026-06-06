using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using AwsKart.Network;

namespace AwsKart.Game
{
    /// <summary>
    /// Singleton that orchestrates the entire race lifecycle:
    ///   - Lap counting and checkpoint validation per kart
    ///   - Position ranking (sorted by total checkpoints + laps)
    ///   - Fires race events to ApiClient for Bedrock commentary
    ///   - Submits final result to leaderboard on finish
    ///   - Provides utility methods (StunAllOpponents, ThrottleOpponents, GetNearestCheckpoint)
    /// </summary>
    public class RaceManager : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Singleton
        // -------------------------------------------------------------------------

        public static RaceManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // -------------------------------------------------------------------------
        // Inspector configuration
        // -------------------------------------------------------------------------

        [Header("Race Configuration")]
        [Tooltip("Number of laps to complete the race.")]
        [SerializeField] private int totalLaps = 3;

        [Tooltip("Track identifier sent to the backend (e.g. 'us-east-1').")]
        [SerializeField] private string trackId = "us-east-1";

        [Tooltip("All checkpoints on the track in order. The last one is the finish line.")]
        [SerializeField] private Transform[] checkpoints;

        [Tooltip("The player's kart GameObject (has KartCharacter, PowerUpSystem).")]
        [SerializeField] private GameObject playerKart;

        [Tooltip("The player's chosen character ScriptableObject.")]
        [SerializeField] private KartCharacter playerCharacter;

        [Tooltip("Player display name for the leaderboard.")]
        [SerializeField] private string playerName = "Racer";

        [Header("Commentary")]
        [Tooltip("Minimum seconds between commentary requests to avoid spamming Bedrock.")]
        [SerializeField] private float commentaryCooldown = 5f;

        [Header("Events")]
        public UnityEvent OnRaceStart;
        public UnityEvent<int> OnLapCompleted;          // passes lap number
        public UnityEvent<RaceResult> OnRaceFinished;
        public UnityEvent<string> OnCommentaryReceived; // passes commentary text

        // -------------------------------------------------------------------------
        // Race state
        // -------------------------------------------------------------------------

        [Serializable]
        public struct RacerState
        {
            public GameObject kartObject;
            public string characterName;
            public int currentLap;
            public int nextCheckpointIndex;
            public int totalCheckpointsPassed;
            public float lastCheckpointTime;
            public bool hasFinished;
            public long finishTimeMs;
        }

        [Serializable]
        public struct RaceResult
        {
            public string playerName;
            public string characterName;
            public string trackId;
            public long totalTimeMs;
            public int finalPosition;
            public string[] powerupsUsed;
        }

        private List<RacerState> _racers = new();
        private int _playerRacerIndex = -1;
        private bool _raceStarted = false;
        private bool _raceFinished = false;
        private float _raceStartTime;
        private float _lastCommentaryTime = -999f;
        private List<string> _playerPowerupsUsed = new();

        // -------------------------------------------------------------------------
        // Public accessors
        // -------------------------------------------------------------------------

        public string TrackId => trackId;
        public bool RaceFinished => _raceFinished;

        public int CurrentLap
        {
            get
            {
                if (_playerRacerIndex < 0 || _playerRacerIndex >= _racers.Count) return 1;
                return _racers[_playerRacerIndex].currentLap;
            }
        }

        public int TotalLaps => totalLaps;

        /// <summary>Returns the current position of each kart (1-indexed), sorted by race progress.</summary>
        public List<(string characterName, int position)> CurrentPositions
        {
            get
            {
                var sorted = _racers
                    .OrderByDescending(r => r.totalCheckpointsPassed)
                    .ThenByDescending(r => r.currentLap)
                    .ToList();

                var result = new List<(string, int)>();
                for (int i = 0; i < sorted.Count; i++)
                    result.Add((sorted[i].characterName, i + 1));
                return result;
            }
        }

        public int PlayerCurrentPosition
        {
            get
            {
                var pos = CurrentPositions;
                if (_playerRacerIndex < 0 || _playerRacerIndex >= _racers.Count) return 1;
                string playerChar = _racers[_playerRacerIndex].characterName;
                var entry = pos.FirstOrDefault(p => p.characterName == playerChar);
                return entry.position > 0 ? entry.position : 1;
            }
        }

        // -------------------------------------------------------------------------
        // Race start
        // -------------------------------------------------------------------------

        /// <summary>
        /// Call this to register all karts and begin the race.
        /// </summary>
        public void StartRace(List<(GameObject kart, string characterName)> racerData)
        {
            _racers.Clear();
            _playerRacerIndex = -1;

            for (int i = 0; i < racerData.Count; i++)
            {
                var (kart, charName) = racerData[i];
                var state = new RacerState
                {
                    kartObject = kart,
                    characterName = charName,
                    currentLap = 1,
                    nextCheckpointIndex = 0,
                    totalCheckpointsPassed = 0,
                    hasFinished = false,
                    finishTimeMs = 0,
                };
                _racers.Add(state);

                if (kart == playerKart)
                    _playerRacerIndex = i;
            }

            _raceStarted = true;
            _raceFinished = false;
            _raceStartTime = Time.time;
            _playerPowerupsUsed.Clear();

            OnRaceStart?.Invoke();
            FireCommentaryEvent(new RaceEvent
            {
                event_type = "race_start",
                player = playerCharacter != null ? playerCharacter.characterName : "Racer",
                track = trackId,
                lap = 1,
                position = 1,
            });

            Debug.Log($"[RaceManager] Race started on {trackId} with {_racers.Count} racers.");
        }

        // -------------------------------------------------------------------------
        // Checkpoint validation — called by checkpoint trigger components
        // -------------------------------------------------------------------------

        /// <summary>
        /// Called when a kart passes through a checkpoint trigger.
        /// </summary>
        public void OnKartReachedCheckpoint(GameObject kart, int checkpointIndex)
        {
            if (!_raceStarted || _raceFinished) return;

            int racerIndex = _racers.FindIndex(r => r.kartObject == kart);
            if (racerIndex < 0) return;

            var state = _racers[racerIndex];

            // Validate sequential checkpoint order
            if (checkpointIndex != state.nextCheckpointIndex)
            {
                // Allow skipping only if the kart is at a checkpoint significantly ahead
                // (handles minor desync or checkpoint ordering)
                if (checkpointIndex != (state.nextCheckpointIndex % checkpoints.Length))
                    return;
            }

            state.nextCheckpointIndex = (checkpointIndex + 1) % checkpoints.Length;
            state.totalCheckpointsPassed++;
            state.lastCheckpointTime = Time.time;

            bool isFinishLine = (checkpointIndex == checkpoints.Length - 1);

            if (isFinishLine)
            {
                if (state.currentLap >= totalLaps)
                {
                    // Race complete for this racer
                    state.hasFinished = true;
                    state.finishTimeMs = (long)((Time.time - _raceStartTime) * 1000f);
                    _racers[racerIndex] = state;
                    HandleRacerFinished(racerIndex);
                    return;
                }
                else
                {
                    state.currentLap++;
                    _racers[racerIndex] = state;
                    HandleLapCompleted(racerIndex);
                    return;
                }
            }

            _racers[racerIndex] = state;

            // Check for overtake
            if (racerIndex == _playerRacerIndex)
                CheckForOvertake();
        }

        private void HandleLapCompleted(int racerIndex)
        {
            var state = _racers[racerIndex];
            int lap = state.currentLap;

            Debug.Log($"[RaceManager] {state.characterName} completed lap {lap - 1}.");

            if (racerIndex == _playerRacerIndex)
            {
                OnLapCompleted?.Invoke(lap - 1);
                FireCommentaryEvent(new RaceEvent
                {
                    event_type = "lap_complete",
                    player = state.characterName,
                    lap = lap - 1,
                    position = PlayerCurrentPosition,
                    track = trackId,
                });
            }
        }

        private void HandleRacerFinished(int racerIndex)
        {
            var state = _racers[racerIndex];
            int position = GetFinishPosition(racerIndex);

            Debug.Log($"[RaceManager] {state.characterName} finished in position {position} " +
                      $"with time {state.finishTimeMs}ms.");

            if (racerIndex == _playerRacerIndex)
            {
                _raceFinished = true;

                FireCommentaryEvent(new RaceEvent
                {
                    event_type = "finish",
                    player = state.characterName,
                    lap = totalLaps,
                    position = position,
                    track = trackId,
                    race_time_ms = state.finishTimeMs,
                });

                var result = new RaceResult
                {
                    playerName = playerName,
                    characterName = state.characterName,
                    trackId = trackId,
                    totalTimeMs = state.finishTimeMs,
                    finalPosition = position,
                    powerupsUsed = _playerPowerupsUsed.ToArray(),
                };

                OnRaceFinished?.Invoke(result);
                StartCoroutine(SubmitLeaderboardEntry(result));
            }
        }

        private int GetFinishPosition(int racerIndex)
        {
            int finishedCount = _racers.Count(r => r.hasFinished);
            return finishedCount; // 1-indexed because we incremented before calling this
        }

        // -------------------------------------------------------------------------
        // Overtake detection
        // -------------------------------------------------------------------------

        private int _lastKnownPlayerPosition = 1;

        private void CheckForOvertake()
        {
            int currentPosition = PlayerCurrentPosition;
            if (currentPosition < _lastKnownPlayerPosition)
            {
                // Player moved up in positions — overtake
                var playerState = _racers[_playerRacerIndex];

                // Find who we just overtook (was at our new position before)
                var positions = CurrentPositions;
                string overtakenChar = "";
                for (int i = 0; i < positions.Count; i++)
                {
                    if (positions[i].position == currentPosition + 1)
                    {
                        overtakenChar = positions[i].characterName;
                        break;
                    }
                }

                Debug.Log($"[RaceManager] Overtake! {playerState.characterName} now in position {currentPosition}.");

                FireCommentaryEvent(new RaceEvent
                {
                    event_type = "overtake",
                    player = playerState.characterName,
                    target = overtakenChar,
                    lap = playerState.currentLap,
                    position = currentPosition,
                    track = trackId,
                });
            }
            _lastKnownPlayerPosition = currentPosition;
        }

        // -------------------------------------------------------------------------
        // Commentary
        // -------------------------------------------------------------------------

        private void FireCommentaryEvent(RaceEvent evt)
        {
            if (Time.time - _lastCommentaryTime < commentaryCooldown)
            {
                Debug.Log($"[RaceManager] Commentary throttled (cooldown). Skipping: {evt.event_type}");
                return;
            }

            _lastCommentaryTime = Time.time;

            ApiClient.Instance.PostCommentaryEvent(evt, (commentary) =>
            {
                if (!string.IsNullOrEmpty(commentary))
                {
                    Debug.Log($"[RaceManager] Commentary: {commentary}");
                    OnCommentaryReceived?.Invoke(commentary);
                }
            });
        }

        /// <summary>
        /// Called by PowerUpSystem when player activates a power-up,
        /// so RaceManager can trigger commentary and track usage.
        /// </summary>
        public void NotifyPowerUpActivated(string powerUpType, string flavorText)
        {
            _playerPowerupsUsed.Add(powerUpType);

            if (!string.IsNullOrEmpty(flavorText))
                OnCommentaryReceived?.Invoke(flavorText);
        }

        // -------------------------------------------------------------------------
        // Leaderboard submission
        // -------------------------------------------------------------------------

        private IEnumerator SubmitLeaderboardEntry(RaceResult result)
        {
            yield return new WaitForSeconds(1f); // brief delay after finish

            bool submitted = false;
            ApiClient.Instance.PostLeaderboardEntry(
                playerName: result.playerName,
                character: result.characterName,
                track: result.trackId,
                timeMs: result.totalTimeMs,
                playerId: SystemInfo.deviceUniqueIdentifier,
                powerupsUsed: result.powerupsUsed,
                onComplete: (success) =>
                {
                    submitted = success;
                    if (success)
                        Debug.Log("[RaceManager] Leaderboard entry submitted successfully.");
                    else
                        Debug.LogWarning("[RaceManager] Failed to submit leaderboard entry.");
                }
            );
        }

        // -------------------------------------------------------------------------
        // Utility methods called by power-up effects
        // -------------------------------------------------------------------------

        /// <summary>
        /// Stun all opponent karts for the given duration (CloudWatch Alarm power-up).
        /// </summary>
        public void StunAllOpponents(PowerUpSystem sourceKart, float duration)
        {
            var allPowerUpSystems = FindObjectsByType<PowerUpSystem>(FindObjectsSortMode.None);
            foreach (var pus in allPowerUpSystems)
            {
                if (pus == sourceKart) continue;
                var controller = pus.GetComponent<AwsKart.VR.VRKartController>();
                if (controller == null)
                    controller = pus.GetComponentInParent<AwsKart.VR.VRKartController>();
                if (controller != null)
                    controller.StartCoroutine(StunKartCoroutine(controller, duration));
            }
        }

        private static IEnumerator StunKartCoroutine(AwsKart.VR.VRKartController controller, float duration)
        {
            controller.SetFrozen(true);
            yield return new WaitForSeconds(duration);
            controller.SetFrozen(false);
        }

        /// <summary>
        /// Throttle all opponent karts (Cost Optimizer power-up).
        /// </summary>
        public void ThrottleOpponents(PowerUpSystem sourceKart, float reductionPercent, float duration)
        {
            var allPowerUpSystems = FindObjectsByType<PowerUpSystem>(FindObjectsSortMode.None);
            foreach (var pus in allPowerUpSystems)
            {
                if (pus == sourceKart) continue;
                pus.ApplyThrottle(reductionPercent, duration);
            }
        }

        /// <summary>
        /// Returns the nearest checkpoint Transform to the given world position
        /// (used by the Elastic IP power-up for teleportation).
        /// </summary>
        public Transform GetNearestCheckpoint(Vector3 fromPosition)
        {
            if (checkpoints == null || checkpoints.Length == 0) return null;

            Transform nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var cp in checkpoints)
            {
                if (cp == null) continue;
                float dist = Vector3.Distance(fromPosition, cp.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = cp;
                }
            }
            return nearest;
        }

        // -------------------------------------------------------------------------
        // Update — crash detection (velocity-based)
        // -------------------------------------------------------------------------

        private float _lastCrashCommentaryTime = -999f;
        private const float CRASH_COMMENTARY_COOLDOWN = 10f;
        private const float CRASH_SPEED_THRESHOLD = 5f;

        private void Update()
        {
            if (!_raceStarted || _raceFinished) return;
            CheckPlayerCrash();
        }

        private void CheckPlayerCrash()
        {
            if (_playerRacerIndex < 0) return;
            if (Time.time - _lastCrashCommentaryTime < CRASH_COMMENTARY_COOLDOWN) return;

            // Detect crash by watching for abrupt deceleration events
            // (VRKartController sets a flag via OnCollisionEnter)
        }

        /// <summary>Called by VRKartController on significant collision.</summary>
        public void NotifyPlayerCrash()
        {
            if (Time.time - _lastCrashCommentaryTime < CRASH_COMMENTARY_COOLDOWN) return;
            _lastCrashCommentaryTime = Time.time;

            if (_playerRacerIndex < 0) return;
            var state = _racers[_playerRacerIndex];

            FireCommentaryEvent(new RaceEvent
            {
                event_type = "crash",
                player = state.characterName,
                lap = state.currentLap,
                position = PlayerCurrentPosition,
                track = trackId,
            });
        }
    }
}
