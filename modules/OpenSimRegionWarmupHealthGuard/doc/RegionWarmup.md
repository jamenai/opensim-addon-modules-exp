# RegionWarmup

## Configuration

~~~ini
[Modules]
    RegionWarmup = enabled

[RegionWarmup]
    ; Sofortiges Warmup bei Regionstart
    WarmupOnRegionLoaded = true
    ; Einzelne Warmup-Schritte
    TouchTerrain = true
    PreloadAssets = true
    PrimeScriptVM = true
    ; Optionaler tiefer Warmup-Scan (Anzahl Objekte) nach Verzögerung
    DeepWarmupLimit = 200
    DeepWarmupDelaySec = 30
~~~