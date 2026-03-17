using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>Finds all ResidentialHubMono in the scene at start and exposes them. No count requirement. No statics.</summary>
    public class HubRegistry : MonoBehaviour
    {
        private readonly List<ResidentialHubMono> _hubs = new List<ResidentialHubMono>();
        private bool _validated;

        /// <summary>Read-only list of hubs found in scene. Populated in Awake.</summary>
        public IReadOnlyList<ResidentialHubMono> Hubs => _hubs;

        /// <summary>True after Awake has run and at least one hub was found.</summary>
        public bool IsValid => _validated;

        private void Awake()
        {
            FetchHubs();
        }

        /// <summary>Find all ResidentialHubMono in scene. Call from Awake or before first use.</summary>
        public void FetchHubs()
        {
            _hubs.Clear();
            _validated = false;

            var found = GetComponentsInChildren<ResidentialHubMono>(false);
            foreach (var hub in found)
            {
                if (hub != null)
                    _hubs.Add(hub);
            }

            if (_hubs.Count == 0)
            {
                Debug.LogWarning("[HubRegistry] No ResidentialHubMono found in scene. Simulation will use transit graph nodes if available.");
                return;
            }

            var ids = new HashSet<string>();
            foreach (var hub in _hubs)
            {
                if (string.IsNullOrEmpty(hub.HubId))
                    Debug.LogWarning($"[HubRegistry] Hub on '{hub.gameObject.name}' has empty HubId.");
                else if (!ids.Add(hub.HubId))
                    Debug.LogWarning($"[HubRegistry] Duplicate HubId '{hub.HubId}' on '{hub.gameObject.name}'.");
                if (hub.Population <= 0)
                    Debug.LogWarning($"[HubRegistry] Hub '{hub.HubId}' on '{hub.gameObject.name}' has Population <= 0.");
            }

            _validated = true;
            Debug.Log($"[HubRegistry] Found {_hubs.Count} hubs: {string.Join(", ", _hubs.Select(h => $"{h.HubId}={h.Population:N0}"))}");
        }
    }
}
