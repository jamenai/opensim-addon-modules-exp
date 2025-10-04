# ConcurrentFlotsamAssetCache Console Commands

All commands are available in the "Assets" scope. Use from the OpenSim console.

| Command | Syntax | Description | Notes |
|---|---|---|---|
| Status | cfcache status | Displays cache status, hit-rates, and (when safe) counts the number of files in the cache. | Skips file counting if a deep scan/cleanup is running. |
| Clear | cfcache clear [file] [memory] | Clears cache contents. With no arguments, clears both memory and file caches. | Also clears negative-cache if enabled. |
| Clear Negatives | cfcache clearnegatives | Clears the negative (miss) cache only. | Useful after source issues are fixed to avoid stale “miss” suppression. |
| Assets (Deep Cache) | cfcache assets | Deep scans all scenes, “touches” assets in use, and tries to fetch cache-misses. | Temporarily pauses the periodic cleaner while it runs. Can take long on large scenes. |
| Expire (By Date) | cfcache expire <datetime> | Purges cached files older than the specified date/time. | Accepts “now” or any valid date/time (e.g., 03/15/2025 10:30). Must be in the past. |
| Cache Default Assets | cfcache cachedefaultassets | Loads default assets from the configured asset loader into the cache, replacing any existing entries. | Use with care: may override assets provided by your grid. |
| Delete Default Assets | cfcache deletedefaultassets | Removes default assets previously loaded from the local asset loader from the cache. | Use to force refresh from the upstream grid/service. |
| Clean .bak Files | cfcache cleanbak | Immediately scans the cache directory (including subfolders) and deletes stale .bak files older than the configured age. | Only if BakCleanupEnabled is true. Useful to force cleanup after node crashes. |

## Examples

- Show current cache status:
    - cfcache status

- Clear both memory and file caches:
    - cfcache clear

- Clear only the memory cache:
    - cfcache clear memory

- Clear only the file cache:
    - cfcache clear file

- Remove all negative-cache entries:
    - cfcache clearnegatives

- Deep scan and pre-cache assets for all scenes:
    - cfcache assets

- Expire files older than a given moment:
    - cfcache expire 03/01/2025 00:00
    - cfcache expire now

- Manage default assets:
    - cfcache cachedefaultassets
    - cfcache deletedefaultassets

- Manually clean stale backup files:
    - cfcache cleanbak

## Operational Notes

- The periodic cleaner runs only when:
    - FileCacheEnabled = true
    - FileCacheTimeout > 0
    - FileCleanupTimer > 0
    - At least one region is loaded (RegionLoaded triggered)

- During long operations (assets deep-scan, expire), the cleaner timer is paused and resumed automatically.

- .bak files are created next to the target cache file (under your CacheDirectory). They are deleted either:
    - Immediately after a successful atomic replace (when backups are enabled), or
    - By the periodic cleaner or the cfcache cleanbak command if they are older than BakCleanupMaxAgeHours.