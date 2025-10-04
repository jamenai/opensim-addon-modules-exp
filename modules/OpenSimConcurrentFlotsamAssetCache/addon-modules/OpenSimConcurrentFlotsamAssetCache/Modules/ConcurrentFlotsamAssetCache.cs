/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Buffers;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Timers;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

[assembly: Addin("OpenSimConcurrentFlotsamAssetCache", OpenSim.VersionInfo.VersionNumber + "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSimConcurrentFlotsamAssetCache module.")]
[assembly: AddinAuthor("Christopher Händler")]

namespace OpenSim.Region.CoreModules.Asset
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ConcurrentFlotsamAssetCache")]
    public class ConcurrentFlotsamAssetCache : ISharedRegionModule, IAssetCache, IAssetService
    {
        private struct WriteAssetInfo
        {
            public string filename;
            public AssetBase asset;
            public bool replace;
        }
        
        // fast, safe, versioned binary format
        private const int AssetFileMagic = 0x46414348; // "FACH"
        private const int AssetFileVersion = 1;

        private static void SerializeAsset(Stream stream, AssetBase asset)
        {
            using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            bw.Write(AssetFileMagic);
            bw.Write(AssetFileVersion);

            static void WriteString(BinaryWriter w, string s)
            {
                if (string.IsNullOrEmpty(s))
                {
                    w.Write(0);
                    return;
                }
                int byteCount = Encoding.UTF8.GetByteCount(s);
                w.Write(byteCount);
                if (byteCount == 0)
                    return;
                byte[] buf = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    Encoding.UTF8.GetBytes(s, 0, s.Length, buf, 0);
                    w.Write(buf, 0, byteCount);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            WriteString(bw, asset.ID);
            WriteString(bw, asset.Name);
            WriteString(bw, asset.Description);

            bw.Write((sbyte)asset.Type);
            bw.Write((uint)asset.Flags);

            var data = asset.Data ?? Array.Empty<byte>();
            bw.Write(data.Length);
            if (data.Length > 0)
                bw.Write(data);

            bw.Write(asset.Local ? (byte)1 : (byte)0);
            bw.Write(asset.Temporary ? (byte)1 : (byte)0);

            var guid = asset.FullID.Guid;
            Span<byte> gbuf = stackalloc byte[16];
            guid.TryWriteBytes(gbuf);
            bw.Write(gbuf);
            bw.Flush();
        }

        private static AssetBase DeserializeAsset(Stream stream, int maxStringLenBytes, int maxDataLenBytes)
        {
            using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            int magic = br.ReadInt32();
            if (magic != AssetFileMagic)
                throw new System.Runtime.Serialization.SerializationException("Unknown cache format (magic mismatch)");
            int version = br.ReadInt32();
            if (version != AssetFileVersion)
                throw new System.Runtime.Serialization.SerializationException("Unsupported cache version");

            static string ReadStringCapped(BinaryReader r, int cap)
            {
                int len = r.ReadInt32();
                if (len <= 0) return string.Empty;
                if (len > cap) throw new System.Runtime.Serialization.SerializationException("String too large");
                byte[] buf = ArrayPool<byte>.Shared.Rent(len);
                try
                {
                    int read = r.Read(buf, 0, len);
                    if (read != len) throw new EndOfStreamException();
                    return Encoding.UTF8.GetString(buf, 0, len);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            string id = ReadStringCapped(br, maxStringLenBytes);
            string name = ReadStringCapped(br, maxStringLenBytes);
            string desc = ReadStringCapped(br, maxStringLenBytes);
            sbyte type = br.ReadSByte();
            uint flags = br.ReadUInt32();

            int dataLen = br.ReadInt32();
            if (dataLen < 0 || dataLen > maxDataLenBytes)
                throw new System.Runtime.Serialization.SerializationException("Asset data too large or invalid");

            byte[] data = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
            if (dataLen > 0)
            {
                int read = br.Read(data, 0, dataLen);
                if (read != dataLen) throw new EndOfStreamException();
            }

            bool local = br.ReadByte() != 0;
            bool temp = br.ReadByte() != 0;

            Span<byte> gbuf = stackalloc byte[16];
            int got = br.Read(gbuf);
            if (got != 16) throw new EndOfStreamException();
            var guid = new Guid(gbuf);

            var asset = new AssetBase
            {
                ID = id,
                Name = name,
                Description = desc,
                Type = type,
                Flags = (AssetFlags)flags,
                Data = data,
                Local = local,
                Temporary = temp,
                FullID = new UUID(guid)
            };
            return asset;
        }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled;
        private bool m_timerRunning;
        private bool m_cleanupRunning;

        private const string m_ModuleName = "ConcurrentFlotsamAssetCache";
        private string m_CacheDirectory = "c_assetcache";
        private string m_assetLoader;
        private string m_assetLoaderArgs;

        private readonly char[] m_InvalidChars;

        private int m_LogLevel = 0;

        private ulong m_Requests;
        private ulong m_RequestsForInprogress;
        private ulong m_DiskHits;
        private ulong m_MemoryHits;
        private ulong m_weakRefHits;

        private static readonly ConcurrentDictionary<string, byte> m_CurrentlyWriting = new();
        private static ObjectJobEngine m_assetFileWriteWorker = null;
        private static HashSet<string> m_defaultAssets = new();

        private bool m_FileCacheEnabled = true;

        private ExpiringCacheOS<string, AssetBase> m_MemoryCache;
        private bool m_MemoryCacheEnabled = false;
        
        // new negative cache is a dictionary of asset ids to the time they were last accessed
        private ConcurrentDictionary<string, long> m_negativeCache;
        private bool m_negativeCacheEnabled = true;
        
        // Negative cache (miss cache) with optional size limit and pruning
        // (keep only sizing/pruning fields here; DO NOT redeclare m_negativeCache or m_negativeCacheEnabled)
        private static int m_negativeCacheMaxEntries = 100_000; // configurable cap
        private static int m_negativeCachePruneBatch = 5_000;   // how many to remove when pruning   
        
        // Expiration is expressed in hours for memory cache; negative cache in seconds
        private double m_MemoryExpiration = 0.016;
        private const double m_DefaultFileExpiration = 48;
        private int m_negativeExpiration = 120;
        private TimeSpan m_FileExpiration = TimeSpan.FromHours(m_DefaultFileExpiration);
        private TimeSpan m_FileExpirationCleanupTimer = TimeSpan.FromHours(1.0);

        private static int m_CacheDirectoryTiers = 1;
        private static int m_CacheDirectoryTierLen = 3;
        private static int m_CacheWarnAt = 30000;

        private System.Timers.Timer m_CacheCleanTimer;

        private IAssetService m_AssetService;
        private readonly List<Scene> m_Scenes = new();
        private readonly object timerLock = new();
        
        private ConcurrentDictionary<string, WeakReference<AssetBase>> weakAssetReferences = new();
        private readonly ConcurrentDictionary<string, Lazy<AssetBase>> m_inflightFetches = new();
        
        private static bool m_updateFileTimeOnCacheHit = false;

        private static ExpiringKey<string> m_lastFileAccessTimeChange = null;
        
        private static bool m_enableReplaceBackup = false;   // optionally keep a backup on atomic replace
        private static bool m_enableMoveOverwrite = true;    // prefer overwrite move when available
        
        private ulong m_HitRateDisplay = 100; // How often to display hit statistics, given in requests
        private int m_HitReportWeakRefSampleTarget = 2000; // configurable sample size for weak refs
        
        // Configurable deserialization caps (defaults conservative)
        private int m_MaxStringLenBytes = 256 * 1024;   // 256 KB per string
        private int m_MaxDataLenMB = 64;               // 64 MB asset data
        private int m_MaxDataLenBytes => m_MaxDataLenMB * 1024 * 1024;

        // Configurable backoff for concurrent write contention
        private int m_BackoffAttempts = 3;     // number of retries
        private int m_BackoffInitialMs = 5;    // initial delay in ms
        private int m_BackoffMaxMs = 40;       // max delay cap in ms
        
        // .bak cleanup options
        private bool m_EnableBakCleanup = true;
        private TimeSpan m_BakMaxAge = TimeSpan.FromHours(24); 
        
        // fileWriter concurrency options
        private int m_FileWriterConcurrencyWorker = 1; // number of workers to use for concurrent writes (default: 1, max: 4)
        
        public ConcurrentFlotsamAssetCache()
        {
            List<char> invalidChars = new();
            invalidChars.AddRange(Path.GetInvalidPathChars());
            invalidChars.AddRange(Path.GetInvalidFileNameChars());
            m_InvalidChars = invalidChars.ToArray();
        }

        public Type ReplaceableInterface => null;

        public string Name => m_ModuleName;

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig is null)
                return;

            string name = moduleConfig.GetString("AssetCaching", string.Empty);

            if (name != Name)
                return;

            m_negativeCache = new ConcurrentDictionary<string, long>();
            m_Enabled = true;

            m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE]: {this.Name} enabled");

            IConfig assetConfig = source.Configs["AssetCache"];
            if (assetConfig is null)
            {
                m_log.Debug("[CONCURRENT FLOTSAM ASSET CACHE]: AssetCache section missing from config (using defaults).");
            }
            else
            {
                m_FileCacheEnabled = assetConfig.GetBoolean("FileCacheEnabled", m_FileCacheEnabled);
                m_CacheDirectory = assetConfig.GetString("CacheDirectory", m_CacheDirectory);
                m_CacheDirectory = Path.GetFullPath(m_CacheDirectory);

                m_MemoryCacheEnabled = assetConfig.GetBoolean("MemoryCacheEnabled", m_MemoryCacheEnabled);
                m_MemoryExpiration = assetConfig.GetDouble("MemoryCacheTimeout", m_MemoryExpiration);
                m_MemoryExpiration *= 3600.0; // hours to seconds

                m_negativeCacheEnabled = assetConfig.GetBoolean("NegativeCacheEnabled", m_negativeCacheEnabled);
                m_negativeExpiration = assetConfig.GetInt("NegativeCacheTimeout", m_negativeExpiration);

                m_updateFileTimeOnCacheHit = assetConfig.GetBoolean("UpdateFileTimeOnCacheHit", m_updateFileTimeOnCacheHit);
                m_updateFileTimeOnCacheHit &= m_FileCacheEnabled;

                m_LogLevel = assetConfig.GetInt("LogLevel", m_LogLevel);

                m_FileExpiration = TimeSpan.FromHours(assetConfig.GetDouble("FileCacheTimeout", m_DefaultFileExpiration));
                m_FileExpirationCleanupTimer = TimeSpan.FromHours(
                        assetConfig.GetDouble("FileCleanupTimer", m_FileExpirationCleanupTimer.TotalHours));

                m_CacheDirectoryTiers = assetConfig.GetInt("CacheDirectoryTiers", m_CacheDirectoryTiers);
                m_CacheDirectoryTierLen = assetConfig.GetInt("CacheDirectoryTierLength", m_CacheDirectoryTierLen);

                m_CacheWarnAt = assetConfig.GetInt("CacheWarnAt", m_CacheWarnAt);
                
                // Optional controls for safer replace behavior
                m_enableReplaceBackup = assetConfig.GetBoolean("FileReplaceKeepBackup", m_enableReplaceBackup);
                m_enableMoveOverwrite = assetConfig.GetBoolean("FileMoveAllowOverwrite", m_enableMoveOverwrite);
                
                // Negative cache sizing (optional)
                m_negativeCacheMaxEntries = assetConfig.GetInt("NegativeCacheMaxEntries", m_negativeCacheMaxEntries);
                m_negativeCachePruneBatch = assetConfig.GetInt("NegativeCachePruneBatch", m_negativeCachePruneBatch);
                if (m_negativeCacheMaxEntries < 1000) m_negativeCacheMaxEntries = 1000;
                if (m_negativeCachePruneBatch < 100) m_negativeCachePruneBatch = 100;
                
                m_HitRateDisplay = (ulong)assetConfig.GetLong("HitRateDisplay", (long)m_HitRateDisplay);
                m_HitReportWeakRefSampleTarget = assetConfig.GetInt("HitReportWeakRefSampleTarget", m_HitReportWeakRefSampleTarget);
                if (m_HitReportWeakRefSampleTarget < 100)
                    m_HitReportWeakRefSampleTarget = 100; // lower bound to avoid excessive iteration
                
                // Deserialization limits (optional)
                m_MaxStringLenBytes = assetConfig.GetInt("DeserializeMaxStringLenBytes", m_MaxStringLenBytes);
                m_MaxDataLenMB = assetConfig.GetInt("DeserializeMaxDataLenMB", m_MaxDataLenMB);
                
                // floors (avoid too small values that break normal assets)
                if (m_MaxStringLenBytes < 32 * 1024) m_MaxStringLenBytes = 32 * 1024; // 32 KB
                if (m_MaxDataLenMB < 8) m_MaxDataLenMB = 8; // 8 MB
                
                // ceilings (protect from misconfiguration)
                if (m_MaxStringLenBytes > 2 * 1024 * 1024) m_MaxStringLenBytes = 2 * 1024 * 1024; // 2 MB
                if (m_MaxDataLenMB > 512) m_MaxDataLenMB = 512; // 512 MB

                // Backoff tuning (optional)
                m_BackoffAttempts = assetConfig.GetInt("BackoffAttempts", m_BackoffAttempts);
                m_BackoffInitialMs = assetConfig.GetInt("BackoffInitialMs", m_BackoffInitialMs);
                m_BackoffMaxMs = assetConfig.GetInt("BackoffMaxMs", m_BackoffMaxMs);
                
                // sanitize backoff
                if (m_BackoffAttempts < 0) m_BackoffAttempts = 0;
                if (m_BackoffAttempts > 10) m_BackoffAttempts = 10; // hard upper bound
                if (m_BackoffInitialMs < 0) m_BackoffInitialMs = 0;
                if (m_BackoffInitialMs > 500) m_BackoffInitialMs = 500;
                if (m_BackoffMaxMs < m_BackoffInitialMs) m_BackoffMaxMs = m_BackoffInitialMs;
                if (m_BackoffMaxMs > 2000) m_BackoffMaxMs = 2000;
                
                // .bak cleanup config (optional)
                m_EnableBakCleanup = assetConfig.GetBoolean("BakCleanupEnabled", m_EnableBakCleanup);
                double bakMaxAgeHours = assetConfig.GetDouble("BakCleanupMaxAgeHours", m_BakMaxAge.TotalHours);
                if (bakMaxAgeHours < 1.0) bakMaxAgeHours = 1.0; // floor
                if (bakMaxAgeHours > 168.0) bakMaxAgeHours = 168.0; // cap at 7 days
                m_BakMaxAge = TimeSpan.FromHours(bakMaxAgeHours);
                
                // fileWriter concurrency config (optional)
                // WARNING: this is a very experimental option and can damage the disk if used incorrectly
                m_FileWriterConcurrencyWorker = assetConfig.GetInt("FileWriterConcurrencyWorker", m_FileWriterConcurrencyWorker);
                if (m_FileWriterConcurrencyWorker > 4) m_FileWriterConcurrencyWorker = 4; // keep it limited to max: 4     
                if (m_FileWriterConcurrencyWorker > 1)
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: FileWriterConcurrencyWorker={m_FileWriterConcurrencyWorker} – ensure your storage can handle parallel writes.");
                
            }

            if (m_updateFileTimeOnCacheHit)
                m_lastFileAccessTimeChange = new ExpiringKey<string>(300000);

            if (m_MemoryCacheEnabled)
                m_MemoryCache = new ExpiringCacheOS<string, AssetBase>((int)m_MemoryExpiration * 500);

            m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE]: Cache Directory {m_CacheDirectory}");

            if (m_CacheDirectoryTiers < 1)
                m_CacheDirectoryTiers = 1;
            else if (m_CacheDirectoryTiers > 3)
                m_CacheDirectoryTiers = 3;

            if (m_CacheDirectoryTierLen < 1)
                m_CacheDirectoryTierLen = 1;
            else if (m_CacheDirectoryTierLen > 4)
                m_CacheDirectoryTierLen = 4;

            m_negativeExpiration *= 1000; // ms for legacy callers

            assetConfig = source.Configs["AssetService"];
            if (assetConfig is not null)
            {
                m_assetLoader = assetConfig.GetString("DefaultAssetLoader", string.Empty);
                m_assetLoaderArgs = assetConfig.GetString("AssetLoaderArgs", string.Empty);
                if (string.IsNullOrWhiteSpace(m_assetLoaderArgs))
                    m_assetLoader = string.Empty;
            }

            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache status", "cfcache status", "Display cache status", HandleConsoleCommand);
            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache clear", "cfcache clear [file] [memory]", "Remove all assets in the cache.  If file or memory is specified then only this cache is cleared.", HandleConsoleCommand);
            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache clearnegatives", "cfcache clearnegatives", "Remove cache of assets previously not found in services.", HandleConsoleCommand);
            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache assets", "cfcache assets", "Attempt a deep scan and cache of all assets in all scenes", HandleConsoleCommand);
            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache expire", "cfcache expire <datetime(mm/dd/YYYY)>", "Purge cached assets older than the specified date/time", HandleConsoleCommand);
            if (!string.IsNullOrWhiteSpace(m_assetLoader))
            {
                MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache cachedefaultassets", "cfcache cachedefaultassets", "loads local default assets to cache. This may override grid ones. use with care", HandleConsoleCommand);
                MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache deletedefaultassets", "cfcache deletedefaultassets", "deletes default local assets from cache so they can be refreshed from grid. use with care", HandleConsoleCommand);
            }
            MainConsole.Instance.Commands.AddCommand("Assets", true, "cfcache cleanbak", "cfcache cleanbak", "Clean stale .bak files from cache now", HandleConsoleCommand);
        }

        public void PostInitialise() { }

        public void Close()
        {
            if (m_Scenes.Count <= 0)
            {
                lock (timerLock)
                {
                    m_cleanupRunning = false;
                    if (m_timerRunning)
                    {
                        m_timerRunning = false;
                        m_CacheCleanTimer.Stop();
                        m_CacheCleanTimer.Close();
                    }
                    if (m_assetFileWriteWorker != null)
                    {
                        m_assetFileWriteWorker.Dispose();
                        m_assetFileWriteWorker = null;
                    }
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IAssetCache>(this);
            m_Scenes.Add(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.UnregisterModuleInterface<IAssetCache>(this);
            m_Scenes.Remove(scene);
            lock (timerLock)
            {
                if (m_Scenes.Count <= 0)
                {
                    m_cleanupRunning = false;
                    if (m_timerRunning)
                    {
                        m_timerRunning = false;
                        m_CacheCleanTimer.Stop();
                        m_CacheCleanTimer.Close();
                    }
                    if (m_assetFileWriteWorker != null)
                    {
                        m_assetFileWriteWorker.Dispose();
                        m_assetFileWriteWorker = null;
                    }
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_AssetService ??= scene.RequestModuleInterface<IAssetService>();
            lock (timerLock)
            {
                if (!m_timerRunning)
                {
                    if (m_FileCacheEnabled && (m_FileExpiration > TimeSpan.Zero) && (m_FileExpirationCleanupTimer > TimeSpan.Zero))
                    {
                        m_CacheCleanTimer = new System.Timers.Timer(m_FileExpirationCleanupTimer.TotalMilliseconds)
                        {
                            AutoReset = false
                        };
                        m_CacheCleanTimer.Elapsed += CleanupExpiredFiles;
                        m_CacheCleanTimer.Start();
                        m_timerRunning = true;
                    }
                }

                if (m_FileCacheEnabled && m_assetFileWriteWorker == null)
                {
                    m_assetFileWriteWorker = new ObjectJobEngine(ProcessWrites, "ConcurrentFlotsamCacheWriter", 1000, m_FileWriterConcurrencyWorker);
                }

                if (!string.IsNullOrWhiteSpace(m_assetLoader) && scene.RegionInfo.RegionID == m_Scenes[0].RegionInfo.RegionID)
                {
                    IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(m_assetLoader, Array.Empty<object>());
                    if (assetLoader is not null)
                    {
                        HashSet<string> ids = new();
                        assetLoader.ForEachDefaultXmlAsset(
                            m_assetLoaderArgs,
                            delegate (AssetBase a)
                            {
                                Cache(a, true);
                                ids.Add(a.ID);
                            });
                        m_defaultAssets = ids;
                    }
                }
            }
        }

        private void ProcessWrites(object o)
        {
            try
            {
                WriteAssetInfo wai = (WriteAssetInfo)o;
                WriteFileCache(wai.filename, wai.asset, wai.replace);
                wai.asset = null;
                Thread.Yield();
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Write worker failed: {e.Message}");
            }
        }

        ////////////////////////////////////////////////////////////
        // IAssetCache
        //
        private void UpdateWeakReference(string key, AssetBase asset)
        {
            weakAssetReferences.AddOrUpdate(
                key,
                _ => new WeakReference<AssetBase>(asset),
                (_, existing) =>
                {
                    // defensive: handle rare case where existing could be null
                    if (existing is null)
                        return new WeakReference<AssetBase>(asset);
                    
                    existing.SetTarget(asset);
                    return existing;
                });
        }

        private void UpdateMemoryCache(string key, AssetBase asset)
        {
            m_MemoryCache.AddOrUpdate(key, asset, m_MemoryExpiration);
        }

        private void UpdateFileCache(string key, AssetBase asset, bool replace = false)
        {
            if (m_assetFileWriteWorker is null)
                return;

            string filename = GetFileName(key);

            try
            {
                if (!m_CurrentlyWriting.TryAdd(filename, 0))
                    return;

                if (m_assetFileWriteWorker is not null)
                {
                    WriteAssetInfo wai = new()
                    {
                        filename = filename,
                        asset = asset,
                        replace = replace
                    };
                    m_assetFileWriteWorker.Enqueue(wai);
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Failed to update cache for asset {asset.ID}: {e.Message}");
                m_CurrentlyWriting.TryRemove(filename, out _);
            }
        }

        public void Cache(AssetBase asset, bool replace = false)
        {
            if (asset is null)
                return;

            UpdateWeakReference(asset.ID, asset);

            if (m_MemoryCacheEnabled)
                UpdateMemoryCache(asset.ID, asset);

            if (m_FileCacheEnabled)
                UpdateFileCache(asset.ID, asset, replace);

            if (m_negativeCacheEnabled)
                m_negativeCache.TryRemove(asset.ID, out _);
        }

        public void CacheNegative(string id)
        {
            if (!m_negativeCacheEnabled || string.IsNullOrEmpty(id))
                return;

            // opportunistic prune on insert if over capacity
            TryPruneNegativeCacheIfNeeded();

            long ttlMs = (long)m_negativeExpiration; // ms
            long expiresAt = Environment.TickCount64 + ttlMs;
            m_negativeCache[id] = expiresAt;
        }

        /// <summary>
        /// Updates the cached file with the current time.
        /// </summary>
        /// <param name="filename">Filename.</param>
        /// <returns><c>true</c>, if the update was successful, false otherwise.</returns>
        private static bool CheckUpdateFileLastAccessTime(string filename)
        {
            try
            {
                File.SetLastAccessTime(filename, DateTime.Now);
                m_lastFileAccessTimeChange?.Add(filename, 900000);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch
            {
                return true; // ignore other errors
            }
        }

        private static void UpdateFileLastAccessTime(string filename)
        {
            try
            {
                if (!m_lastFileAccessTimeChange.ContainsKey(filename))
                {
                    File.SetLastAccessTime(filename, DateTime.Now);
                    m_lastFileAccessTimeChange.Add(filename, 900000);
                }
            }
            catch { }
        }

        private AssetBase GetFromWeakReference(string id)
        {
            if (weakAssetReferences.TryGetValue(id, out WeakReference<AssetBase> aref)
                && aref.TryGetTarget(out var asset) && asset is not null)
            {
                m_weakRefHits++;
                return asset;
            }
            return null;
        }

        /// <summary>
        /// Try to get an asset from the in-memory cache.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private AssetBase GetFromMemoryCache(string id)
        {
            if (m_MemoryCache.TryGetValue(id, out AssetBase asset))
            {
                m_MemoryHits++;
                return asset;
            }
            return null;
        }

        private bool CheckFromMemoryCache(string id) => m_MemoryCache.Contains(id);

        /// <summary>
        /// Try to get an asset from the file cache.
        /// </summary>
        /// <param name="id"></param>
        /// <returns>An asset retrieved from the file cache.  null if there was a problem retrieving an asset.</returns>
        private AssetBase GetFromFileCache(string id)
        {
            string filename = GetFileName(id);
            if (filename is null)
                return null;

            // Track how often we have the problem that an asset is requested while
            // it is still being downloaded by a previous request.
            if (m_CurrentlyWriting.ContainsKey(filename))
            {
                m_RequestsForInprogress++;
                return null;
            }

            AssetBase asset = null;

            try
            {
                using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                if (stream.Length == 0) // Empty file will trigger exception below
                    return null;

                asset = DeserializeAsset(stream, m_MaxStringLenBytes, m_MaxDataLenBytes);
                m_DiskHits++;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (System.Runtime.Serialization.SerializationException e)
            {
                long size = 0;
                try { size = new FileInfo(filename).Length; } catch { }
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Bad cache file {filename} (size {size}): {e.Message}");

                // If there was a problem deserializing the asset, the asset may
                // either be corrupted OR was serialized under an old format
                // {different version of AssetBase} -- we should attempt to
                // delete it and re-cache
                try { File.Delete(filename); } catch { }
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Failed to get file {filename} for asset {id}: {e.Message}");
            }

            return asset;
        }

        private bool CheckFromFileCache(string id)
        {
            try
            {
                string fn = GetFileName(id);
                return fn != null && File.Exists(fn);
            }
            catch { return false; }
        }

        private bool IsNegative(string id)
        {
            if (!m_negativeCacheEnabled)
                return false;
            if (m_negativeCache.TryGetValue(id, out long expiresAt))
            {
                if (Environment.TickCount64 <= expiresAt)
                    return true;
                m_negativeCache.TryRemove(id, out _);
            }
            return false;
        }
        
        private AssetBase FetchUpstream(string id)
        {
            // guard for self calling
            if (m_AssetService == null || ReferenceEquals(m_AssetService, this))
                return null;
            return m_AssetService.Get(id);
        }
        
        private long m_InFlightJoins;
        
        // In-flight de-duplication + upstream fetch integration
        private bool TryGetWithUpstream(string id, out AssetBase asset)
        {
            asset = null;

            // 1) Fast paths: weak + memory + file
            asset = GetFromWeakReference(id);
            if (asset is not null)
            {
                if (m_MemoryCacheEnabled) UpdateMemoryCache(id, asset);
                return true;
            }

            if (m_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if (asset is not null)
                {
                    UpdateWeakReference(id, asset);
                    return true;
                }
            }

            if (m_FileCacheEnabled)
            {
                // small wait if a write is in-flight for this file to avoid unnecessary upstream fetch
                string fname = GetFileName(id);
                if (fname != null && m_CurrentlyWriting.ContainsKey(fname))
                {
                    int delay = Math.Min(10, m_BackoffInitialMs > 0 ? m_BackoffInitialMs : 5);
                    if (delay > 0)
                        Thread.Sleep(delay);
                }
                
                asset = GetFromFileCache(id);
                if (asset is not null)
                {
                    UpdateWeakReference(id, asset);
                    if (m_MemoryCacheEnabled) UpdateMemoryCache(id, asset);
                    return true;
                }
            }

            // 2) Negative cache check before upstream
            if (IsNegative(id))
                return false;

            // 3) In-flight de-duplication for upstream fetch
            try
            {
                // guard for self calling
                if (m_AssetService == null || ReferenceEquals(m_AssetService, this))
                    return false; 
                
                bool created = false;
                var lazyFetch = m_inflightFetches.GetOrAdd(
                    id,
                    _ => { created = true; return new Lazy<AssetBase>(() => FetchUpstream(id), LazyThreadSafetyMode.ExecutionAndPublication); }
                );
                if (!created) Interlocked.Increment(ref m_InFlightJoins);

                asset = lazyFetch.Value; // triggers single actual fetch; others await the result

                if (asset is null)
                {
                    // upstream miss -> record negative (bounded)
                    CacheNegative(id);
                    return false;
                }

                // Success: populate caches
                UpdateWeakReference(id, asset);
                if (m_MemoryCacheEnabled) UpdateMemoryCache(id, asset);
                if (m_FileCacheEnabled) UpdateFileCache(id, asset, replace: false);

                // Ensure negatives do not hide this id
                if (m_negativeCacheEnabled) m_negativeCache.TryRemove(id, out _);

                return true;
            }
            catch (Exception e)
            {
                if (m_LogLevel >= 1)
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Upstream error for {id}: {e.Message}");
                // On error, mark as negative (short TTL) to avoid stampede??
                //CacheNegative(id);
                return false;
            }
            finally
            {
                // Remove in-flight entry so future requests can refetch if needed
                m_inflightFetches.TryRemove(id, out _);
            }
        }
        
        
        // For IAssetService
        public AssetBase Get(string id)
        {
            Get(id, out AssetBase asset);
            return asset;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool dummy) => null;

        public bool Get(string id, out AssetBase asset)
        {
            asset = null;

            m_Requests++;
            if (string.IsNullOrWhiteSpace(id) || id.Equals(UUID.ZeroString))
                return false;

            // Try local caches + file + upstream with in-flight de-duplication
            bool ok = TryGetWithUpstream(id, out asset);
            if (ok && m_updateFileTimeOnCacheHit)
            {
                var fn = GetFileName(id);
                if (fn != null) UpdateFileLastAccessTime(fn);
            }
            return ok;
        }
        
        // @todo: in-flight de-duplication (sync or async?? keep mono comp. = sync??
        /*
        public bool Get(string id, out AssetBase asset)
        {
            asset = null;

            m_Requests++;

            if (string.IsNullOrWhiteSpace(id) || id.Equals(UUID.ZeroString))
                return false;

            if (IsNegative(id))
                return false;

            asset = GetFromWeakReference(id);
            if (asset is not null)
            {
                if (m_updateFileTimeOnCacheHit)
                {
                    var fn = GetFileName(id);
                    if (fn != null) UpdateFileLastAccessTime(fn);
                }
                if (m_MemoryCacheEnabled)
                    UpdateMemoryCache(id, asset);
                return true;
            }

            if (m_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if (asset is not null)
                {
                    UpdateWeakReference(id, asset);
                    if (m_updateFileTimeOnCacheHit)
                    {
                        var fn = GetFileName(id);
                        if (fn != null) UpdateFileLastAccessTime(fn);
                    }
                    return true;
                }
            }

            if (m_FileCacheEnabled)
            {
                // Configurable exponential backoff if a write is in progress
                int delay = m_BackoffInitialMs;
                for (int attempts = 0; attempts < m_BackoffAttempts; attempts++)
                {
                    asset = GetFromFileCache(id);
                    if (asset is not null)
                    {
                        UpdateWeakReference(id, asset);
                        if (m_MemoryCacheEnabled)
                            UpdateMemoryCache(id, asset);
                        return true;
                    }

                    var fname = GetFileName(id);
                    if (fname is null || !m_CurrentlyWriting.ContainsKey(fname))
                        break;

                    if (delay > 0)
                        Thread.Sleep(Math.Min(delay, m_BackoffMaxMs));
                    // exponential growth with cap
                    delay = Math.Min(delay == 0 ? 1 : delay * 2, m_BackoffMaxMs);
                }
            }
            // @todo: should we also check the asset service (upstream)? (AssetProxy support too? or AssetCache only ??)
            // without upstream we produce may be silent misses...
            return false;
        }
        */

        public bool GetFromMemory(string id, out AssetBase asset)
        {
            asset = null;

            m_Requests++;
            
            if (string.IsNullOrWhiteSpace(id) || id.Equals(UUID.ZeroString))
                return false;

            if (IsNegative(id))
                return false;

            asset = GetFromWeakReference(id);
            if (asset != null)
            {
                if (m_updateFileTimeOnCacheHit)
                {
                    var filename = GetFileName(id);
                    if (filename != null) UpdateFileLastAccessTime(filename);
                }
                if (m_MemoryCacheEnabled)
                    UpdateMemoryCache(id, asset);
                return true;
            }

            if (m_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if (asset != null)
                {
                    UpdateWeakReference(id, asset);
                    if (m_updateFileTimeOnCacheHit)
                    {
                        var filename = GetFileName(id);
                        if (filename != null) UpdateFileLastAccessTime(filename);
                    }
                    return true;
                }
            }
            return false;
        }

        public bool Check(string id)
        {
            if (GetFromWeakReference(id) is not null)
                return true;

            if (m_MemoryCacheEnabled && CheckFromMemoryCache(id))
                return true;

            if (m_FileCacheEnabled && CheckFromFileCache(id))
                return true;

            return false;
        }

        // does not check negative cache
        public AssetBase GetCached(string id)
        {
            //m_Requests++;

            AssetBase asset = GetFromWeakReference(id);
            if (asset is not null)
            {
                if (m_updateFileTimeOnCacheHit)
                    UpdateFileLastAccessTime(GetFileName(id));

                if (m_MemoryCacheEnabled)
                    UpdateMemoryCache(id, asset);
                return asset;
            }

            if (m_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if (asset is not null)
                {
                    UpdateWeakReference(id, asset);
                    if (m_updateFileTimeOnCacheHit)
                        UpdateFileLastAccessTime(GetFileName(id));

                    return asset;
                }
            }

            if (m_FileCacheEnabled)
            {
                asset = GetFromFileCache(id);
                if (asset is not null)
                {
                    UpdateWeakReference(id, asset);
                    if (m_MemoryCacheEnabled)
                        UpdateMemoryCache(id, asset);
                }
            }
            return asset;
        }

        public void Expire(string id)
        {
            if (m_LogLevel >= 2)
                m_log.Debug($"[CONCURRENT FLOTSAM ASSET CACHE]: Expiring Asset {id}");

            try
            {
                weakAssetReferences.TryRemove(id, out _);

                if (m_MemoryCacheEnabled)
                    m_MemoryCache.Remove(id);

                if (m_negativeCacheEnabled)
                    m_negativeCache.TryRemove(id, out _);

                if (m_FileCacheEnabled)
                    File.Delete(GetFileName(id));
            }
            catch (Exception e)
            {
                if (m_LogLevel >= 2)
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Failed to expire cached file {id}: {e.Message}");
            }
        }

        public void Clear()
        {
            if (m_LogLevel >= 2)
                m_log.Debug("[CONCURRENT FLOTSAM ASSET CACHE]: Clearing caches.");

            if (m_FileCacheEnabled && Directory.Exists(m_CacheDirectory))
            {
                foreach (string dir in Directory.EnumerateDirectories(m_CacheDirectory))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }

            if (m_MemoryCacheEnabled)
            {
                m_MemoryCache.Dispose();
                m_MemoryCache = new ExpiringCacheOS<string, AssetBase>((int)m_MemoryExpiration * 500);
            }
            if (m_negativeCacheEnabled)
            {
                m_negativeCache.Clear();
            }

            weakAssetReferences = new ConcurrentDictionary<string, WeakReference<AssetBase>>();
        }

        private void CleanupExpiredFiles(object source, ElapsedEventArgs e)
        {
            lock (timerLock)
            {
                if (!m_timerRunning || m_cleanupRunning || !Directory.Exists(m_CacheDirectory))
                    return;
                m_cleanupRunning = true;
            }

            // Purge all files last accessed prior to this point
            DoCleanExpiredFiles(DateTime.Now - m_FileExpiration);
        }

        private void DoCleanExpiredFiles(DateTime purgeLine)
        {
            bool restartTimer = false;
            lock (timerLock) restartTimer = m_timerRunning;
            try
            {
                m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE]: Start background expiring files older than {purgeLine}");
                long heap = GC.GetTotalMemory(false);

                if (m_negativeCacheEnabled)
                {
                    long now = Environment.TickCount64;
                    foreach (var kv in m_negativeCache)
                    {
                        if (kv.Value < now)
                            m_negativeCache.TryRemove(kv.Key, out _);
                    }
                    // also prune if size is above cap (periodic cleanup)
                    TryPruneNegativeCacheIfNeeded();
                }

                Dictionary<UUID, sbyte> gids = GatherSceneAssets();

                int cooldown = 0;
                m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] start asset files expire");
                foreach (string subdir in Directory.EnumerateDirectories(m_CacheDirectory))
                {
                    if (!m_cleanupRunning)
                        break;
                    cooldown = CleanExpiredFiles(subdir, gids, purgeLine, cooldown);
                    if (++cooldown >= 10)
                    {
                        Thread.Sleep(120);
                        cooldown = 0;
                    }
                }

                weakAssetReferences = new ConcurrentDictionary<string, WeakReference<AssetBase>>();
                m_weakRefHits = 0;

                double fheap = Math.Round((double)((GC.GetTotalMemory(false) - heap) / (1024 * 1024)), 3);
                m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE]: Finished expiring files, heap delta: {fheap}MB.");
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Cleaner failed: {e.Message}");
            }
            finally
            {
                lock (timerLock)
                {
                    if (restartTimer && m_timerRunning)
                        m_CacheCleanTimer.Start();
                    m_cleanupRunning = false;
                }
            }
        }
        
        private void TryPruneNegativeCacheIfNeeded()
        {
            if (!m_negativeCacheEnabled)
                return;

            int count = m_negativeCache.Count;
            if (count <= m_negativeCacheMaxEntries)
                return;

            long now = Environment.TickCount64;

            // 1) remove expired entries opportunistically
            int removed = 0;
            foreach (var kv in m_negativeCache)
            {
                if (kv.Value < now)
                {
                    if (m_negativeCache.TryRemove(kv.Key, out _))
                    {
                        if (++removed >= m_negativeCachePruneBatch)
                            break;
                    }
                }
            }

            // 2) if still over capacity, remove oldest entries
            if (m_negativeCache.Count > m_negativeCacheMaxEntries)
            {
                // sample up to N entries to avoid full sort when map is huge
                const int sampleTarget = 5000;
                var sample = new List<KeyValuePair<string, long>>(Math.Min(sampleTarget, m_negativeCache.Count));
                int step = Math.Max(1, m_negativeCache.Count / Math.Min(sampleTarget, m_negativeCache.Count));
                int idx = 0;
                foreach (var kv in m_negativeCache)
                {
                    if ((idx++ % step) == 0)
                    {
                        sample.Add(kv);
                        if (sample.Count >= sampleTarget) break;
                    }
                }

                // sort by expiration ascending (oldest first)
                sample.Sort(static (a, b) => a.Value.CompareTo(b.Value));

                int needToRemove = Math.Min(m_negativeCachePruneBatch, Math.Max(0, m_negativeCache.Count - m_negativeCacheMaxEntries));
                for (int i = 0; i < sample.Count && needToRemove > 0; i++)
                {
                    if (m_negativeCache.TryRemove(sample[i].Key, out _))
                        needToRemove--;
                }
            }
        }        

        /// <summary>
        /// Recurses through specified directory checking for asset files last
        /// accessed prior to purgeTimeline and deletes them. Also removes empty tier directories.
        /// </summary>
        /// <param name="dir"></param>
        /// <param name="purgeTimeline"></param>
        private int CleanExpiredFiles(string dir, Dictionary<UUID, sbyte> gids, DateTime purgeTimeline, int cooldown)
        {
            try
            {
                if (!m_cleanupRunning)
                    return cooldown;

                int dirSize = 0;

                // Recurse into lower tiers
                foreach (string subdir in Directory.EnumerateDirectories(dir))
                {
                    if (!m_cleanupRunning)
                        return cooldown;

                    ++dirSize;
                    cooldown = CleanExpiredFiles(subdir, gids, purgeTimeline, cooldown);
                    if (++cooldown > 10)
                    {
                        Thread.Sleep(60);
                        cooldown = 0;
                    }
                }

                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (!m_cleanupRunning)
                        return cooldown;

                    ++dirSize;
                    string id = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    // Optional cleanup of stale .bak files (from interrupted Replace)
                    if (m_EnableBakCleanup && id.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            DateTime lastWrite = File.GetLastWriteTime(file);
                            if (DateTime.Now - lastWrite > m_BakMaxAge)
                            {
                                File.Delete(file);
                                --dirSize;
                                cooldown += 2;
                                if (cooldown >= 20)
                                {
                                    Thread.Sleep(60);
                                    cooldown = 0;
                                }
                                continue;
                            }
                        }
                        catch { /* ignore */ }
                    }

                    if (m_defaultAssets.Contains(id) || (UUID.TryParse(id, out UUID uid) && gids.ContainsKey(uid)))
                    {
                        ++cooldown;
                        if (cooldown >= 20)
                        {
                            Thread.Sleep(60);
                            cooldown = 0;
                        }
                        continue;
                    }

                    if (File.GetLastAccessTime(file) < purgeTimeline)
                    {
                        try
                        {
                            File.Delete(file);
                            weakAssetReferences.TryRemove(id, out _);
                        }
                        catch { }
                        --dirSize;
                        cooldown += 5;
                    }
                }
                
                if (++cooldown >= 20)
                {
                    Thread.Sleep(60);
                    cooldown = 0;
                }

                // Check if a tier directory is empty, if so, delete it
                if (m_cleanupRunning && dirSize == 0)
                {
                    try { Directory.Delete(dir); } catch { }
                    cooldown += 5;
                    if (cooldown >= 20)
                    {
                        Thread.Sleep(60);
                        cooldown = 0;
                    }
                }
                else if (dirSize >= m_CacheWarnAt)
                {
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Cache folder exceeded CacheWarnAt limit {dir} {dirSize}. Suggest increasing tiers, tier length, or reducing cache expiration");
                }
            }
            catch (DirectoryNotFoundException)
            {
                // already removed; continue
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Could not complete clean of expired files in {dir}: {e.Message}");
            }
            return cooldown;
        }

        /// <summary>
        /// Determines the filename for an AssetID stored in the file cache
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private string GetFileName(string id)
        {
            // guard for empty/invalid ids
            if (string.IsNullOrWhiteSpace(id))
                return null;
            
            int indx = id.IndexOfAny(m_InvalidChars);
            string safeId = id;
            if (indx >= 0)
            {
                StringBuilder sb = osStringBuilderCache.Acquire();
                sb.Append(id);
                int sublen = id.Length - indx;
                for (int i = 0; i < m_InvalidChars.Length; ++i)
                    sb.Replace(m_InvalidChars[i], '_', indx, sublen);
                safeId = osStringBuilderCache.GetStringAndRelease(sb);
            }
            
            int minLen = Math.Max(m_CacheDirectoryTierLen, m_CacheDirectoryTiers * m_CacheDirectoryTierLen);
            if (safeId.Length < minLen)
            {
                // pad to avoid substring errors
                safeId = safeId.PadRight(minLen, '_');
            }

            StringBuilder pathSb = osStringBuilderCache.Acquire();
            if (m_CacheDirectoryTiers == 1)
            {
                pathSb.Append(safeId.AsSpan(0, m_CacheDirectoryTierLen));
                pathSb.Append(Path.DirectorySeparatorChar);
            }
            else
            {
                for (int p = 0; p < m_CacheDirectoryTiers * m_CacheDirectoryTierLen; p += m_CacheDirectoryTierLen)
                {
                    pathSb.Append(safeId.AsSpan(p, m_CacheDirectoryTierLen));
                    pathSb.Append(Path.DirectorySeparatorChar);
                }
            }
            pathSb.Append(safeId);

            return Path.Combine(m_CacheDirectory, osStringBuilderCache.GetStringAndRelease(pathSb));
        }

        /// <summary>
        /// Writes a file to the file cache, creating any necessary
        /// tier directories along the way
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="asset"></param>
        /// <param name="replace"></param>
        private static void WriteFileCache(string filename, AssetBase asset, bool replace)
        {
            try
            {
                // If the file is already cached, don't cache it, just touch it so access time is updated
                if (!replace && File.Exists(filename))
                {
                    if (m_updateFileTimeOnCacheHit)
                        UpdateFileLastAccessTime(filename);
                    return;
                }

                string directory = Path.GetDirectoryName(filename);
                string tempname = Path.Combine(directory!, Path.GetRandomFileName());
                try
                {
                    if (!Directory.Exists(directory))
                        Directory.CreateDirectory(directory!);

                    using (var stream = new FileStream(tempname, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
                    {
                        SerializeAsset(stream, asset);
                        stream.Flush();
                    }
                    m_lastFileAccessTimeChange?.Add(filename, 900000);
                }
                catch (IOException e)
                {
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Failed to write asset {asset.ID} to temporary location {tempname} (final {filename}) on cache in {directory}: {e.Message}");
                    return;
                }
                catch (UnauthorizedAccessException e)
                {
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Unauthorized writing asset {asset.ID} to {directory}: {e.Message}");
                    return;
                }

                try
                {
                    // 1) Try atomic replace with optional backup (Windows/NTFS)
                    if (replace && File.Exists(filename))
                    {
                        string backupPath = null;
                        if (m_enableReplaceBackup)
                        {
                            // keep a backup alongside the file to minimize cross-device issues
                            backupPath = filename + ".bak";
                            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                        }

                        try
                        {
                            File.Replace(tempname, filename, backupPath, ignoreMetadataErrors: true);
                            // on success, consider cleaning backup (optional)
                            if (backupPath != null)
                            {
                                try { File.Delete(backupPath); } catch { }
                            }
                            return;
                        }
                        catch
                        {
                            // fall through to move-based strategies
                        }
                    }

                    // 2) Prefer move with overwrite when available (Framework >= .NET Core)
                    // If not replacing, this also serves as the default move
#if NET6_0_OR_GREATER
                    if (m_enableMoveOverwrite)
                    {
                        File.Move(tempname, filename, overwrite: replace);
                        return;
                    }
#endif
                    // 3) Legacy fallback: delete + move (small window)
                    if (replace && File.Exists(filename))
                    {
                        try { File.Delete(filename); } catch { }
                    }
                    File.Move(tempname, filename);
                }
                catch (Exception e)
                {
                    try { File.Delete(tempname); } catch { }
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Failed to finalize write for {asset.ID} to {filename}: {e.Message}");
                }
            }
            finally
            {
                m_CurrentlyWriting.TryRemove(filename, out _);
            }
        }

        /// <summary>
        /// Scan through the file cache and return number of assets currently cached.
        /// </summary>
        /// <param name="dir"></param>
        /// <returns></returns>
        private int GetFileCacheCount(string dir)
        {
            try
            {
                int count = 0;
                foreach (string subdir in Directory.EnumerateDirectories(dir))
                {
                    count += GetFileCacheCount(subdir);
                }
                foreach (var _ in Directory.EnumerateFiles(dir))
                    count++;
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// This notes the last time the Region had a deep asset scan performed on it.
        /// </summary>
        /// <param name="regionID"></param>
        private void StampRegionStatusFile(UUID regionID)
        {
            string RegionCacheStatusFile = Path.Combine(m_CacheDirectory, $"RegionStatus_{regionID}.fac");

            try
            {
                if (File.Exists(RegionCacheStatusFile))
                    File.SetLastWriteTime(RegionCacheStatusFile, DateTime.Now);
                else
                {
                    File.WriteAllText(
                        RegionCacheStatusFile,
                        "Please do not delete this file unless you are manually clearing your Flotsam Asset Cache.");
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Could not stamp region status file for region {regionID}: {e.Message}");
            }
        }

        /// <summary>
        /// Iterates through all Scenes, doing a deep scan through assets
        /// to update the access time of all assets present in the scene or referenced by assets
        /// in the scene.
        /// </summary>
        /// <param name="tryGetUncached">
        /// If true, then assets scanned which are not found in cache are added to the cache.
        /// </param>
        /// <returns>Number of distinct asset references found in the scene.</returns>
        private int TouchAllSceneAssets(bool tryGetUncached)
        {
            m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] start touch files of assets in use");

            Dictionary<UUID, sbyte> gatheredids = GatherSceneAssets();

            int cooldown = 0;
            foreach (UUID id in gatheredids.Keys)
            {
                if (!m_cleanupRunning)
                    break;

                string idstr = id.ToString();
                if (!CheckUpdateFileLastAccessTime(GetFileName(idstr)) && tryGetUncached)
                {
                    cooldown += 5;
                    m_AssetService.Get(idstr);
                }
                if (++cooldown > 50)
                {
                    Thread.Sleep(50);
                    cooldown = 0;
                }
            }
            return gatheredids.Count;
        }

        private Dictionary<UUID, sbyte> GatherSceneAssets()
        {
            m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] gather assets in use");

            Dictionary<UUID, sbyte> gatheredids = new();
            UuidGatherer gatherer = new(m_AssetService, gatheredids);

            int cooldown = 0;
            foreach (Scene s in m_Scenes)
            {
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture1, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture2, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture3, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture4, (sbyte)AssetType.Texture);

                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainPBR1, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainPBR2, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainPBR3, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainPBR4, (sbyte)AssetType.Texture);

                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainImageID, (sbyte)AssetType.Texture);

                s.RegionEnvironment?.GatherAssets(gatheredids);

                if (s.LandChannel is not null)
                {
                    List<ILandObject> landObjects = s.LandChannel.AllParcels();
                    foreach (ILandObject lo in landObjects)
                    {
                        if (lo.LandData is not null && lo.LandData.Environment is not null)
                            lo.LandData.Environment.GatherAssets(gatheredids);
                    }
                }

                EntityBase[] entities = s.Entities.GetEntities();
                foreach (EntityBase entity in entities.AsSpan())
                {
                    if (!m_cleanupRunning)
                        break;

                    if (entity is SceneObjectGroup sog)
                    {
                        if (sog.IsDeleted)
                            continue;

                        gatherer.AddForInspection(sog);
                        while (gatherer.GatherNext())
                        {
                            if (++cooldown > 50)
                            {
                                Thread.Sleep(60);
                                cooldown = 0;
                            }
                        }
                        if (++cooldown > 25)
                        {
                            Thread.Sleep(60);
                            cooldown = 0;
                        }
                    }
                    else if (entity is ScenePresence sp)
                    {
                        if (sp.IsChildAgent || sp.IsDeleted || sp.Appearance is null)
                            continue;

                        Primitive.TextureEntry Texture = sp.Appearance.Texture;
                        if (Texture is null)
                            continue;

                        Primitive.TextureEntryFace[] FaceTextures = Texture.FaceTextures;
                        if (FaceTextures is null)
                            continue;

                        for (int it = 0; it < AvatarAppearance.BAKE_INDICES.Length; it++)
                        {
                            int idx = AvatarAppearance.BAKE_INDICES[it];
                            if (idx < FaceTextures.Length)
                            {
                                Primitive.TextureEntryFace face = FaceTextures[idx];
                                if (face is null)
                                    continue;
                                if (face.TextureID.IsZero() || face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                                    continue;
                                gatherer.AddGathered(face.TextureID, (sbyte)AssetType.Texture);
                            }
                        }
                    }
                }
                entities = null;
                if (!m_cleanupRunning)
                    break;

                StampRegionStatusFile(s.RegionInfo.RegionID);
            }

            gatherer.GatherAll();
            gatherer.FailedUUIDs.Clear();
            gatherer.UncertainAssetsUUIDs.Clear();

            m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE]     found {gatheredids.Count} possible assets in use)");
            return gatheredids;
        }

        /// <summary>
        /// Deletes all cache contents
        /// </summary>
        private void ClearFileCache()
        {
            if (!Directory.Exists(m_CacheDirectory))
                return;

            foreach (string dir in Directory.EnumerateDirectories(m_CacheDirectory))
            {
                try { Directory.Delete(dir, true); }
                catch (Exception e)
                {
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Couldn't clear asset cache directory {dir} from {m_CacheDirectory}: {e.Message}");
                }
            }

            foreach (string file in Directory.GetFiles(m_CacheDirectory))
            {
                try { File.Delete(file); }
                catch (Exception e)
                {
                    m_log.Warn($"[CONCURRENT FLOTSAM ASSET CACHE]: Couldn't clear asset cache file {file} from {m_CacheDirectory}: {e.Message}");
                }
            }
        }

        private List<string> GenerateCacheHitReport()
        {
            List<string> outputLines = new();

            double invReq = 100.0 / Math.Max(1UL, m_Requests);

            double weakHitRate = m_weakRefHits * invReq;

            // Sample-based alive counting to reduce CPU cost on large maps (configurable)
            int weakEntries = weakAssetReferences.Count;
            int weakEntriesAliveSampled = 0;
            int sampled = 0;

            int sampleTarget = m_HitReportWeakRefSampleTarget; // from config
            int sampleStep = 1;

            if (weakEntries > sampleTarget && sampleTarget > 0)
                sampleStep = Math.Max(1, weakEntries / sampleTarget);

            int index = 0;
            foreach (var kv in weakAssetReferences)
            {
                if ((index++ % sampleStep) != 0)
                    continue;

                if (kv.Value.TryGetTarget(out _))
                    weakEntriesAliveSampled++;

                sampled++;
                if (sampled >= sampleTarget)
                    break; // cap the work even if map grows while iterating
            }

            int weakEntriesAlive = weakEntries == 0 || sampled == 0
                ? 0
                : (int)Math.Round((double)weakEntriesAliveSampled / sampled * weakEntries);

            double fileHitRate = m_DiskHits * invReq;
            double TotalHitRate = weakHitRate + fileHitRate;

            outputLines.Add($"Total requests: {m_Requests}");
            outputLines.Add($"unCollected Hit Rate: {weakHitRate:0.00}% ({weakEntries} entries ~{weakEntriesAlive} alive [sampled {sampled}])");
            outputLines.Add($"File Hit Rate: {fileHitRate:0.00}%");

            if (m_MemoryCacheEnabled)
            {
                double HitRate = m_MemoryHits * invReq;
                outputLines.Add($"Memory Hit Rate: {HitRate:0.00}%");
                TotalHitRate += HitRate;
            }
            outputLines.Add($"Total Hit Rate: {TotalHitRate:0.00}%");

            outputLines.Add($"Requests overlap during file writing: {m_RequestsForInprogress}");

            return outputLines;
        }

        #region Console Commands
        private void HandleConsoleCommand(string module, string[] cmdparams)
        {
            ICommandConsole con = MainConsole.Instance;

            if (cmdparams.Length >= 2)
            {
                string cmd = cmdparams[1];

                switch (cmd)
                {
                    case "cleanbak":
                    {
                        if (!m_FileCacheEnabled)
                        {
                            con.Output("File cache not enabled.");
                            break;
                        }
                        if (!m_EnableBakCleanup)
                        {
                            con.Output("Bak cleanup disabled by config.");
                            break;
                        }

                        int removed = 0;
                        try
                        {
                            foreach (string dir in Directory.EnumerateDirectories(m_CacheDirectory))
                                removed += CleanBakInDir(dir);

                            // also check root for stray .bak files
                            foreach (string file in Directory.EnumerateFiles(m_CacheDirectory, "*.bak"))
                            {
                                try
                                {
                                    if (DateTime.Now - File.GetLastWriteTime(file) > m_BakMaxAge)
                                    {
                                        File.Delete(file);
                                        removed++;
                                    }
                                }
                                catch { }
                            }
                            con.Output($"Bak cleanup finished. Removed {removed} files older than {m_BakMaxAge}.");
                        }
                        catch (Exception e)
                        {
                            con.Output($"Bak cleanup failed: {e.Message}");
                        }
                        break;
                    }                    
                    
                    case "status":
                    {
                        WorkManager.RunInThreadPool(delegate
                        {
                            if (m_MemoryCacheEnabled)
                                con.Output("[CONCURRENT FLOTSAM ASSET CACHE] Memory Cache: {0} assets", m_MemoryCache.Count);
                            else
                                con.Output("[CONCURRENT FLOTSAM ASSET CACHE] Memory cache disabled");

                            if (m_FileCacheEnabled)
                            {
                                bool doingscan;
                                lock (timerLock)
                                {
                                    doingscan = m_cleanupRunning;
                                }
                                if (doingscan)
                                {
                                    con.Output("[CONCURRENT FLOTSAM ASSET CACHE] a deep scan is in progress, skipping file cache assets count");
                                }
                                else
                                {
                                    con.Output("[CONCURRENT FLOTSAM ASSET CACHE] counting file cache assets");
                                    int fileCount = GetFileCacheCount(m_CacheDirectory);
                                    con.Output("[CONCURRENT FLOTSAM ASSET CACHE]   File Cache: {0} assets", fileCount);
                                }
                            }
                            else
                            {
                                con.Output("[CONCURRENT FLOTSAM ASSET CACHE] File cache disabled");
                            }

                            GenerateCacheHitReport().ForEach(l => con.Output(l));

                            if (m_FileCacheEnabled)
                            {
                                con.Output("[CONCURRENT FLOTSAM ASSET CACHE] Deep scans have previously been performed on the following regions:");

                                foreach (string s in Directory.GetFiles(m_CacheDirectory, "*.fac"))
                                {
                                    int start = s.IndexOf('_');
                                    int end = s.IndexOf('.');
                                    if (start > 0 && end > 0)
                                    {
                                        string RegionID = s.Substring(start + 1, end - start - 1);
                                        DateTime RegionDeepScanTMStamp = File.GetLastWriteTime(s);
                                        con.Output("[CONCURRENT FLOTSAM ASSET CACHE] Region: {0}, {1}", RegionID, RegionDeepScanTMStamp.ToString("MM/dd/yyyy hh:mm:ss"));
                                    }
                                }
                            }
                            
                            con.Output("[CONCURRENT FLOTSAM ASSET CACHE] In-flight joins: {0}", m_InFlightJoins);
                            
                        }, null, "CacheStatus", false);

                        break;
                    }
                    case "clear":
                        if (cmdparams.Length < 2)
                        {
                            con.Output("Usage is cfcache clear [file] [memory]");
                            break;
                        }

                        bool clearMemory = false, clearFile = false;

                        if (cmdparams.Length == 2)
                        {
                            clearMemory = true;
                            clearFile = true;
                        }
                        foreach (string s in cmdparams)
                        {
                            if (s.ToLower() == "memory")
                                clearMemory = true;
                            else if (s.ToLower() == "file")
                                clearFile = true;
                        }

                        if (clearMemory)
                        {
                            if (m_MemoryCacheEnabled)
                            {
                                m_MemoryCache.Clear();
                                con.Output("Memory cache cleared.");
                            }
                            else
                            {
                                con.Output("Memory cache not enabled.");
                            }
                        }

                        if (clearFile)
                        {
                            if (m_FileCacheEnabled)
                            {
                                ClearFileCache();
                                con.Output("File cache cleared.");
                            }
                            else
                            {
                                con.Output("File cache not enabled.");
                            }
                        }
                        if (m_negativeCacheEnabled)
                            m_negativeCache.Clear();
                        break;

                    case "clearnegatives":
                        if (m_negativeCacheEnabled)
                        {
                            int nsz = m_negativeCache.Count;
                            m_negativeCache.Clear();
                            con.Output($"Concurrent Flotsam cache of negatives cleared ({nsz} entries)");
                        }
                        else
                            con.Output("Concurrent Flotsam cache of negatives not enabled");
                        break;

                    case "assets":
                        lock (timerLock)
                        {
                            if (m_cleanupRunning)
                            {
                                con.Output("Concurrent Flotsam assets check already running");
                                return;
                            }
                            m_cleanupRunning = true;
                        }

                        con.Output("Concurrent Flotsam Ensuring assets are cached for all scenes.");

                        WorkManager.RunInThreadPool(delegate
                        {
                            bool wasRunning = false;
                            lock (timerLock)
                            {
                                if (m_timerRunning)
                                {
                                    m_CacheCleanTimer.Stop();
                                    m_timerRunning = false;
                                    wasRunning = true;
                                }
                            }

                            if (wasRunning)
                                Thread.Sleep(120);

                            int assetReferenceTotal = TouchAllSceneAssets(true);

                            lock (timerLock)
                            {
                                if (wasRunning)
                                {
                                    m_CacheCleanTimer.Start();
                                    m_timerRunning = true;
                                }
                                m_cleanupRunning = false;
                            }
                            con.Output("Completed check with {0} assets.", assetReferenceTotal);
                        }, null, "TouchAllSceneAssets", false);

                        break;

                    case "expire":
                        lock (timerLock)
                        {
                            if (m_cleanupRunning)
                            {
                                con.Output("Concurrent Flotsam assets check already running");
                                return;
                            }
                            m_cleanupRunning = true;
                        }

                        if (cmdparams.Length < 3)
                        {
                            con.Output("Invalid parameters for Expire, please specify a valid date & time");
                            m_cleanupRunning = false;
                            break;
                        }

                        string s_expirationDate;
                        DateTime expirationDate;

                        if (cmdparams.Length > 3)
                            s_expirationDate = string.Join(" ", cmdparams, 2, cmdparams.Length - 2);
                        else
                            s_expirationDate = cmdparams[2];

                        if (s_expirationDate.Equals("now", StringComparison.InvariantCultureIgnoreCase))
                            expirationDate = DateTime.Now;
                        else
                        {
                            if (!DateTime.TryParse(s_expirationDate, out expirationDate))
                            {
                                con.Output("{0} is not a valid date & time", cmd);
                                m_cleanupRunning = false;
                                break;
                            }
                            if (expirationDate >= DateTime.Now)
                            {
                                con.Output("{0} date & time must be in past", cmd);
                                m_cleanupRunning = false;
                                break;
                            }
                        }
                        if (m_FileCacheEnabled)
                        {
                            WorkManager.RunInThreadPool(delegate
                            {
                                bool wasRunning = false;
                                lock (timerLock)
                                {
                                    if (m_timerRunning)
                                    {
                                        m_CacheCleanTimer.Stop();
                                        m_timerRunning = false;
                                        wasRunning = true;
                                    }
                                }

                                if (wasRunning)
                                    Thread.Sleep(120);

                                DoCleanExpiredFiles(expirationDate);

                                lock (timerLock)
                                {
                                    if (wasRunning)
                                    {
                                        m_CacheCleanTimer.Start();
                                        m_timerRunning = true;
                                    }
                                    m_cleanupRunning = false;
                                }
                            }, null, "ExpireSceneAssets", false);
                        }
                        else
                            con.Output("File cache not active, not clearing.");

                        break;
                    case "cachedefaultassets":
                        HandleLoadDefaultAssets();
                        break;
                    case "deletedefaultassets":
                        HandleDeleteDefaultAssets();
                        break;
                    default:
                        con.Output("Unknown command {0}", cmd);
                        break;
                }
            }
            else if (cmdparams.Length == 1)
            {
                con.Output("cfcache assets - Attempt a deep cache of all assets in all scenes");
                con.Output("cfcache expire <datetime> - Purge assets older than the specified date & time");
                con.Output("cfcache clear [file] [memory] - Remove cached assets");
                con.Output("cfcache status - Display cache status");
                con.Output("cfcache cachedefaultassets - loads default assets to cache replacing existent ones, this may override grid assets. Use with care");
                con.Output("cfcache deletedefaultassets - deletes default local assets from cache so they can be refreshed from grid");
                con.Output("cfcache cleanbak - Remove stale .bak files from file cache now");
            }
        }
        
        private int CleanBakInDir(string dir)
        {
            int removed = 0;
            try
            {
                foreach (string subdir in Directory.EnumerateDirectories(dir))
                    removed += CleanBakInDir(subdir);

                foreach (string file in Directory.EnumerateFiles(dir, "*.bak"))
                {
                    try
                    {
                        if (DateTime.Now - File.GetLastWriteTime(file) > m_BakMaxAge)
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        #endregion

        #region IAssetService Members (remaining)

        public AssetMetadata GetMetadata(string id)
        {
            Get(id, out AssetBase asset);
            if (asset == null)
                return null;
            return asset.Metadata;
        }

        public byte[] GetData(string id)
        {
            Get(id, out AssetBase asset);
            if (asset == null)
                return null;
            return asset.Data;
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            if (!Get(id, out AssetBase asset))
                return false;
            handler(id, sender, asset);
            return true;
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
        }

        public bool[] AssetsExist(string[] ids)
        {
            bool[] exist = new bool[ids.Length];

            for (int i = 0; i < ids.Length; i++)
                exist[i] = Check(ids[i]);

            return exist;
        }

        public string Store(AssetBase asset)
        {
            if (asset.FullID.IsZero())
                asset.FullID = UUID.Random();

            Cache(asset);

            return asset.ID;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            if (!Get(id, out AssetBase asset))
                return false;
            asset.Data = data;
            Cache(asset, true);
            return true;
        }

        public bool Delete(string id)
        {
            Expire(id);
            return true;
        }

        private void HandleLoadDefaultAssets()
        {
            if (string.IsNullOrWhiteSpace(m_assetLoader))
            {
                m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] default assets loader not defined");
                return;
            }

            IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(m_assetLoader, Array.Empty<object>());
            if (assetLoader == null)
            {
                m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] default assets loader not found");
                return;
            }

            m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] start loading local default assets");

            int count = 0;
            HashSet<string> ids = new();
            assetLoader.ForEachDefaultXmlAsset(
                    m_assetLoaderArgs,
                    delegate (AssetBase a)
                    {
                        Cache(a, true);
                        ids.Add(a.ID);
                        ++count;
                    });
            m_defaultAssets = ids;
            m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE] loaded {count} local default assets");
        }

        private void HandleDeleteDefaultAssets()
        {
            if (string.IsNullOrWhiteSpace(m_assetLoader))
            {
                m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] default assets loader not defined");
                return;
            }

            IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(m_assetLoader, Array.Empty<object>());
            if (assetLoader is null)
            {
                m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] default assets loader not found");
                return;
            }

            m_log.Info("[CONCURRENT FLOTSAM ASSET CACHE] started deleting local default assets");
            int count = 0;
            assetLoader.ForEachDefaultXmlAsset(
                    m_assetLoaderArgs,
                    delegate (AssetBase a)
                    {
                        Expire(a.ID);
                        ++count;
                    });
            m_defaultAssets = new HashSet<string>();
            m_log.Info($"[CONCURRENT FLOTSAM ASSET CACHE] deleted {count} local default assets");
        }
        #endregion
    }
}
