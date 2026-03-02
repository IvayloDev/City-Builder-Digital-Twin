using CityTwin.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace CityTwin.Input
{
    /// <summary>Optional debug overlay: packets/sec and last pose. Assign a TextMeshProUGUI or leave empty to auto-find. Toggle with F1.</summary>
    public class TileTrackingDebugOverlay : MonoBehaviour
    {
        [SerializeField] private bool showInGame = true;
        [SerializeField] private Key toggleKey = Key.F1;
        [Tooltip("Assign a TextMeshProUGUI to show debug stats. If unset, uses one on this GameObject.")]
        [SerializeField] private TextMeshProUGUI debugText;

        private TileTrackingManager _manager;
        private GameInstanceRoot _root;
        private int _messageCount;
        private float _lastResetTime;
        private Vector2 _lastPosition;
        private bool _visible = true;

        private void Awake()
        {
            _manager = GetComponent<TileTrackingManager>();
            _root = GetComponent<GameInstanceRoot>();
            if (debugText == null)
                debugText = GetComponent<TextMeshProUGUI>();
            if (debugText != null)
                debugText.gameObject.SetActive(_visible && showInGame);
        }

        private void OnEnable()
        {
            if (_manager != null)
                _manager.OnTileUpdated += OnTileUpdated;
            _lastResetTime = Time.time;
        }

        private void OnDisable()
        {
            if (_manager != null)
                _manager.OnTileUpdated -= OnTileUpdated;
        }

        private void OnTileUpdated(Core.TilePose pose)
        {
            _messageCount++;
            _lastPosition = pose.Position;
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
            {
                _visible = !_visible;
                if (debugText != null)
                    debugText.gameObject.SetActive(_visible && showInGame);
            }

            if (debugText == null || !showInGame || !_visible) return;

            float elapsed = Time.time - _lastResetTime;
            if (elapsed >= 1f)
            {
                _messageCount = 0;
                _lastResetTime = Time.time;
            }
            int id = _root != null ? _root.InstanceId : -1;
            int port = _root != null ? _root.ListenPort : 0;
            float rate = elapsed > 0 ? _messageCount / elapsed : 0;
            debugText.text = $"[Q{id}] port {port} | {rate:F0} msg/s | last pos {_lastPosition.x:F2},{_lastPosition.y:F2}";
        }
    }
}
