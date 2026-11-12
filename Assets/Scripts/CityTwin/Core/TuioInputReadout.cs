using TMPro;
using UnityEngine;
using CityTwin.Input;

namespace CityTwin.Core
{
    /// <summary>
    /// Debug readout shown just below the "DROP HERE TO REMOVE" label: the number of live TUIO
    /// objects, the total /tuio/2Dobj messages received (climbs whenever TUIO is arriving at all),
    /// and the listen port. Lets staff confirm at a glance whether reacTIVision/TUIO reaches the game.
    /// Runtime-only, self-installing; delete this file to remove it. Not localized (diagnostic only).
    /// </summary>
    public class TuioInputReadout : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("TuioInputReadout");
            go.AddComponent<TuioInputReadout>();
        }

        private TMP_Text _label;
        private TileTrackingManager[] _managers;
        private float _nextScan;

        private void Update()
        {
            if (_label == null)
            {
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + 0.5f;
                TryCreateLabel();
                return;
            }

            int live = 0, msgs = 0, port = 0;
            Vector2 last = Vector2.zero;
            for (int i = 0; i < _managers.Length; i++)
            {
                var m = _managers[i];
                if (m == null || !m.isActiveAndEnabled) continue;
                live += m.LiveInputCount;
                msgs += m.OscMessageCount;
                if (port == 0) { port = m.ListenPort; last = m.LastRawPosition; }
            }
            _label.text = $"TUIO  live:{live}  msgs:{msgs}  :{port}  last {last.x:F2},{last.y:F2}";
        }

        private void TryCreateLabel()
        {
            _managers = FindObjectsByType<TileTrackingManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (_managers.Length == 0) return;

            // Sit beside an active "DROP HERE TO REMOVE" (a "Label" TMP under a "Clear Drop Zone").
            foreach (var zone in FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (zone.name != "Clear Drop Zone") continue;
                var src = zone.GetComponentInChildren<TMP_Text>(true);
                if (src == null) continue;

                var clone = Instantiate(src.gameObject, src.transform.parent);
                clone.name = "TuioInputReadoutLabel";

                // Strip anything but the text component (e.g. a localizer) so our text is not overwritten.
                foreach (var comp in clone.GetComponents<MonoBehaviour>())
                    if (!(comp is TMP_Text)) Destroy(comp);

                var rt = (RectTransform)clone.transform;
                var srcRt = (RectTransform)src.transform;
                rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0f, -44f);

                _label = clone.GetComponent<TMP_Text>();
                _label.enableAutoSizing = false;
                _label.fontSize = Mathf.Max(10f, src.fontSize * 0.6f);
                _label.text = "TUIO  live:0  msgs:0";
                return;
            }
        }
    }
}
