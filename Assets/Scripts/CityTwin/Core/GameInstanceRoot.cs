using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>
    /// Root component for the master game instance prefab. Each copy of the prefab represents one game instance.
    /// Set InstanceId (0-3) and ListenPort (e.g. 3333 for TUIO simulator, 9001-9004 for multi-instance) per copy. No statics.
    /// </summary>
    public class GameInstanceRoot : MonoBehaviour
    {
        [Tooltip("Quadrant/instance index 0-3. Used to identify this instance and select OSC source from config.")]
        [Range(0, 3)]
        [SerializeField] private int instanceId = 0;

        [Tooltip("UDP port this instance listens on for OSC. Use 3333 for TUIO simulator; 9001–9004 for multi-instance.")]
        [SerializeField] private int listenPort = 3333;

        /// <summary>Quadrant/instance index (0-3).</summary>
        public int InstanceId => instanceId;

        /// <summary>UDP port for this instance's OSC receiver. Any valid port 1024–65535.</summary>
        public int ListenPort => listenPort;

        private void OnValidate()
        {
            instanceId = Mathf.Clamp(instanceId, 0, 3);
            listenPort = Mathf.Clamp(listenPort, 1, 65535);
        }
    }
}
