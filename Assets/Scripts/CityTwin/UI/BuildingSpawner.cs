using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>Spawns a building marker visual when a tile is added and removes it when the tile is removed. Place on Game Instance; assign content root and marker prefab.</summary>
    public class BuildingSpawner : MonoBehaviour
    {
        [Tooltip("Root to parent spawned markers (e.g. a RectTransform for the table area).")]
        [SerializeField] private RectTransform contentRoot;

        [Tooltip("Prefab to instantiate per building (will be positioned at TUIO coordinates).")]
        [SerializeField] private GameObject buildingMarkerPrefab;

        [Header("Coordinates")]
        [Tooltip("TUIO typically sends 0-1. Table size maps that range to local position (e.g. 300,300 to match simulation graph).")]
        [SerializeField] private Vector2 tableSize = new Vector2(300f, 300f);

        [Tooltip("Enable so TUIO bottom (y≈1) appears at bottom of table. TUIO uses top-left origin; Unity UI uses bottom-left.")]
        [SerializeField] private bool flipY = true;

        [Tooltip("Enable when Content Root is center-anchored. Maps TUIO (0.5, 0.5) to local (0,0) so center of simulator = center of table.")]
        [SerializeField] private bool centerOrigin = true;

        private readonly Dictionary<string, GameObject> _spawned = new Dictionary<string, GameObject>();

        private void Awake()
        {
            if (contentRoot == null) contentRoot = GetComponentInChildren<RectTransform>(true);
        }

        /// <summary>Convert raw TUIO (0-1) position to content root local space, applying flipY and centerOrigin.</summary>
        public Vector2 TuioToLocalPosition(Vector2 tuioRaw)
        {
            Vector2 pos = tuioRaw;
            if (flipY) pos.y = 1f - pos.y;
            return TuioToLocal(pos);
        }

        /// <summary>Convert any world position to content root local space (2D).</summary>
        public Vector2 WorldToContentLocal(Vector3 worldPos)
        {
            if (contentRoot == null) return new Vector2(worldPos.x, worldPos.y);
            Vector3 local = contentRoot.InverseTransformPoint(worldPos);
            return new Vector2(local.x, local.y);
        }

        public RectTransform ContentRoot => contentRoot;

        private Vector2 TuioToLocal(Vector2 pos)
        {
            if (centerOrigin)
                return new Vector2((pos.x - 0.5f) * tableSize.x, (pos.y - 0.5f) * tableSize.y);
            return new Vector2(pos.x * tableSize.x, pos.y * tableSize.y);
        }

        /// <summary>Spawn a building marker at the tile pose. Call after simulation AddTile.</summary>
        public void SpawnBuilding(TilePose pose, string engineTileId)
        {
            Debug.Log($"[BuildingSpawner] SpawnBuilding buildingId={pose.BuildingId} engineTileId={engineTileId}");
            if (buildingMarkerPrefab == null) { Debug.LogWarning("[BuildingSpawner] buildingMarkerPrefab is not assigned. Assign in Inspector."); return; }
            if (contentRoot == null) { Debug.LogWarning("[BuildingSpawner] contentRoot is not assigned. Assign a RectTransform (e.g. table area)."); return; }
            if (string.IsNullOrEmpty(engineTileId)) { Debug.LogWarning("[BuildingSpawner] engineTileId is empty."); return; }
            if (_spawned.ContainsKey(engineTileId)) { Debug.Log($"[BuildingSpawner] Already spawned for {engineTileId}, skipping."); return; }

            GameObject instance = Instantiate(buildingMarkerPrefab, contentRoot);
            instance.name = $"{pose.BuildingId}_{engineTileId}";

            Vector2 pos = pose.Position;
            if (flipY) pos.y = 1f - pos.y;
            Vector2 localPos = TuioToLocal(pos);

            if (instance.transform is RectTransform rt)
            {
                rt.anchoredPosition = localPos;
                rt.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
            else
            {
                instance.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
                instance.transform.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }

            var display = instance.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null) display.SetBuilding(pose.BuildingId);

            _spawned[engineTileId] = instance;
            Debug.Log($"[BuildingSpawner] Spawned {pose.BuildingId} at ({localPos.x:F0},{localPos.y:F0})");
        }

        /// <summary>Move an existing building marker to the new pose (e.g. TUIO position update).</summary>
        public void MoveBuilding(TilePose pose, string engineTileId)
        {
            if (contentRoot == null || string.IsNullOrEmpty(engineTileId)) return;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return;

            Vector2 pos = pose.Position;
            if (flipY) pos.y = 1f - pos.y;
            Vector2 localPos = TuioToLocal(pos);

            if (go.transform is RectTransform rt)
            {
                rt.anchoredPosition = localPos;
                rt.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
            else
            {
                go.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
                go.transform.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
        }

        /// <summary>Remove the building marker for this engine tile id. Call when tile is removed.</summary>
        public void RemoveBuilding(string engineTileId)
        {
            if (string.IsNullOrEmpty(engineTileId)) return;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go)) return;
            _spawned.Remove(engineTileId);
            if (go != null) Destroy(go);
        }

        /// <summary>Get the local-space position of a spawned building marker. Returns false if not found.</summary>
        public bool TryGetMarkerPosition(string engineTileId, out Vector2 localPos)
        {
            localPos = Vector2.zero;
            if (string.IsNullOrEmpty(engineTileId)) return false;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return false;
            if (go.transform is RectTransform rt)
                localPos = rt.anchoredPosition;
            else
                localPos = new Vector2(go.transform.localPosition.x, go.transform.localPosition.y);
            return true;
        }

        /// <summary>Remove all spawned markers (e.g. on reset).</summary>
        public void ClearAll()
        {
            foreach (var kv in _spawned)
            {
                if (kv.Value != null) Destroy(kv.Value);
            }
            _spawned.Clear();
        }
    }
}
