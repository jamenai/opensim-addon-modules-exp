# RegionMetricsExporter

## Configuration
~~~ini
[Modules]
    RegionMetricsExporter = enabled

[RegionMetricsExporter]
    ; HTTP pull endpoint for Prometheus (low overhead)
    HttpPort = 9109
    ; Bind only to loopback by default (recommended). Set 0.0.0.0 to expose network-wide.
    BindAddress = 127.0.0.1
    ; Metric name prefix
    MetricsPrefix = opensim_region_
    ; Include region name as a label
    IncludeRegionLabel = true
~~~

## Notes
Hinweise:
Dieses Modul liest HealthSamples vom RegionHealthMonitor (IRegionHealthBus). Stelle sicher, dass RegionHealthMonitor aktiv ist.
In Multi-Region-Prozessen sollte jede Region einen eigenen Port nutzen (oder du baust eine gemeinsame, prozessweite Export-Instanz).
Endpoint: http://BindAddress:HttpPort/metrics
Pull-only, minimaler Ressourcenverbrauch.