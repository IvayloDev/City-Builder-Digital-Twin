using UnityEngine;
using UnityEngine.InputSystem;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Mouse / pen / touch positions for code that hit-tests the table while the projection rig is on.
    /// The rig renders the table into a texture, so the UI camera works in table-texture pixels while the
    /// pointer reports window pixels of the warped projector output. These helpers undo the warp; with no rig
    /// active they return the position unchanged.
    /// </summary>
    public static class ProjectedPointer
    {
        public const string ProcessorName = "ProjectedPointer";

        /// <summary>Window pixel position -> the same point in the table camera's pixel space. Off every projector frame -> far off-table.</summary>
        public static Vector2 ToTable(Vector2 windowPosition)
        {
            var rig = ProjectionOutputRig.Active;
            return rig != null ? rig.WindowToTablePixels(windowPosition) : windowPosition;
        }

        /// <summary>Table-camera pixel position -> where it shows in the main window (for IMGUI overlays drawn over the output).</summary>
        public static Vector2 FromTable(Vector2 tablePixels)
        {
            var rig = ProjectionOutputRig.Active;
            return rig != null ? rig.TablePixelsToWindow(tablePixels) : tablePixels;
        }

        /// <summary>Current mouse position in table-camera pixels (see <see cref="ToTable"/>).</summary>
        public static Vector2 MousePosition => Mouse.current != null ? ToTable(Mouse.current.position.ReadValue()) : Vector2.zero;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => InputSystem.RegisterProcessor<ProjectedPointerProcessor>(ProcessorName);
    }

    /// <summary>
    /// Input processor added to the UI module's pointer bindings while the rig is on, so EventSystem clicks and
    /// drags (buttons, the building palette) land where they appear on the projected table.
    /// </summary>
    public class ProjectedPointerProcessor : InputProcessor<Vector2>
    {
        public override Vector2 Process(Vector2 value, InputControl control) => ProjectedPointer.ToTable(value);
    }
}
