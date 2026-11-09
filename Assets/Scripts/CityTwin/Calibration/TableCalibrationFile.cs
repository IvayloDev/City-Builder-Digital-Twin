using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>One sensor's saved mapping (raw reacTIVision coordinates -> table uv).</summary>
    [Serializable]
    public class SensorCalibration
    {
        [Tooltip("Matches the game instance / OSC source this sensor feeds (0-3).")]
        public int sensorIndex;
        public SensorMapping mapping = new SensorMapping();
        [Tooltip("RMS error of the last fit in table uv, for display only.")]
        public double rmsError;
        public int pointCount;
        [Tooltip("Jitter filter for this sensor. Kept when the fit is cleared; a new camera position doesn't change how noisy the tracking is.")]
        public SensorFilterSettings filter = new SensorFilterSettings();
        [Tooltip("The projector lighting this play area was moved after this fit, so the fit should be checked. The fit stays in use.")]
        public bool projectorMovedSinceFit;

        /// <summary>False until a capture has produced a mapping; raw coordinates are used until then.</summary>
        public bool IsFitted => pointCount >= 4 && mapping != null;

        public void ClearFit()
        {
            mapping = new SensorMapping();
            rmsError = 0;
            pointCount = 0;
            projectorMovedSinceFit = false;
        }
    }

    /// <summary>
    /// Everything calibrated on site for one table, saved as a single JSON file:
    /// projector warp/seam (output) and sensor mappings (input, filled in by phase 6).
    /// JsonUtility-friendly: public fields and serializable types only.
    /// </summary>
    [Serializable]
    public class TableCalibrationFile
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        [Tooltip("Id of the table setup this calibration belongs to (see TableSetups). Informational; the file name decides which setup loads it.")]
        public string setup;
        [Tooltip("UTC time of the save, ISO 8601. Informational.")]
        public string savedAtUtc;
        [Tooltip("Free text, e.g. who calibrated and why.")]
        public string note;
        public List<ProjectorCalibration> projectors = new List<ProjectorCalibration>();
        public List<SensorCalibration> sensors = new List<SensorCalibration>();

        /// <summary>Uncalibrated placeholder for a setup.</summary>
        public static TableCalibrationFile CreateDefault(TableSetup setup)
        {
            return new TableCalibrationFile { setup = setup.Id, projectors = setup.CreateDefaultProjectors() };
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        /// <summary>Parse and validate. Returns false with a reason when the JSON is unusable.</summary>
        public static bool TryFromJson(string json, out TableCalibrationFile file, out string error)
        {
            file = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "file is empty"; return false; }
            try
            {
                file = JsonUtility.FromJson<TableCalibrationFile>(json);
            }
            catch (Exception e)
            {
                error = "invalid JSON: " + e.Message;
                return false;
            }
            if (file == null) { error = "invalid JSON"; return false; }
            if (file.version > CurrentVersion)
            {
                error = $"saved by a newer build (version {file.version}, this build reads up to {CurrentVersion})";
                file = null;
                return false;
            }
            if (file.projectors == null) file.projectors = new List<ProjectorCalibration>();
            if (file.sensors == null) file.sensors = new List<SensorCalibration>();
            foreach (var sensor in file.sensors)
            {
                if (sensor == null) continue;
                if (sensor.filter == null) sensor.filter = new SensorFilterSettings();
                sensor.filter.Sanitize();
            }
            for (int i = 0; i < file.projectors.Count; i++)
            {
                var p = file.projectors[i];
                if (p == null) { error = $"projector {i} is missing"; file = null; return false; }
                if (!p.TryGetTableToProjector(out _))
                {
                    error = $"projector {i} ('{p.name}') has no valid control points";
                    file = null;
                    return false;
                }
            }
            error = null;
            return true;
        }

        /// <summary>Deep copy (via JSON), e.g. to keep an untouched copy for Revert while editing.</summary>
        public TableCalibrationFile Clone()
        {
            return JsonUtility.FromJson<TableCalibrationFile>(JsonUtility.ToJson(this));
        }

        public SensorCalibration FindSensor(int sensorIndex)
        {
            foreach (var s in sensors)
                if (s != null && s.sensorIndex == sensorIndex) return s;
            return null;
        }

        public SensorCalibration GetOrAddSensor(int sensorIndex)
        {
            var sensor = FindSensor(sensorIndex);
            if (sensor != null) return sensor;
            sensor = new SensorCalibration { sensorIndex = sensorIndex };
            sensors.Add(sensor);
            return sensor;
        }
    }
}
