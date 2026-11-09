using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Loads and saves one table setup's calibration file (table_calibration.&lt;setup id&gt;.json).
    /// Load order: the site calibration (persistentDataPath, written by staff) -> the shipped default
    /// (StreamingAssets, baked into the build from a known-good site calibration) -> built-in placeholder.
    /// Every save first copies the previous site file into a timestamped backup (per setup), so a bad calibration
    /// can always be rolled back. Writes go through a temp file so a crash never leaves a half-written file.
    /// Desktop only (the table runs as a Windows build); WebGL builds just get the built-in placeholder.
    /// </summary>
    public class TableCalibrationStore
    {
        public const string BackupFolderName = "table_calibration_backups";
        public const int DefaultMaxBackups = 30;

        public enum Source { None, Site, Shipped, Backup, BuiltInDefault }

        private static readonly Dictionary<string, TableCalibrationStore> Stores = new Dictionary<string, TableCalibrationStore>();

        // Fresh state each Play session even with domain reload disabled (Enter Play Mode Options).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStores() => Stores.Clear();

        /// <summary>The store for a setup, using the standard player paths. One per setup per process; shared by output and input calibration.</summary>
        public static TableCalibrationStore For(TableSetup setup)
        {
            if (!Stores.TryGetValue(setup.Id, out var store))
            {
                store = new TableCalibrationStore(setup, Application.persistentDataPath, Application.streamingAssetsPath);
                Stores[setup.Id] = store;
            }
            return store;
        }

        public static string FileNameFor(TableSetup setup) => $"table_calibration.{setup.Id}.json";

        public TableSetup Setup { get; }
        public string SitePath { get; }
        public string ShippedPath { get; }
        public string BackupDirectory { get; }
        public int MaxBackups { get; set; } = DefaultMaxBackups;

        /// <summary>The calibration currently in use (after the last Load / Save / restore). Never null after Load.</summary>
        public TableCalibrationFile Current { get; private set; }
        public Source CurrentSource { get; private set; }

        public TableCalibrationStore(TableSetup setup, string siteDirectory, string shippedDirectory)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            SitePath = Path.Combine(siteDirectory, FileNameFor(setup));
            ShippedPath = Path.Combine(shippedDirectory, FileNameFor(setup));
            BackupDirectory = Path.Combine(siteDirectory, BackupFolderName, setup.Id);
        }

        /// <summary>Load the best available calibration into <see cref="Current"/>. Always succeeds (falls back to the placeholder).</summary>
        public TableCalibrationFile Load()
        {
            if (TryReadFile(SitePath, out var site, out string siteError))
                return SetCurrent(site, Source.Site);
            if (siteError != null)
                Debug.LogError($"[TableCalibrationStore] Site calibration unusable ({siteError}): {SitePath}. Falling back; restore a backup from calibration mode.");

            if (TryReadFile(ShippedPath, out var shipped, out string shippedError))
                return SetCurrent(shipped, Source.Shipped);
            if (shippedError != null)
                Debug.LogError($"[TableCalibrationStore] Shipped calibration unusable ({shippedError}): {ShippedPath}.");

            return SetCurrent(TableCalibrationFile.CreateDefault(Setup), Source.BuiltInDefault);
        }

        /// <summary>Save as the site calibration, backing up the previous one first. Returns false (and logs) on IO failure.</summary>
        public bool Save(TableCalibrationFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            try
            {
                file.version = TableCalibrationFile.CurrentVersion;
                file.setup = Setup.Id;
                file.savedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Directory.CreateDirectory(Path.GetDirectoryName(SitePath));
                if (File.Exists(SitePath)) BackupCurrentSiteFile();
                WriteAtomic(SitePath, file.ToJson());
                SetCurrent(file, Source.Site);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TableCalibrationStore] Save failed: {e.Message} ({SitePath})");
                return false;
            }
        }

        /// <summary>Backup files, newest first.</summary>
        public List<string> ListBackups()
        {
            var list = new List<string>();
            if (!Directory.Exists(BackupDirectory)) return list;
            list.AddRange(Directory.GetFiles(BackupDirectory, "*.json"));
            // Timestamped names sort chronologically.
            list.Sort(StringComparer.Ordinal);
            list.Reverse();
            return list;
        }

        /// <summary>Make a backup the site calibration again (the replaced site file is itself backed up first).</summary>
        public bool TryRestoreBackup(string backupPath, out string error)
        {
            if (!TryReadFile(backupPath, out var file, out error))
            {
                error ??= "backup not found";
                return false;
            }
            if (!Save(file)) { error = "could not write the site calibration"; return false; }
            CurrentSource = Source.Backup;
            return true;
        }

        /// <summary>Copy the current site calibration into StreamingAssets so new builds ship with it (Editor workflow).</summary>
        public bool TryCopySiteToShipped(out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(SitePath)) { error = "no site calibration saved yet"; return false; }
                Directory.CreateDirectory(Path.GetDirectoryName(ShippedPath));
                File.Copy(SitePath, ShippedPath, true);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private TableCalibrationFile SetCurrent(TableCalibrationFile file, Source source)
        {
            if (!string.IsNullOrEmpty(file.setup) && file.setup != Setup.Id)
                Debug.LogWarning($"[TableCalibrationStore] {source} file for setup '{Setup.Id}' says it belongs to '{file.setup}' (copied by hand?). Using it anyway.");
            Current = file;
            CurrentSource = source;
            return file;
        }

        // error stays null when the file simply doesn't exist (not a problem worth logging).
        private static bool TryReadFile(string path, out TableCalibrationFile file, out string error)
        {
            file = null;
            error = null;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                return TableCalibrationFile.TryFromJson(File.ReadAllText(path), out file, out error);
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private void BackupCurrentSiteFile()
        {
            Directory.CreateDirectory(BackupDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string target = Path.Combine(BackupDirectory, $"table_calibration_{stamp}.json");
            for (int n = 1; File.Exists(target); n++)
                target = Path.Combine(BackupDirectory, $"table_calibration_{stamp}_{n}.json");
            File.Copy(SitePath, target);
            PruneBackups();
        }

        private void PruneBackups()
        {
            var backups = ListBackups();
            for (int i = Mathf.Max(1, MaxBackups); i < backups.Count; i++)
                File.Delete(backups[i]);
        }

        private static void WriteAtomic(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
