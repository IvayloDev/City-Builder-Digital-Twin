using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

/// <summary>
/// Loads StreamingAssets/road_layout.json once per session and caches the raw JSON for
/// every consumer (RoadNetworkEditor applies it, HubLayoutManager peeks the pinned preset).
/// Desktop and editor read straight from disk; WebGL must fetch over HTTP because
/// Application.streamingAssetsPath is a URL there - File.Exists on it is always false,
/// which is why builds used to silently fall back to the default random map.
/// </summary>
public static class RoadLayoutStore
{
    /// <summary>True once the load attempt finished, successful or not. Json stays null when no layout exists.</summary>
    public static bool Resolved { get; private set; }

    /// <summary>Raw road_layout.json text, or null when no saved layout is available.</summary>
    public static string Json { get; private set; }

    private static bool _loading;

    public static string SharedPath => Path.Combine(Application.streamingAssetsPath, "road_layout.json");

    /// <summary>Yield until the layout JSON is fetched (or known missing). Safe to yield from multiple callers.</summary>
    public static IEnumerator EnsureLoaded()
    {
        if (Resolved) yield break;
        if (_loading)
        {
            while (!Resolved) yield return null;
            yield break;
        }
        _loading = true;

#if UNITY_WEBGL && !UNITY_EDITOR
        string url = SharedPath.Replace("\\", "/");
        using (var req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
                Json = req.downloadHandler.text;
            else
                Debug.LogWarning($"[RoadLayoutStore] road_layout.json fetch failed: {url} ({req.error}) - using default map.");
        }
#else
        Json = ReadFromDisk();
#endif
        Resolved = true;
        _loading = false;
    }

    /// <summary>Keep the cache in sync after an in-session save so restarts reload the new layout.</summary>
    public static void UpdateCache(string json)
    {
        Json = json;
        Resolved = true;
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    private static string ReadFromDisk()
    {
        try
        {
            if (File.Exists(SharedPath)) return File.ReadAllText(SharedPath);

            // Legacy per-instance files (road_layout_<name>.json): all quadrants share the same
            // map, so the first one found is a valid layout for every copy.
            var candidates = Directory.GetFiles(Application.streamingAssetsPath, "road_layout_*.json");
            if (candidates.Length > 0)
            {
                System.Array.Sort(candidates);
                return File.ReadAllText(candidates[0]);
            }
        }
        catch { /* StreamingAssets may be missing entirely; default map */ }
        return null;
    }
#endif
}
