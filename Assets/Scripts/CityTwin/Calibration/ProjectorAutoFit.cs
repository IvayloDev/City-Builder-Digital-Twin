using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;

namespace CityTwin.Calibration
{
    /// <summary>
    /// While the projector still runs on the built-in placeholder layout (nothing calibrated or saved yet),
    /// frame it around the game content that is actually on the table: the union of the visible UI under the
    /// running game instance(s), widened to the projector's aspect so nothing is stretched. Without this the
    /// placeholder's guessed window can crop the game. A saved (site/shipped) calibration is never touched,
    /// and calibration mode starts from the fitted frame. Logs the chosen frame to the player log.
    /// </summary>
    public class ProjectorAutoFit : MonoBehaviour
    {
        private const float FullscreenCoverage = 0.9f; // a graphic this big in both axes is a backdrop/overlay: ignore it
        private const float Margin = 1.02f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => new GameObject("ProjectorAutoFit").AddComponent<ProjectorAutoFit>();

        private IEnumerator Start()
        {
            yield return null; // let the rig redirect the camera and the canvas lay out at table size
            yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            Fit();
            Destroy(gameObject);
        }

        private static void Fit()
        {
            var rig = ProjectionOutputRig.Active;
            if (rig == null) return; // rig disabled: flat output, nothing to frame
            if (rig.Store.CurrentSource != TableCalibrationStore.Source.BuiltInDefault)
            {
                Debug.Log($"[ProjectorAutoFit] Using the {rig.Store.CurrentSource} calibration; no auto-fit.");
                return;
            }
            if (rig.Projectors == null || rig.Projectors.Count != 1) return; // only the single-projector pilot
            if (rig.UsesFlatView)
            {
                // The default frame already is the flat view; framing the graphics would cut it again.
                var c = rig.Projectors[0];
                DumpTableTexture(rig);
                Debug.Log($"[ProjectorAutoFit] Flat view: projector frame x {c.tablePoints[0].x:F3}..{c.tablePoints[1].x:F3}, y {c.tablePoints[0].y:F3}..{c.tablePoints[2].y:F3} (aspect {c.aspect:F2}); no content fit.");
                return;
            }

            bool any = false;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            var corners = new Vector3[4];
            foreach (var root in FindObjectsByType<GameInstanceRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                foreach (var g in root.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.isActiveAndEnabled || g.color.a <= 0.01f) continue;
                    if (IsStandInPin(g.transform, root.transform)) continue; // a tile lying on the table, not game layout
                    g.rectTransform.GetWorldCorners(corners);
                    Vector2 gMin = new Vector2(float.MaxValue, float.MaxValue), gMax = new Vector2(float.MinValue, float.MinValue);
                    bool ok = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!rig.TryWorldToTableUv(corners[i], out Vector2 uv)) { ok = false; break; }
                        gMin = Vector2.Min(gMin, uv);
                        gMax = Vector2.Max(gMax, uv);
                    }
                    if (!ok) continue;
                    if (gMax.x <= 0f || gMax.y <= 0f || gMin.x >= 1f || gMin.y >= 1f) continue; // entirely off the table image
                    gMin = Vector2.Max(gMin, Vector2.zero); // only the part on the table image can be projected
                    gMax = Vector2.Min(gMax, Vector2.one);
                    if (gMax.x - gMin.x > FullscreenCoverage && gMax.y - gMin.y > FullscreenCoverage) continue;
                    min = Vector2.Min(min, gMin);
                    max = Vector2.Max(max, gMax);
                    any = true;
                }
            }
            if (!any)
            {
                Debug.LogWarning("[ProjectorAutoFit] No visible game content found; keeping the placeholder frame.");
                return;
            }

            var cal = rig.Projectors[0];
            float aspect = cal.aspect > 0f ? cal.aspect : 16f / 10f; // table texture is square, so uv aspect == pixel aspect
            Vector2 center = (min + max) * 0.5f;
            Vector2 size = (max - min) * Margin;
            if (size.x / Mathf.Max(size.y, 1e-4f) < aspect) size.x = size.y * aspect;
            else size.y = size.x / aspect;

            float l = center.x - size.x * 0.5f, r = center.x + size.x * 0.5f;
            float b = center.y - size.y * 0.5f, t = center.y + size.y * 0.5f;
            cal.tablePoints = new[] { new Vector2(l, b), new Vector2(r, b), new Vector2(r, t), new Vector2(l, t) };
            cal.projectorPoints = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            cal.meshColumns = 0;
            cal.meshRows = 0;
            cal.meshOffsets = null;
            rig.MarkAllDirty();

            DumpTableTexture(rig);
            Debug.Log($"[ProjectorAutoFit] Content uv x {min.x:F3}..{max.x:F3}, y {min.y:F3}..{max.y:F3} -> " +
                      $"projector frame x {l:F3}..{r:F3}, y {b:F3}..{t:F3} (aspect {aspect:F2}).");
        }

        private static bool IsStandInPin(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
                if (t.name.StartsWith("SimTile_")) return true;
            return false;
        }

        /// <summary>Saves the table render texture as table_texture.jpg in the persistent data folder, for framing diagnostics.</summary>
        private static void DumpTableTexture(ProjectionOutputRig rig)
        {
            var rt = rig.TableTexture;
            if (rt == null) return;
            var previous = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                string path = System.IO.Path.Combine(Application.persistentDataPath, "table_texture.jpg");
                System.IO.File.WriteAllBytes(path, tex.EncodeToJPG(85));
                Debug.Log($"[ProjectorAutoFit] Table image ({rt.width}x{rt.height}) saved to {path}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[ProjectorAutoFit] Could not save table image: {e.Message}"); }
            finally
            {
                RenderTexture.active = previous;
                Destroy(tex);
            }
        }
    }
}
