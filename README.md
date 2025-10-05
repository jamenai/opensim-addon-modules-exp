# OpenSim Add-on Modules

Curated add-on modules for OpenSimulator (OpenSim). Each module is focused on a specific task, comes with example configuration, and integrates via standard OpenSim Region Framework APIs.

- License: MIT
- Status: Community modules (alpha). Not yet tested in production environments.
- Compatibility: Typical OpenSim builds using Mono.Addins and OpenSim.Region.Framework.*

> Note: These modules have not been production-tested by the author. Use at your own discretion and test in staging first.

---

## Modules Overview

| Module | Description | Key Features | Docs |
|---|---|---|---|
| SceneSnapshot | Automatic region snapshots (OAR) with retention and one-step restore, executed inside the simulator. | Interval-based snapshots; hourly/daily retention; console commands (create/list/restore/prune/status); atomic writes; optional merge-restore. | Folder: [modules/OpenSimSceneSnapshot](modules/OpenSimSceneSnapshot) • README: [modules/OpenSimSceneSnapshot/README.MD](modules/OpenSimSceneSnapshot/README.MD) • Config: [SceneSnapshot.ini.example](modules/OpenSimSceneSnapshot/bin/config-include/SceneSnapshot.ini.example) |
| ConcurrentFlotsamAssetCache | High-throughput asset cache with multi-layer design, in-flight de-duplication and safe on-disk persistence. | WeakRef/memory/file cache layers; atomic replace/move; negative cache; periodic cleanup; console tooling; upstream de-dup. | Folder: [modules/OpenSimConcurrentFlotsamAssetCache](modules/OpenSimConcurrentFlotsamAssetCache) • README: [modules/OpenSimConcurrentFlotsamAssetCache/README.MD](modules/OpenSimConcurrentFlotsamAssetCache/README.MD) • Commands: [COMMANDS.MD](modules/OpenSimConcurrentFlotsamAssetCache/COMMANDS.MD) • Migration: [MIGRATION.MD](modules/OpenSimConcurrentFlotsamAssetCache/MIGRATION.MD) • Comparison: [COMPARISON.MD](modules/OpenSimConcurrentFlotsamAssetCache/COMPARISON.MD) • Config: [ConcurrentFlotsamAssetCache.ini.example](modules/OpenSimConcurrentFlotsamAssetCache/bin/config-include/ConcurrentFlotsamAssetCache.ini.example) |
| OpenSimRegionWarmupHealthGuard | Region warmup, health monitoring and self-healing toolset (multiple region modules in one assembly). | HealthMonitor with thresholds + CSV; Prometheus metrics exporter; Webhook alerts (batch + rate-limit); Region warmup (terrain touch, asset pre-touch, VM prime); Auto-heal (dry-run by default); Policy engine for time-based overlays. | Folder: [modules/OpenSimRegionWarmupHealthGuard](modules/OpenSimRegionWarmupHealthGuard) • Docs: [doc/RegionHealthMonitor.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionHealthMonitor.md) • [doc/RegionMetricsExporter.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionMetricsExporter.md) • [doc/RegionWebhookAlerts.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionWebhookAlerts.md) • [doc/RegionWarmup.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionWarmup.md) • [doc/RegionAutoHeal.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionAutoHeal.md) • [doc/RegionPolicyEngine.md](modules/OpenSimRegionWarmupHealthGuard/doc/RegionPolicyEngine.md) |

---

## Quick Start

1) Build
- Build these modules alongside your OpenSim solution so they reference the standard OpenSim Region Framework assemblies.

2) Deploy
- Copy the compiled assemblies to your OpenSim bin/ (or RegionModules) directory as you would for any add-on.

3) Configure
- Enable modules in your OpenSim.ini [Modules] section.
- Include the matching config file(s) from each module’s bin/config-include folder.

4) Restart OpenSim
- Restart to let Mono.Addins discover and load the modules.

---

## Enable Examples

SceneSnapshot (OpenSim.ini):

~~~
[Modules] 
    SceneSnapshot = enabled 
    Include-SceneSnapshot = "config-include/SceneSnapshot.ini"
~~~

ConcurrentFlotsamAssetCache (OpenSim.ini):

~~~
[Modules] 
    AssetCaching = ConcurrentFlotsamAssetCache 
    Include-ConcurrentFlotsamCache = "config-include/ConcurrentFlotsamAssetCache.ini"
~~~

---

## Notes and Recommendations

- Validation first: Please evaluate in a staging or test region before deploying to production.
- Backups: Keep external/offsite backups even when using SceneSnapshot for frequent in-sim snapshots.
- Storage: For the asset cache, use reliable storage that supports atomic renames/replaces; monitor disk usage and cleanup cycles.
- Contributions: Feedback and PRs for fixes and hardening are welcome.

---

## License

MIT License. See [LICENSE](LICENSE) for details.