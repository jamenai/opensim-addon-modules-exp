# RegionPolicyEngine

## Configuration
~~~ini
[Modules]
    RegionPolicyEngine = enabled

[RegionPolicyEngine]
    Enabled = true
    ; Prüfintervall (Sekunden)
    CheckIntervalSec = 60

    ; Liste von Profilen (kommasepariert)
    Profiles = Nightly,Event

    ; Einfaches Stundenfenster (Beispiele):
    ; 20-6  -> zwischen 20:00 und 06:59
    ; 8-18  -> zwischen 08:00 und 18:59
    ; 22    -> genau um 22 Uhr
    Nightly.Cron = * * 20-6
    ; Key=Value;Key=Value (Overlays, die die Module verstehen müssen)
    Nightly.Overrides = HealthIntervalSec=60; DeepWarmupLimit=400; MinSeverity=Error

    Event.Cron =
    Event.Overrides = ThrottleHeavyUpdaters=false
~~~

## Notes

Die Overlays sind bewusst generisch (Key=Value). Ein Modul, das Overlays unterstützen soll, implementiert optional IPolicyOverlayConsumer und mappt relevante Keys auf interne Settings.
Die mitgelieferten Module funktionieren auch ohne Overlay-Unterstützung. Du kannst Overlay-Support schrittweise in einzelnen Modulen ergänzen (z. B. HealthMonitor: HealthIntervalSec, WebhookAlerts: MinSeverity, Warmup: DeepWarmupLimit, AutoHeal: ThrottleHeavyUpdaters).
Das Cron-Feld ist hier stark vereinfacht (Stundenfenster). Für echtes Cron-Verhalten könnte später ein Parser ergänzt werden.