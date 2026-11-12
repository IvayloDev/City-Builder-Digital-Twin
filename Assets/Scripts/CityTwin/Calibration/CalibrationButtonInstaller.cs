using TMPro;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.UI;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Adds a CALIBRATION button directly below every RESTART button, for keyboards without the
    /// Ctrl+Shift+F4 shortcut. It is a copy of the RESTART button (same look and anchoring), so it lives in
    /// the same playtest UI and disappears with it. Clicking it opens the calibration menu
    /// (<see cref="ProjectionCalibrationController.Open"/>), which needs the ProjectionOutputRig enabled.
    /// Self-installing at scene load; delete this file to remove the button.
    /// </summary>
    public class CalibrationButtonInstaller : MonoBehaviour
    {
        private const string CloneName = "Calibration Button";
        private const float Gap = 12f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            new GameObject("CalibrationButtonInstaller").AddComponent<CalibrationButtonInstaller>();
        }

        private float _nextScan;
        private int _scans;

        private void Update()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.5f;
            if (TryInstall() || ++_scans > 20) Destroy(gameObject); // done, or give up after ~10 s
        }

        private static bool TryInstall()
        {
            var restarts = FindObjectsByType<RestartGameButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (restarts.Length == 0) return false;

            foreach (var restart in restarts)
            {
                Transform parent = restart.transform.parent;
                if (parent == null || parent.Find(CloneName) != null) continue;

                var clone = Instantiate(restart.gameObject, parent);
                clone.name = CloneName;
                clone.transform.SetSiblingIndex(restart.transform.GetSiblingIndex() + 1);
                Destroy(clone.GetComponent<RestartGameButton>());

                // Fresh click event: drops the restart listener the copied component added in Awake
                // and any persistent calls from the original.
                var button = clone.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(OpenCalibration);

                foreach (var comp in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (comp != null && comp.GetType().Name.Contains("Locali")) Destroy(comp);
                foreach (var text in clone.GetComponentsInChildren<TMP_Text>(true))
                    text.text = "CALIBRATION";

                var src = (RectTransform)restart.transform;
                var rt = (RectTransform)clone.transform;
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, src.rect.height + Gap);
            }
            return true;
        }

        private static void OpenCalibration()
        {
            var controller = FindFirstObjectByType<ProjectionCalibrationController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                Debug.LogWarning("[CalibrationButton] No ProjectionCalibrationController in the scene.");
                return;
            }
            controller.Open();
            if (!controller.IsActive)
                Debug.LogWarning("[CalibrationButton] Calibration did not open: enable the ProjectionOutputRig component (calibration needs it).");
        }
    }
}
