# MIGRATION

This document highlights conditions and considerations when migrating to ConcurrentFlotsamAssetCache. It focuses on cache behavior, configuration, and operational impacts.

## English

## 1. Preconditions and Compatibility
- Runtime: Requires a modern .NET/Mono environment matching your OpenSim build. Ensure File.Replace and optional overwrite move behavior are supported on your platform.
- Storage: Ensure the cache directory is on a filesystem that supports reliable atomic renames/replaces. On networked storage, semantics may differ.
- Permissions: The process must have read/write/create/delete permissions for the cache directory (including subfolders and temporary files).
- Time attributes: LastAccessTime updates are used (throttled). If your filesystem mounts with noatime or ignores access time updates, cleanup behavior will rely more on periodic scans.

## 2. Cache Directory Layout
- Tiered structure: Files are distributed into tiered subdirectories based on ID prefixes. Ensure:
    - CacheDirectoryTiers ≥ 1 and ≤ 3.
    - CacheDirectoryTierLength between 1 and 4.
- Moving from Core: If you have an existing core cache, you can:
    - Keep the directory but allow the new module to repopulate progressively, or
    - Start with a clean directory to avoid mixing formats.
- Backups: If enabling atomic replace backups (.bak), allow extra disk space and optionally schedule cleanup.

## 3. File Format and Serialization
- Versioned binary format with size caps for strings and data to guard against corruption and out-of-memory.
- Existing files in unknown/old formats will be skipped and may be deleted upon read failure to allow fresh caching.
- Ensure Deserialize caps (max string length, max data size) fit your asset profile. Too small values may reject valid assets; too large may increase memory pressure.

## 4. Memory and Weak Reference Cache
- WeakReference layer avoids strong memory retention of assets but speeds repeated hits.
- Optional memory cache increases RAM usage but reduces I/O latency. Size is indirectly controlled by expiration.
- Garbage Collection: Weak references can expire at any time; logic assumes this and falls back gracefully.

## 5. Negative (Miss) Cache
- Enabled by default to reduce upstream/load on repeated misses.
- Bounded with pruning. Tune NegativeCacheMaxEntries and NegativeCacheTimeout for your workload.
- If you expect assets to appear shortly after a miss, consider shorter timeouts to reduce false negatives.

## 6. In-flight De-duplication (Upstream Single-Flight)
- Concurrent requests for the same missing asset will share one upstream fetch.
- If upstream is unstable/slow, consider monitoring and shorter negative TTL on failures to avoid request stampedes.
- Ensure upstream IAssetService is not self-referential to avoid loops.

## 7. Write Behavior and Disk Load
- Per-file write gate prevents concurrent writes to the same asset file.
- Optional multi-worker writer can increase throughput but may spike IOPS. Validate your storage performance before increasing concurrency.
- Atomic replace/move minimizes partial files. On platforms not supporting overwrite move, a delete+move fallback is used.

## 8. Cleanup and Expiration
- Periodic cleaner removes files based on LastAccessTime and optionally cleans stale .bak files.
- Scene-aware protection prevents deletion of assets referenced by active scenes or defined as default assets.
- If your environment disables access times, ensure FileCacheTimeout is appropriate and consider manual “touch” commands.

## 9. Configuration Migration Tips
- Start conservatively:
    - Keep FileWriterConcurrencyWorker at 1 initially.
    - Use default deserialize caps unless you know your asset size profile.
    - Enable NegativeCache with moderate limits.
- Observe logs and hit-rate reports; adjust:
    - Backoff attempts/delays if you see contention on writes.
    - Negative cache size/timeouts if you see persistent misses or late arrivals.
- Commands are available to inspect status, clear caches, and trigger cleanup.

## 10. Operational Safety
- Always test on a staging region with a copy of your cache directory.
- Ensure reliable backups when enabling FileReplaceKeepBackup; monitor .bak cleanup schedules.
- Monitor “in-flight joins” (if exposed) to detect stampedes and upstream pressure.

---

## Deutsch

## 1. Voraussetzungen und Kompatibilität
- Laufzeit: Moderne .NET/Mono-Umgebung entsprechend dem OpenSim-Build. Prüfen, ob File.Replace und optionales Overwrite-Move auf Ihrer Plattform unterstützt werden.
- Storage: Der Cache-Ordner sollte auf einem Dateisystem liegen, das atomare Umbenennungen/Ersetzungen zuverlässig unterstützt. Auf Netzwerkshares können sich Semantiken unterscheiden.
- Berechtigungen: Lese/Schreib/Erstell-/Löschrechte für Cache-Verzeichnis inkl. Unterordnern und temporären Dateien sind erforderlich.
- Zeitstempel: LastAccessTime-Updates werden (gedrosselt) genutzt. Bei noatime/ignorierten Access-Times stützt sich die Bereinigung stärker auf periodische Scans.

## 2. Cache-Verzeichnisstruktur
- Gestufte Struktur: Dateien werden anhand von ID-Präfixen in Unterordner verteilt. Sicherstellen:
    - CacheDirectoryTiers ≥ 1 und ≤ 3.
    - CacheDirectoryTierLength zwischen 1 und 4.
- Migration vom Core:
    - Entweder vorhandenes Verzeichnis beibehalten und neu befüllen lassen oder
    - Mit leerem Verzeichnis starten, um Formatmischungen zu vermeiden.
- Backups: Bei aktivierten .bak-Backups atomarer Replaces genügend Speicherplatz einplanen und Cleanup einrichten.

## 3. Dateiformat und Serialisierung
- Versioniertes Binärformat mit Größenlimits für Strings und Daten (Schutz vor Korruption/OOM).
- Unbekannte/alte Dateien werden ggf. übersprungen und bei Lesefehler gelöscht, damit sie neu gecacht werden können.
- Deserialize-Limits (max. Stringlänge, max. Datengröße) an das Asset-Profil anpassen. Zu klein -> valide Assets abgelehnt; zu groß -> mehr Speicherlast.

## 4. Memory- und Weak-Reference-Cache
- WeakReference-Schicht verhindert harte Speicherbindung und beschleunigt wiederholte Zugriffe.
- Optionaler Memory-Cache erhöht RAM-Bedarf, reduziert aber I/O-Latenz. Größe indirekt über Ablaufzeit gesteuert.
- GC: Weak-Referenzen können jederzeit verfallen; Logik fällt sauber auf andere Ebenen zurück.

## 5. Negative-(Miss)-Cache
- Standardmäßig aktiv zur Entlastung des Upstreams bei wiederholten Misses.
- Begrenzt und mit Pruning. NegativeCacheMaxEntries und NegativeCacheTimeout passend zum Workload wählen.
- Erwarten Sie kurzzeitige Verfügbarkeit nach einem Miss, wählen Sie kürzere Timeouts, um falsche Negative zu reduzieren.

## 6. In-Flight-De-Dup (Upstream-Single-Flight)
- Gleichzeitige Anfragen für dasselbe fehlende Asset teilen sich einen Upstream-Fetch.
- Bei instabilem/slow Upstream Monitoring einplanen und ggf. kürzere Negative-TTL bei Fehlern, um Stampedes zu vermeiden.
- Sicherstellen, dass der Upstream-IAssetService nicht auf sich selbst verweist (Loop vermeiden).

## 7. Schreibverhalten und Plattenlast
- Pro-Datei-Schreibsperre verhindert parallele Writes derselben Datei.
- Optional mehrere Writer möglich, erhöht Durchsatz, kann aber IOPS-Spitzen verursachen. Speichertauglichkeit vorab prüfen.
- Atomare Replace/Moves minimieren Teil-Dateien. Falls Overwrite-Move nicht unterstützt, wird Delete+Move als Fallback genutzt.

## 8. Bereinigung und Ablauf
- Periodische Bereinigung entfernt Dateien anhand LastAccessTime und optional alte .bak-Dateien.
- Szenenbewusster Schutz verhindert Löschen aktiv referenzierter oder als Default definierter Assets.
- Wenn Access-Times deaktiviert sind, FileCacheTimeout passend wählen und ggf. manuelles „Touch“ nutzen.

## 9. Konfigurations-Migrationstipps
- Konservativ starten:
    - FileWriterConcurrencyWorker anfangs auf 1 belassen.
    - Standard-Deserialize-Limits verwenden, sofern das Asset-Größenprofil unbekannt ist.
    - Negative-Cache aktiviert mit moderaten Grenzen.
- Logs und Hit-Rate-Reports beobachten; anpassen:
    - Backoff/Delays bei Write-Contention.
    - Negative-Cache-Größe/Timeouts bei häufigen Misses oder verspäteten Assets.
- Befehle stehen zur Statusprüfung, Cache-Leerung und Bereinigung bereit.

## 10. Betriebssicherheit
- Migration zunächst auf Staging-Region mit Kopie des Cache-Verzeichnisses testen.
- Bei aktivierten FileReplaceKeepBackup verlässliche Backups und .bak-Cleanup überwachen.
- „In-flight joins“ (falls verfügbar) beobachten, um Stampedes und Upstream-Druck zu erkennen.****