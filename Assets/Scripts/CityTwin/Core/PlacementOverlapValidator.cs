using System.Collections.Generic;
using UnityEngine;
using CityTwin.UI;

namespace CityTwin.Core
{
    /// <summary>
    /// Centralized overlap validation based on 2D circles in content-local space.
    /// Radii are derived from marker halo visuals and hub visuals.
    /// </summary>
    public class PlacementOverlapValidator : MonoBehaviour
    {
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private HubRegistry hubRegistry;
        [SerializeField] private Color invalidHaloColor = new Color(1f, 0.25f, 0.25f, 0.95f);
        [SerializeField] private float fallbackBuildingRadius = 24f;
        [SerializeField] private float fallbackHubRadius = 28f;

        private struct TileFootprint
        {
            public Vector2 position;
            public float radius;
            public bool invalid;
        }

        private readonly Dictionary<string, TileFootprint> _tileFootprints = new Dictionary<string, TileFootprint>();
        private readonly List<(Vector2 position, float radius)> _hubFootprints = new List<(Vector2, float)>();

        private void Awake()
        {
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
        }

        public void RefreshHubFootprints()
        {
            _hubFootprints.Clear();
            if (hubRegistry == null)
                hubRegistry = GetComponentInChildren<HubRegistry>(true);
            if (hubRegistry == null) return;

            hubRegistry.FetchHubs();
            var hubs = hubRegistry.Hubs;
            for (int i = 0; i < hubs.Count; i++)
            {
                var hub = hubs[i];
                if (hub == null) continue;

                Vector2 center = buildingSpawner != null
                    ? buildingSpawner.WorldToContentLocal(hub.transform.position)
                    : hub.Position2D;

                float radius = ResolveHubRadius(hub);
                _hubFootprints.Add((center, radius));
            }
        }

        public float ResolveRadiusForBuilding(string buildingId)
        {
            if (buildingSpawner != null && buildingSpawner.TryGetEstimatedBuildingRadius(buildingId, out float radius))
                return Mathf.Max(1f, radius);
            return Mathf.Max(1f, fallbackBuildingRadius);
        }

        public float ResolveRadiusForTile(string tileId, string buildingId)
        {
            if (!string.IsNullOrEmpty(tileId) && buildingSpawner != null &&
                buildingSpawner.TryGetMarkerVisualRadius(tileId, out float radiusFromMarker))
            {
                return Mathf.Max(1f, radiusFromMarker);
            }

            if (!string.IsNullOrEmpty(tileId) && _tileFootprints.TryGetValue(tileId, out var footprint))
                return Mathf.Max(1f, footprint.radius);

            return ResolveRadiusForBuilding(buildingId);
        }

        public bool IsOverlapping(string movingTileIdOrNull, Vector2 candidatePosition, float candidateRadius)
        {
            float safeRadius = Mathf.Max(1f, candidateRadius);
            float minSqrDist;

            foreach (var kv in _tileFootprints)
            {
                if (!string.IsNullOrEmpty(movingTileIdOrNull) && kv.Key == movingTileIdOrNull)
                    continue;

                float sum = safeRadius + Mathf.Max(1f, kv.Value.radius);
                minSqrDist = sum * sum;
                if ((candidatePosition - kv.Value.position).sqrMagnitude < minSqrDist)
                    return true;
            }

            for (int i = 0; i < _hubFootprints.Count; i++)
            {
                float sum = safeRadius + Mathf.Max(1f, _hubFootprints[i].radius);
                minSqrDist = sum * sum;
                if ((candidatePosition - _hubFootprints[i].position).sqrMagnitude < minSqrDist)
                    return true;
            }

            return false;
        }

        public void UpsertTile(string tileId, Vector2 position, float radius, bool isInvalid)
        {
            if (string.IsNullOrEmpty(tileId)) return;
            _tileFootprints[tileId] = new TileFootprint
            {
                position = position,
                radius = Mathf.Max(1f, radius),
                invalid = isInvalid
            };
        }

        public void RemoveTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId)) return;
            _tileFootprints.Remove(tileId);
        }

        public void SetTileVisualInvalid(string tileId, bool isInvalid)
        {
            if (string.IsNullOrEmpty(tileId)) return;
            if (_tileFootprints.TryGetValue(tileId, out var footprint))
            {
                footprint.invalid = isInvalid;
                _tileFootprints[tileId] = footprint;
            }

            buildingSpawner?.SetMarkerPlacementInvalid(tileId, isInvalid, invalidHaloColor);
        }

        private float ResolveHubRadius(ResidentialHubMono hub)
        {
            if (hub == null) return Mathf.Max(1f, fallbackHubRadius);

            Transform visualTransform = hub.VisualRoot != null ? hub.VisualRoot : hub.transform;
            if (buildingSpawner != null && buildingSpawner.ContentRoot != null)
            {
                if (visualTransform is RectTransform visualRect)
                {
                    Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(buildingSpawner.ContentRoot, visualRect);
                    float r = Mathf.Max(b.extents.x, b.extents.y);
                    if (r > 0.001f) return r;
                }

                if (hub.transform is RectTransform hubRect)
                {
                    Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(buildingSpawner.ContentRoot, hubRect);
                    float r = Mathf.Max(b.extents.x, b.extents.y);
                    if (r > 0.001f) return r;
                }
            }

            var renderers = visualTransform.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                Bounds combined = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    combined.Encapsulate(renderers[i].bounds);

                float worldRadius = Mathf.Max(combined.extents.x, combined.extents.y, combined.extents.z);
                if (buildingSpawner != null)
                {
                    Vector2 center = buildingSpawner.WorldToContentLocal(combined.center);
                    Vector2 edge = buildingSpawner.WorldToContentLocal(combined.center + Vector3.right * worldRadius);
                    float contentRadius = Vector2.Distance(center, edge);
                    if (contentRadius > 0.001f) return contentRadius;
                }
                if (worldRadius > 0.001f) return worldRadius;
            }

            return Mathf.Max(1f, fallbackHubRadius);
        }
    }
}
