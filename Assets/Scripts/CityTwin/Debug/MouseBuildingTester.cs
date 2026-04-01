using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Config;
using CityTwin.Localization;
using CityTwin.Simulation;
using CityTwin.UI;


/// <summary>
/// Simple editor/play-mode helper:
/// - Press 1/2/3/4 to select a building id.
/// - Left-click on the table area to spawn that building.
/// - Drag with left mouse to move an existing building.
///
/// This emulates OSC/TUIO updates through GameInstanceCoordinator so budget and
/// placement/removal logic match physical marker behavior in the Unity editor.
/// </summary>
public class MouseBuildingTester : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SimulationEngine simulationEngine;
    [SerializeField] private GameInstanceCoordinator coordinator;
    [SerializeField] private GameConfigLoader configLoader;
    [SerializeField] private LocalizationService localization;
    [SerializeField] private BuildingSpawner buildingSpawner;
    [SerializeField] private RectTransform tableArea;
    [SerializeField] private GameObject markerPrefab;
    [Tooltip("UI camera used for ScreenPointToLocalPointInRectangle. Leave null to use Camera.main.")]
    [SerializeField] private Camera uiCamera;

    [Header("Building ids for number keys")]
    [SerializeField] private string key1BuildingId = "garden";
    [SerializeField] private string key2BuildingId = "office";
    [SerializeField] private string key3BuildingId = "hospital";
    [SerializeField] private string key4BuildingId = "school";

    [Header("Debug picker UI")]
    [SerializeField] private bool enableBackquotePicker = true;
    [SerializeField] private Key togglePickerKey = Key.Backquote;
    [SerializeField] private bool pauseInputWhilePickerOpen = true;
    [SerializeField] private float pickerUiScale = 1f;

    private class ActiveTile
    {
        public string DebugTileId;
        public string EngineId;
        public RectTransform Marker;
        public string BuildingId;
    }

    private readonly List<ActiveTile> _tiles = new List<ActiveTile>();
    private ActiveTile _dragging;
    private Vector2 _dragOffset;
    private string _currentBuildingId;
    private int _nextDebugTileId;

    private bool _pickerOpen;
    private Vector2 _pickerScroll;
    private string _pickerFilter = "";

    private void Awake()
    {
        if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true);
        if (coordinator == null) coordinator = GetComponentInParent<GameInstanceCoordinator>(true);
        if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
        if (localization == null) localization = GetComponentInChildren<LocalizationService>(true);
        if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
        if (tableArea == null) tableArea = GetComponentInChildren<RectTransform>(true);
        if (tableArea == null && buildingSpawner != null) tableArea = buildingSpawner.ContentRoot;
        if (uiCamera == null) uiCamera = Camera.main;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        if (enableBackquotePicker && WasPressedThisFrame(keyboard, togglePickerKey))
            _pickerOpen = !_pickerOpen;

        if (_pickerOpen && pauseInputWhilePickerOpen)
            return;

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
        if (tableArea == null || simulationEngine == null) return;
        // If we don't have a BuildingSpawner, we need markerPrefab for the fallback debug visuals.
        if (buildingSpawner == null && markerPrefab == null) return;

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
                        if (coordinator != null && !string.IsNullOrEmpty(tile.DebugTileId))
                        {
                            coordinator.TryProcessTileRemoval(tile.DebugTileId);
                        }
                        else if (!string.IsNullOrEmpty(tile.EngineId))
                        {
                            simulationEngine.RemoveTile(tile.EngineId);
                            buildingSpawner?.RemoveBuilding(tile.EngineId);
                        }
                        if (tile.Marker != null) Object.Destroy(tile.Marker.gameObject);
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

        string engineId = null;
        string debugTileId = $"debug_mouse_{_nextDebugTileId++}";
        bool useCoordinatorPath = coordinator != null && buildingSpawner != null;
        if (useCoordinatorPath)
        {
            Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(spawnLocal);
            var debugPose = new TilePose(tuioPos, 0f, _currentBuildingId, 0, debugTileId);
            bool placed = coordinator.TryProcessTileUpdate(debugPose, out engineId);
            if (!placed || string.IsNullOrEmpty(engineId))
                return;
        }
        else
        {
            // Fallback direct simulation path for scenes without coordinator/building spawner.
            var simPose = new TilePose(spawnLocal, 0f, _currentBuildingId, 0, null);
            engineId = simulationEngine.AddTile(simPose);
            if (string.IsNullOrEmpty(engineId)) return;
        }

        // Resolve marker created by coordinator/spawner (instance name = "{BuildingId}_{engineTileId}").
        RectTransform rt = null;
        if (buildingSpawner != null && buildingSpawner.ContentRoot != null)
        {
            if (!useCoordinatorPath)
            {
                Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(spawnLocal);
                var spawnerPose = new TilePose(tuioPos, 0f, _currentBuildingId, 0, null);
                buildingSpawner.SpawnBuilding(spawnerPose, engineId);
            }

            string instanceName = $"{_currentBuildingId}_{engineId}";
            rt = buildingSpawner.ContentRoot.Find(instanceName) as RectTransform;
            if (rt == null)
                rt = FindMarkerRecursive(instanceName);
        }

        // Fallback: old debug marker spawning (connections won't draw correctly).
        if (rt == null)
        {
            if (markerPrefab == null) return;
            var go = Object.Instantiate(markerPrefab, tableArea);
            rt = go.GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = spawnLocal;

            var display = go.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null) display.SetBuilding(_currentBuildingId);
        }

        var active = new ActiveTile
        {
            DebugTileId = debugTileId,
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
        if (_dragging == null || simulationEngine == null || tableArea == null)
            return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                tableArea, screenPos, uiCamera, out var local))
            return;

        Vector2 targetLocal = local + _dragOffset;
        bool useCoordinatorPath = coordinator != null && buildingSpawner != null && !string.IsNullOrEmpty(_dragging.DebugTileId);
        if (useCoordinatorPath)
        {
            Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(targetLocal);
            var debugPose = new TilePose(tuioPos, 0f, _dragging.BuildingId, 0, _dragging.DebugTileId);
            coordinator.TryProcessTileUpdate(debugPose, out _);
        }
        else
        {
            simulationEngine.UpdateTilePosition(_dragging.EngineId, targetLocal, 0f);

            // Keep marker in sync (when using BuildingSpawner we must move via it because it owns the registered marker).
            if (buildingSpawner != null)
            {
                Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(targetLocal);
                var spawnerPose = new TilePose(tuioPos, 0f, _dragging.BuildingId, 0, null);
                buildingSpawner.MoveBuilding(spawnerPose, _dragging.EngineId);
            }
            else
            {
                _dragging.Marker.anchoredPosition = targetLocal;
            }
        }
    }

    private void OnGUI()
    {
        if (!enableBackquotePicker || !_pickerOpen) return;

        float scale = Mathf.Clamp(pickerUiScale, 1f, 20f);
        float inv = 1f / scale;

        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f)) * oldMatrix;

        const float pad = 10f;
        float width = Mathf.Min(560f, (Screen.width * inv) - pad * 2f);
        float height = Mathf.Min(520f, (Screen.height * inv) - pad * 2f);
        var rect = new Rect(pad, pad, width, height);

        GUI.Box(rect, "Debug Building Picker");

        GUILayout.BeginArea(new Rect(rect.x + pad, rect.y + 28f, rect.width - pad * 2f, rect.height - 28f - pad));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Filter", GUILayout.Width(40f));
        _pickerFilter = GUILayout.TextField(_pickerFilter ?? "", GUILayout.MinWidth(120f));
        GUILayout.FlexibleSpace();
        GUILayout.Label($"Selected: {(_currentBuildingId ?? "(none)")}");
        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            _pickerOpen = false;
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);

        var buildings = configLoader != null ? configLoader.Config?.Buildings : null;
        if (buildings == null || buildings.Length == 0)
        {
            GUILayout.Label("No buildings found. Ensure `GameConfigLoader` is present and loaded.");
            GUILayout.EndArea();
            return;
        }

        _pickerScroll = GUILayout.BeginScrollView(_pickerScroll, GUI.skin.box);
        for (int i = 0; i < buildings.Length; i++)
        {
            var b = buildings[i];
            if (b == null || string.IsNullOrEmpty(b.Id)) continue;

            string name = GetBuildingDisplayName(b);
            if (!PassesFilter(b, name, _pickerFilter)) continue;

            bool selected = _currentBuildingId == b.Id;
            var oldColor = GUI.color;
            if (selected) GUI.color = new Color(0.65f, 0.9f, 1f, 1f);

            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Pick", GUILayout.Width(54f)))
                _currentBuildingId = b.Id;
            GUILayout.Label($"{name}  ({b.Id})", GUILayout.ExpandWidth(true));
            GUILayout.Label($"Price: {b.Price}", GUILayout.Width(90f));
            GUILayout.EndHorizontal();

            if (b.BaseValues != null)
            {
                GUILayout.Label(
                    $"Impact: {b.ImpactSize} | Importance: {b.Importance:0.00} | " +
                    $"Env {b.BaseValues.environment}  Eco {b.BaseValues.economy}  Safe {b.BaseValues.healthSafety}  Cul {b.BaseValues.cultureEdu}");
            }
            else
            {
                GUILayout.Label($"Impact: {b.ImpactSize} | Importance: {b.Importance:0.00}");
            }

            GUILayout.EndVertical();
            GUI.color = oldColor;
        }
        GUILayout.EndScrollView();

        GUILayout.EndArea();

        GUI.matrix = oldMatrix;
    }

    private string GetBuildingDisplayName(BuildingDefinition b)
    {
        if (b == null) return "(null)";
        if (localization != null && !string.IsNullOrEmpty(b.LocalizationKey))
        {
            string s = localization.GetString(b.LocalizationKey);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return string.IsNullOrEmpty(b.Id) ? "(unnamed)" : b.Id;
    }

    private static bool PassesFilter(BuildingDefinition b, string displayName, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        filter = filter.Trim();
        if (b == null) return false;

        if (!string.IsNullOrEmpty(displayName) && displayName.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.Id) && b.Id.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.Category) && b.Category.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.ImpactSize) && b.ImpactSize.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    private static bool WasPressedThisFrame(Keyboard keyboard, Key key)
    {
        if (keyboard == null) return false;
        // Generic lookup so whichever Key you set in the inspector will toggle reliably.
        var control = keyboard[key];
        return control != null && control.wasPressedThisFrame;
    }

    private RectTransform FindMarkerRecursive(string instanceName)
    {
        if (buildingSpawner == null || buildingSpawner.ContentRoot == null) return null;
        var all = buildingSpawner.ContentRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == instanceName)
                return all[i] as RectTransform;
        }
        return null;
    }
}

