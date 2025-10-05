# OpenSim SceneSnapshot Module

## What is it?
SceneSnapshot is a lightweight OpenSim region module that creates automatic region snapshots (OAR files), keeps them tidy with a retention policy, and lets you restore them quickly from the OpenSim console.

It integrates directly with the running simulator:
- no extra cron/supervisor scripts
- no racy console automation
- consistent snapshots via the Archiver API

## Why this module (vs. cron/supervisor)?
While cron jobs or external scripts can call “save oar”, this module offers:
- In-sim consistency: Uses the archiver API in-process, reducing timing issues while content is being modified.
- Simple retention: Keeps the latest hourly snapshots and one per day automatically.
- One command surface: Create, list, prune, restore directly from the region console.
- Atomic writes and cleanup: Writes to a temp file first and cleans up stale files on start.

## Features
- Auto snapshots at configurable intervals
- Retention policy: keep N hourly, plus one per day for D days
- Console commands: create, list, prune, restore
- Restore modes:
    - Replace (default): restore entire OAR
    - Merge: merge objects without typically overwriting terrain/parcels

## Installation
- Place the module in your OpenSim addon-modules (or build it as part of your solution).
- Enable it in OpenSim.ini (or your region config):

## Usage (Console)

| Command                     |Description|
|-----------------------------|---|
| ss create                   |Create a snapshot immediately.|
| ss list                     |List available snapshots (most recent first).|
| ss restore [file] or latest |Restore a snapshot by file name or the latest snapshot. Uses RestoreMode from config (Replace or Merge).|
| ss prune                    |Delete snapshots according to the retention policy.|
| ss status                   |Show current settings and counts.|

Snapshot filenames:
- RegionName_yyyyMMdd-HHmmss.oar, stored under SnapshotDirectory.

Example:
- ss restore latest

## Configuration
Enable the module:

add this part to OpenSim.ini [Modules] section

~~~
[Modules] 
    SceneSnapshot = enabled 
    Include-SceneSnapshot = "config-include/SceneSnapshot.ini"
~~~

Example settings (config-include/SceneSnapshot.ini):

~~~
[SceneSnapshot] 
    SnapshotDirectory = snapshots 
    IntervalMinutes = 60 
    KeepHourly = 24 
    KeepDaily = 7 
    AutoAtStartup = false 
    AutoAtShutdown = true 
    
    ; Replace: OAR replaces the scene (default) 
    ; Merge: OAR objects are merged; terrain/parcels are typically preserved 
    RestoreMode = Replace
~~~

## Who is it for?
- Grid/region operators wanting automated, reliable, and self-contained backups without external orchestration.
- Operators who value in-sim retention policies and easy restore flows during maintenance or testing.
- Anyone who prefers a minimal and robust solution that just works with the built-in archiver.

## Notes
- The module saves full OARs (including assets).
- Selective restore of terrain or parcels only is not supported by the underlying archiver. Use Merge to avoid overwriting terrain/parcels.
- Atomic writes ensure that incomplete files won’t appear; stale .tmp files are cleaned up on startup.



