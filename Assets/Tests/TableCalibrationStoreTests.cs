#if UNITY_INCLUDE_TESTS
using System.IO;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class TableCalibrationStoreTests
    {
        private string _root;
        private string _siteDir;
        private string _shippedDir;
        private string _shippedPath;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Application.temporaryCachePath, "CalibrationStoreTests_" + System.Guid.NewGuid().ToString("N"));
            _siteDir = Path.Combine(_root, "site");
            _shippedDir = Path.Combine(_root, "shipped");
            _shippedPath = Path.Combine(_shippedDir, TableCalibrationStore.FileNameFor(TableSetups.TestRig));
            Directory.CreateDirectory(_siteDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private TableCalibrationStore NewStore() => NewStore(TableSetups.TestRig);
        private TableCalibrationStore NewStore(TableSetup setup) => new TableCalibrationStore(setup, _siteDir, _shippedDir);

        private static TableCalibrationFile Sample()
        {
            var file = TableCalibrationFile.CreateDefault(TableSetups.TestRig);
            file.projectors[0].projectorPoints[2] = new Vector2(0.93f, 0.97f);
            file.projectors[1].meshColumns = 1;
            file.projectors[1].meshRows = 1;
            file.projectors[1].meshOffsets = new[] { Vector2.zero, new Vector2(0.01f, 0f), Vector2.zero, new Vector2(0f, -0.02f) };
            file.sensors.Add(new SensorCalibration
            {
                sensorIndex = 2,
                mapping = new SensorMapping { homography = new Homography(new[] { 1.1, 0.2, -0.3, 0.05, 0.9, 0.12, 0.001, -0.002, 1.0 }), k1 = -0.137 },
                rmsError = 0.004,
                pointCount = 9,
            });
            return file;
        }

        [Test]
        public void Json_RoundTripsEverything()
        {
            var original = Sample();
            Assert.That(TableCalibrationFile.TryFromJson(original.ToJson(), out var back, out string error), Is.True, error);

            Assert.That(back.projectors.Count, Is.EqualTo(2));
            Assert.That(back.projectors[0].projectorPoints[2], Is.EqualTo(new Vector2(0.93f, 0.97f)));
            Assert.That(back.projectors[0].viewport, Is.EqualTo(original.projectors[0].viewport));
            Assert.That(back.projectors[1].HasMeshWarp, Is.True);
            Assert.That(back.projectors[1].meshOffsets[3], Is.EqualTo(new Vector2(0f, -0.02f)));
            Assert.That(back.projectors[1].seamNormal, Is.EqualTo(original.projectors[1].seamNormal));

            var sensor = back.FindSensor(2);
            Assert.That(sensor, Is.Not.Null);
            Assert.That(sensor.mapping.k1, Is.EqualTo(-0.137).Within(1e-12));
            var h0 = original.sensors[0].mapping.homography.ToRowMajor();
            var h1 = sensor.mapping.homography.ToRowMajor();
            for (int i = 0; i < 9; i++) Assert.That(h1[i], Is.EqualTo(h0[i]).Within(1e-12), $"h[{i}]");
        }

        [Test]
        public void Json_RejectsGarbageAndNewerVersions()
        {
            Assert.That(TableCalibrationFile.TryFromJson("", out _, out _), Is.False);
            Assert.That(TableCalibrationFile.TryFromJson("{ not json", out _, out _), Is.False);
            var future = Sample();
            future.version = TableCalibrationFile.CurrentVersion + 1;
            Assert.That(TableCalibrationFile.TryFromJson(future.ToJson(), out _, out string error), Is.False);
            StringAssert.Contains("newer", error);
        }

        [Test]
        public void Json_RejectsDegenerateProjector()
        {
            var bad = Sample();
            bad.projectors[0].projectorPoints = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
            Assert.That(TableCalibrationFile.TryFromJson(bad.ToJson(), out _, out string error), Is.False);
            StringAssert.Contains("control points", error);
        }

        [Test]
        public void Load_NothingSaved_UsesBuiltInDefault()
        {
            var store = NewStore();
            var file = store.Load();
            Assert.That(store.CurrentSource, Is.EqualTo(TableCalibrationStore.Source.BuiltInDefault));
            Assert.That(file.projectors.Count, Is.EqualTo(2));
        }

        [Test]
        public void Load_PrefersSiteOverShipped()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_shippedPath));
            var shipped = Sample();
            shipped.note = "shipped";
            File.WriteAllText(_shippedPath, shipped.ToJson());

            var store = NewStore();
            store.Load();
            Assert.That(store.CurrentSource, Is.EqualTo(TableCalibrationStore.Source.Shipped));

            var site = Sample();
            site.note = "site";
            Assert.That(store.Save(site), Is.True);
            var reloaded = NewStore().Load();
            Assert.That(reloaded.note, Is.EqualTo("site"));
        }

        [Test]
        public void Load_CorruptSiteFile_FallsBackToShipped()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_shippedPath));
            var shipped = Sample();
            shipped.note = "shipped";
            File.WriteAllText(_shippedPath, shipped.ToJson());
            File.WriteAllText(Path.Combine(_siteDir, TableCalibrationStore.FileNameFor(TableSetups.TestRig)), "{ corrupt");

            Debug.unityLogger.logEnabled = false; // the store logs an error by design
            try
            {
                var store = NewStore();
                Assert.That(store.Load().note, Is.EqualTo("shipped"));
                Assert.That(store.CurrentSource, Is.EqualTo(TableCalibrationStore.Source.Shipped));
            }
            finally { Debug.unityLogger.logEnabled = true; }
        }

        [Test]
        public void Save_BacksUpPreviousAndRestoreRollsBack()
        {
            var store = NewStore();
            var first = Sample();
            first.note = "first";
            Assert.That(store.Save(first), Is.True);
            Assert.That(store.ListBackups().Count, Is.EqualTo(0), "nothing to back up on the first save");

            var second = Sample();
            second.note = "second";
            Assert.That(store.Save(second), Is.True);
            var backups = store.ListBackups();
            Assert.That(backups.Count, Is.EqualTo(1));

            Assert.That(store.TryRestoreBackup(backups[0], out string error), Is.True, error);
            Assert.That(store.Current.note, Is.EqualTo("first"));
            Assert.That(NewStore().Load().note, Is.EqualTo("first"));
            // The replaced "second" was itself backed up, so the restore can be undone too.
            Assert.That(store.ListBackups().Count, Is.EqualTo(2));
        }

        [Test]
        public void Save_PrunesOldBackups()
        {
            var store = NewStore();
            store.MaxBackups = 3;
            for (int i = 0; i < 7; i++)
            {
                var f = Sample();
                f.note = "save " + i;
                Assert.That(store.Save(f), Is.True);
            }
            var backups = store.ListBackups();
            Assert.That(backups.Count, Is.EqualTo(3));
            // Newest backup holds the save just before the last one.
            Assert.That(TableCalibrationFile.TryFromJson(File.ReadAllText(backups[0]), out var newest, out _), Is.True);
            Assert.That(newest.note, Is.EqualTo("save 5"));
        }

        [Test]
        public void CopySiteToShipped_MakesSiteTheShippedDefault()
        {
            var store = NewStore();
            Assert.That(store.TryCopySiteToShipped(out _), Is.False, "nothing saved yet");
            var f = Sample();
            f.note = "bake me";
            store.Save(f);
            Assert.That(store.TryCopySiteToShipped(out string error), Is.True, error);
            File.Delete(store.SitePath);
            Assert.That(NewStore().Load().note, Is.EqualTo("bake me"));
        }

        [Test]
        public void Setups_KeepSeparateFilesAndBackups()
        {
            var rig = NewStore(TableSetups.TestRig);
            var pilot = NewStore(TableSetups.Pilot);
            Assert.That(pilot.Load().projectors.Count, Is.EqualTo(1), "pilot placeholder = one projector");
            Assert.That(rig.Load().projectors.Count, Is.EqualTo(2));
            Assert.That(pilot.SitePath, Is.Not.EqualTo(rig.SitePath));

            var r = Sample(); r.note = "rig 1"; rig.Save(r);
            r = Sample(); r.note = "rig 2"; rig.Save(r);
            var p = TableCalibrationFile.CreateDefault(TableSetups.Pilot); p.note = "pilot"; pilot.Save(p);

            Assert.That(NewStore(TableSetups.TestRig).Load().note, Is.EqualTo("rig 2"));
            var reloadedPilot = NewStore(TableSetups.Pilot).Load();
            Assert.That(reloadedPilot.note, Is.EqualTo("pilot"));
            Assert.That(reloadedPilot.setup, Is.EqualTo("pilot"));
            Assert.That(rig.ListBackups().Count, Is.EqualTo(1));
            Assert.That(pilot.ListBackups().Count, Is.EqualTo(0), "restoring a backup never crosses setups");
        }

        [Test]
        public void Clone_IsIndependent()
        {
            var a = Sample();
            var b = a.Clone();
            b.projectors[0].projectorPoints[0] = new Vector2(0.5f, 0.5f);
            Assert.That(a.projectors[0].projectorPoints[0], Is.EqualTo(Vector2.zero));
        }
    }
}
#endif
