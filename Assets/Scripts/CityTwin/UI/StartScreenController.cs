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
    /// </summary>
    public class StartScreenController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private SessionTimer sessionTimer;

        [Header("UI")]
        [Tooltip("Root RectTransform for hit testing (should match BuildingSpawner.ContentRoot / table area).")]
        [SerializeField] private RectTransform tableRoot;
        [SerializeField] private GameObject overlayRoot;

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

        private void OnTileUpdated(TilePose pose)
        {
            if (_started) return;
            if (overlayRoot == null || !overlayRoot.activeInHierarchy) return;
            if (tableRoot == null) return;

            // Convert incoming OSC/TUIO to table local space for hit testing over the language buttons.
            Vector2 tableLocal = OscToTableLocal(pose.Position);

            if (IsPointInside(buttonEN, tableLocal)) { StartWithLanguage("EN"); return; }
            if (IsPointInside(buttonRU, tableLocal)) { StartWithLanguage("RU"); return; }
            if (IsPointInside(buttonKZ, tableLocal)) { StartWithLanguage("KZ"); return; }
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

            if (sessionTimer != null)
                sessionTimer.StartSession();
        }
    }
}

