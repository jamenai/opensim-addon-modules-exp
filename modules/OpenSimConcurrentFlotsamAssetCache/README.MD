# ConcurrentFlotsamAssetCache (NON-CORE DEVELOPMENT)

A high-throughput, concurrent asset cache module for OpenSimulator. It augments the classic FlotsamAssetCache with parallel I/O, multi-layer caching, and in-flight de-duplication for upstream fetches to reduce latency spikes and thundering herds.

- Project type: OpenSim region module
- Purpose: Faster asset retrieval with better concurrency and robust on-disk safety
- Status: DEV/TESTING (alpha)

## Key Features

- Multi-layer caching:
    - WeakReference cache for ultra-fast hits without pinning memory
    - Optional memory cache for hot assets
    - File cache with tiered directory layout
- Robust file persistence:
    - Atomic replace/move operations
    - Optional .bak backup support and cleanup
    - Throttled LastAccessTime updates to reduce syscalls
- Concurrency controls:
    - Per-file write gating to avoid partial/corrupted files
    - Optional multi-worker writer for higher throughput
    - In-flight de-duplication (single-flight) for upstream requests
- Negative cache:
    - Bounded miss cache with TTL and pruning to prevent stampedes
- Safety and maintenance:
    - Periodic cleanup with scene-aware protection
    - Versioned binary serialization with size caps (OOM/corruption guard)
    - Console commands for status, cleanup, and cache control

## When to use

- You have highly parallel workloads and want to smooth load spikes
- You run on fast storage (NVMe/SSD) and can benefit from parallel I/O
- You need to reduce redundant upstream fetches under pressure

If you prefer simplicity and minimal resources, the core FlotsamAssetCache (Core) may be a better fit.

## Installation

1. Build the module along with your OpenSim environment.
2. Place the compiled assembly in your RegionModules path.
3. Enable the module in your configuration:
   ```ini
   ; bin/OpenSim.ini
   [Modules]
   AssetCaching = ConcurrentFlotsamAssetCache

   
   ; bin/config-include/ConcurrentFlotsamAssetCache.ini
   ; EXAMPLE CONFIGURATION
   [AssetCache]
   FileCacheEnabled = true
   MemoryCacheEnabled = true
   CacheDirectory = c_assetcache
   FileCacheTimeout = 48
   UpdateFileTimeOnCacheHit = true
   NegativeCacheEnabled = true
   NegativeCacheTimeout = 120
   ; Optional tuning:
   ; FileWriterConcurrencyWorker = 1
   ; FileReplaceKeepBackup = false
   ; FileMoveAllowOverwrite = true
   ; DeserializeMaxStringLenBytes = 262144
   ; DeserializeMaxDataLenMB = 64
   ```
4. Restart your simulator.

See MIGRATION.md for platform preconditions and configuration guidance.

## Configuration Overview

- AssetCache section highlights:
    - FileCacheEnabled: enable file persistence
    - MemoryCacheEnabled: enable memory layer
    - CacheDirectory: base folder for cache tiers
    - FileCacheTimeout (hours): file expiration
    - UpdateFileTimeOnCacheHit: throttle-touched on hits
    - NegativeCacheEnabled / NegativeCacheTimeout (seconds)
    - FileWriterConcurrencyWorker: writer threads (default 1; increase cautiously)
    - FileReplaceKeepBackup (.bak) / FileMoveAllowOverwrite
    - DeserializeMaxStringLenBytes / DeserializeMaxDataLenMB: safety caps

- Advanced:
    - CacheDirectoryTiers (1–3), CacheDirectoryTierLength (1–4)
    - BackoffAttempts, BackoffInitialMs, BackoffMaxMs for contention
    - BakCleanupEnabled and BakCleanupMaxAgeHours

## Console Commands

- cfcache status
- cfcache clear [file] [memory]
- cfcache clearnegatives
- cfcache assets
- cfcache expire <datetime(mm/dd/YYYY)>
- cfcache cachedefaultassets
- cfcache deletedefaultassets
- cfcache cleanbak

## Performance Tips

- Start conservative: keep FileWriterConcurrencyWorker at 1; scale only after storage validation.
- Keep NegativeCache enabled with moderate timeouts to prevent upstream stampedes.
- Use default serialization caps unless your assets regularly exceed them.
- Monitor hit-rate output and logs to tune backoff and cache sizes.

## Safety Notes

- Ensure the cache directory is on reliable storage that supports atomic renames/replaces.
- Verify permissions for read/write/create/delete on the cache path.
- If your filesystem ignores LastAccessTime, rely on periodic cleanup and deep scans.

## Documentation

- COMPARISON.md: Side-by-side comparison to core FlotsamAssetCache
- MIGRATION.md: Preconditions, migration guidance, and operational considerations
