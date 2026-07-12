using System.Collections;
using UnityEngine;
using CityTwin.Config;
using CityTwin.Core;
using CityTwin.Input;
using CityTwin.Localization;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Shows a popup after X seconds of no tile activity (place/move/remove).
    /// Resets and hides on any tile event or when the session timer ends.
    /// </summary>
    public class InactivityPopupController : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private GameInstanceCoordinator coordinator;

        [Header("UI")]
        [SerializeField] private TutorialPopup popup;

        [Header("Idle session end")]
        [Tooltip("Seconds of tile inactivity after which the session ends on its own (0 = off). Longer than the popup timeout: the popup nudges first, this gives up. With tiles on the table the session ends and the end screen jumps straight to the clear-the-table message; with an empty table the game restarts directly for the next visitor.")]
        [SerializeField] private float idleEndSeconds = 0f;
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private EndScreenController endScreen;

        private float _timeoutSeconds = 30f;
        private string _textKey = "ui.inactivity";
        private float _idleTimer;
        private bool _popupVisible;
        private bool _active;

        /// <summary>Live inactivity timeout in seconds. The debug/playtest menu reads and writes this.</summary>
        public float TimeoutSeconds { get => _timeoutSeconds; set => _timeoutSeconds = Mathf.Max(1f, value); }

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true) ?? GetComponentInParent<GameConfigLoader>();
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true) ?? GetComponentInParent<LocalizationService>();
            if (tileTracking == null) tileTracking = GetComponentInChildren<TileTrackingManager>(true) ?? GetComponentInParent<TileTrackingManager>();
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true) ?? GetComponentInParent<SessionTimer>();
            if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true) ?? GetComponentInParent<GameInstanceCoordinator>();
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true) ?? GetComponentInParent<SimulationEngine>();
            if (endScreen == null) endScreen = GetComponentInChildren<EndScreenController>(true) ?? GetComponentInParent<EndScreenController>();
        }

        private void OnEnable()
        {
            if (coordinator != null)
                coordinator.OnTileActivity += OnActivity;
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded += OnSessionEnded;

            ApplyConfig();
        }

        private void OnDisable()
        {
            if (coordinator != null)
                coordinator.OnTileActivity -= OnActivity;
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded -= OnSessionEnded;
        }

        public void SetFromConfig(GameConfig config)
        {
            if (config?.Inactivity == null) return;
            _timeoutSeconds = config.Inactivity.timeoutSeconds > 0 ? config.Inactivity.timeoutSeconds : 30f;
            _textKey = config.Inactivity.textKey ?? "ui.inactivity";
        }

        /// <summary>Enable inactivity tracking. Call after tutorial finishes and gameplay starts.</summary>
        public void Activate()
        {
            _active = true;
            ResetTimer();
        }

        /// <summary>Disable inactivity tracking and hide popup.</summary>
        public void Deactivate()
        {
            _active = false;
            HidePopup();
        }

        private void Update()
        {
            if (!_active) return;
            if (sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.End) return;

            _idleTimer += Time.deltaTime;

            if (!_popupVisible && _idleTimer >= _timeoutSeconds)
                ShowPopup();

            if (idleEndSeconds > 0f && _idleTimer >= idleEndSeconds)
                TriggerIdleEnd();
        }

        /// <summary>The table was abandoned: give it back to the next visitor. Empty table restarts
        /// the game directly; a table with tiles ends the session through the normal pipeline and
        /// jumps the end screen straight to the clear-the-table message (no scorecard for no one).</summary>
        private void TriggerIdleEnd()
        {
            _active = false;   // one-shot; the next session's Activate() re-arms it
            HidePopup();

            int tiles = simulationEngine != null ? simulationEngine.PlacedTileCount : 0;
            if (tiles == 0)
            {
                coordinator?.RestartGame();
                return;
            }

            // Force the timer to zero: its next Update fires the full end pipeline
            // (end screen, restart flow), exactly like a natural session end.
            if (sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.Gameplay)
            {
                sessionTimer.SetRemainingSeconds(0f);
                StartCoroutine(SkipScorecardWhenEndScreenUp());
            }
        }

        private IEnumerator SkipScorecardWhenEndScreenUp()
        {
            // The end screen appears on the timer's OnTimerEnded, a frame or two out.
            float deadline = Time.unscaledTime + 2f;
            while (Time.unscaledTime < deadline)
            {
                if (endScreen != null && endScreen.IsVisible)
                {
                    endScreen.SkipToCompletionMessage();
                    yield break;
                }
                yield return null;
            }
        }

        private void OnActivity()
        {
            ResetTimer();
        }

        private void OnSessionEnded()
        {
            Deactivate();
        }

        private void ResetTimer()
        {
            _idleTimer = 0f;
            HidePopup();
        }

        private void ShowPopup()
        {
            if (popup == null) return;
            string text = localization != null ? localization.GetString(_textKey) : _textKey;
            popup.SetText(text);
            popup.Show();
            _popupVisible = true;
        }

        private void HidePopup()
        {
            if (popup == null) return;
            popup.Hide();
            _popupVisible = false;
        }

        private void ApplyConfig()
        {
            if (configLoader?.Config != null)
                SetFromConfig(configLoader.Config);
        }
    }
}
