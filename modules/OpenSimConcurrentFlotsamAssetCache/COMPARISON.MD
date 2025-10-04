# COMPARISON

## Comparison to FlotsamAssetCache (ENG)

| Aspect | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelism | High parallelism for reads/writes with contention backoff; lock-free maps where possible | Lower parallelism; centralized locks and single-writer patterns |
| Latency under load | Lower at peaks; background writer and non-blocking reads reduce head-of-line blocking | Higher at peaks due to serialization and lock contention |
| Thundering herd handling | In-flight write detection for same file; optional exponential backoff on readers | Simpler; may trigger redundant fetches and waits |
| Consistency/ordering | Careful ordering for touch/replace; atomic replace/move on disk to avoid partials | More deterministic single-path writes; simpler ordering |
| Debugging/maintenance | More complex (multiple caches: weak/memory/file, negative cache, timers) | Simpler (single-path logic) |
| Resource usage | Higher (job engine, timers, concurrent structures) | Lower (fewer moving parts) |
| Memory footprint | Slightly higher (WeakReference map + optional memory cache) | Lower |
| Concurrency risk | More tuning required (starvation/livelock mitigated by backoff caps) | Lower risk by design |
| I/O behavior | Parallel I/O; atomic replace (with optional .bak) and overwrite moves | Serialized I/O; predictable but lower throughput |
| Expire/cleanup | Periodic timer with scene-aware protection and optional .bak cleanup; LastAccessTime updates throttled | Simpler periodic cleanup; fewer edge cases |
| Negative cache | Bounded negative cache with pruning (expiry + oldest sampling) | Typically unbounded/simple or absent |
| Serialization format | Versioned binary format with size caps to guard corruption/OOM | Simpler/legacy formats |
| Fit – large load/cluster | Very good (scales, smooths spikes) | Limited |
| Fit – small setups | Possibly overkill | Very good (simple, frugal) |
| Overall | Optimized for throughput and concurrency with higher complexity | Optimized for simplicity and predictability |

## Vergleich zu FlotsamAssetCache (DEU)

| Aspekt | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelität | Hohe Parallelität bei Reads/Writes; Backoff bei Contention; lock-arme Strukturen | Geringere Parallelität; zentrale Locks/Single-Writer |
| Latenz unter Last | Niedriger bei Peaks; Writer-Worker und non-blocking Reads | Höher bei Peaks durch Serialisierung und Locks |
| Thundering-Herd | Erkennung paralleler Writes je Datei; optionaler Exponential-Backoff für Leser | Einfacher; kann zu redundanten Fetches führen |
| Konsistenz/Ordnung | Sorgfältige Ordnung von Touch/Replace; atomare Replace/Move-Strategien | Deterministischer; einfacher zu überblicken |
| Fehlersuche/Wartung | Komplexer (Weak/Mem/File-Cache, Negative-Cache, Timer) | Einfacher (ein Pfad) |
| Ressourcenverbrauch | Höher (Job-Engine, Timer, Concurrent-Maps) | Geringer |
| Speicherbedarf | Etwas höher (WeakReference-Map + optionaler Memory-Cache) | Niedriger |
| Risiko Concurrency-Bugs | Höher, aber durch begrenzten Backoff/Überwachung mitigiert | Geringer |
| I/O-Verhalten | Paralleler I/O; atomare Replace-Option (.bak optional) und Overwrite-Move | Serieller I/O; planbarer, begrenzter Durchsatz |
| Expire/Cleanup | Periodischer Timer; szenenbewusste Schonung; optionale .bak-Bereinigung; gedrosselte LastAccessTime-Updates | Einfachere Periodik; weniger Randfälle |
| Negative-Cache | Begrenzter Miss-Cache mit Pruning (Ablauf + Sampling der Ältesten) | Einfach/unbegrenzt oder nicht vorhanden |
| Serialisierungsformat | Versioniertes Binärformat mit Größenlimits (Schutz vor Korruption/OOM) | Einfacher/älter |
| Eignung – hohe Last/Cluster | Sehr gut (Skalierung, Peak-Glättung) | Eingeschränkt |
| Eignung – kleine Setups | Mitunter Overkill | Sehr gut (einfach, sparsam) |
| Gesamtfazit | Auf Durchsatz und Parallelität optimiert bei höherer Komplexität | Auf Einfachheit und Vorhersagbarkeit optimiert |

Hinweis: Wähle Concurrent für stark parallelisierte Workloads und schnelle Datenträger; nutze Core, wenn Einfachheit, geringer Ressourcenbedarf und Wartbarkeit im Vordergrund stehen.