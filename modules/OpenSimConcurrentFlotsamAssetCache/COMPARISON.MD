# COMPARISON

## Comparison to FlotsamAssetCache (ENG)

| Aspect | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelism | High parallelism for reads/writes; reduced contention | Lower parallelism; possible bottlenecks due to locks/single-writer |
| Latency under load | Lower at peaks; higher throughput | Higher at peaks; waits due to serialization |
| Thundering herd handling | Better coordination of concurrent requests for the same asset | Simpler but may trigger redundant fetches on concurrent requests |
| Consistency/ordering | More complex ordering of touch/replace; careful sync needed | More deterministic; fewer race conditions |
| Debugging/maintenance | More complex, harder to debug | Simpler, easier to reason about |
| Resource usage | Higher (more threads/concurrent structures) | Lower overhead |
| Memory footprint | Slightly higher (concurrent data structures, coordination) | Lower |
| Concurrency risk | Increased risk (livelock/starvation if misconfigured) | Lower risk |
| I/O behavior | More parallel I/O, can better utilize fast storage | More serialized/controlled I/O; predictable but limited throughput |
| Expire/cleanup interaction | More sensitive to LastAccessTime/ordering with parallel updates | Simpler expire logic due to consistent timestamps |
| Fit – large load/cluster | Very good (scales, smooths spikes) | Limited |
| Fit – small setups | May be overkill | Very good (simple, frugal) |
| Overall | Better for high parallelism and throughput with higher complexity | Better for simplicity, predictability, and minimal resources |

## Vergleich zu FlotsamAssetCache (DEU)

| Aspekt | ConcurrentFlotsamAssetCache | FlotsamAssetCache (Core) |
|---|---|---|
| Parallelität | Hohe Parallelität bei Reads/Writes; weniger Contention | Geringere Parallelität; mögliche Engpässe durch Locks/Single-Writer |
| Latenz unter Last | Niedriger bei Peaks; bessere Durchsatzraten | Höher bei Peaks; Wartezeiten durch Serialisierung |
| Thundering herd Handling | Bessere Koordination gleichzeitiger Zugriffe auf dasselbe Asset | Einfachere, aber potenziell redundante Zugriffe bei gleichzeitigen Requests |
| Konsistenz/Ordnung | Komplexere Ordnung von Touch/Replace; sorgfältige Synchronisation nötig | Deterministischer und einfacher; weniger Race-Conditions |
| Fehlersuche/Wartung | Komplexer, schwieriger zu debuggen | Einfacher, besser nachvollziehbar |
| Ressourcenverbrauch | Höher (mehr Threads/Concurrent-Strukturen) | Geringer (weniger Overhead) |
| Speicherbedarf | Etwas höher (Concurrent-Datenstrukturen, Koordination) | Niedriger |
| Risiko von Concurrency-Bugs | Erhöht (Livelock/Starvation bei falscher Strategie) | Geringer |
| I/O-Verhalten | Mehr paralleler I/O, potenziell bessere Nutzung schneller Speicher | Serieller/koordinierter I/O, planbarer aber limitierter Durchsatz |
| Expire-/Cleanup-Interaktion | Sensibler bzgl. LastAccessTime/Ordnung bei parallelen Updates | Einfachere Expire-Logik durch konsistentere Zeitstempel |
| Eignung – große Last/Cluster | Sehr gut (Skalierung, Spitzen abfedern) | Eingeschränkt |
| Eignung – kleine Setups | Overkill möglich | Sehr gut (einfach, genügsam) |
| Gesamtfazit | Besser für hohe Parallelität und Durchsatz mit höherer Komplexität | Besser für Einfachheit, Vorhersagbarkeit und geringen Ressourcenbedarf |

Hinweis: Wähle Concurrent für stark parallelisierte Workloads; nutze Core, wenn Einfachheit und Stabilität wichtiger sind als maximale Skalierung.