using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using CityTwin.Core;

namespace CityTwin.Calibration
{
    /// <summary>
    /// In-game projection mapping. While enabled, the source camera (Output Camera) renders the whole table
    /// into a square render texture instead of the screen, and one <see cref="ProjectorOutput"/> per projector
    /// draws that texture warped (corner pin / mesh) and cut at the seam into its part of the screen.
    /// Disable this component to get the original unwarped output back (e.g. for mouse testing or NDI/Resolume).
    /// Game code is untouched: everything happens after the table image is rendered.
    /// Also applies the table setup's play areas on scene load (games not in the setup are switched off), even
    /// while the component itself is disabled. Runs early so they are off before they initialise.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class ProjectionOutputRig : MonoBehaviour
    {
        [Tooltip("Camera that renders the table (the Screen Space - Camera canvas camera). Redirected into the table texture while enabled.")]
        [SerializeField] private Camera sourceCamera;

        [Tooltip("Size of the square table render texture. Should match the canvas reference resolution (3840).")]
        [SerializeField] private int tableResolution = 3840;

        [Tooltip("CityTwin/ProjectorWarp. Assigned here so the shader is included in builds.")]
        [SerializeField] private Shader warpShader;

        [Tooltip("Layer used only by the warp meshes and projector cameras. Excluded from the source camera.")]
        [SerializeField] private string outputLayerName = "ProjectionOutput";

        [Tooltip("Warp mesh subdivisions per side. 32 is visually perspective-correct; raise only for extreme keystone.")]
        [Range(4, 128)]
        [SerializeField] private int meshResolution = 32;

        [Tooltip("Table setup used until staff pick one: pilot (1 camera, 1 projector) or testrig (4 stations, 2 projectors). " +
                 "Staff switch in calibration mode (Ctrl+Shift+F4, then O) and the choice is remembered; the launch option -tableSetup <id> overrides both.")]
        [SerializeField] private string defaultSetup = "pilot";

        [Tooltip("Load projector calibration from the active setup's saved calibration file (site, then shipped, then placeholder). Off = use the list below.")]
        [SerializeField] private bool useSavedCalibration = true;

        [Tooltip("Per-projector calibration. In Play mode with Use Saved Calibration on, this shows the live loaded data (edits apply immediately; use Save Calibration to keep them). Empty = the active setup's placeholder layout.")]
        [SerializeField] private List<ProjectorCalibration> projectors = new List<ProjectorCalibration>();

        [Tooltip("One projector (pilot): scale the canvas so the square table image is exactly as wide as the flat view (rig off) at the projector's shape. The projector then shows the same picture as a flat window instead of a narrower, cropped one. Off = the canvas keeps its own scaling into the square image.")]
        [SerializeField] private bool matchFlatViewForSingleProjector = true;

        [Tooltip("Test pattern on every projector (grid, table rim, central piece, seam). Calibration mode drives this while open.")]
        [SerializeField] private TestPattern testPattern = TestPattern.Off;

        private static readonly HashSet<int> ActivatedDisplays = new HashSet<int>();

        private static readonly Vector2 OffTable = new Vector2(-100000f, -100000f);

        private readonly List<ProjectorOutput> _outputs = new List<ProjectorOutput>();
        private CanvasScaler _flatViewScaler;
        private CanvasScaler.ScaleMode _previousScaleMode;
        private float _previousScaleFactor;

        /// <summary>The table image is scaled to the flat view's width (single projector): the frame x 0..1, centred, is exactly the flat picture.</summary>
        public bool UsesFlatView { get; private set; }
        private readonly List<Camera> _backdrops = new List<Camera>();
        private readonly List<int> _hookedPointerBindings = new List<int>();
        private InputAction _hookedPointerAction;

        /// <summary>The enabled rig, if any. Pointer remapping (<see cref="ProjectedPointer"/>) goes through it.</summary>
        public static ProjectionOutputRig Active { get; private set; }
        private RenderTexture _tableTexture;
        private RenderTexture _previousTarget;
        private int _previousCullingMask;
        private bool _redirected;
        private int _outputLayer;
        private TableSetup _setup;
        private string _setupSource;

        public RenderTexture TableTexture => _tableTexture;
        public IReadOnlyList<ProjectorOutput> Outputs => _outputs;
        public List<ProjectorCalibration> Projectors => projectors;

        /// <summary>The table setup in use (chosen once at startup, then by <see cref="SwitchSetup"/>).</summary>
        public TableSetup ActiveSetup => _setup ??= ResolveStartupSetup();

        /// <summary>Why <see cref="ActiveSetup"/> was picked at startup (launch option, last choice, default), or "switched".</summary>
        public string SetupSource => _setupSource;

        /// <summary>The active setup's calibration store.</summary>
        public TableCalibrationStore Store => TableCalibrationStore.For(ActiveSetup);

        public TestPattern Pattern
        {
            get => testPattern;
            set => testPattern = value;
        }

        /// <summary>Raised after the projector outputs were (re)created, e.g. after Reload or Restore. Old outputs are gone.</summary>
        public event System.Action OutputsRebuilt;

        /// <summary>Raised when the sensor mappings or filters in <see cref="Store"/> were replaced or edited, so input picks them up.</summary>
        public event System.Action SensorCalibrationChanged;

        public void NotifySensorCalibrationChanged() => SensorCalibrationChanged?.Invoke();

        // Awake runs even while this component is disabled, so the play areas follow the setup either way.
        private void Awake() => ApplyPlayAreas();

        private void ApplyPlayAreas()
        {
            var setup = ActiveSetup;
            foreach (var root in FindObjectsByType<GameInstanceRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (root.gameObject.scene != gameObject.scene) continue;
                bool run = setup.RunsPlayArea(root.InstanceId);
                if (root.gameObject.activeSelf != run) root.gameObject.SetActive(run);
            }
            Debug.Log($"[ProjectionOutputRig] Setup '{setup.Id}' ({_setupSource}): play areas {setup.PlayAreaList}.", this);
        }

        private void OnEnable()
        {
            if (sourceCamera == null)
            {
                Debug.LogError("[ProjectionOutputRig] No source camera assigned; projection mapping disabled.", this);
                enabled = false;
                return;
            }
            if (warpShader == null) warpShader = Shader.Find("CityTwin/ProjectorWarp");
            if (warpShader == null)
            {
                Debug.LogError("[ProjectionOutputRig] Shader 'CityTwin/ProjectorWarp' not found; projection mapping disabled.", this);
                enabled = false;
                return;
            }
            _outputLayer = LayerMask.NameToLayer(outputLayerName);
            if (_outputLayer < 0)
            {
                Debug.LogError($"[ProjectionOutputRig] Layer '{outputLayerName}' does not exist (add it in Tags and Layers); projection mapping disabled.", this);
                enabled = false;
                return;
            }

            if (useSavedCalibration)
            {
                // Always from disk: after a setup switch (scene reload) no unsaved edits linger in the store.
                var store = Store;
                store.Load();
                projectors = store.Current.projectors;
                Debug.Log($"[ProjectionOutputRig] Setup '{ActiveSetup.Id}' ({_setupSource}): using {store.CurrentSource} calibration ({projectors.Count} projector(s)).", this);
            }
            if (projectors == null || projectors.Count == 0)
                projectors = ActiveSetup.CreateDefaultProjectors();

            CreateTableTexture();
            MatchFlatView();
            RedirectSourceCamera(_outputLayer);
            CreateOutputs();
            Active = this;
            HookUiPointer();
        }

        private void OnDisable()
        {
            RestoreCanvasScaler();
            UnhookUiPointer();
            if (Active == this) Active = null;
            DestroyOutputs();

            if (_redirected && sourceCamera != null)
            {
                sourceCamera.targetTexture = _previousTarget;
                sourceCamera.cullingMask = _previousCullingMask;
            }
            _redirected = false;

            if (_tableTexture != null)
            {
                _tableTexture.Release();
                Destroy(_tableTexture);
                _tableTexture = null;
            }
        }

        private void Update()
        {
            for (int i = 0; i < _outputs.Count; i++)
                _outputs[i].Pattern = testPattern;
        }

        /// <summary>Rebuild every projector's mesh after editing control points or mesh offsets.</summary>
        public void MarkAllDirty()
        {
            foreach (var o in _outputs)
                if (o != null) o.MarkDirty();
        }

        /// <summary>
        /// A main-window pixel position on the projector output -> the same spot in the table camera's pixel space
        /// (what UI raycasts and RectTransformUtility expect while the table renders into the texture). Points on no
        /// projector frame, or on the cut side of a seam, map far off the table so they hit nothing.
        /// </summary>
        public Vector2 WindowToTablePixels(Vector2 windowPosition)
        {
            if (_tableTexture == null) return windowPosition;
            for (int i = 0; i < _outputs.Count; i++)
            {
                var o = _outputs[i];
                if (o == null || o.OutputCamera == null || o.OutputCamera.targetDisplay != 0) continue;
                Rect r = o.OutputCamera.pixelRect;
                if (!r.Contains(windowPosition)) continue;
                var projectorUv = new Vector2((windowPosition.x - r.x) / r.width, (windowPosition.y - r.y) / r.height);
                Vector2 uv = ProjectionMath.ProjectorUvToTableUv(o.Calibration, o.ProjectorToTable, projectorUv);
                if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y)) continue; // degenerate mapping: treat as off-table
                if (!o.Calibration.KeepsTablePoint(uv)) continue;
                return new Vector2(uv.x * _tableTexture.width, uv.y * _tableTexture.height);
            }
            return OffTable;
        }

        /// <summary>Inverse of <see cref="WindowToTablePixels"/>: where a table-camera pixel shows in the main window (off-screen if no projector shows it).</summary>
        public Vector2 TablePixelsToWindow(Vector2 tablePixels)
        {
            if (_tableTexture == null) return tablePixels;
            var uv = new Vector2(tablePixels.x / _tableTexture.width, tablePixels.y / _tableTexture.height);
            for (int i = 0; i < _outputs.Count; i++)
            {
                var o = _outputs[i];
                if (o == null || o.OutputCamera == null || o.OutputCamera.targetDisplay != 0) continue;
                if (!o.Calibration.KeepsTablePoint(uv)) continue;
                Vector2 p = ProjectionMath.TableUvToProjectorUv(o.Calibration, o.TableToProjector, uv);
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) continue;
                if (p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f) continue;
                Rect r = o.OutputCamera.pixelRect;
                return new Vector2(r.x + p.x * r.width, r.y + p.y * r.height);
            }
            return OffTable;
        }

        /// <summary>Table uv -> the world point on a UI rect's plane. Same pixel space as <see cref="WindowToTablePixels"/>.</summary>
        public bool TryTableUvToWorld(Vector2 tableUv, RectTransform plane, out Vector3 world)
        {
            world = default;
            if (sourceCamera == null || plane == null) return false;
            var pixels = new Vector2(tableUv.x * sourceCamera.pixelWidth, tableUv.y * sourceCamera.pixelHeight);
            return RectTransformUtility.ScreenPointToWorldPointInRectangle(plane, pixels, sourceCamera, out world);
        }

        /// <summary>World point -> table uv.</summary>
        public bool TryWorldToTableUv(Vector3 world, out Vector2 tableUv)
        {
            tableUv = default;
            if (sourceCamera == null) return false;
            tableUv = sourceCamera.WorldToViewportPoint(world);
            return true;
        }

        /// <summary>Where a table point shows in the main window (Input System pixels), far off-screen if it doesn't.</summary>
        public Vector2 TableUvToWindow(Vector2 tableUv)
        {
            if (_tableTexture == null) return OffTable;
            return TablePixelsToWindow(new Vector2(tableUv.x * _tableTexture.width, tableUv.y * _tableTexture.height));
        }

        /// <summary>Which projector shows a table point and where in its frame. False if none does.</summary>
        public bool TryTableUvToProjector(Vector2 tableUv, out int projector, out Vector2 projectorUv)
        {
            for (int i = 0; i < _outputs.Count; i++)
            {
                var o = _outputs[i];
                if (o == null || !o.Calibration.KeepsTablePoint(tableUv)) continue;
                Vector2 p = ProjectionMath.TableUvToProjectorUv(o.Calibration, o.TableToProjector, tableUv);
                if (p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f) continue;
                projector = i;
                projectorUv = p;
                return true;
            }
            projector = -1;
            projectorUv = default;
            return false;
        }

        // EventSystem clicks/drags read the UI module's Point action: add the remapping processor to its bindings.
        private void HookUiPointer()
        {
            var module = FindFirstObjectByType<InputSystemUIInputModule>();
            var action = module != null && module.point != null ? module.point.action : null;
            if (action == null) return;
            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                if (bindings[i].isComposite || bindings[i].isPartOfComposite) continue;
                string processors = string.IsNullOrEmpty(bindings[i].processors)
                    ? ProjectedPointer.ProcessorName
                    : bindings[i].processors + "," + ProjectedPointer.ProcessorName;
                action.ApplyBindingOverride(i, new InputBinding { overrideProcessors = processors });
                _hookedPointerBindings.Add(i);
            }
            _hookedPointerAction = action;
        }

        private void UnhookUiPointer()
        {
            if (_hookedPointerAction != null)
                foreach (int i in _hookedPointerBindings) _hookedPointerAction.RemoveBindingOverride(i);
            _hookedPointerBindings.Clear();
            _hookedPointerAction = null;
        }

        private void OnValidate()
        {
            // Inspector edits in Play mode take effect immediately.
            if (Application.isPlaying && isActiveAndEnabled) MarkAllDirty();
        }

        /// <summary>
        /// Switch to another table setup: remember the choice and reload the scene, so the setup's play areas and
        /// projector calibration start fresh (games are only switched on or off at scene start, never mid-session).
        /// The choice beats the -tableSetup launch option for the rest of this run and is saved for the next launch.
        /// Unsaved calibration edits are dropped, so callers should save or discard first. Returns false if already active.
        /// </summary>
        public bool SwitchSetup(TableSetup setup)
        {
            if (setup == null || setup == ActiveSetup) return false;
            TableSetups.ChooseForSession(setup);
            TableSetups.WriteSavedChoice(Application.persistentDataPath, setup);
            Debug.Log($"[ProjectionOutputRig] Switching to setup '{setup.Id}': reloading the scene.", this);
            ReloadScene();
            return true;
        }

        private void ReloadScene()
        {
            var scene = gameObject.scene;
            if (scene.buildIndex >= 0)
            {
                SceneManager.LoadScene(scene.buildIndex);
                return;
            }
#if UNITY_EDITOR
            // Scene not enabled in Build Settings: load it by path (Editor only).
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            Debug.LogError($"[ProjectionOutputRig] Scene '{scene.name}' is not in the build; restart to apply the new setup.", this);
#endif
        }

        /// <summary>Save the live projector calibration (with backup of the previous file). Returns false on failure.</summary>
        [ContextMenu("Save Calibration")]
        public bool SaveCalibration()
        {
            var store = Store;
            if (store.Current == null) store.Load();
            store.Current.projectors = projectors;
            bool ok = store.Save(store.Current);
            if (ok) Debug.Log($"[ProjectionOutputRig] Calibration saved to {store.SitePath}", this);
            return ok;
        }

        /// <summary>Discard unsaved changes: reload from disk and rebuild the outputs.</summary>
        [ContextMenu("Reload Calibration")]
        public void ReloadCalibration()
        {
            var store = Store;
            store.Load();
            ApplyCalibration(store.Current);
        }

        /// <summary>Roll back to a backup (see <see cref="TableCalibrationStore.ListBackups"/>) and rebuild the outputs.</summary>
        public bool RestoreBackup(string backupPath, out string error)
        {
            var store = Store;
            if (!store.TryRestoreBackup(backupPath, out error)) return false;
            ApplyCalibration(store.Current);
            return true;
        }

        /// <summary>Roll back to the calibration saved before the current one.</summary>
        [ContextMenu("Restore Previous Calibration")]
        public void RestorePreviousCalibration()
        {
            var backups = Store.ListBackups();
            if (backups.Count == 0) { Debug.LogWarning("[ProjectionOutputRig] No calibration backups yet.", this); return; }
            if (!RestoreBackup(backups[0], out string error))
                Debug.LogError($"[ProjectionOutputRig] Restore failed: {error}", this);
        }

#if UNITY_EDITOR
        [ContextMenu("Copy Saved Calibration To StreamingAssets")]
        private void CopySavedCalibrationToStreamingAssets()
        {
            if (Store.TryCopySiteToShipped(out string error))
                Debug.Log($"[ProjectionOutputRig] Copied to {Store.ShippedPath}; builds now ship with it.", this);
            else
                Debug.LogError($"[ProjectionOutputRig] Copy failed: {error}", this);
        }
#endif

        private TableSetup ResolveStartupSetup()
        {
            return TableSetups.Resolve(TableSetups.SessionChoice, System.Environment.GetCommandLineArgs(),
                TableSetups.ReadSavedChoice(Application.persistentDataPath), defaultSetup, out _setupSource);
        }

        private void ApplyCalibration(TableCalibrationFile file)
        {
            projectors = file.projectors;
            if (projectors == null || projectors.Count == 0)
                projectors = file.projectors = ActiveSetup.CreateDefaultProjectors();
            SensorCalibrationChanged?.Invoke();
            if (!isActiveAndEnabled || _tableTexture == null) return;
            // Projector count or displays may have changed: rebuild rather than patch.
            DestroyOutputs();
            CreateOutputs();
        }

        private void CreateOutputs()
        {
            var displays = new HashSet<int>();
            for (int i = 0; i < projectors.Count; i++)
            {
                CreateOutput(projectors[i], i, _outputLayer);
                displays.Add(Mathf.Clamp(projectors[i].displayIndex, 0, 7));
            }
            foreach (int d in displays) CreateBackdrop(d);
            OutputsRebuilt?.Invoke();
        }

        private void DestroyOutputs()
        {
            foreach (var o in _outputs)
                if (o != null) Destroy(o.gameObject);
            _outputs.Clear();
            foreach (var b in _backdrops)
                if (b != null) Destroy(b.gameObject);
            _backdrops.Clear();
        }

        // Clears a display to black under the projector frames, so letterbox bars never show stale pixels.
        private void CreateBackdrop(int display)
        {
            var go = new GameObject($"Backdrop (display {display})");
            go.transform.SetParent(transform, false);
            var cam = go.AddComponent<Camera>();
            cam.cullingMask = 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.depth = sourceCamera.depth + 0.5f;
            cam.targetDisplay = display;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            _backdrops.Add(cam);
        }

        /// <summary>
        /// The canvas scales with screen size. Rendered into the square table image it would get the square's canvas size
        /// (narrower than a 16:10 screen's), so the outer strips of the game, visible in a flat window, fall off the table
        /// image and the projector shows a cropped, roughly 4:3 picture. With one projector, scale the canvas instead so
        /// the table image spans exactly the flat view's width at the projector's shape.
        /// </summary>
        private void MatchFlatView()
        {
            UsesFlatView = false;
            if (!matchFlatViewForSingleProjector || projectors == null || projectors.Count != 1) return;

            CanvasScaler scaler = null;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera != sourceCamera) continue;
                scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) break;
            }
            if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize
                || scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight)
            {
                Debug.LogWarning("[ProjectionOutputRig] No 'Scale With Screen Size / Match Width Or Height' canvas on the source camera; the table image keeps the canvas's own scaling.", this);
                return;
            }

            float aspect = projectors[0].aspect > 0f ? projectors[0].aspect : 16f / 10f;
            Vector2 reference = scaler.referenceResolution;
            // CanvasScaler's own formula for a screen of this shape (size cancels out): canvas width in canvas units.
            float logWidth = Mathf.Log(aspect / reference.x, 2f), logHeight = Mathf.Log(1f / reference.y, 2f);
            float flatScale = Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, scaler.matchWidthOrHeight));
            float flatWidthUnits = aspect / flatScale;

            _flatViewScaler = scaler;
            _previousScaleMode = scaler.uiScaleMode;
            _previousScaleFactor = scaler.scaleFactor;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = _tableTexture.width / flatWidthUnits;
            UsesFlatView = true;
            Debug.Log($"[ProjectionOutputRig] Table image matches the flat {aspect:F2} view: {flatWidthUnits:F0} canvas units across ({scaler.scaleFactor:F3} px per unit).", this);
        }

        private void RestoreCanvasScaler()
        {
            if (_flatViewScaler != null)
            {
                _flatViewScaler.uiScaleMode = _previousScaleMode;
                _flatViewScaler.scaleFactor = _previousScaleFactor;
            }
            _flatViewScaler = null;
            UsesFlatView = false;
        }

        private void CreateTableTexture()
        {
            int size = Mathf.Clamp(tableResolution, 256, 8192);
            // sRGB in a linear project so the table image is stored like the screen it replaces.
            _tableTexture = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "Projection Table",
                // Each projector shows about half the table at its own (likely lower) resolution: mips avoid shimmer.
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                wrapMode = TextureWrapMode.Clamp,
            };
            _tableTexture.Create();
        }

        private void RedirectSourceCamera(int outputLayer)
        {
            _previousTarget = sourceCamera.targetTexture;
            _previousCullingMask = sourceCamera.cullingMask;
            sourceCamera.targetTexture = _tableTexture;
            sourceCamera.cullingMask &= ~(1 << outputLayer); // never render the warp meshes back into the table
            _redirected = true;
        }

        private void CreateOutput(ProjectorCalibration calibration, int index, int layer)
        {
            ActivateDisplay(calibration.displayIndex);

            var go = new GameObject($"Projector {index} ({calibration.name})");
            go.layer = layer;
            go.transform.SetParent(transform, false);
            // Space the outputs apart so each camera only frames its own mesh.
            go.transform.localPosition = new Vector3(index * 10f, 0f, 0f);

            var output = go.AddComponent<ProjectorOutput>();
            output.Init(calibration, _tableTexture, warpShader, layer, sourceCamera.depth + 1f + index, meshResolution);
            output.Pattern = testPattern;
            _outputs.Add(output);
        }

        private static void ActivateDisplay(int index)
        {
            // Displays can only be activated once and never deactivated; in the Editor there is only one.
            if (index <= 0 || ActivatedDisplays.Contains(index) || index >= Display.displays.Length) return;
            Display.displays[index].Activate();
            ActivatedDisplays.Add(index);
        }
    }
}
