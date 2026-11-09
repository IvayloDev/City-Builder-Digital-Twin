using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// One physical table configuration (how many projectors, how they split the table). Each setup keeps its
    /// own calibration file, so switching between test rigs never overwrites the other rig's calibration.
    /// Phase 6 (input calibration) hangs its sensor list off the same setup.
    /// </summary>
    public sealed class TableSetup
    {
        private readonly Func<List<ProjectorCalibration>> _defaultProjectors;
        private readonly int[] _playAreas;

        public TableSetup(string id, string displayName, string description, int[] playAreas, Func<List<ProjectorCalibration>> defaultProjectors)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            _playAreas = playAreas;
            _defaultProjectors = defaultProjectors;
        }

        /// <summary>Short stable id, used in file names and the -tableSetup launch option.</summary>
        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }

        /// <summary>True if the game instance with this id (GameInstanceRoot.InstanceId, 0-3) runs in this setup.</summary>
        public bool RunsPlayArea(int instanceId) => Array.IndexOf(_playAreas, instanceId) >= 0;

        /// <summary>Play areas as people count them (1-4), for logs and menus.</summary>
        public string PlayAreaList
        {
            get
            {
                var names = new string[_playAreas.Length];
                for (int i = 0; i < names.Length; i++) names[i] = (_playAreas[i] + 1).ToString();
                return string.Join(", ", names);
            }
        }

        /// <summary>Placeholder projector layout used until this setup is calibrated on site.</summary>
        public List<ProjectorCalibration> CreateDefaultProjectors() => _defaultProjectors();

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The built-in table setups, plus which one is active: a switch made in-game this run wins, then the
    /// -tableSetup launch option, then the choice staff last made (remembered in a small text file), then the rig's default.
    /// </summary>
    public static class TableSetups
    {
        public const string LaunchOption = "-tableSetup";
        public const string ChoiceFileName = "table_setup.txt";

        public static readonly TableSetup Pilot = new TableSetup(
            "pilot",
            "1 screen, 1 play area",
            "Pilot: only play area 1 (the bottom game, camera on port 3333) runs; one projector centred on it. First setup, to prove the concept.",
            new[] { 0 },
            ProjectorCalibration.DefaultSingleProjectorLayout);

        public static readonly TableSetup TestRig = new TableSetup(
            "testrig",
            "2 screens, 4 play areas",
            "Test rig: all four play areas; two projectors over the whole table, split along the diagonal gap between the games (A: top + left, B: bottom + right).",
            new[] { 0, 1, 2, 3 },
            ProjectorCalibration.DefaultTwoProjectorLayout);

        private static string _sessionChoice;

        // Fresh each Play session even with domain reload disabled; survives scene reloads within one run.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSessionChoice() => _sessionChoice = null;

        /// <summary>Setup id switched to in-game during this run, or null.</summary>
        public static string SessionChoice => _sessionChoice;

        /// <summary>Record an in-game switch so the scene reload picks it up even when a launch option names another setup.</summary>
        public static void ChooseForSession(TableSetup setup) => _sessionChoice = setup?.Id;

        public static IReadOnlyList<TableSetup> All { get; } = new[] { Pilot, TestRig };

        /// <summary>Setup with this id (case-insensitive), or null.</summary>
        public static TableSetup Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            foreach (var s in All)
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Value of the -tableSetup launch option ("-tableSetup testrig" or "-tableSetup=testrig"), or null.</summary>
        public static string FromLaunchOptions(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, LaunchOption, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : null;
                if (a != null && a.StartsWith(LaunchOption + "=", StringComparison.OrdinalIgnoreCase))
                    return a.Substring(LaunchOption.Length + 1);
            }
            return null;
        }

        /// <summary>
        /// Pick the active setup. Unknown ids are skipped with a warning; <paramref name="source"/> says which input won,
        /// for display in calibration mode.
        /// </summary>
        public static TableSetup Resolve(string sessionId, string[] launchArgs, string savedId, string defaultId, out string source)
        {
            var setup = Find(sessionId);
            if (setup != null) { source = "switched in game"; return setup; }

            string fromLaunch = FromLaunchOptions(launchArgs);
            setup = Find(fromLaunch);
            if (setup != null) { source = $"launch option {LaunchOption}"; return setup; }
            if (fromLaunch != null) Debug.LogWarning($"[TableSetups] Unknown {LaunchOption} '{fromLaunch}'. Known: {KnownIds()}.");

            setup = Find(savedId);
            if (setup != null) { source = "last choice"; return setup; }

            setup = Find(defaultId);
            if (setup != null) { source = "default"; return setup; }
            if (!string.IsNullOrEmpty(defaultId)) Debug.LogWarning($"[TableSetups] Unknown default setup '{defaultId}'. Known: {KnownIds()}.");

            source = "default";
            return Pilot;
        }

        /// <summary>The setup id staff last chose, or null.</summary>
        public static string ReadSavedChoice(string directory)
        {
            try
            {
                string path = Path.Combine(directory, ChoiceFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableSetups] Could not read the saved setup choice: {e.Message}");
                return null;
            }
        }

        /// <summary>Remember the chosen setup for the next launch. Returns false (and logs) on IO failure.</summary>
        public static bool WriteSavedChoice(string directory, TableSetup setup)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ChoiceFileName), setup.Id);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TableSetups] Could not save the setup choice: {e.Message}");
                return false;
            }
        }

        private static string KnownIds()
        {
            var ids = new string[All.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = All[i].Id;
            return string.Join(", ", ids);
        }
    }
}
