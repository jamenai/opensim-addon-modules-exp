# RegionAutoHeal

## Configuration
~~~ini
[Modules]
    RegionAutoHeal = enabled

[RegionAutoHeal]
    ; Sicherer Start: nur simulieren (Logs/Alerts), keine Änderungen
    DryRun = true
    ; Echte Script-Resets nur bei expliziter Freigabe
    EnableScriptReset = false
    ; Best-Effort-Drosselung „lauter“ Updater
    ThrottleHeavyUpdaters = true
    ThrottleThresholdUpdatesPerSec = 30
    ; Ab wann reagiert werden soll (Skriptfehler-Burst)
    ScriptErrorBurstThreshold = 25
    ; Sperrzeit zwischen Eingriffen (Sekunden)
    CooldownSec = 60
~~~