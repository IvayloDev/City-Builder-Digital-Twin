using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace CityTwin.UI
{
    /// <summary>
    /// Keeps the whole game in view at any window shape: measures the play area (the "Main BG" fan art, the palette
    /// pins, the drop zone and the testing notice) and sets the canvas scaler to Expand with a reference size that
    /// holds all of it with a margin. The flat preview uses that directly; the projection rig reads the same scaler
    /// (it calls <see cref="Apply"/> first), so fullscreen shows the same framing.
    /// Runs in Edit mode too, so the Game view preview has the same framing. Added at runtime to any Scale With Screen
    /// Size canvas holding a "Main BG" that lacks it.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(CanvasScaler))]
    public class PlayAreaFit : MonoBehaviour
    {
        [Tooltip("Fraction of the window the play area may fill (width and height). Lower = smaller game, more margin.")]
        [SerializeField, Range(0.5f, 1f)] private float fill = 0.9f;
        [Tooltip("Objects whose rect is part of the play area, matched by exact name.")]
        [SerializeField] private string[] boundsNames = { "Main BG", "Clear Drop Zone", "Testing Notice" };
        [Tooltip("Objects whose name starts with this count too (the palette pin anchors), with PinHalfSize around them.")]
        [SerializeField] private string pinPrefix = "Pin ";
        [SerializeField] private float pinHalfSize = 80f; // pin (39) plus the price label hanging under it

        [Header("Testing notice")]
        [Tooltip("The bottom notice is found by this text (any language spelling of it that contains it). Part of the play area bounds.")]
        [SerializeField] private string noticeContains = "testing purposes";
        [Tooltip("Play mode: move the notice down by this much (canvas units), into the black under the fan.")]
        [SerializeField] private float noticeDrop = 66f;
        [Tooltip("Play mode: the notice gets its own canvas at this sorting order, above the start screen and other overlays.")]
        [SerializeField] private int noticeSortingOrder = 10;

        private RectTransform _notice;
        private bool _noticePlaced;

        private CanvasScaler _scaler;

        private void OnEnable()
        {
            if (Application.isPlaying) PlaceNotice(); // never in Edit mode: it would add a canvas to the scene
            Apply();
        }

        private RectTransform FindNotice()
        {
            if (_notice != null || string.IsNullOrEmpty(noticeContains)) return _notice;
            foreach (var text in GetComponentsInChildren<TMP_Text>(true))
                if (text.text != null && text.text.IndexOf(noticeContains, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return _notice = text.rectTransform;
            return null;
        }

        private void PlaceNotice()
        {
            if (_noticePlaced || FindNotice() == null) return;
            _noticePlaced = true;
            _notice.anchoredPosition += new Vector2(0f, -noticeDrop);
            var canvas = _notice.GetComponent<Canvas>();
            if (canvas == null) canvas = _notice.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = noticeSortingOrder;
        }

        private float _nextCheck;
        private string _lastLogged;

        // Edit mode: follow layout edits (Update only runs when something in the scene changes). Play mode: re-check
        // twice a second, so a window resize, a layout change or anything resetting the scaler can't leave it cropped.
        // Not while the projection rig drives the canvas (Constant Pixel Size); it applies the fit when it starts.
        private void Update()
        {
            if (!Application.isPlaying) { Apply(); return; }
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            if (_scaler != null && _scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize) Apply();
        }

        private void OnValidate()
        {
            // Not while the projection rig drives the canvas (Constant Pixel Size): it reads the fit when it starts.
            if (isActiveAndEnabled && GetComponent<CanvasScaler>().uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize) Apply();
        }

        /// <summary>Measure the play area and set the scaler. False when there is nothing to measure.</summary>
        public bool Apply()
        {
            if (!isActiveAndEnabled) return false;
            if (_scaler == null) _scaler = GetComponent<CanvasScaler>();
            var root = transform as RectTransform;
            if (_scaler == null || root == null) return false;

            // Half extents around the canvas centre (canvas units), so the canvas can stay centred.
            float halfW = 0f, halfH = 0f;
            var corners = new Vector3[4];
            foreach (var rt in GetComponentsInChildren<RectTransform>(false))
            {
                bool named = System.Array.IndexOf(boundsNames, rt.name) >= 0 || rt == FindNotice();
                bool pin = !string.IsNullOrEmpty(pinPrefix) && rt.name.StartsWith(pinPrefix);
                if (!named && !pin) continue;
                rt.GetWorldCorners(corners);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 p = root.InverseTransformPoint(corners[i]);
                    p -= root.rect.center;
                    float pad = pin ? pinHalfSize : 0f;
                    halfW = Mathf.Max(halfW, Mathf.Abs(p.x) + pad);
                    halfH = Mathf.Max(halfH, Mathf.Abs(p.y) + pad);
                }
            }
            if (halfW < 1f || halfH < 1f) return false;

            // Only write real changes: in Edit mode every write marks the scene dirty.
            var reference = new Vector2(Mathf.Round(2f * halfW / fill), Mathf.Round(2f * halfH / fill));
            if (_scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            if (_scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.Expand) _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            if ((_scaler.referenceResolution - reference).sqrMagnitude > 1f) _scaler.referenceResolution = reference;
            if (!Mathf.Approximately(_scaler.referencePixelsPerUnit, 100f)) _scaler.referencePixelsPerUnit = 100f;
            if (Application.isPlaying)
            {
                string summary = $"[PlayAreaFit] '{name}': play area {2f * halfW:F0} x {2f * halfH:F0} units, Expand reference {reference.x:F0} x {reference.y:F0} (fill {fill:F2}), screen {Screen.width} x {Screen.height}.";
                if (summary != _lastLogged) { _lastLogged = summary; Debug.Log(summary, this); }
            }
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            AddToCanvases();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AddToCanvases();

        private static void AddToCanvases()
        {
            foreach (var scaler in Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                EnsureOn(scaler);
        }

        /// <summary>The fit for this scaler's canvas (added when its canvas holds the play area art), or null.</summary>
        public static PlayAreaFit EnsureOn(CanvasScaler scaler)
        {
            if (scaler == null) return null;
            var fit = scaler.GetComponent<PlayAreaFit>();
            if (fit != null) return fit;
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return null;
            var images = new List<Image>();
            scaler.GetComponentsInChildren(false, images);
            foreach (var image in images)
                if (image.name == "Main BG") return scaler.gameObject.AddComponent<PlayAreaFit>(); // Awake applies it
            return null;
        }
    }
}
