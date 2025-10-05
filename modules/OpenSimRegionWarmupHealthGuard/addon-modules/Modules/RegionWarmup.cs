using System;
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

[assembly: Addin("RegionWarmup", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("Region warmup: terrain touch, limited asset pre-touch, prime script VM, optional deep warmup tick.")]
[assembly: AddinAuthor("Christopher Händler")]

namespace OpenSimRegionWarmupHealthGuard.Modules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionWarmup")]
    public class RegionWarmupModule : INonSharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _enabled;

        // Config
        private bool _warmupOnRegionLoaded = true;
        private bool _preloadAssets = true;
        private bool _primeScriptVm = true;
        private bool _touchTerrain = true;
        private int _deepWarmupLimit = 200;
        private int _deepWarmupDelaySec = 30;

        private Timer _deepTimer;

        public string Name => "RegionWarmup";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var modules = source.Configs["Modules"];
            if (modules == null) return;

            var status = modules.GetString("RegionWarmup", string.Empty);
            if (!string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)) return;

            var cfg = source.Configs["RegionWarmup"] ?? source.AddConfig("RegionWarmup");

            _warmupOnRegionLoaded = cfg.GetBoolean("WarmupOnRegionLoaded", _warmupOnRegionLoaded);
            _preloadAssets = cfg.GetBoolean("PreloadAssets", _preloadAssets);
            _primeScriptVm = cfg.GetBoolean("PrimeScriptVM", _primeScriptVm);
            _touchTerrain = cfg.GetBoolean("TouchTerrain", _touchTerrain);
            _deepWarmupLimit = Math.Max(0, cfg.GetInt("DeepWarmupLimit", _deepWarmupLimit));
            _deepWarmupDelaySec = Math.Max(0, cfg.GetInt("DeepWarmupDelaySec", _deepWarmupDelaySec));

            _enabled = true;
            Log.Info("[WARMUP] RegionWarmup enabled");
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

            MainConsole.Instance.Commands.AddCommand("Region", false, "warmup",
                "warmup",
                "Warmup commands. Usage: warmup run|status",
                HandleConsole);

            if (_warmupOnRegionLoaded)
            {
                RunWarmup(tag: "startup");
            }

            if (_deepWarmupLimit > 0 && _deepWarmupDelaySec > 0)
            {
                _deepTimer = new Timer(_deepWarmupDelaySec * 1000) { AutoReset = false };
                _deepTimer.Elapsed += (_, __) =>
                {
                    try
                    {
                        DeepWarmup(_deepWarmupLimit);
                    }
                    catch (Exception e)
                    {
                        Log.Debug($"[WARMUP] deep warmup failed: {e.Message}");
                    }
                };
                _deepTimer.Start();
            }

            Log.Info($"[WARMUP] Ready for region '{_scene.RegionInfo.RegionName}'.");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            try { _deepTimer?.Stop(); _deepTimer?.Dispose(); } catch { }
            _deepTimer = null;

            _scene.UnregisterModuleInterface(this);
            _scene = null;
        }

        public void Close()
        {
            try { _deepTimer?.Stop(); _deepTimer?.Dispose(); } catch { }
            _deepTimer = null;
        }

        private void HandleConsole(string module, string[] args)
        {
            if (_scene == null) { MainConsole.Instance.Output("[WARMUP] No active scene."); return; }
            if (args.Length < 2)
            {
                MainConsole.Instance.Output("warmup status   - show config");
                MainConsole.Instance.Output("warmup run      - run warmup now");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "status":
                    MainConsole.Instance.Output($"[WARMUP] OnLoad={_warmupOnRegionLoaded} Terrain={_touchTerrain} PreloadAssets={_preloadAssets} PrimeVM={_primeScriptVm} DeepLimit={_deepWarmupLimit} DeepDelay={_deepWarmupDelaySec}s");
                    break;
                case "run":
                    RunWarmup(tag: "manual");
                    MainConsole.Instance.Output("[WARMUP] Warmup completed.");
                    break;
                default:
                    MainConsole.Instance.Output("warmup status|run");
                    break;
            }
        }

        private void RunWarmup(string tag)
        {
            if (_touchTerrain)
            {
                try
                {
                    var t = _scene.TerrainChannel;
                    var x = _scene.RegionInfo.RegionSizeX / 2;
                    var y = _scene.RegionInfo.RegionSizeY / 2;
                    var h = t.GetNormalizedGroundHeight(x, y);
                    Log.Debug($"[WARMUP] terrain touch ok (h={h:0.00})");
                }
                catch (Exception e) { Log.Debug($"[WARMUP] terrain touch skipped: {e.Message}"); }
            }

            if (_preloadAssets)
            {
                try
                {
                    foreach (var so in _scene.GetSceneObjectGroups().Take(50))
                    {
                        var _ = so.Parts?.Length;
                        foreach (var p in so.Parts)
                            _ = p.Inventory?.GetInventoryItems()?.Count;
                    }
                    Log.Debug("[WARMUP] limited asset pre-touch ok");
                }
                catch (Exception e) { Log.Debug($"[WARMUP] pre-touch skipped: {e.Message}"); }
            }

            if (_primeScriptVm)
            {
                try
                {
                    var engine = _scene.RequestModuleInterface<IScriptModule>();
                    if (engine != null)
                    {
                        int cnt = engine.GetScriptCount();
                        Log.Debug($"[WARMUP] script VM primed (scripts={cnt})");
                    }
                }
                catch (Exception e) { Log.Debug($"[WARMUP] script VM prime skipped: {e.Message}"); }
            }

            Log.Info($"[WARMUP] Warmup done ({tag})");
        }

        private void DeepWarmup(int limit)
        {
            try
            {
                int scanned = 0;
                foreach (var so in _scene.GetSceneObjectGroups())
                {
                    var parts = so.Parts;
                    if (parts == null) continue;
                    foreach (var p in parts)
                    {
                        var _ = p.Inventory?.GetInventoryItems()?.Count;
                    }
                    scanned++;
                    if (scanned >= limit) break;
                }
                Log.Info($"[WARMUP] deep warmup scanned objects={scanned}/{limit}");
            }
            catch (Exception e)
            {
                Log.Debug($"[WARMUP] deep warmup error: {e.Message}");
            }
        }
    }
}