using System;
using System.Collections.Generic;
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
using System.IO;

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    public enum AlertSeverity { Trace = 0, Info = 1, Warn = 2, Error = 3 }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionWebhookAlerts")]
    public class RegionWebhookAlertsModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private string _url = "";                 // webhook endpoint (e.g., n8n)
        private AlertSeverity _minSeverity = AlertSeverity.Warn;
        private int _batchWindowSec = 10;         // collect events for N seconds before sending
        private int _rateLimitPerMin = 20;        // max POSTs per minute
        private int _connectTimeoutMs = 5000;
        private int _sendTimeoutMs = 5000;
        private string _payloadFields = "region,agents,prims,scriptMs,physMs,netMs,scriptErrors,uptime,severity,message,ts"; // selectable

        // State
        private IRegionHealthBus _bus;
        private readonly object _queueLock = new object();
        private readonly List<HealthEvent> _queue = new List<HealthEvent>();
        private Timer _batchTimer;
        private DateTime _windowStartUtc;
        private int _sentThisMinute;
        private DateTime _minuteWindowUtc;

        public string Name => "RegionWebhookAlerts";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionWebhookAlerts", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionWebhookAlerts"] ?? source.AddConfig("RegionWebhookAlerts");

            _url = cfg.GetString("Url", _url);
            _minSeverity = ParseSeverity(cfg.GetString("MinSeverity", _minSeverity.ToString())) ?? _minSeverity;
            _batchWindowSec = Math.Max(1, cfg.GetInt("BatchWindowSec", _batchWindowSec));
            _rateLimitPerMin = Math.Max(1, cfg.GetInt("RateLimitPerMin", _rateLimitPerMin));
            _connectTimeoutMs = Math.Max(1000, cfg.GetInt("ConnectTimeoutMs", _connectTimeoutMs));
            _sendTimeoutMs = Math.Max(1000, cfg.GetInt("SendTimeoutMs", _sendTimeoutMs));
            _payloadFields = cfg.GetString("PayloadFields", _payloadFields);

            if (string.IsNullOrWhiteSpace(_url))
            {
                Log.Warn("[ALERTS] Url not configured. Module will remain idle.");
            }

            _enabled = true;
            Log.Info($"[ALERTS] RegionWebhookAlerts enabled (min={_minSeverity}, batch={_batchWindowSec}s, rate={_rateLimitPerMin}/min)");
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled) return;
            _scene = scene;

            _bus = _scene.RequestModuleInterface<IRegionHealthBus>();
            if (_bus == null)
            {
                Log.Warn("[ALERTS] No IRegionHealthBus found. Alerts will remain idle until RegionHealthMonitor is enabled.");
            }
            else
            {
                _bus.OnIncident += OnIncident;
            }

            _scene.RegisterModuleInterface(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled) return;

            MainConsole.Instance.Commands.AddCommand("Region", false, "alerts",
                "alerts",
                "Webhook alerts commands. Usage: alerts status|test \"Message...\"",
                HandleConsole);

            _windowStartUtc = DateTime.UtcNow;
            _minuteWindowUtc = _windowStartUtc;
            _batchTimer = new Timer(_ => FlushIfWindowElapsed(), null, _batchWindowSec * 1000, _batchWindowSec * 1000);

            Log.Info($"[ALERTS] Ready for region '{_scene.RegionInfo.RegionName}'.");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            try { _batchTimer?.Dispose(); } catch { }
            _batchTimer = null;

            if (_bus != null)
                _bus.OnIncident -= OnIncident;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            try { _batchTimer?.Dispose(); } catch { }
            _batchTimer = null;
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[ALERTS] No active scene."); return; }
            if (args.Length < 2)
            {
                MainConsole.Instance.Output("alerts status            - show current status");
                MainConsole.Instance.Output("alerts test \"Message\"   - enqueue a test incident with severity=Warn");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "status":
                    MainConsole.Instance.Output($"[ALERTS] Url={(string.IsNullOrWhiteSpace(_url) ? "(not set)" : _url)} " +
                                                $"MinSeverity={_minSeverity} Batch={_batchWindowSec}s Rate={_rateLimitPerMin}/min");
                    break;

                case "test":
                    {
                        string msg = args.Length >= 3 ? string.Join(" ", args, 2, args.Length - 2) : "Test alert";
                        var ev = new HealthEvent
                        {
                            TimestampUtc = DateTime.UtcNow,
                            Region = _scene.RegionInfo.RegionName,
                            Severity = HealthSeverity.Warn,
                            Message = msg,
                            Sample = null
                        };
                        OnIncident(ev);
                        MainConsole.Instance.Output("[ALERTS] Test event queued.");
                        break;
                    }

                default:
                    MainConsole.Instance.Output("alerts status|test \"Message\"");
                    break;
            }
        }

        private void OnIncident(HealthEvent e)
        {
            if (string.IsNullOrWhiteSpace(_url))
                return;

            var sev = MapSeverity(e.Severity);
            if (sev < _minSeverity)
                return;

            lock (_queueLock)
            {
                _queue.Add(e);
            }
        }

        private void FlushIfWindowElapsed()
        {
            try
            {
                var now = DateTime.UtcNow;

                // rate-limit window
                if ((now - _minuteWindowUtc).TotalSeconds >= 60)
                {
                    _minuteWindowUtc = now;
                    _sentThisMinute = 0;
                }

                // batch window
                if ((now - _windowStartUtc).TotalSeconds < _batchWindowSec)
                    return;

                List<HealthEvent> batch;
                lock (_queueLock)
                {
                    if (_queue.Count == 0)
                    {
                        _windowStartUtc = now;
                        return;
                    }
                    batch = new List<HealthEvent>(_queue);
                    _queue.Clear();
                    _windowStartUtc = now;
                }

                if (_sentThisMinute >= _rateLimitPerMin)
                {
                    Log.Warn("[ALERTS] Rate limit reached; dropping batch.");
                    return;
                }

                // Build JSON and POST
                var payload = BuildJson(batch);
                if (PostJson(_url, payload))
                {
                    _sentThisMinute++;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ALERTS] Flush failed: {ex.Message}");
            }
        }

        private string BuildJson(List<HealthEvent> events)
        {
            // Minimal custom JSON builder (avoid external deps)
            // PayloadFields controls which top-level fields/metrics are included.
            var include = new HashSet<string>(SplitCsv(_payloadFields), StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder(events.Count * 256);
            sb.Append('[');
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                sb.Append('{');

                bool first = true;
                void add(string key, string value, bool raw = false)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(E(key)).Append("\":");
                    if (raw) sb.Append(value);
                    else sb.Append('"').Append(E(value)).Append('"');
                }

                if (include.Contains("ts")) add("ts", e.TimestampUtc.ToString("o"));
                if (include.Contains("region")) add("region", e.Region ?? _scene?.RegionInfo?.RegionName ?? "unknown");
                if (include.Contains("severity")) add("severity", e.Severity.ToString());
                if (include.Contains("message")) add("message", e.Message ?? "");

                if (include.Contains("metrics"))
                {
                    // combined object "metrics"
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("\"metrics\":");
                    sb.Append('{');
                    bool fm = true;
                    void addm(string k, string v, bool raw = false)
                    {
                        if (!fm) sb.Append(',');
                        fm = false;
                        sb.Append('"').Append(E(k)).Append("\":");
                        if (raw) sb.Append(v);
                        else sb.Append('"').Append(E(v)).Append('"');
                    }

                    var s = e.Sample;
                    if (s != null)
                    {
                        if (include.Contains("agents")) addm("agents", s.Agents.ToString(), true);
                        if (include.Contains("prims")) addm("prims", s.Prims.ToString(), true);
                        if (include.Contains("scriptMs")) addm("scriptMs", s.ScriptTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("physMs")) addm("physMs", s.PhysicsTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("netMs")) addm("netMs", s.NetTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("scriptErrors")) addm("scriptErrors", s.ScriptErrors.ToString(), true);
                        if (include.Contains("uptime")) addm("uptimeSec", ((long)s.Uptime.TotalSeconds).ToString(), true);
                    }
                    sb.Append('}');
                }
                else
                {
                    // flat metrics if requested
                    var s = e.Sample;
                    if (s != null)
                    {
                        if (include.Contains("agents")) add("agents", s.Agents.ToString(), true);
                        if (include.Contains("prims")) add("prims", s.Prims.ToString(), true);
                        if (include.Contains("scriptMs")) add("scriptMs", s.ScriptTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("physMs")) add("physMs", s.PhysicsTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("netMs")) add("netMs", s.NetTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), true);
                        if (include.Contains("scriptErrors")) add("scriptErrors", s.ScriptErrors.ToString(), true);
                        if (include.Contains("uptime")) add("uptimeSec", ((long)s.Uptime.TotalSeconds).ToString(), true);
                    }
                }

                sb.Append('}');
                if (i < events.Count - 1) sb.Append(',');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private bool PostJson(string url, string json)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = _connectTimeoutMs;
                req.ReadWriteTimeout = _sendTimeoutMs;

                var bytes = Encoding.UTF8.GetBytes(json);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                    s.Write(bytes, 0, bytes.Length);

                using var resp = (HttpWebResponse)req.GetResponse();
                var code = (int)resp.StatusCode;
                if (code >= 200 && code < 300)
                    return true;

                Log.Warn($"[ALERTS] Webhook returned HTTP {code}");
                return false;
            }
            catch (WebException wex)
            {
                try
                {
                    var resp = wex.Response as HttpWebResponse;
                    if (resp != null)
                        Log.Warn($"[ALERTS] Webhook failed: HTTP {(int)resp.StatusCode} {resp.StatusDescription}");
                    else
                        Log.Warn($"[ALERTS] Webhook failed: {wex.Message}");
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ALERTS] Webhook exception: {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var part in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                yield return part.Trim();
        }

        private static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static AlertSeverity? ParseSeverity(string v)
        {
            if (Enum.TryParse<AlertSeverity>(v, true, out var sev))
                return sev;
            return null;
        }

        private static AlertSeverity MapSeverity(HealthSeverity sev)
        {
            return sev switch
            {
                HealthSeverity.Error => AlertSeverity.Error,
                HealthSeverity.Warn => AlertSeverity.Warn,
                HealthSeverity.Info => AlertSeverity.Info,
                _ => AlertSeverity.Trace
            };
        }
    }
}