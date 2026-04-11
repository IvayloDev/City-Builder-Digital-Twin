using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>
    /// A self-contained hub layout preset. Add ResidentialHubMono instances as children,
    /// then configure which hubs connect to each other via the Connections list.
    /// HubLayoutManager activates one preset at random on startup and deactivates the rest.
    /// </summary>
    public class HubLayoutPreset : MonoBehaviour
    {
        [Tooltip("Which hub pairs should have a visual connection line drawn between them.")]
        [SerializeField] private List<HubConnectionPair> connections = new List<HubConnectionPair>();

        public IReadOnlyList<HubConnectionPair> Connections => connections;

        [Serializable]
        public struct HubConnectionPair
        {
            public ResidentialHubMono hubA;
            public ResidentialHubMono hubB;
        }

#if UNITY_EDITOR
        [Header("Debug")]
        [SerializeField] private Color gizmoColor = new Color(0.5f, 0.85f, 1f, 0.7f);

        private void OnValidate()
        {
            UnityEditor.SceneView.RepaintAll();
        }

        private void OnDrawGizmosSelected()
        {
            if (connections == null) return;
            Gizmos.color = gizmoColor;
            foreach (var pair in connections)
            {
                if (pair.hubA == null || pair.hubB == null) continue;
                Gizmos.DrawLine(pair.hubA.transform.position, pair.hubB.transform.position);
                Gizmos.DrawWireSphere(pair.hubA.transform.position, 3f);
                Gizmos.DrawWireSphere(pair.hubB.transform.position, 3f);
            }
        }
#endif
    }
}
