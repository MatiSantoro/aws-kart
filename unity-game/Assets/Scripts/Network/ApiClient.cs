using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AwsKart.Network
{
    // -------------------------------------------------------------------------
    // Data transfer objects
    // -------------------------------------------------------------------------

    [Serializable]
    public struct RaceEvent
    {
        public string event_type;   // "overtake" | "lap_complete" | "powerup_used" | "crash" | "finish" | "race_start" | "new_lap_record"
        public string player;       // Character name, e.g. "Lambda"
        public string target;       // Optional: overtaken player
        public int lap;
        public int position;
        public string track;        // e.g. "us-east-1"
        public string powerup;      // Optional: powerup type name
        public long race_time_ms;   // Optional: for finish events
    }

    [Serializable]
    public struct LeaderboardEntry
    {
        public string entry_id;
        public string player_id;
        public string player_name;
        public string character;
        public string track_id;
        public long time_ms;
        public string[] powerups_used;
        public long timestamp;
        public int rank;

        // Formatted time as MM:SS.mmm
        public string FormattedTime
        {
            get
            {
                long totalMs = time_ms;
                int minutes = (int)(totalMs / 60000);
                int seconds = (int)((totalMs % 60000) / 1000);
                int ms = (int)(totalMs % 1000);
                return $"{minutes:D2}:{seconds:D2}.{ms:D3}";
            }
        }
    }

    [Serializable]
    public struct PowerUpResult
    {
        public string activation_id;
        public string powerup_type;
        public string name;
        public string description;
        public PowerUpEffect effect;
        public float duration_seconds;
        public float cooldown_seconds;
        public string flavor_text;
        public bool used_fallback;
        public long timestamp_ms;
    }

    [Serializable]
    public struct PowerUpEffect
    {
        public string type;             // "clone", "invincibility", "teleport", "stun_all_opponents", "throttle_opponents", "freeze_nearest"
        public float clone_duration;
        public int clone_count;
        public float invincibility_duration;
        public string teleport_to;
        public float stun_duration;
        public int speed_reduction_percent;
        public float throttle_duration;
        public float freeze_duration;
        public int target_count;
    }

    // Internal wrapper types for JSON deserialization
    [Serializable]
    internal class CommentaryResponse
    {
        public string commentary;
        public string event_type;
        public string player;
        public bool used_fallback;
    }

    [Serializable]
    internal class LeaderboardResponse
    {
        public string track;
        public string scope;
        public int count;
        public LeaderboardEntry[] entries;
    }

    [Serializable]
    internal class SubmitLeaderboardResponse
    {
        public string entryId;
        public string playerId;
        public string message;
    }

    // -------------------------------------------------------------------------
    // ApiClient MonoBehaviour
    // -------------------------------------------------------------------------

    /// <summary>
    /// Handles all HTTP communication between the Unity game client and the
    /// AWS API Gateway backend. All methods are coroutine-based using callbacks
    /// for maximum Unity compatibility (no UniTask dependency required).
    ///
    /// Usage:
    ///   ApiClient.Instance.PostCommentaryEvent(myEvent, (commentary) => Debug.Log(commentary));
    /// </summary>
    public class ApiClient : MonoBehaviour
    {
        // Replace this with the CDK stack output "ApiUrl" after deploying.
        // Example: "https://abc123.execute-api.us-east-1.amazonaws.com/prod/"
        public const string API_BASE_URL = "https://YOUR_API_GATEWAY_URL/prod/";

        private const float REQUEST_TIMEOUT_SECONDS = 15f;
        private const int MAX_RETRIES = 2;

        // ---- Singleton ----
        private static ApiClient _instance;
        public static ApiClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ApiClient");
                    _instance = go.AddComponent<ApiClient>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // -------------------------------------------------------------------------
        // Public API methods
        // -------------------------------------------------------------------------

        /// <summary>
        /// Send a race event and receive AI-generated commentary.
        /// Callback receives commentary string, or null on failure.
        /// </summary>
        public void PostCommentaryEvent(RaceEvent evt, Action<string> onComplete)
        {
            string json = JsonUtility.ToJson(evt);
            StartCoroutine(PostWithRetry(
                url: API_BASE_URL + "commentary",
                jsonBody: json,
                onSuccess: (responseText) =>
                {
                    try
                    {
                        var response = JsonUtility.FromJson<CommentaryResponse>(responseText);
                        onComplete?.Invoke(response.commentary);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ApiClient] Failed to parse commentary response: {ex.Message}");
                        onComplete?.Invoke(null);
                    }
                },
                onFailure: () => onComplete?.Invoke(null)
            ));
        }

        /// <summary>
        /// Submit a race result to the leaderboard.
        /// Callback receives true on success, false on failure.
        /// </summary>
        public void PostLeaderboardEntry(
            string playerName,
            string character,
            string track,
            long timeMs,
            string playerId = null,
            string[] powerupsUsed = null,
            Action<bool> onComplete = null)
        {
            var payload = new
            {
                player_name = playerName,
                character = character,
                track = track,
                time_ms = timeMs,
                player_id = playerId ?? SystemInfo.deviceUniqueIdentifier,
                powerups_used = powerupsUsed ?? Array.Empty<string>()
            };

            // Use anonymous class serialization via a helper struct
            string json = BuildLeaderboardPostJson(playerName, character, track, timeMs,
                playerId ?? SystemInfo.deviceUniqueIdentifier, powerupsUsed ?? Array.Empty<string>());

            StartCoroutine(PostWithRetry(
                url: API_BASE_URL + "leaderboard",
                jsonBody: json,
                onSuccess: (_) => onComplete?.Invoke(true),
                onFailure: () => onComplete?.Invoke(false)
            ));
        }

        private string BuildLeaderboardPostJson(
            string playerName, string character, string track,
            long timeMs, string playerId, string[] powerupsUsed)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"player_name\":\"{EscapeJson(playerName)}\",");
            sb.Append($"\"character\":\"{EscapeJson(character)}\",");
            sb.Append($"\"track\":\"{EscapeJson(track)}\",");
            sb.Append($"\"time_ms\":{timeMs},");
            sb.Append($"\"player_id\":\"{EscapeJson(playerId)}\",");
            sb.Append("\"powerups_used\":[");
            for (int i = 0; i < powerupsUsed.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(powerupsUsed[i])}\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Fetch the top times for a specific track.
        /// Callback receives array of LeaderboardEntry, or empty array on failure.
        /// </summary>
        public void GetLeaderboard(string track, int limit, Action<LeaderboardEntry[]> onComplete)
        {
            string url = $"{API_BASE_URL}leaderboard?track={Uri.EscapeUriString(track)}&limit={limit}";
            StartCoroutine(GetRequest(
                url: url,
                onSuccess: (responseText) =>
                {
                    try
                    {
                        var response = JsonUtility.FromJson<LeaderboardResponse>(responseText);
                        onComplete?.Invoke(response.entries ?? Array.Empty<LeaderboardEntry>());
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ApiClient] Failed to parse leaderboard response: {ex.Message}");
                        onComplete?.Invoke(Array.Empty<LeaderboardEntry>());
                    }
                },
                onFailure: () => onComplete?.Invoke(Array.Empty<LeaderboardEntry>())
            ));
        }

        /// <summary>
        /// Fetch the global top times across all tracks.
        /// </summary>
        public void GetGlobalLeaderboard(int limit, Action<LeaderboardEntry[]> onComplete)
        {
            string url = $"{API_BASE_URL}leaderboard/global?limit={limit}";
            StartCoroutine(GetRequest(
                url: url,
                onSuccess: (responseText) =>
                {
                    try
                    {
                        var response = JsonUtility.FromJson<LeaderboardResponse>(responseText);
                        onComplete?.Invoke(response.entries ?? Array.Empty<LeaderboardEntry>());
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ApiClient] Failed to parse global leaderboard: {ex.Message}");
                        onComplete?.Invoke(Array.Empty<LeaderboardEntry>());
                    }
                },
                onFailure: () => onComplete?.Invoke(Array.Empty<LeaderboardEntry>())
            ));
        }

        /// <summary>
        /// Activate a power-up. Sends activation to backend for logging and
        /// optional Bedrock flavor text generation.
        /// Callback receives PowerUpResult with effect parameters and flavor text.
        /// </summary>
        public void ActivatePowerUp(
            string playerId,
            string character,
            string powerUpType,
            string track,
            int lap,
            Action<PowerUpResult> onComplete)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"player_id\":\"{EscapeJson(playerId)}\",");
            sb.Append($"\"character\":\"{EscapeJson(character)}\",");
            sb.Append($"\"powerup_type\":\"{EscapeJson(powerUpType)}\",");
            sb.Append($"\"track\":\"{EscapeJson(track)}\",");
            sb.Append($"\"lap\":{lap},");
            sb.Append("\"generate_flavor\":true");
            sb.Append("}");

            StartCoroutine(PostWithRetry(
                url: API_BASE_URL + "powerups/activate",
                jsonBody: sb.ToString(),
                onSuccess: (responseText) =>
                {
                    try
                    {
                        var result = JsonUtility.FromJson<PowerUpResult>(responseText);
                        onComplete?.Invoke(result);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ApiClient] Failed to parse power-up response: {ex.Message}");
                        onComplete?.Invoke(default);
                    }
                },
                onFailure: () => onComplete?.Invoke(default)
            ));
        }

        // -------------------------------------------------------------------------
        // Private HTTP coroutines
        // -------------------------------------------------------------------------

        private IEnumerator PostWithRetry(
            string url,
            string jsonBody,
            Action<string> onSuccess,
            Action onFailure,
            int retryCount = 0)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = (int)REQUEST_TIMEOUT_SECONDS;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke(request.downloadHandler.text);
            }
            else if (retryCount < MAX_RETRIES &&
                     request.result == UnityWebRequest.Result.ConnectionError)
            {
                Debug.LogWarning($"[ApiClient] POST {url} failed (attempt {retryCount + 1}), retrying...");
                yield return new WaitForSeconds(1f * (retryCount + 1));
                yield return StartCoroutine(PostWithRetry(url, jsonBody, onSuccess, onFailure, retryCount + 1));
            }
            else
            {
                Debug.LogError($"[ApiClient] POST {url} failed: {request.error} (HTTP {request.responseCode})");
                onFailure?.Invoke();
            }
        }

        private IEnumerator GetRequest(
            string url,
            Action<string> onSuccess,
            Action onFailure)
        {
            using var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = (int)REQUEST_TIMEOUT_SECONDS;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke(request.downloadHandler.text);
            }
            else
            {
                Debug.LogError($"[ApiClient] GET {url} failed: {request.error} (HTTP {request.responseCode})");
                onFailure?.Invoke();
            }
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
