using System;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Input;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>
    /// Splash/start overlay that lets the player pick a language and begin the session.
    /// Supports both UI button clicks and "place any tile on a language button" (TUIO).
    /// After language selection, starts the tutorial. Session timer begins after tutorial completes.
    /// </summary>
    public class StartScreenController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private TutorialSequenceController tutorialSequence;
        [SerializeField] private InactivityPopupController inactivityPopup;

        [Header("UI")]
        [Tooltip("Root RectTransform for hit testing (should match BuildingSpawner.ContentRoot / table area).")]
        [SerializeField] private RectTransform tableRoot;
        [SerializeField] private GameObject overlayRoot;
        [Tooltip("While the start screen shows, these siblings of it are hidden (faded out, still running), so only the start graphics and the map image show: hubs, roads, buildings, top bar.")]
        [SerializeField] private string[] hideDuringStartScreen =
        {
            "Hubs Connector Lines Holder", "Roads Connector Lines Holder", "Buildings Holder", "Hubs Registry",
            "RootTable", "Top Bar UI", "DebugOverlayText",
        };
        private readonly System.Collections.Generic.List<CanvasGroup> _hiddenGroups = new System.Collections.Generic.List<CanvasGroup>();
        private readonly System.Collections.Generic.List<float> _hiddenAlpha = new System.Collections.Generic.List<float>();
        private bool _hidingGame;

        [Header("OSC → Table Mapping")]
        [Tooltip("If true, treats incoming TilePose.Position as normalized (0..1) and maps it into tableRoot local space using tableRoot.rect.size.")]
        [SerializeField] private bool assumeNormalizedTuio = true;

        [Tooltip("Enable so TUIO bottom (y≈1) appears at bottom of table. TUIO uses top-left origin; Unity UI uses bottom-left.")]
        [SerializeField] private bool flipY = true;

        [Tooltip("Maps TUIO (0.5, 0.5) to local (0,0) so table center = (0,0).")]
        [SerializeField] private bool centerOrigin = true;

        [Header("Language Buttons (RectTransform hit areas)")]
        [SerializeField] private RectTransform buttonEN;
        [SerializeField] private RectTransform buttonRU;
        [SerializeField] private RectTransform buttonKZ;

        [Header("Optional: wire these Button onClick in Inspector")]
        [SerializeField] private Button clickEN;
        [SerializeField] private Button clickRU;
        [SerializeField] private Button clickKZ;

        private bool _started;

        private void Awake()
        {
            if (tileTracking == null) tileTracking = GetComponentInChildren<TileTrackingManager>(true) ?? GetComponentInParent<TileTrackingManager>();
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true) ?? GetComponentInParent<LocalizationService>();
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true) ?? GetComponentInParent<SessionTimer>();
            if (tutorialSequence == null) tutorialSequence = GetComponentInChildren<TutorialSequenceController>(true) ?? GetComponentInParent<TutorialSequenceController>();
            if (inactivityPopup == null) inactivityPopup = GetComponentInChildren<InactivityPopupController>(true) ?? GetComponentInParent<InactivityPopupController>();
        }

        // Start screen up: only its graphics and the map underneath. Faded, not deactivated, so nothing stops running.
        private void LateUpdate()
        {
            bool hide = overlayRoot != null && overlayRoot.activeInHierarchy;
            if (hide == _hidingGame && (!hide || _hiddenGroups.Count > 0)) return;
            _hidingGame = hide;
            if (hide && _hiddenGroups.Count == 0 && overlayRoot.transform.parent != null)
            {
                foreach (Transform sibling in overlayRoot.transform.parent)
                {
                    if (System.Array.IndexOf(hideDuringStartScreen, sibling.name) < 0) continue;
                    var group = sibling.GetComponent<CanvasGroup>();
                    if (group == null) group = sibling.gameObject.AddComponent<CanvasGroup>();
                    _hiddenGroups.Add(group);
                    _hiddenAlpha.Add(group.alpha);
                }
            }
            for (int i = 0; i < _hiddenGroups.Count; i++)
            {
                var group = _hiddenGroups[i];
                if (group == null) continue;
                if (hide)
                {
                    _hiddenAlpha[i] = group.alpha; // their own fades (tutorial, end screen) get back what they had
                    group.alpha = 0f;
                }
                else group.alpha = _hiddenAlpha[i];
                group.blocksRaycasts = !hide;
            }
        }

        private void OnEnable()
        {
            if (overlayRoot != null) overlayRoot.SetActive(true);
            _started = false;

            if (tableRoot == null)
                tableRoot = transform as RectTransform;

            if (tileTracking != null)
                tileTracking.OnTileUpdated += OnTileUpdated;

            if (clickEN != null) clickEN.onClick.AddListener(() => StartWithLanguage("EN"));
            if (clickRU != null) clickRU.onClick.AddListener(() => StartWithLanguage("RU"));
            if (clickKZ != null) clickKZ.onClick.AddListener(() => StartWithLanguage("KZ"));
        }

        private void OnDisable()
        {
            if (tileTracking != null)
                tileTracking.OnTileUpdated -= OnTileUpdated;

            if (clickEN != null) clickEN.onClick.RemoveAllListeners();
            if (clickRU != null) clickRU.onClick.RemoveAllListeners();
            if (clickKZ != null) clickKZ.onClick.RemoveAllListeners();
        }

        private void OnTileUpdated(TilePose pose) => TryPickLanguage(pose);

        /// <summary>
        /// A tile at <paramref name="pose"/> (TUIO-normalized position) picks the language whose button it sits on.
        /// Works from the first frame, even while the buttons are still fading in. Also called by the coordinator
        /// for tiles placed in the Editor (mouse tester, palette), so they behave like physical tiles.
        /// Returns true if a language was picked.
        /// </summary>
        public bool TryPickLanguage(TilePose pose)
        {
            if (_started) return false;
            if (overlayRoot == null || !overlayRoot.activeInHierarchy) return false;
            if (tableRoot == null) return false;

            // Convert incoming OSC/TUIO to table local space for hit testing over the language buttons.
            Vector2 tableLocal = OscToTableLocal(pose.Position);

            if (IsPointInside(buttonEN, tableLocal)) { StartWithLanguage("EN"); return true; }
            if (IsPointInside(buttonRU, tableLocal)) { StartWithLanguage("RU"); return true; }
            if (IsPointInside(buttonKZ, tableLocal)) { StartWithLanguage("KZ"); return true; }
            return false;
        }

        /// <summary>True when a tile at this TUIO position lies on the target (e.g. the tutorial's SKIP), like the language buttons.</summary>
        public bool IsTileOver(RectTransform target, Vector2 tuioPosition)
        {
            if (target == null || tableRoot == null) return false;
            return IsPointInside(target, OscToTableLocal(tuioPosition));
        }

        private Vector2 OscToTableLocal(Vector2 oscPos)
        {
            if (!assumeNormalizedTuio)
                return oscPos;

            Vector2 pos = oscPos;
            if (flipY) pos.y = 1f - pos.y;

            Vector2 size = tableRoot.rect.size;
            if (size.x <= 0.0001f || size.y <= 0.0001f)
                size = new Vector2(300f, 300f);

            if (centerOrigin)
                return new Vector2((pos.x - 0.5f) * size.x, (pos.y - 0.5f) * size.y);
            return new Vector2(pos.x * size.x, pos.y * size.y);
        }

        private bool IsPointInside(RectTransform target, Vector2 pointInTableLocal)
        {
            if (target == null || tableRoot == null) return false;

            // tableLocal -> world -> targetLocal, then rect-contains
            Vector3 world = tableRoot.TransformPoint(new Vector3(pointInTableLocal.x, pointInTableLocal.y, 0f));
            Vector3 local3 = target.InverseTransformPoint(world);
            return target.rect.Contains(new Vector2(local3.x, local3.y));
        }

        public void StartWithLanguage(string languageCode)
        {
            if (_started) return;
            _started = true;

            if (localization != null)
                localization.CurrentLanguage = string.IsNullOrEmpty(languageCode) ? "EN" : languageCode;

            if (overlayRoot != null)
                overlayRoot.SetActive(false);

            BeginSession();

            if (tutorialSequence != null)
                tutorialSequence.StartTutorial();
        }

        private void BeginSession()
        {
            if (sessionTimer != null)
                sessionTimer.StartSession();

            if (inactivityPopup != null)
                inactivityPopup.Activate();
        }

        /// <summary>Re-show the start overlay and reset the started flag so the next language pick begins a fresh session. Call from restart flows.</summary>
        public void ShowStartScreen()
        {
            _started = false;
            tutorialSequence?.StopTutorial();
            inactivityPopup?.Deactivate();
            if (overlayRoot != null) overlayRoot.SetActive(true);
        }
    }
}

