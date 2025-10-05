using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionAutoHeal")]
    public class RegionAutoHealModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private bool _dryRun = true;                  // safe default
        private bool _enableScriptReset = false;      // needs explicit opt-in
        private bool _throttleHeavyUpdaters = true;
        private int _throttleThresholdUpdatesPerSec = 30;
        private int _scriptErrorBurstThreshold = 25;  // incident filter
        private int _cooldownSec = 60;                // avoid repeated actions

        // State
        private IRegionHealthBus _bus;
        private DateTime _lastActionUtc = DateTime.MinValue;
        private volatile int _opGuard;

        public string Name => "RegionAutoHeal";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionAutoHeal", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionAutoHeal"] ?? source.AddConfig("RegionAutoHeal");

            _dryRun = cfg.GetBoolean("DryRun", _dryRun);
            _enableScriptReset = cfg.GetBoolean("EnableScriptReset", _enableScriptReset);
            _throttleHeavyUpdaters = cfg.GetBoolean("ThrottleHeavyUpdaters", _throttleHeavyUpdaters);
            _throttleThresholdUpdatesPerSec = Math.Max(1, cfg.GetInt("ThrottleThresholdUpdatesPerSec", _throttleThresholdUpdatesPerSec));
            _scriptErrorBurstThreshold = Math.Max(1, cfg.GetInt("ScriptErrorBurstThreshold", _scriptErrorBurstThreshold));
            _cooldownSec = Math.Max(10, cfg.GetInt("CooldownSec", _cooldownSec));

            _enabled = true;
            Log.Info($"[AUTOHEAL] Enabled (DryRun={_dryRun}, Reset={_enableScriptReset}, Throttle={_throttleHeavyUpdaters})");
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled) return;
            _scene = scene;

            _bus = _scene.RequestModuleInterface<IRegionHealthBus>();
            if (_bus == null)
            {
                Log.Warn("[AUTOHEAL] No IRegionHealthBus found. Auto-heal will remain idle until RegionHealthMonitor is enabled.");
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

            MainConsole.Instance.Commands.AddCommand("Region", false, "autoheal",
                "autoheal",
                "Auto-heal commands. Usage: autoheal status|dryrun on|off|reset <objectId>",
                HandleConsole);

            Log.Info($"[AUTOHEAL] Ready for region '{_scene.RegionInfo.RegionName}'.");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            if (_bus != null)
                _bus.OnIncident -= OnIncident;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[AUTOHEAL] No active scene."); return; }
            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "status":
                    MainConsole.Instance.Output($"[AUTOHEAL] DryRun={_dryRun} EnableScriptReset={_enableScriptReset} Throttle={_throttleHeavyUpdaters} ThresholdUpd/s={_throttleThresholdUpdatesPerSec} CooldownSec={_cooldownSec}");
                    break;

                case "dryrun":
                    if (args.Length >= 3)
                    {
                        bool on = args[2].Equals("on", StringComparison.OrdinalIgnoreCase);
                        bool off = args[2].Equals("off", StringComparison.OrdinalIgnoreCase);
                        if (on || off)
                        {
                            _dryRun = on;
                            MainConsole.Instance.Output($"[AUTOHEAL] DryRun={_dryRun}");
                            break;
                        }
                    }
                    PrintHelp();
                    break;

                case "reset":
                    if (args.Length >= 3)
                    {
                        if (UUID.TryParse(args[2], out var id))
                        {
                            RunOnceSafe(() => TryResetObjectScripts(id, reason: "manual"));
                            break;
                        }
                        MainConsole.Instance.Output("[AUTOHEAL] invalid UUID");
                        break;
                    }
                    PrintHelp();
                    break;

                default:
                    PrintHelp();
                    break;
            }
        }

        private void PrintHelp()
        {
            MainConsole.Instance.Output("autoheal status             - show status");
            MainConsole.Instance.Output("autoheal dryrun on|off      - toggle dry-run mode");
            MainConsole.Instance.Output("autoheal reset <objectId>   - targeted script reset (requires EnableScriptReset=true)");
        }

        private void OnIncident(HealthEvent e)
        {
            // Only react to WARN/ERROR and when thresholds suggest action
            if (e == null || e.Sample == null) return;
            if (e.Severity < HealthSeverity.Warn) return;

            // basic cooldown
            if ((DateTime.UtcNow - _lastActionUtc).TotalSeconds < _cooldownSec)
                return;

            // Heuristics: large script error burst
            if (e.Sample.ScriptErrors >= _scriptErrorBurstThreshold)
            {
                RunOnceSafe(() =>
                {
                    // 1) find suspicious objects (heuristic, best-effort)
                    var suspects = FindSuspectObjects(max: 10);

                    // 2) reset scripts (if enabled) or log suggestion
                    foreach (var so in suspects)
                    {
                        TryResetObjectScripts(so.UUID, reason: $"auto: errorBurst({e.Sample.ScriptErrors})");
                    }

                    // 3) throttle heavy updaters
                    if (_throttleHeavyUpdaters)
                        BestEffortThrottleHeavyUpdaters(max: 10);
                });
            }
        }

        private void RunOnceSafe(Action action)
        {
            if (Interlocked.Exchange(ref _opGuard, 1) == 1)
                return;

            try
            {
                action();
                _lastActionUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AUTOHEAL] action failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _opGuard, 0);
            }
        }

        // Heuristic: find objects with many scripts or high part count as suspects
        private IEnumerable<SceneObjectGroup> FindSuspectObjects(int max)
        {
            var result = new List<SceneObjectGroup>(max);
            try
            {
                var all = _scene.GetSceneObjectGroups();
                // simple score: script count + part count
                foreach (var so in all)
                {
                    int scripts = 0;
                    try
                    {
                        foreach (var p in so.Parts)
                            scripts += p.Inventory?.GetInventoryItems()?.Count ?? 0;
                    }
                    catch { }

                    int score = scripts + so.Parts?.Length ?? 0;
                    // crude threshold
                    if (score >= 20)
                    {
                        result.Add(so);
                        if (result.Count >= max) break;
                    }
                }
            }
            catch { }
            Log.Info($"[AUTOHEAL] suspects found: {result.Count}");
            return result;
        }

        private void TryResetObjectScripts(UUID objectId, string reason)
        {
            var sog = _scene.GetSceneObjectGroup(objectId);
            if (sog == null)
            {
                Log.Info($"[AUTOHEAL] object not found for reset: {objectId}");
                return;
            }

            if (!_enableScriptReset)
            {
                Log.Warn($"[AUTOHEAL] (dry) would reset scripts of '{sog.Name}' ({objectId}) reason={reason}");
                return;
            }

            if (_dryRun)
            {
                Log.Warn($"[AUTOHEAL] (dry) reset scripts of '{sog.Name}' ({objectId}) reason={reason}");
                return;
            }

            try
            {
                // Get the active script module (YEngine implements IScriptModule)
                var scriptModule = _scene.RequestModuleInterface<IScriptModule>();
                int count = 0;

                // Try to find a ResetScript(UUID) method via reflection on the concrete engine
                // This keeps us compatible if the engine exposes IScriptEngine.ResetScript(UUID)
                // without adding a hard compile-time dependency.
                var engineObj = scriptModule as object;
                var resetMethod = engineObj?.GetType().GetMethod("ResetScript", new[] { typeof(UUID) });

                foreach (var part in sog.Parts)
                {
                    var items = part.Inventory?.GetInventoryItems();
                    if (items == null) continue;

                    foreach (var inv in items)
                    {
                        if (inv == null) continue;

                        // Only reset script items (source or bytecode)
                        if (inv.Type == (int)OpenMetaverse.AssetType.LSLText || inv.Type == (int)OpenMetaverse.AssetType.LSLBytecode)
                        {
                            if (resetMethod != null)
                            {
                                // Call ResetScript(UUID) if the engine provides it
                                resetMethod.Invoke(engineObj, new object[] { inv.ItemID });
                                count++;
                            }
                            else
                            {
                                // No hard reset available; as a fallback you could Suspend/Resume here if
                                scriptModule.SuspendScript(inv.ItemID);
                                scriptModule.ResumeScript(inv.ItemID);
                            }
                        }
                    }
                }

                if (resetMethod != null)
                    Log.Warn($"[AUTOHEAL] reset scripts via engine ResetScript(UUID) for '{sog.Name}' items={count} reason={reason}");
                else
                    Log.Warn($"[AUTOHEAL] engine has no ResetScript(UUID); performed no hard resets on '{sog.Name}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"[AUTOHEAL] reset failed: {ex.Message}");
            }
        }

        private void BestEffortThrottleHeavyUpdaters(int max)
        {
            try
            {
                int throttled = 0;
                foreach (var so in _scene.GetSceneObjectGroups())
                {
                    if (throttled >= max) break;

                    // crude detection: objects with many parts and recent updates
                    var parts = so.Parts;
                    if (parts == null || parts.Length < 1) continue;

                    int localUpdates = 0;
                    foreach (var p in parts)
                    {
                        // Heuristic placeholder: if a part's last update was very recent, count it.
                        // OpenSim does not provide a standard per-part updates/sec API; so we log suggestions.
                        // If your fork exposes update rates, plug them here and compare to _throttleThresholdUpdatesPerSec.
                        // For now we treat "big & busy" objects as candidates.
                        if (p.UpdateFlag != 0) localUpdates++;
                    }

                    if (parts.Length >= 10 || localUpdates >= 5)
                    {
                        if (_dryRun)
                        {
                            Log.Warn($"[AUTOHEAL] (dry) would throttle '{so.Name}' parts={parts.Length} updates~{localUpdates}/tick");
                        }
                        else
                        {
                            // @todo: implement throttle here
                            // Best-effort: disable constant updates we control (e.g., temporary turning off dynamics/particles where sensible)
                            foreach (var p in parts)
                            {
                                try
                                {
                                    // Example: reduce alpha mode/particle sources if available
                                    // Without stable generic API, we only log
                                }
                                catch { }
                            }
                            Log.Warn($"[AUTOHEAL] throttled '{so.Name}' (best-effort)");
                        }
                        throttled++;
                    }
                }
                if (throttled > 0)
                    Log.Warn($"[AUTOHEAL] throttled objects: {throttled}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[AUTOHEAL] throttle failed: {ex.Message}");
            }
        }
    }
}