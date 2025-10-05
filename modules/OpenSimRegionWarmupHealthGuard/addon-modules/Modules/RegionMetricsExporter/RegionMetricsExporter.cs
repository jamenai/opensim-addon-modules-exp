using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionMetricsExporter")]
    public class RegionMetricsExporterModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private int _httpPort = 9109;
        private string _metricsPrefix = "opensim_region_";
        private string _bindAddress = "127.0.0.1"; // default loopback for safety
        private bool _includeRegionLabel = true;

        // State
        private HttpListener _listener;
        private Thread _httpThread;
        private volatile bool _httpRunning;
        private readonly object _lastLock = new object();
        private HealthSample _lastSample; // last health sample for this region

        // Bus
        private IRegionHealthBus _bus;

        public string Name => "RegionMetricsExporter";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionMetricsExporter", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionMetricsExporter"] ?? source.AddConfig("RegionMetricsExporter");

            _httpPort = Math.Max(1, cfg.GetInt("HttpPort", _httpPort));
            _metricsPrefix = cfg.GetString("MetricsPrefix", _metricsPrefix);
            _bindAddress = cfg.GetString("BindAddress", _bindAddress);
            _includeRegionLabel = cfg.GetBoolean("IncludeRegionLabel", _includeRegionLabel);

            _enabled = true;
            Log.Info($"[METRICS] RegionMetricsExporter enabled (port={_httpPort}, bind={_bindAddress})");
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled) return;
            _scene = scene;

            // Discover health bus (provided by RegionHealthMonitor)
            _bus = _scene.RequestModuleInterface<IRegionHealthBus>();
            if (_bus == null)
            {
                Log.Warn("[METRICS] No IRegionHealthBus found. Metrics will remain empty until RegionHealthMonitor is enabled.");
            }
            else
            {
                _bus.OnSample += OnHealthSample;
            }

            _scene.RegisterModuleInterface(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled) return;

            // Start HTTP listener per region module instance (separate port or one shared port per process recommended)
            // Here: single-port per region instance. In multi-region processes, use different ports per region.
            StartHttp();

            MainConsole.Instance.Commands.AddCommand("Region", false, "metrics",
                "metrics",
                "Metrics exporter commands. Usage: metrics status",
                HandleConsole);

            Log.Info($"[METRICS] Ready for region '{_scene.RegionInfo.RegionName}'. Endpoint: http://{_bindAddress}:{_httpPort}/metrics");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            StopHttp();

            if (_bus != null)
                _bus.OnSample -= OnHealthSample;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            StopHttp();
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[METRICS] No active scene."); return; }
            if (args.Length < 2)
            {
                MainConsole.Instance.Output("metrics status - show exporter status");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "status":
                    MainConsole.Instance.Output($"[METRICS] Endpoint: http://{_bindAddress}:{_httpPort}/metrics Running={_httpRunning}");
                    break;
                default:
                    MainConsole.Instance.Output("metrics status");
                    break;
            }
        }

        private void OnHealthSample(HealthSample s)
        {
            lock (_lastLock)
            {
                _lastSample = s;
            }
        }

        private void StartHttp()
        {
            try
            {
                if (_httpRunning) return;

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{_bindAddress}:{_httpPort}/metrics/");
                // Allow also without trailing slash
                _listener.Prefixes.Add($"http://{_bindAddress}:{_httpPort}/metrics");
                _listener.Start();

                _httpRunning = true;
                _httpThread = new Thread(HttpLoop) { IsBackground = true, Name = "RegionMetricsExporter-HTTP" };
                _httpThread.Start();

                Log.Info($"[METRICS] HTTP metrics endpoint started on http://{_bindAddress}:{_httpPort}/metrics");
            }
            catch (Exception e)
            {
                Log.Error($"[METRICS] Failed to start HTTP listener: {e.Message}");
                _httpRunning = false;
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }
        }

        private void StopHttp()
        {
            try
            {
                _httpRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;
            }
            catch { }

            if (_httpThread != null)
            {
                try { _httpThread.Join(1000); } catch { }
                _httpThread = null;
            }
        }

        private void HttpLoop()
        {
            while (_httpRunning && _listener != null)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_httpRunning) break;
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.HttpMethod != "GET")
                {
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Close();
                    return;
                }

                if (!ctx.Request.Url.AbsolutePath.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                var text = BuildPrometheusText();
                var buffer = Encoding.UTF8.GetBytes(text);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                ctx.Response.ContentEncoding = Encoding.UTF8;
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        private string BuildPrometheusText()
        {
            HealthSample s;
            lock (_lastLock)
            {
                s = _lastSample;
            }

            var region = _scene?.RegionInfo?.RegionName ?? "unknown";
            var sb = new StringBuilder(512);
            var prefix = _metricsPrefix;

            // HELP/TYPE
            AppendHelpType(sb, prefix + "agents", "Number of agents (root + child) in region", "gauge");
            AppendHelpType(sb, prefix + "prims", "Total number of prims/objects in region", "gauge");
            AppendHelpType(sb, prefix + "script_time_ms", "Script time (ms)", "gauge");
            AppendHelpType(sb, prefix + "physics_time_ms", "Physics time (ms)", "gauge");
            AppendHelpType(sb, prefix + "net_time_ms", "Network time (ms)", "gauge");
            AppendHelpType(sb, prefix + "script_errors", "Script error count", "gauge");
            AppendHelpType(sb, prefix + "uptime_seconds", "Region uptime (seconds)", "gauge");
            AppendHelpType(sb, prefix + "sample_timestamp_seconds", "Last sample timestamp (unix seconds)", "gauge");

            var labels = _includeRegionLabel ? $"{{region=\"{EscapeLabel(region)}\"}}" : "";

            if (s == null)
            {
                // no sample yet: export zeros
                sb.AppendLine($"{prefix}agents{labels} 0");
                sb.AppendLine($"{prefix}prims{labels} 0");
                sb.AppendLine($"{prefix}script_time_ms{labels} 0");
                sb.AppendLine($"{prefix}physics_time_ms{labels} 0");
                sb.AppendLine($"{prefix}net_time_ms{labels} 0");
                sb.AppendLine($"{prefix}script_errors{labels} 0");
                sb.AppendLine($"{prefix}uptime_seconds{labels} 0");
                sb.AppendLine($"{prefix}sample_timestamp_seconds{labels} 0");
                return sb.ToString();
            }

            // Invariant culture for dots
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine($"{prefix}agents{labels} {s.Agents.ToString(ci)}");
            sb.AppendLine($"{prefix}prims{labels} {s.Prims.ToString(ci)}");
            sb.AppendLine($"{prefix}script_time_ms{labels} {s.ScriptTimeMs.ToString(ci)}");
            sb.AppendLine($"{prefix}physics_time_ms{labels} {s.PhysicsTimeMs.ToString(ci)}");
            sb.AppendLine($"{prefix}net_time_ms{labels} {s.NetTimeMs.ToString(ci)}");
            sb.AppendLine($"{prefix}script_errors{labels} {s.ScriptErrors.ToString(ci)}");
            sb.AppendLine($"{prefix}uptime_seconds{labels} {((long)s.Uptime.TotalSeconds).ToString(ci)}");
            var unix = new DateTimeOffset(s.TimestampUtc).ToUnixTimeSeconds();
            sb.AppendLine($"{prefix}sample_timestamp_seconds{labels} {unix.ToString(ci)}");

            return sb.ToString();
        }

        private static void AppendHelpType(StringBuilder sb, string name, string help, string type)
        {
            sb.AppendLine($"# HELP {name} {help}");
            sb.AppendLine($"# TYPE {name} {type}");
        }

        private static string EscapeLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}