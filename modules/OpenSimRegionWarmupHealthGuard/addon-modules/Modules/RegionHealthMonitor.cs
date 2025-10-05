using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("RegionHealthMonitor", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("Region health polling + threshold warnings + CSV export + health event bus.")]
[assembly: AddinAuthor("Christopher Händler")]

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    // Simple health sample DTO
    public class HealthSample
    {
        public DateTime TimestampUtc;
        public string Region;
        public int Agents;
        public int Prims;
        public float ScriptTimeMs;
        public float PhysicsTimeMs;
        public float NetTimeMs;
        public int ScriptErrors;
        public TimeSpan Uptime;
    }

    public enum HealthSeverity { Trace, Info, Warn, Error }

    public class HealthEvent
    {
        public DateTime TimestampUtc;
        public string Region;
        public HealthSeverity Severity;
        public string Message;
        public HealthSample Sample; // optional snapshot of metrics
    }

    // Event-bus interface for other modules (alerts/export/auto-heal/policy)
    public interface IRegionHealthBus
    {
        event Action<HealthSample> OnSample;    // every poll
        event Action<HealthEvent> OnIncident;   // only on rule/threshold violation

        void PublishSample(HealthSample s);
        void PublishIncident(HealthEvent e);
    }

    public class RegionHealthBus : IRegionHealthBus
    {
        public event Action<HealthSample> OnSample;
        public event Action<HealthEvent> OnIncident;

        public void PublishSample(HealthSample s) => OnSample?.Invoke(s);
        public void PublishIncident(HealthEvent e) => OnIncident?.Invoke(e);
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionHealthMonitor")]
    public class RegionHealthMonitorModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private int _healthIntervalSec = 30;
        private float _warnScriptTimeMs = 12.0f;
        private float _warnPhysicsTimeMs = 6.0f;
        private float _warnNetTimeMs = 6.0f;
        private int _warnScriptErrors = 10;
        private string _metricsExportFile = ""; // optional CSV export

        private Timer _healthTimer;
        private volatile int _opGuard;
        
        // Per-region start time to compute uptime for this scene instance.
        // This is important if multiple regions run in the same process.
        private DateTime _regionStartUtc = DateTime.UtcNow;

        // Expose bus for other modules
        private readonly RegionHealthBus _bus = new RegionHealthBus();
        public IRegionHealthBus Bus => _bus;

        public string Name => "RegionHealthMonitor";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionHealthMonitor", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionHealthMonitor"] ?? source.AddConfig("RegionHealthMonitor");

            _healthIntervalSec = Math.Max(5, cfg.GetInt("HealthIntervalSec", _healthIntervalSec));
            _warnScriptTimeMs = (float)cfg.GetDouble("WarnScriptTimeMs", _warnScriptTimeMs);
            _warnPhysicsTimeMs = (float)cfg.GetDouble("WarnPhysicsTimeMs", _warnPhysicsTimeMs);
            _warnNetTimeMs = (float)cfg.GetDouble("WarnNetTimeMs", _warnNetTimeMs);
            _warnScriptErrors = Math.Max(0, cfg.GetInt("WarnScriptErrors", _warnScriptErrors));
            _metricsExportFile = cfg.GetString("MetricsExportFile", _metricsExportFile);

            _enabled = true;
            Log.Info("[HEALTH] RegionHealthMonitor enabled");
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled) return;
            _scene = scene;
            
            // Track per-region start moment (now that we know which scene this module is bound to).
            _regionStartUtc = DateTime.UtcNow;
            
            // Make bus discoverable to other modules
            _scene.RegisterModuleInterface<IRegionHealthBus>(_bus);
            _scene.RegisterModuleInterface(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled) return;

            MainConsole.Instance.Commands.AddCommand("Region", false, "health",
                "health",
                "Health monitor commands. Usage: health status|export",
                HandleConsole);

            _healthTimer = new Timer(_healthIntervalSec * 1000) { AutoReset = true };
            _healthTimer.Elapsed += (_, __) => SafeRun("HealthPoll", PollHealth);
            _healthTimer.Start();

            Log.Info($"[HEALTH] Ready for region '{_scene.RegionInfo.RegionName}'. Interval={_healthIntervalSec}s.");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;
            _healthTimer?.Stop();
            _healthTimer?.Dispose();
            _healthTimer = null;

            _scene.UnregisterModuleInterface<IRegionHealthBus>(_bus);
            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            _healthTimer?.Stop();
            _healthTimer?.Dispose();
            _healthTimer = null;
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[HEALTH] No active scene."); return; }
            if (args.Length < 2)
            {
                MainConsole.Instance.Output("health status  - show current health");
                MainConsole.Instance.Output("health export  - export one CSV line (if MetricsExportFile configured)");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "status":
                    ReportHealth(toConsole: true);
                    break;
                case "export":
                    var path = ExportMetricsOnce();
                    MainConsole.Instance.Output(string.IsNullOrEmpty(path)
                        ? "[HEALTH] Export disabled or failed."
                        : $"[HEALTH] Exported metrics to: {path}");
                    break;
                default:
                    MainConsole.Instance.Output("health status|export");
                    break;
            }
        }

        private void SafeRun(string tag, Action action)
        {
            if (System.Threading.Interlocked.Exchange(ref _opGuard, 1) == 1)
                return;

            try { action(); }
            catch (Exception e) { Log.Warn($"[HEALTH] {tag} failed: {e.Message}"); }
            finally { System.Threading.Interlocked.Exchange(ref _opGuard, 0); }
        }

        private void PollHealth()
        {
            var s = CollectStats();

            // Publish every sample
            _bus.PublishSample(s);

            // Threshold checks -> incidents
            if (s.ScriptTimeMs >= _warnScriptTimeMs)
                PublishIncident(HealthSeverity.Warn, $"High script time: {s.ScriptTimeMs:0.00} ms", s);

            if (s.PhysicsTimeMs >= _warnPhysicsTimeMs)
                PublishIncident(HealthSeverity.Warn, $"High physics time: {s.PhysicsTimeMs:0.00} ms", s);

            if (s.NetTimeMs >= _warnNetTimeMs)
                PublishIncident(HealthSeverity.Warn, $"High network time: {s.NetTimeMs:0.00} ms", s);

            if (s.ScriptErrors >= _warnScriptErrors)
                PublishIncident(HealthSeverity.Warn, $"High script errors: {s.ScriptErrors}", s);

            ExportMetrics(s);
        }

        private void PublishIncident(HealthSeverity sev, string msg, HealthSample s)
        {
            var e = new HealthEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Region = _scene.RegionInfo.RegionName,
                Severity = sev,
                Message = msg,
                Sample = s
            };
            _bus.PublishIncident(e);
            if (sev >= HealthSeverity.Warn)
                Log.Warn($"[HEALTH] {msg}");
            else
                Log.Info($"[HEALTH] {msg}");
        }

        private void ReportHealth(bool toConsole)
        {
            var s = CollectStats();
            var line = $"[Health] Agents={s.Agents} Prims={s.Prims} " +
                       $"ScriptMs={s.ScriptTimeMs:0.00} PhysMs={s.PhysicsTimeMs:0.00} NetMs={s.NetTimeMs:0.00} " +
                       $"ScriptErrors={s.ScriptErrors} Uptime={s.Uptime:hh\\:mm\\:ss}";
            if (toConsole) MainConsole.Instance.Output(line);
            Log.Info(line);
        }

        private HealthSample CollectStats()
        {
            float scriptMs = 0f, physMs = 0f, netMs = 0f;
            try
            {
                var stats = _scene.StatsReporter;
                if (stats != null)
                {
                    var arr = stats.LastReportedSimStats;
                    if (arr != null && arr.Length > (int)StatsIndex.ScriptMS)
                    {
                        // Read times from SimStatsReporter via indexed array.
                        netMs = arr[(int)StatsIndex.NetMS];
                        physMs = arr[(int)StatsIndex.PhysicsMS];
                        scriptMs = arr[(int)StatsIndex.ScriptMS];
                    }
                }
            }
            catch { }

            int agents = 0, prims = 0, scriptErrors = 0;
            try
            {
                // Agent counts via SceneGraph.
                agents = _scene.SceneGraph.GetRootAgentCount() + _scene.SceneGraph.GetChildAgentCount();
                // Total object count via SceneGraph.
                prims = _scene.SceneGraph.GetTotalObjectsCount();
            }
            catch { }

            try
            {
                var engine = _scene.RequestModuleInterface<IScriptModule>();
                if (engine != null)
                {
                    // No direct "script error count" in this API; use active script count as a proxy
                    // or set this to 0 if you prefer not to approximate.
                    scriptErrors = _scene.SceneGraph.GetActiveScriptsCount();
                }
            }
            catch { }

            // Compute per-region uptime based on when this module attached to the scene.
            TimeSpan uptime = TimeSpan.Zero;
            try
            {
                var now = DateTime.UtcNow;
                if (now > _regionStartUtc)
                    uptime = now - _regionStartUtc;
            }
            catch { }

            return new HealthSample
            {
                TimestampUtc = DateTime.UtcNow,
                Region = _scene.RegionInfo.RegionName,
                Agents = agents,
                Prims = prims,
                ScriptTimeMs = scriptMs,
                PhysicsTimeMs = physMs,
                NetTimeMs = netMs,
                ScriptErrors = scriptErrors,
                Uptime = uptime
            };
        }

        private void ExportMetrics(HealthSample s)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_metricsExportFile))
                    return;

                bool header = !File.Exists(_metricsExportFile);
                using (var sw = new StreamWriter(_metricsExportFile, append: true))
                {
                    if (header)
                        sw.WriteLine("ts,region,agents,prims,script_ms,phys_ms,net_ms,script_errors,uptime_s");

                    sw.WriteLine($"{DateTime.UtcNow:O},{Csv(_scene.RegionInfo.RegionName)},{s.Agents},{s.Prims}," +
                                 $"{s.ScriptTimeMs:0.###},{s.PhysicsTimeMs:0.###},{s.NetTimeMs:0.###}," +
                                 $"{s.ScriptErrors},{(long)s.Uptime.TotalSeconds}");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[HEALTH] CSV export failed: {e.Message}");
            }
        }

        private string ExportMetricsOnce()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_metricsExportFile))
                    return null;

                var s = CollectStats();
                ExportMetrics(s);
                return _metricsExportFile;
            }
            catch { return null; }
        }

        private string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}