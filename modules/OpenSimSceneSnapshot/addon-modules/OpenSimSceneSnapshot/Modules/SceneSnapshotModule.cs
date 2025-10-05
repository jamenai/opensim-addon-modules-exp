using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("SceneSnapshot", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("Lightweight scene snapshot module (auto OAR snapshots + list/prune/restore).")]
[assembly: AddinAuthor("Christopher Händler")]

namespace OpenSimSceneSnapshot.Modules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SceneSnapshot")]
    public class SceneSnapshotModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private string _snapshotDir = "snapshots";
        private int _intervalMinutes = 60;
        private int _keepHourly = 24;
        private int _keepDaily = 7;

        // Restore mode: Replace (default) or Merge (protects existing terrain/parcels typically)
        private string _restoreMode = "Replace";

        private bool _autoAtStartup = false;
        private bool _autoAtShutdown = false;

        private Timer _timer;
        private int _autoOpGuard;   // guard for timer-triggered operations
        private int _opInProgress;  // guard for any snapshot/restore operation

        public string Name => "SceneSnapshot";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null)
                return;

            var status = modules.GetString("SceneSnapshot", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase))
                return;

            var cfg = source.Configs["SceneSnapshot"] ?? source.AddConfig("SceneSnapshot");

            _snapshotDir = Path.GetFullPath(cfg.GetString("SnapshotDirectory", _snapshotDir));
            _intervalMinutes = Math.Max(5, cfg.GetInt("IntervalMinutes", _intervalMinutes));
            _keepHourly = Math.Max(0, cfg.GetInt("KeepHourly", _keepHourly));
            _keepDaily = Math.Max(0, cfg.GetInt("KeepDaily", _keepDaily));
            _restoreMode = cfg.GetString("RestoreMode", _restoreMode); // Replace | Merge
            _autoAtStartup = cfg.GetBoolean("AutoAtStartup", _autoAtStartup);
            _autoAtShutdown = cfg.GetBoolean("AutoAtShutdown", _autoAtShutdown);

            Directory.CreateDirectory(_snapshotDir);

            _enabled = true;
            Log.Info("[SCENESNAPSHOT] Module enabled.");
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled) return;
            _scene = scene;
            _scene.RegisterModuleInterface(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled) return;

            MainConsole.Instance.Commands.AddCommand(
                "Region",
                false,
                "ss",
                "ss",
                "SceneSnapshot commands. Usage: ss create|list|restore <file>|prune|status",
                HandleConsole);

            TryCleanupTmp();

            _timer = new Timer(_intervalMinutes * 60_000) { AutoReset = true };
            _timer.Elapsed += (_, __) => SafeAutoSnapshot();
            _timer.Start();

            if (_autoAtStartup)
                SafeAutoSnapshot();

            Log.Info($"[SCENESNAPSHOT] Ready for region '{_scene.RegionInfo.RegionName}'. Saving to '{_snapshotDir}'.");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            if (_autoAtShutdown)
                SafeAutoSnapshot();

            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null)
            {
                MainConsole.Instance.Output("[SCENESNAPSHOT] No active scene.");
                return;
            }

            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            string sub = args[1].ToLowerInvariant();
            try
            {
                switch (sub)
                {
                    case "create":
                    {
                        if (!BeginOperation()) { MainConsole.Instance.Output("[SCENESNAPSHOT] Another operation is in progress."); return; }
                        try
                        {
                            string path = CreateSnapshot();
                            MainConsole.Instance.Output($"[SCENESNAPSHOT] Snapshot created: {path}");
                        }
                        finally { EndOperation(); }
                        break;
                    }
                    case "list":
                    {
                        var files = ListSnapshots();
                        if (files.Length == 0)
                            MainConsole.Instance.Output("[SCENESNAPSHOT] No snapshots found.");
                        else
                            foreach (var f in files) MainConsole.Instance.Output(f);
                        break;
                    }
                    case "restore":
                    {
                        if (args.Length < 3)
                        {
                            MainConsole.Instance.Output("Usage: ss restore <fileOrLatest>");
                            return;
                        }

                        string target = args[2];
                        string file = ResolveSnapshotPath(target);
                        if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        {
                            MainConsole.Instance.Output($"[SCENESNAPSHOT] File not found: {target}");
                            return;
                        }

                        if (!BeginOperation()) { MainConsole.Instance.Output("[SCENESNAPSHOT] Another operation is in progress."); return; }
                        try
                        {
                            RestoreSnapshot(file);
                            MainConsole.Instance.Output($"[SCENESNAPSHOT] Restored from: {file}");
                        }
                        finally { EndOperation(); }
                        break;
                    }
                    case "prune":
                    {
                        if (!BeginOperation()) { MainConsole.Instance.Output("[SCENESNAPSHOT] Another operation is in progress."); return; }
                        try
                        {
                            int removed = PruneSnapshots();
                            MainConsole.Instance.Output($"[SCENESNAPSHOT] Pruned {removed} files.");
                        }
                        finally { EndOperation(); }
                        break;
                    }
                    case "status":
                    {
                        MainConsole.Instance.Output($"[SCENESNAPSHOT] Dir={_snapshotDir}");
                        MainConsole.Instance.Output($"Interval={_intervalMinutes}m KeepHourly={_keepHourly} KeepDaily={_keepDaily}");
                        MainConsole.Instance.Output($"RestoreMode={_restoreMode}");
                        var files = ListSnapshots();
                        MainConsole.Instance.Output($"Snapshots count={files.Length}");
                        break;
                    }
                    default:
                        PrintHelp();
                        break;
                }
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"[SCENESNAPSHOT] Error: {ex.Message}");
            }
        }

        private void PrintHelp()
        {
            MainConsole.Instance.Output("ss create                - Create a snapshot now");
            MainConsole.Instance.Output("ss list                  - List snapshots");
            MainConsole.Instance.Output("ss restore <file|latest> - Restore snapshot");
            MainConsole.Instance.Output("ss prune                 - Prune snapshots by retention policy");
            MainConsole.Instance.Output("ss status                - Show config/status");
            MainConsole.Instance.Output("Config keys (SceneSnapshot):");
            MainConsole.Instance.Output("  SnapshotDirectory, IntervalMinutes, KeepHourly, KeepDaily");
            MainConsole.Instance.Output("  RestoreMode = Replace | Merge");
        }

        private void SafeAutoSnapshot()
        {
            try
            {
                if (System.Threading.Interlocked.Exchange(ref _autoOpGuard, 1) == 1)
                    return;

                if (!BeginOperation()) return;
                try
                {
                    CreateSnapshot();
                    PruneSnapshots();
                }
                finally { EndOperation(); }
            }
            catch (Exception e)
            {
                Log.Warn($"[SCENESNAPSHOT] Auto snapshot failed: {e.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _autoOpGuard, 0);
            }
        }

        private bool BeginOperation()
        {
            return System.Threading.Interlocked.Exchange(ref _opInProgress, 1) == 0;
        }

        private void EndOperation()
        {
            System.Threading.Interlocked.Exchange(ref _opInProgress, 0);
        }

        private string CreateSnapshot()
        {
            string region = SanitizeName(_scene.RegionInfo.RegionName);
            string ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string fileName = $"{region}_{ts}.oar";
            string tmpFile = Path.Combine(_snapshotDir, fileName + ".tmp");
            string finalFile = Path.Combine(_snapshotDir, fileName);

            IRegionArchiverModule archiver = _scene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver == null)
                throw new InvalidOperationException("RegionArchiver not available.");

            try
            {
                using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var options = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["all"] = true // full OAR incl. assets
                    };
                    archiver.ArchiveRegion(fs, Guid.Empty, options);
                }

                if (File.Exists(finalFile)) File.Delete(finalFile);
                File.Move(tmpFile, finalFile);

                Log.Info($"[SCENESNAPSHOT] Created snapshot {finalFile}");
                return finalFile;
            }
            catch
            {
                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                throw;
            }
        }

        private void RestoreSnapshot(string filePath)
        {
            IRegionArchiverModule archiver = _scene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver == null)
                throw new InvalidOperationException("RegionArchiver not available.");

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var options = new System.Collections.Generic.Dictionary<string, object>();
                if (string.Equals(_restoreMode, "Merge", StringComparison.OrdinalIgnoreCase))
                    options["merge"] = null;

                archiver.DearchiveRegion(fs, Guid.Empty, options);
            }

            Log.Info($"[SCENESNAPSHOT] Restored snapshot {filePath}");
        }

        private int PruneSnapshots()
        {
            var files = ListSnapshots();
            int removed = 0;

            var entries = files
                .Select(f => new { Path = f, Time = ParseTimestamp(f) })
                .Where(x => x.Time != DateTime.MinValue)
                .OrderByDescending(x => x.Time)
                .ToArray();

            var keep = entries.Take(_keepHourly).Select(x => x.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_keepDaily > 0)
            {
                var byDate = entries
                    .GroupBy(x => x.Time.Date)
                    .OrderByDescending(g => g.Key)
                    .Take(_keepDaily)
                    .Select(g => g.First().Path);

                foreach (var p in byDate)
                    keep.Add(p);
            }

            foreach (var f in files)
            {
                if (!keep.Contains(f))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }

            if (removed > 0)
                Log.Info($"[SCENESNAPSHOT] Pruned {removed} snapshots.");

            return removed;
        }

        private string[] ListSnapshots()
        {
            try
            {
                if (!Directory.Exists(_snapshotDir))
                    return Array.Empty<string>();

                string region = SanitizeName(_scene.RegionInfo.RegionName);
                return Directory.GetFiles(_snapshotDir, $"{region}_*.oar")
                    .Select(p => new { Path = p, Time = ParseTimestamp(p) })
                    .OrderByDescending(x => x.Time)
                    .ThenByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Path)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private string ResolveSnapshotPath(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
                return null;

            if (string.Equals(arg, "latest", StringComparison.OrdinalIgnoreCase))
                return ListSnapshots().FirstOrDefault();

            string path = Path.IsPathRooted(arg) ? arg : Path.Combine(_snapshotDir, arg);
            if (!path.EndsWith(".oar", StringComparison.OrdinalIgnoreCase))
                path += ".oar";
            return path;
        }

        private void TryCleanupTmp()
        {
            try
            {
                if (!Directory.Exists(_snapshotDir)) return;
                foreach (var f in Directory.EnumerateFiles(_snapshotDir, "*.oar.tmp"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private static DateTime ParseTimestamp(string path)
        {
            try
            {
                string file = Path.GetFileNameWithoutExtension(path);
                int us = file.LastIndexOf('_');
                if (us < 0 || us + 1 >= file.Length) return DateTime.MinValue;
                string ts = file.Substring(us + 1);
                if (DateTime.TryParseExact(ts, "yyyyMMdd-HHmmss", null,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                    return dt;
            }
            catch { }

            return DateTime.MinValue;
        }
    }
}