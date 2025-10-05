# RegionHealthMonitor


## Configuration
~~~ini
[Modules]
    RegionHealthMonitor = enabled

[RegionHealthMonitor]
    HealthIntervalSec = 30
    WarnScriptTimeMs = 12.0
    WarnPhysicsTimeMs = 6.0
    WarnNetTimeMs = 6.0
    WarnScriptErrors = 10
    ; Optional CSV export (leave empty to disable)
    MetricsExportFile =
~~~

## Console commands

health status
health export