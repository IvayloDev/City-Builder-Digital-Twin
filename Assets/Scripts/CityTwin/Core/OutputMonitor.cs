using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>
    /// Puts the game's single main window full-screen on the output monitor (the projector) at that monitor's
    /// native resolution. Deliberately avoids Unity's multi-display feature: secondary displays were rendered at
    /// the main monitor's size, so a 1920x1080 projector showed a zoomed, cropped top-left corner.
    ///
    /// Target, in order: launch option -outputMonitor N (1-based index in Unity's display layout; 0 = stay where
    /// the window opened) or -outputMonitor &lt;part of the display name&gt;; otherwise the first display whose work area is
    /// not at the virtual-screen origin (i.e. not the Windows primary display); otherwise the second display in the
    /// layout. Never "the display we are not on": Unity reopens the window where it last was, so that toggled monitors
    /// on every launch. Logs the layout and the choice. No effect in the Editor or with a single monitor.
    /// </summary>
    public class OutputMonitor : MonoBehaviour
    {
        private const string LaunchOption = "-outputMonitor";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.ScriptOnly); // player log: who logged a warning
            if (Application.isEditor) return;
            new GameObject("OutputMonitor").AddComponent<OutputMonitor>();
        }

        private IEnumerator Start()
        {
            var displays = new List<DisplayInfo>();
            Screen.GetDisplayLayout(displays);
            for (int i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                Debug.Log($"[OutputMonitor] Display {i + 1}: '{d.name}' {d.width}x{d.height} workArea {d.workArea}");
            }
            DisplayInfo current = Screen.mainWindowDisplayInfo;
            Debug.Log($"[OutputMonitor] Window opened on '{current.name}' {current.width}x{current.height}.");

            string option = ReadLaunchOption();
            if (!TryPickTarget(displays, option, out DisplayInfo target, out string why))
            {
                Debug.Log($"[OutputMonitor] Keeping the window where it is ({why}).");
                Destroy(gameObject);
                yield break;
            }

            yield return Screen.MoveMainWindowTo(target, Vector2Int.zero);
            Screen.SetResolution(target.width, target.height, FullScreenMode.FullScreenWindow);
            yield return null;
            yield return null;
            Debug.Log($"[OutputMonitor] Main window on '{target.name}' {target.width}x{target.height} ({why}); screen now {Screen.width}x{Screen.height}.");
            Destroy(gameObject);
        }

        private static bool TryPickTarget(List<DisplayInfo> displays, string option, out DisplayInfo target, out string why)
        {
            target = default;
            if (displays.Count < 2) { why = "single display"; return false; }

            if (!string.IsNullOrEmpty(option))
            {
                if (int.TryParse(option, out int index))
                {
                    if (index <= 0) { why = $"{LaunchOption} {index}"; return false; }
                    if (index <= displays.Count) { target = displays[index - 1]; why = $"{LaunchOption} {index}"; return true; }
                    Debug.LogWarning($"[OutputMonitor] {LaunchOption} {index}: only {displays.Count} display(s); using the default rule.");
                }
                else
                {
                    foreach (var d in displays)
                        if (!string.IsNullOrEmpty(d.name) && d.name.IndexOf(option, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        { target = d; why = $"{LaunchOption} '{option}'"; return true; }
                    Debug.LogWarning($"[OutputMonitor] No display name contains '{option}'; using the default rule.");
                }
            }

            // The Windows primary display sits at the virtual-screen origin; the projector is any other one.
            foreach (var d in displays)
                if (d.workArea.x != 0 || d.workArea.y != 0) { target = d; why = "first non-primary display"; return true; }

            target = displays[1];
            why = "second display in the layout";
            return true;
        }

        private static string ReadLaunchOption()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, LaunchOption, System.StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
                if (a.StartsWith(LaunchOption + "=", System.StringComparison.OrdinalIgnoreCase)) return a.Substring(LaunchOption.Length + 1);
            }
            return null;
        }
    }
}
