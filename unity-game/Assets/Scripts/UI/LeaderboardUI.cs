using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using AwsKart.Network;
using AwsKart.Game;

namespace AwsKart.UI
{
    /// <summary>
    /// End-of-race leaderboard screen. Fetches and displays the top 10 times
    /// for the current track. Each row shows character icon, rank, player name,
    /// character name, and race time formatted as MM:SS.mmm.
    ///
    /// Attach to the leaderboard canvas GameObject (World Space or Screen Space Overlay).
    /// Subscribe to RaceManager.OnRaceFinished to auto-show this panel.
    ///
    /// Requirements:
    ///   - entryRowPrefab: a prefab with LeaderboardRowUI component
    ///   - entryContainer: parent transform where rows are instantiated
    ///   - ApiClient singleton in scene
    /// </summary>
    public class LeaderboardUI : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Inspector configuration
        // -------------------------------------------------------------------------

        [Header("References")]
        [Tooltip("Prefab for each leaderboard row. Must have a LeaderboardRowUI component.")]
        [SerializeField] private LeaderboardRowUI entryRowPrefab;

        [Tooltip("Vertical layout group that parents the row instances.")]
        [SerializeField] private Transform entryContainer;

        [Tooltip("Loading spinner or text shown while fetching from the API.")]
        [SerializeField] private GameObject loadingIndicator;

        [Tooltip("Shown when the API returns no results or fails.")]
        [SerializeField] private TextMeshProUGUI emptyStateText;

        [Tooltip("Track name label at the top of the screen.")]
        [SerializeField] private TextMeshProUGUI trackNameLabel;

        [Tooltip("Player's own result displayed separately at the top.")]
        [SerializeField] private LeaderboardRowUI playerResultRow;

        [Header("Settings")]
        [Tooltip("Number of top entries to fetch.")]
        [SerializeField] private int fetchLimit = 10;

        [Tooltip("Seconds to wait after race finish before fetching/showing the board.")]
        [SerializeField] private float showDelay = 2f;

        [Tooltip("If true, shows the global leaderboard tab in addition to the track tab.")]
        [SerializeField] private bool showGlobalTab = true;

        [Header("Tabs (optional)")]
        [SerializeField] private Button trackTabButton;
        [SerializeField] private Button globalTabButton;

        [Header("Navigation")]
        [Tooltip("Button to return to the main menu or character select.")]
        [SerializeField] private Button continueButton;

        // -------------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------------

        private string _currentTrackId = "us-east-1";
        private RaceManager.RaceResult _playerResult;
        private bool _playerResultSet = false;
        private readonly List<LeaderboardRowUI> _activeRows = new();
        private bool _showingGlobal = false;

        // -------------------------------------------------------------------------
        // Lifecycle
        // -------------------------------------------------------------------------

        private void Start()
        {
            gameObject.SetActive(false);

            if (continueButton != null)
                continueButton.onClick.AddListener(OnContinuePressed);

            if (trackTabButton != null)
                trackTabButton.onClick.AddListener(OnTrackTabPressed);

            if (globalTabButton != null)
                globalTabButton.onClick.AddListener(OnGlobalTabPressed);

            // Subscribe to race finish
            if (RaceManager.Instance != null)
            {
                _currentTrackId = RaceManager.Instance.TrackId;
                RaceManager.Instance.OnRaceFinished.AddListener(OnRaceFinished);
            }
        }

        // -------------------------------------------------------------------------
        // Race finished handler
        // -------------------------------------------------------------------------

        private void OnRaceFinished(RaceManager.RaceResult result)
        {
            _playerResult = result;
            _playerResultSet = true;
            _currentTrackId = result.trackId;

            StartCoroutine(ShowAfterDelay(showDelay));
        }

        private IEnumerator ShowAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            gameObject.SetActive(true);
            yield return StartCoroutine(FetchAndDisplayLeaderboard());
        }

        // -------------------------------------------------------------------------
        // Fetch and display
        // -------------------------------------------------------------------------

        private IEnumerator FetchAndDisplayLeaderboard()
        {
            SetLoadingState(true);

            // Update track label
            if (trackNameLabel != null)
                trackNameLabel.text = _showingGlobal
                    ? "Global Rankings"
                    : $"Track: {_currentTrackId}";

            // Display player's own result immediately (local data)
            if (_playerResultSet && playerResultRow != null)
            {
                var playerEntry = new LeaderboardEntry
                {
                    player_name = _playerResult.playerName,
                    character = _playerResult.characterName,
                    track_id = _playerResult.trackId,
                    time_ms = _playerResult.totalTimeMs,
                    rank = _playerResult.finalPosition,
                };
                playerResultRow.Populate(playerEntry, isPlayer: true);
                playerResultRow.gameObject.SetActive(true);
            }

            // Fetch from backend
            LeaderboardEntry[] entries = null;
            bool received = false;

            if (_showingGlobal)
            {
                ApiClient.Instance.GetGlobalLeaderboard(fetchLimit, (result) =>
                {
                    entries = result;
                    received = true;
                });
            }
            else
            {
                ApiClient.Instance.GetLeaderboard(_currentTrackId, fetchLimit, (result) =>
                {
                    entries = result;
                    received = true;
                });
            }

            // Wait for response
            float timeout = 10f;
            float elapsed = 0f;
            while (!received && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            SetLoadingState(false);

            if (!received || entries == null || entries.Length == 0)
            {
                if (emptyStateText != null)
                {
                    emptyStateText.text = received
                        ? "No times recorded yet. Be the first!"
                        : "Could not load leaderboard. Check your connection.";
                    emptyStateText.gameObject.SetActive(true);
                }
                yield break;
            }

            if (emptyStateText != null) emptyStateText.gameObject.SetActive(false);

            PopulateRows(entries);
        }

        private void PopulateRows(LeaderboardEntry[] entries)
        {
            ClearRows();

            string localPlayerId = SystemInfo.deviceUniqueIdentifier;

            for (int i = 0; i < entries.Length; i++)
            {
                if (entryRowPrefab == null || entryContainer == null) break;

                var row = Instantiate(entryRowPrefab, entryContainer);
                bool isLocalPlayer = entries[i].player_id == localPlayerId;
                row.Populate(entries[i], isPlayer: isLocalPlayer);
                _activeRows.Add(row);
            }
        }

        private void ClearRows()
        {
            foreach (var row in _activeRows)
            {
                if (row != null) Destroy(row.gameObject);
            }
            _activeRows.Clear();
        }

        // -------------------------------------------------------------------------
        // Tab switching
        // -------------------------------------------------------------------------

        private void OnTrackTabPressed()
        {
            if (_showingGlobal)
            {
                _showingGlobal = false;
                StartCoroutine(FetchAndDisplayLeaderboard());
            }
        }

        private void OnGlobalTabPressed()
        {
            if (!_showingGlobal)
            {
                _showingGlobal = true;
                StartCoroutine(FetchAndDisplayLeaderboard());
            }
        }

        // -------------------------------------------------------------------------
        // Navigation
        // -------------------------------------------------------------------------

        private void OnContinuePressed()
        {
            // Load the main menu or character select scene
            // Replace "MainMenu" with your scene name
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }

        // -------------------------------------------------------------------------
        // Loading state
        // -------------------------------------------------------------------------

        private void SetLoadingState(bool loading)
        {
            if (loadingIndicator != null) loadingIndicator.SetActive(loading);
            if (entryContainer != null)  entryContainer.gameObject.SetActive(!loading);
        }
    }

    // -------------------------------------------------------------------------
    // Row component
    // -------------------------------------------------------------------------

    /// <summary>
    /// Component on each leaderboard row prefab.
    /// Receives a LeaderboardEntry and populates the row's UI elements.
    /// </summary>
    public class LeaderboardRowUI : MonoBehaviour
    {
        [Header("Row UI Elements")]
        [SerializeField] private TextMeshProUGUI rankText;
        [SerializeField] private TextMeshProUGUI playerNameText;
        [SerializeField] private TextMeshProUGUI characterNameText;
        [SerializeField] private TextMeshProUGUI timeText;
        [SerializeField] private Image characterIcon;
        [SerializeField] private Image rowBackground;

        [Header("Colours")]
        [SerializeField] private Color playerRowColor = new Color(1f, 0.85f, 0.2f, 0.35f);
        [SerializeField] private Color defaultRowColor = new Color(1f, 1f, 1f, 0.1f);
        [SerializeField] private Color firstPlaceColor = new Color(1f, 0.84f, 0f, 0.5f);
        [SerializeField] private Color secondPlaceColor = new Color(0.75f, 0.75f, 0.75f, 0.5f);
        [SerializeField] private Color thirdPlaceColor = new Color(0.8f, 0.50f, 0.2f, 0.5f);

        /// <summary>
        /// Populate this row with data from a LeaderboardEntry.
        /// </summary>
        public void Populate(LeaderboardEntry entry, bool isPlayer = false)
        {
            if (rankText != null)
                rankText.text = entry.rank > 0 ? $"#{entry.rank}" : "-";

            if (playerNameText != null)
                playerNameText.text = string.IsNullOrEmpty(entry.player_name)
                    ? "Anonymous"
                    : entry.player_name;

            if (characterNameText != null)
                characterNameText.text = string.IsNullOrEmpty(entry.character)
                    ? "Unknown"
                    : entry.character;

            if (timeText != null)
                timeText.text = entry.FormattedTime;

            // Tint background based on rank or player status
            if (rowBackground != null)
            {
                if (isPlayer)
                    rowBackground.color = playerRowColor;
                else
                    rowBackground.color = entry.rank switch
                    {
                        1 => firstPlaceColor,
                        2 => secondPlaceColor,
                        3 => thirdPlaceColor,
                        _ => defaultRowColor
                    };
            }

            // Try to load the character icon from a Resources folder
            // Assets/Resources/CharacterIcons/{characterName}.png
            if (characterIcon != null && !string.IsNullOrEmpty(entry.character))
            {
                var sprite = Resources.Load<Sprite>($"CharacterIcons/{entry.character}");
                if (sprite != null)
                    characterIcon.sprite = sprite;
                else
                    characterIcon.gameObject.SetActive(false);
            }
        }
    }
}
