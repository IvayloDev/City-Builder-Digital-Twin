using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Simulation;
using CityTwin.UI;


/// <summary>
/// Simple editor/play-mode helper:
/// - Press 1/2/3/4 to select a building id.
/// - Left-click on the table area to spawn that building.
/// - Drag with left mouse to move an existing building.
///
/// This bypasses OSC/TUIO and talks directly to SimulationEngine + a marker prefab,
/// so you can quickly test metric behaviour in the Unity editor.
/// </summary>
public class MouseBuildingTester : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SimulationEngine simulationEngine;
    [SerializeField] private RectTransform tableArea;
    [SerializeField] private GameObject markerPrefab;
    [Tooltip("UI camera used for ScreenPointToLocalPointInRectangle. Leave null to use Camera.main.")]
    [SerializeField] private Camera uiCamera;

    [Header("Building ids for number keys")]
    [SerializeField] private string key1BuildingId = "garden";
    [SerializeField] private string key2BuildingId = "office";
    [SerializeField] private string key3BuildingId = "hospital";
    [SerializeField] private string key4BuildingId = "school";

    private class ActiveTile
    {
        public string EngineId;
        public RectTransform Marker;
        public string BuildingId;
    }

    private readonly List<ActiveTile> _tiles = new List<ActiveTile>();
    private ActiveTile _dragging;
    private Vector2 _dragOffset;
    private string _currentBuildingId;

    private void Awake()
    {
        if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        if (tableArea == null) tableArea = GetComponentInChildren<RectTransform>(true);
        if (uiCamera == null) uiCamera = Camera.main;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        // Hotkeys 1-4 select building id
        if (keyboard.digit1Key.wasPressedThisFrame) _currentBuildingId = key1BuildingId;
        if (keyboard.digit2Key.wasPressedThisFrame) _currentBuildingId = key2BuildingId;
        if (keyboard.digit3Key.wasPressedThisFrame) _currentBuildingId = key3BuildingId;
        if (keyboard.digit4Key.wasPressedThisFrame) _currentBuildingId = key4BuildingId;

        Vector2 screenPos = mouse.position.ReadValue();

        if (mouse.leftButton.wasPressedThisFrame)
            OnMouseDown(screenPos);

        if (mouse.leftButton.isPressed)
            OnMouseDrag(screenPos);

        if (mouse.leftButton.wasReleasedThisFrame)
            _dragging = null;
    }

        private void OnMouseDown(Vector2 screenPos)
    {
        if (tableArea == null || markerPrefab == null || simulationEngine == null) return;

            var keyboard = Keyboard.current;
            bool deleteMode = keyboard != null && keyboard.escapeKey.isPressed;

            // First, check if we clicked on an existing marker
            for (int i = 0; i < _tiles.Count; i++)
        {
                var tile = _tiles[i];
            if (tile.Marker == null) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(tile.Marker, screenPos, uiCamera))
            {
                    if (deleteMode)
                {
                        // Remove from simulation and destroy marker when ESC is held.
                        if (!string.IsNullOrEmpty(tile.EngineId))
                            simulationEngine.RemoveTile(tile.EngineId);
                        Object.Destroy(tile.Marker.gameObject);
                        _tiles.RemoveAt(i);
                        _dragging = null;
                        return;
                    }
                    else
                    {
                        // Start dragging this marker.
                        _dragging = tile;
                        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                                tableArea, screenPos, uiCamera, out var local))
                        {
                            _dragOffset = tile.Marker.anchoredPosition - local;
                        }
                        return;
                }
            }
        }

        // Otherwise, spawn a new tile if we have a building selected
        if (string.IsNullOrEmpty(_currentBuildingId)) return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                tableArea, screenPos, uiCamera, out var spawnLocal))
            return;

        // Visual marker
            var go = Object.Instantiate(markerPrefab, tableArea);
            var rt = go.GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition = spawnLocal;

            // Optional: show building id on the marker label for quick identification.
            var display = go.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null)
                display.SetBuilding(_currentBuildingId);

        // Simulation tile: simulation space matches tableArea local space.
        var pose = new TilePose(spawnLocal, 0f, _currentBuildingId, 0, null);
        string engineId = simulationEngine.AddTile(pose);

        if (string.IsNullOrEmpty(engineId))
        {
            Object.Destroy(go);
            return;
        }

        var active = new ActiveTile
        {
            EngineId = engineId,
            Marker = rt,
            BuildingId = _currentBuildingId
        };
        _tiles.Add(active);
        _dragging = active;
        _dragOffset = Vector2.zero;
    }

    private void OnMouseDrag(Vector2 screenPos)
    {
        if (_dragging == null || _dragging.Marker == null || simulationEngine == null || tableArea == null)
            return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                tableArea, screenPos, uiCamera, out var local))
            return;

        Vector2 targetLocal = local + _dragOffset;
        _dragging.Marker.anchoredPosition = targetLocal;
        simulationEngine.UpdateTilePosition(_dragging.EngineId, targetLocal, 0f);
    }
}

