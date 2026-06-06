using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AwsKart.UI
{
    /// <summary>
    /// VR Commentary display — a world-space canvas positioned above the track.
    /// Shows AI-generated commentary text with a typewriter effect, then auto-hides.
    /// Multiple commentaries are queued and played sequentially.
    ///
    /// Attach to a GameObject with a Canvas (World Space) and a TextMeshProUGUI.
    /// The canvas should face the player camera (use a LookAtCamera component or
    /// set up a Billboard shader).
    ///
    /// Usage:
    ///   CommentaryUI.Instance.ShowCommentary("Lambda just overtook EC2 in milliseconds!");
    /// </summary>
    public class CommentaryUI : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Singleton
        // -------------------------------------------------------------------------

        public static CommentaryUI Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        // -------------------------------------------------------------------------
        // Inspector configuration
        // -------------------------------------------------------------------------

        [Header("UI References")]
        [Tooltip("TextMeshPro component that displays the commentary text.")]
        [SerializeField] private TextMeshProUGUI commentaryText;

        [Tooltip("Optional background panel that fades in/out with the commentary.")]
        [SerializeField] private CanvasGroup backgroundPanel;

        [Tooltip("Optional speaker name label (e.g. 'Bedrock Commentator').")]
        [SerializeField] private TextMeshProUGUI speakerLabel;

        [Header("Typewriter Settings")]
        [Tooltip("Seconds between each character reveal in the typewriter effect.")]
        [SerializeField] private float charRevealInterval = 0.03f;

        [Tooltip("Seconds the commentary stays fully visible before fading out.")]
        [SerializeField] private float displayDuration = 4f;

        [Tooltip("Seconds for the fade-in and fade-out transitions.")]
        [SerializeField] private float fadeDuration = 0.4f;

        [Header("World-Space Positioning")]
        [Tooltip("If true, the canvas billboard rotates to face the player camera each frame.")]
        [SerializeField] private bool billboardToCamera = true;

        [Tooltip("Offset from the track anchor point in world space.")]
        [SerializeField] private Vector3 worldOffset = new Vector3(0f, 8f, 0f);

        [Tooltip("Optional track anchor transform. If null, uses this transform's position.")]
        [SerializeField] private Transform trackAnchor;

        [Header("Queue")]
        [Tooltip("Maximum number of commentaries to queue. Excess entries are dropped.")]
        [SerializeField] private int maxQueueSize = 5;

        [Header("Audio")]
        [SerializeField] private AudioClip commentaryAppearSfx;
        private AudioSource _audioSource;

        // -------------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------------

        private readonly Queue<string> _commentaryQueue = new();
        private bool _isDisplaying = false;
        private Coroutine _displayCoroutine = null;
        private Camera _mainCamera;

        // -------------------------------------------------------------------------
        // Lifecycle
        // -------------------------------------------------------------------------

        private void Start()
        {
            _mainCamera = Camera.main;
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();

            // Hide initially
            SetCanvasAlpha(0f);
            if (commentaryText != null) commentaryText.text = "";
            if (speakerLabel != null) speakerLabel.text = "Bedrock Commentator";

            // Position the world-space canvas
            if (trackAnchor != null)
                transform.position = trackAnchor.position + worldOffset;
        }

        private void LateUpdate()
        {
            // Billboard: rotate to face the player camera
            if (billboardToCamera && _mainCamera != null)
            {
                transform.LookAt(
                    transform.position + (_mainCamera.transform.rotation * Vector3.forward),
                    _mainCamera.transform.rotation * Vector3.up
                );
            }

            // Process queue
            if (!_isDisplaying && _commentaryQueue.Count > 0)
            {
                string next = _commentaryQueue.Dequeue();
                _displayCoroutine = StartCoroutine(DisplayCommentaryCoroutine(next));
            }
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Queue a commentary string for display. If the queue is full, the oldest
        /// waiting entry is dropped to make room.
        /// </summary>
        public void ShowCommentary(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (_commentaryQueue.Count >= maxQueueSize)
            {
                // Drop oldest queued item to prevent piling up
                _commentaryQueue.Dequeue();
                Debug.LogWarning("[CommentaryUI] Queue full — dropped oldest commentary.");
            }

            _commentaryQueue.Enqueue(text);
        }

        /// <summary>Clear all queued commentaries and immediately hide the panel.</summary>
        public void ClearAll()
        {
            _commentaryQueue.Clear();
            if (_displayCoroutine != null)
            {
                StopCoroutine(_displayCoroutine);
                _displayCoroutine = null;
            }
            _isDisplaying = false;
            SetCanvasAlpha(0f);
            if (commentaryText != null) commentaryText.text = "";
        }

        // -------------------------------------------------------------------------
        // Display coroutine
        // -------------------------------------------------------------------------

        private IEnumerator DisplayCommentaryCoroutine(string text)
        {
            _isDisplaying = true;

            if (commentaryText == null)
            {
                _isDisplaying = false;
                yield break;
            }

            // Play appear SFX
            if (commentaryAppearSfx != null && _audioSource != null)
                _audioSource.PlayOneShot(commentaryAppearSfx);

            // Fade in
            yield return StartCoroutine(FadeCanvasAlpha(0f, 1f, fadeDuration));

            // Typewriter reveal
            commentaryText.text = "";
            commentaryText.maxVisibleCharacters = 0;
            commentaryText.text = text;

            int totalChars = text.Length;
            for (int i = 0; i <= totalChars; i++)
            {
                commentaryText.maxVisibleCharacters = i;
                yield return new WaitForSeconds(charRevealInterval);
            }

            // Hold at full display
            yield return new WaitForSeconds(displayDuration);

            // Fade out
            yield return StartCoroutine(FadeCanvasAlpha(1f, 0f, fadeDuration));

            commentaryText.text = "";
            _isDisplaying = false;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private IEnumerator FadeCanvasAlpha(float from, float to, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float alpha = Mathf.Lerp(from, to, elapsed / duration);
                SetCanvasAlpha(alpha);
                yield return null;
            }
            SetCanvasAlpha(to);
        }

        private void SetCanvasAlpha(float alpha)
        {
            if (backgroundPanel != null)
                backgroundPanel.alpha = alpha;

            if (commentaryText != null)
            {
                Color c = commentaryText.color;
                c.a = alpha;
                commentaryText.color = c;
            }

            if (speakerLabel != null)
            {
                Color c = speakerLabel.color;
                c.a = alpha;
                speakerLabel.color = c;
            }
        }

        // -------------------------------------------------------------------------
        // Static helper — fire and forget
        // -------------------------------------------------------------------------

        /// <summary>Convenience method: show commentary via the singleton instance.</summary>
        public static void Show(string text)
        {
            if (Instance != null)
                Instance.ShowCommentary(text);
            else
                Debug.LogWarning($"[CommentaryUI] No instance in scene. Commentary dropped: {text}");
        }
    }
}
