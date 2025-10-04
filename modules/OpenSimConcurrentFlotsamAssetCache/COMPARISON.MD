# COMPARISON

## Comparison to FlotsamAssetCache (ENG)

| Aspect | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelism | High parallelism for reads/writes; per-file write gate; optional multi-writer background engine; in-flight upstream de-duplication | Lower parallelism; centralized locks and single-writer patterns |
| Latency under load | Lower at peaks; non-blocking fast paths (weak/memory/file); short wait on in-flight writes | Higher at peaks due to serialization and lock contention |
| Thundering herd handling | In-flight de-duplication for upstream fetches; negative cache with pruning and TTL; small delay if write in progress | Simpler; may trigger redundant fetches and waits |
| Consistency/ordering | Atomic file replace/move; throttled LastAccessTime updates; scene-aware cleanup; guarded write finalization | Deterministic single-path writes; simpler ordering guarantees |
| Debugging/maintenance | More moving parts (weak/memory/file cache, negative cache, background writers, timers, in-flight map) | Simpler (single-path logic) |
| Resource usage | Higher (job engine, concurrent maps, timers; optional parallel file writes) | Lower (fewer components) |
| Memory footprint | Slightly higher (WeakReference map + optional memory cache + transient in-flight map) | Lower |
| Concurrency risk | Requires tuning (backoff, writer concurrency); mitigations in place (per-file gate, de-dup, capped negatives) | Lower risk by design |
| I/O behavior | Parallel I/O; atomic replace with optional .bak; overwrite moves where available; tiered directories | Serialized I/O; predictable, lower throughput |
| Expire/cleanup | Periodic cleanup; scene-aware keep-list; optional .bak cleanup; size-capped negative cache pruning | Simpler periodic cleanup; fewer edge cases |
| Negative cache | Bounded with expiry and sampling-based pruning to avoid unbounded growth | Typically simple/unbounded or absent |
| Serialization format | Versioned binary format with size caps to prevent corruption/OOM | Simpler/legacy formats |
| Upstream fetch | Integrated upstream fetch with single-flight (in-flight de-dup); fallback to negatives on miss | Usually direct or not integrated with cache layer |
| Fit – large load/cluster | Very good (scales, smooths spikes, reduces redundant upstream) | Limited |
| Fit – small setups | Possibly overkill | Very good (simple, frugal) |
| Overall | Optimized for throughput, concurrency, and reduced duplication at the cost of complexity | Optimized for simplicity and predictability |

## Vergleich zu FlotsamAssetCache (DEU)

| Aspekt | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelität | Hohe Parallelität; per-Datei-Schreibsperre; optional mehrere Writer; De-Dup bei Upstream-Anfragen | Geringere Parallelität; zentrale Locks/Single-Writer |
| Latenz unter Last | Niedriger bei Peaks; schnelle Pfade (Weak/Mem/File) ohne Blockieren; kurzer Wait bei laufendem Write | Höher bei Peaks durch Serialisierung und Locks |
| Thundering-Herd | Single-Flight für Upstream; Negative-Cache mit TTL/Pruning; kleine Verzögerung bei in-flight Writes | Einfacher; kann redundante Fetches verursachen |
| Konsistenz/Ordnung | Atomare Replace/Move-Strategien; gedrosselte LastAccessTime; szenenbewusste Bereinigung; abgesicherte Finalisierung | Deterministischer; einfacher zu überblicken |
| Fehlersuche/Wartung | Komplexer (mehr Caches, Writer, Timer, In-Flight-Map) | Einfacher (ein Pfad) |
| Ressourcenverbrauch | Höher (Job-Engine, Concurrent-Maps, Timer; optional parallele Datei-Writes) | Geringer |
| Speicherbedarf | Etwas höher (WeakReference-Map, optionaler Memory-Cache, temporäre In-Flight-Einträge) | Niedriger |
| Risiko Concurrency-Bugs | Erhöht, aber durch Backoff, per-Datei-Gate, De-Dup und begrenzten Negative-Cache mitigiert | Geringer |
| I/O-Verhalten | Paralleler I/O; atomare Replace-Option (.bak optional) und Overwrite-Move; stufige Verzeichnisse | Serieller I/O; planbarer, begrenzter Durchsatz |
| Expire/Cleanup | Periodisch; szenenbewusstes Schonverhalten; optionale .bak-Bereinigung; negatives Pruning mit Größenlimit | Einfachere Periodik; weniger Randfälle |
| Negative-Cache | Begrenzter Miss-Cache mit Ablauf/Pruning (Sampling der Ältesten) | Einfach/unbegrenzt oder nicht vorhanden |
| Serialisierungsformat | Versioniertes Binärformat mit Größenlimits (Schutz vor Korruption/OOM) | Einfacher/älter |
| Upstream-Fetch | Integrierter Upstream mit Single-Flight-De-Dup; Negative bei Miss | Meist direkt oder nicht im Cache integriert |
| Eignung – hohe Last/Cluster | Sehr gut (Skalierung, Spike-Glättung, weniger Doppel-Fetches) | Eingeschränkt |
| Eignung – kleine Setups | Mitunter Overkill | Sehr gut (einfach, sparsam) |
| Gesamtfazit | Auf Durchsatz, Parallelität und Vermeidung doppelter Upstream-Last optimiert, mit höherer Komplexität | Auf Einfachheit und Vorhersagbarkeit optimiert |

Hinweis: Nutze die Concurrent-Variante für stark parallelisierte Workloads, schnelle Datenträger und reduzierten Upstream-Druck; wähle Core, wenn Einfachheit, geringer Ressourcenbedarf und Wartbarkeit wichtiger sind.