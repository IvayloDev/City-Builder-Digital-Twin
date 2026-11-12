using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>What the warp shader draws. Values match _PatternMode in ProjectorWarp.shader.</summary>
    public enum TestPattern
    {
        /// <summary>Game content only.</summary>
        Off = 0,
        /// <summary>Game content with the calibration pattern on top.</summary>
        Overlay = 1,
        /// <summary>Calibration pattern on black.</summary>
        PatternOnly = 2,
        /// <summary>Full white: see each projector's coverage and where the seam cuts.</summary>
        White = 3,
        /// <summary>Full black: check black level in the overlap.</summary>
        Black = 4,
    }

    /// <summary>
    /// One projector's output: an orthographic camera looking at a warp mesh textured with the table image.
    /// Created and owned by <see cref="ProjectionOutputRig"/>. Rebuilds the mesh when the calibration is marked
    /// dirty or the output aspect changes; seam/mask settings are pushed to the material every frame (cheap).
    /// </summary>
    public class ProjectorOutput : MonoBehaviour
    {
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SeamId = Shader.PropertyToID("_Seam");
        private static readonly int CircleMaskId = Shader.PropertyToID("_CircleMask");
        private static readonly int PatternModeId = Shader.PropertyToID("_PatternMode");
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        public ProjectorCalibration Calibration { get; private set; }
        public Camera OutputCamera => _camera;
        public int Layer => _layer;

        /// <summary>Calibration test pattern drawn over / instead of the game.</summary>
        public TestPattern Pattern { get; set; }

        /// <summary>Colour multiplied into test patterns (not the game), so each projector's coverage and the overlap are visible.</summary>
        public Color Tint { get; set; } = Color.white;

        /// <summary>The table -> projector mapping currently drawn (last valid solve).</summary>
        public Homography TableToProjector => _tableToProjector;

        /// <summary>The projector -> table mapping currently drawn (inverse of <see cref="TableToProjector"/>).</summary>
        public Homography ProjectorToTable => _projectorToTable;

        /// <summary>Output size in pixels (the camera's viewport within its display).</summary>
        public Vector2 PixelSize => _camera != null ? new Vector2(_camera.pixelWidth, _camera.pixelHeight) : Vector2.one;

        private Camera _camera;
        private Mesh _mesh;
        private Material _material;
        private Vector3[] _vertices;
        private Vector2[] _uvs;
        private int _meshResolution;
        private Homography _tableToProjector;
        private Homography _projectorToTable;
        private bool _hasMapping;
        private bool _dirty = true;
        private float _builtAspect = -1f;
        private int _layer;

        public void Init(ProjectorCalibration calibration, Texture tableTexture, Shader shader, int layer, float cameraDepth, int meshResolution)
        {
            Calibration = calibration;
            _layer = layer;
            _meshResolution = Mathf.Clamp(meshResolution, 2, 128);

            var meshGo = new GameObject("Warp Mesh");
            meshGo.layer = layer;
            meshGo.transform.SetParent(transform, false);
            _mesh = new Mesh { name = $"{calibration.name} Warp Mesh" };
            _mesh.MarkDynamic();
            int count = (_meshResolution + 1) * (_meshResolution + 1);
            _vertices = new Vector3[count];
            _uvs = new Vector2[count];
            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            _mesh.triangles = ProjectionMath.BuildGridTriangles(_meshResolution, _meshResolution);
            meshGo.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _material = new Material(shader) { name = $"{calibration.name} Warp" };
            _material.SetTexture(MainTexId, tableTexture);
            var meshRenderer = meshGo.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            _camera = gameObject.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.orthographicSize = 0.5f;
            _camera.nearClipPlane = 0.01f;
            _camera.farClipPlane = 2f;
            _camera.cullingMask = 1 << layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.depth = cameraDepth;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.useOcclusionCulling = false;
            meshGo.transform.localPosition = new Vector3(0f, 0f, 1f);

            ApplyOutputSettings();
            SolveMapping(); // valid before the first pointer sample; LateUpdate builds the mesh
        }

        /// <summary>Call after changing control points or mesh offsets.</summary>
        public void MarkDirty() => _dirty = true;

        private void ApplyOutputSettings()
        {
            int display = Mathf.Clamp(Calibration.displayIndex, 0, 7);
            _camera.targetDisplay = display;
            _camera.rect = ProjectionMath.FitAspect(Calibration.viewport, DisplayPixelSize(display), Calibration.aspect);
        }

        // Pixel size of the window/display this projector draws into (the Game view in the Editor).
        private static Vector2 DisplayPixelSize(int display)
        {
            if (display > 0 && display < Display.displays.Length && Display.displays[display].active)
                return new Vector2(Display.displays[display].renderingWidth, Display.displays[display].renderingHeight);
            return new Vector2(Screen.width, Screen.height);
        }

        private void LateUpdate()
        {
            if (Calibration == null || _camera == null) return;
            ApplyOutputSettings();

            float aspect = _camera.aspect;
            if (_dirty || !Mathf.Approximately(aspect, _builtAspect))
                RebuildMesh(aspect);

            PushMaterialSettings();
        }

        private void RebuildMesh(float aspect)
        {
            _dirty = false;
            _builtAspect = aspect;

            SolveMapping();

            ProjectionMath.BuildWarpVertices(Calibration, _projectorToTable, _meshResolution, _meshResolution, aspect, _vertices, _uvs);
            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            // Vertices can sit anywhere in the frame (or off it); never let bounds culling drop the mesh.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 10f));
        }

        /// <summary>Table &lt;-&gt; projector homographies from the control points. Keeps the last good mapping if the
        /// points are momentarily degenerate (e.g. mid-drag); falls back to full-frame until there is one.</summary>
        private void SolveMapping()
        {
            if (Calibration.TryGetTableToProjector(out Homography h) && h.TryInverse(out Homography inv))
            {
                _tableToProjector = h;
                _projectorToTable = inv;
                _hasMapping = true;
            }
            else if (!_hasMapping)
            {
                Debug.LogWarning($"[ProjectorOutput] '{Calibration.name}' has no valid control points (need 4+ non-degenerate pairs); drawing the whole table full-frame.", this);
                _tableToProjector = Homography.Identity;
                _projectorToTable = Homography.Identity;
                _hasMapping = true;
            }
        }

        private void PushMaterialSettings()
        {
            var c = Calibration;
            _material.SetVector(SeamId, new Vector4(c.seamNormal.x, c.seamNormal.y, c.seamOffset, c.seamEnabled ? 1f : 0f));
            _material.SetVector(CircleMaskId, new Vector4(0.5f, 0.5f, c.circleMaskRadius, Mathf.Max(0f, c.circleMaskFeather)));
            _material.SetFloat(PatternModeId, (float)Pattern);
            _material.SetColor(TintId, Tint);
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }
}
