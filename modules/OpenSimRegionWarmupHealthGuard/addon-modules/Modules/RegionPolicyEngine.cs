using System;
using System.Collections.Generic;
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

[assembly: Addin("RegionPolicyEngine", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("Applies time-based/profile overlays to other modules' configs (cooperative).")]
[assembly: AddinAuthor("Christopher Händler")]

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    public interface IPolicyOverlayConsumer
    {
        // Modules can implement this to accept overlays at runtime.
        // Keys/values are simple strings; module maps to local settings.
        void ApplyPolicyOverlay(IDictionary<string, string> overrides, string sourceProfile);
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionPolicyEngine")]
    public class RegionPolicyEngineModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        private bool _engineEnabled = true;
        private string[] _profiles = Array.Empty<string>();
        private readonly Dictionary<string, string> _profileCron = new();
        private readonly Dictionary<string, Dictionary<string, string>> _profileOverrides = new();

        private Timer _tick;
        private int _intervalSec = 60; // check every minute

        public string Name => "RegionPolicyEngine";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionPolicyEngine", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionPolicyEngine"] ?? source.AddConfig("RegionPolicyEngine");

            _engineEnabled = cfg.GetBoolean("Enabled", _engineEnabled);
            _intervalSec = Math.Max(15, cfg.GetInt("CheckIntervalSec", _intervalSec));

            var profilesCsv = cfg.GetString("Profiles", "");
            _profiles = profilesCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(s => s.Trim())
                                   .ToArray();

            foreach (var p in _profiles)
            {
                var cron = cfg.GetString($"{p}.Cron", "");
                var overridesLine = cfg.GetString($"{p}.Overrides", "");

                if (!string.IsNullOrWhiteSpace(cron))
                    _profileCron[p] = cron;

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in overridesLine.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = kv.IndexOf('=');
                    if (i > 0)
                    {
                        var k = kv.Substring(0, i).Trim();
                        var v = kv.Substring(i + 1).Trim();
                        if (k.Length > 0) dict[k] = v;
                    }
                }
                _profileOverrides[p] = dict;
            }

            _enabled = _engineEnabled;
            Log.Info($"[POLICY] RegionPolicyEngine enabled={_engineEnabled} profiles={string.Join(",", _profiles)}");
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

            MainConsole.Instance.Commands.AddCommand("Region", false, "policy",
                "policy",
                "Policy engine commands. Usage: policy status|apply <Profile>|dryrun <Profile>",
                HandleConsole);

            _tick = new Timer(_intervalSec * 1000) { AutoReset = true };
            _tick.Elapsed += (_, __) => EvaluateAndApply();
            _tick.Start();

            Log.Info($"[POLICY] Ready for region '{_scene.RegionInfo.RegionName}'. check={_intervalSec}s");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            try { _tick?.Stop(); _tick?.Dispose(); } catch { }
            _tick = null;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            try { _tick?.Stop(); _tick?.Dispose(); } catch { }
            _tick = null;
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[POLICY] No active scene."); return; }
            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            var cmd = args[1].ToLowerInvariant();
            if (cmd == "status")
            {
                MainConsole.Instance.Output($"[POLICY] Enabled={_engineEnabled} Profiles={string.Join(",", _profiles)}");
                foreach (var p in _profiles)
                {
                    _profileCron.TryGetValue(p, out var cron);
                    _profileOverrides.TryGetValue(p, out var dict);
                    MainConsole.Instance.Output($" - {p}: Cron='{cron}' Overrides='{string.Join(";", dict.Select(kv => kv.Key + \"=\" + kv.Value))}'");
                }
                return;
            }

            if ((cmd == "apply" || cmd == "dryrun") && args.Length >= 3)
            {
                var profile = args[2];
                if (!_profileOverrides.TryGetValue(profile, out var ov))
                {
                    MainConsole.Instance.Output($"[POLICY] Profile not found: {profile}");
                    return;
                }

                if (cmd == "dryrun")
                {
                    MainConsole.Instance.Output($"[POLICY] DryRun profile={profile} -> {string.Join(";", ov.Select(kv => kv.Key + \"=\" + kv.Value))}");
                    return;
                }

                ApplyOverlay(ov, profile);
                MainConsole.Instance.Output($"[POLICY] Applied profile={profile}");
                return;
            }

            PrintHelp();
        }

        private void PrintHelp()
        {
            MainConsole.Instance.Output("policy status                 - show profiles and config");
            MainConsole.Instance.Output("policy apply <Profile>        - apply overrides now");
            MainConsole.Instance.Output("policy dryrun <Profile>       - show what would be applied");
        }

        private void EvaluateAndApply()
        {
            // Minimalistic time matcher:
            // Cron format (simplified): mm HH hhRange? (e.g., "0 0 20-6" -> every minute between 20 and 6)
            // For real cron parsing you could add a tiny parser; here we implement a simple hours window.
            var now = DateTime.Now;
            foreach (var p in _profiles)
            {
                if (!_profileCron.TryGetValue(p, out var cron) || string.IsNullOrWhiteSpace(cron))
                    continue;

                // Expect format: "0 0 20-6" OR "* * 20-6", we only check hours window
                var parts = cron.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string hours = parts.Length >= 3 ? parts[2] : parts.LastOrDefault();
                if (string.IsNullOrWhiteSpace(hours)) continue;

                bool inWindow = IsHourInWindow(now.Hour, hours);
                if (!inWindow) continue;

                if (_profileOverrides.TryGetValue(p, out var ov))
                {
                    ApplyOverlay(ov, p);
                }
            }
        }

        private static bool IsHourInWindow(int hour, string expr)
        {
            // Accept "20-6" or "8-18" or single "22"
            if (expr.Contains("-"))
            {
                var sp = expr.Split('-');
                if (sp.Length != 2) return false;
                if (!int.TryParse(sp[0], out var a)) return false;
                if (!int.TryParse(sp[1], out var b)) return false;
                a = (a % 24 + 24) % 24;
                b = (b % 24 + 24) % 24;

                if (a <= b) return hour >= a && hour <= b;       // e.g., 8-18
                else return hour >= a || hour <= b;              // e.g., 20-6 (wrap)
            }
            else
            {
                return int.TryParse(expr, out var h) && h == hour;
            }
        }

        private void ApplyOverlay(IDictionary<string, string> ov, string profile)
        {
            try
            {
                // Discover consumers and deliver overlay
                var consumers = new List<IPolicyOverlayConsumer>();

                var health = _scene.RequestModuleInterface<RegionHealthMonitorModule>();
                if (health is IPolicyOverlayConsumer c1) consumers.Add(c1);

                var warmup = _scene.RequestModuleInterface<RegionWarmupModule>();
                if (warmup is IPolicyOverlayConsumer c2) consumers.Add(c2);

                var alerts = _scene.RequestModuleInterface<RegionWebhookAlertsModule>();
                if (alerts is IPolicyOverlayConsumer c3) consumers.Add(c3);

                var auto = _scene.RequestModuleInterface<RegionAutoHealModule>();
                if (auto is IPolicyOverlayConsumer c4) consumers.Add(c4);

                foreach (var c in consumers.Distinct())
                {
                    try { c.ApplyPolicyOverlay(ov, profile); }
                    catch (Exception e) { Log.Debug($"[POLICY] overlay error: {e.Message}"); }
                }

                Log.Info($"[POLICY] overlay applied profile={profile} targets={consumers.Count}");
            }
            catch (Exception e)
            {
                Log.Debug($"[POLICY] apply failed: {e.Message}");
            }
        }
    }
}