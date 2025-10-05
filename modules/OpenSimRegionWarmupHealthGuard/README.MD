# OpenSimRegionWarmupHealthGuard

A cohesive toolkit of OpenSimulator region modules to improve startup experience, visibility, and resilience of regions. It combines warmup routines, health monitoring, metrics export, webhook alerts, auto‑healing, and a small policy engine for time‑based config overlays.

- Project type: OpenSim region modules (Mono.Addins)
- Scope: Operations and reliability
- Status: Community modules (alpha) – please test in staging before production

Contents (modules in this assembly):
- RegionHealthMonitor
- RegionMetricsExporter
- RegionWebhookAlerts
- RegionAutoHeal
- RegionWarmup
- RegionPolicyEngine

---

## Key Capabilities

- Faster, safer startups
    - RegionWarmup primes terrain access, lightly pre‑touches assets, and warms script engines.
- Live health visibility
    - RegionHealthMonitor polls sim stats, thresholds incidents, and can export CSV.
- Metrics for observability
    - RegionMetricsExporter exposes Prometheus-compatible /metrics over HTTP.
- Actionable alerts
    - RegionWebhookAlerts batches/rate‑limits JSON webhooks to your alerting endpoint.
- Self-healing helpers
    - RegionAutoHeal (opt‑in) can reset scripts or throttle “noisy” updaters after incidents.
- Scheduled behavior
    - RegionPolicyEngine applies time/profile-based overlays to participating modules.

---

## Quick Start

1) Build alongside OpenSim so the modules resolve OpenSim.Region.Framework.* assemblies.

2) Deploy the produced assembly to your OpenSim bin/ (or RegionModules) directory.

3) Enable modules in OpenSim.ini and include the shared module config:

~~~ini
[Modules] 
    RegionHealthMonitor = enabled 
    RegionMetricsExporter = enabled 
    RegionWebhookAlerts = enabled 
    RegionAutoHeal = enabled 
    RegionWarmup = enabled 
    RegionPolicyEngine = enabled

    Include-RegionWarmupHealthGuard = "config-include/RegionWarmupHealthGuard.ini"
~~~


4) Adjust settings in config-include/RegionWarmupHealthGuard.ini (see example provided).

---

## Configuration Overview

A single ini groups all modules for convenience. Typical keys:

- RegionHealthMonitor
    - HealthIntervalSec, WarnScriptTimeMs, WarnPhysicsTimeMs, WarnNetTimeMs, WarnScriptErrors
    - MetricsExportFile (optional CSV)

- RegionMetricsExporter
    - HttpPort, BindAddress, MetricsPrefix, IncludeRegionLabel

- RegionWebhookAlerts
    - Url, MinSeverity (Trace|Info|Warn|Error), BatchWindowSec, RateLimitPerMin
    - ConnectTimeoutMs, SendTimeoutMs, PayloadFields

- RegionAutoHeal
    - DryRun (default true), EnableScriptReset (opt‑in), ThrottleHeavyUpdaters
    - ThrottleThresholdUpdatesPerSec, ScriptErrorBurstThreshold, CooldownSec

- RegionWarmup
    - WarmupOnRegionLoaded, TouchTerrain, PreloadAssets, PrimeScriptVM
    - DeepWarmupLimit, DeepWarmupDelaySec

- RegionPolicyEngine
    - Enabled, CheckIntervalSec, Profiles
    - <Profile>.Cron (simple hour window: e.g., 20-6), <Profile>.Overrides (Key=Value;Key=Value)

See the full example at bin/config-include/RegionWarmupHealthGuard.ini.example.

---

## Module Details

### RegionHealthMonitor
- Polls sim stats and emits incidents on threshold breaches.
- Console:
    - health status
    - health export
- Optional CSV export via MetricsExportFile.

### RegionMetricsExporter
- HTTP pull endpoint for Prometheus/Grafana (low overhead).
- Endpoint: http://BindAddress:HttpPort/metrics
- Requires RegionHealthMonitor to feed samples.

### RegionWebhookAlerts
- Batches and rate‑limits JSON webhook alerts (e.g., to n8n).
- Select which fields to include (PayloadFields).
- Subscribes to RegionHealthMonitor events.

### RegionAutoHeal
- Dry‑run by default (no changes unless configured).
- Can reset scripts on targeted objects (opt‑in) and best‑effort throttle “heavy updaters” after incidents.
- Console:
    - autoheal status
    - autoheal dryrun on|off
    - autoheal reset <objectId>

### RegionWarmup
- On region load (optional): terrain touch, limited asset pre‑touch, prime script VM.
- Optional deep warmup scan after a delay.
- Console:
    - warmup status
    - warmup run

### RegionPolicyEngine
- Applies profile/time-based overrides to modules that choose to consume them.
- Profiles with simplified hour windows (e.g., Nightly.Cron = * * 20-6).
- Console:
    - policy status
    - policy apply <Profile>
    - policy dryrun <Profile>

---

## Best Practices

- Start conservative:
    - Keep AutoHeal in DryRun=true until you’re confident with actions.
    - Expose metrics on 127.0.0.1 and reverse-proxy if needed.
- Staging first:
    - Validate thresholds and alert volume before production.
- Observe and iterate:
    - Use Prometheus/Grafana dashboards to tune Warn* thresholds.
    - Adjust webhook batch/rate limits to avoid alert noise.

---

## Example Include File

Place at bin/config-include/RegionWarmupHealthGuard.ini (use the provided example as a base and tailor to your environment).

---

## License

MIT – see LICENSE.