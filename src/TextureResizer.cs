using CompactJson;
using K4os.Hash.xxHash;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TeximpNet;
using TeximpNet.Unmanaged;

namespace OptimizedRemix;

// Texture Resizer by Rawra

internal class TextureResizer
{
    private static readonly Lazy<TextureResizer> lazy = new Lazy<TextureResizer>(() => new TextureResizer());

    public static TextureResizer Instance { get { return lazy.Value; } }

    private TextureResizer()
    {
        // resolves to: %MOD_FOLDE%/textureCache.json
        _cachePath = Path.Combine(IOHelper.ModDirectory, "textureCache.json");

        if (File.Exists(_cachePath))
        {
            try
            {
                Dictionary<string, CacheEntry> dict = Serializer.Parse<Dictionary<string, CacheEntry>>(File.ReadAllText(_cachePath));
                _textureCache = new ConcurrentDictionary<string, CacheEntry>(dict);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Failed to load texture cache: {ex}");
                _textureCache = new ConcurrentDictionary<string, CacheEntry>();
            }
        }
        else
        {
            _textureCache = new ConcurrentDictionary<string, CacheEntry>();
        }
    }

    public const int PREFERRED_WIDTH = 426;
    public const int PREFERRED_HEIGHT = 240;

    private readonly ConcurrentDictionary<string, CacheEntry> _textureCache;
    private readonly string _cachePath;

    // serializable holder for both metadata and full content hashes.
    public class CacheEntry
    {
        public UInt64 MetaHash { get; set; }
        public UInt64 ContentHash { get; set; }
    }

    private unsafe static UInt64 ComputeMetaHash(FileInfo fi)
    {
        // combine length + last write ticks into bytes then hash
        byte[] metaBytes = new byte[16];
        BitConverter.GetBytes(fi.Length).CopyTo(metaBytes, 0);
        BitConverter.GetBytes(fi.LastWriteTimeUtc.Ticks).CopyTo(metaBytes, 8);
        fixed (byte* ptr = metaBytes)
        {
            return XXH64.DigestOf(ptr, metaBytes.Length);
        }
    }

    private unsafe UInt64 ComputeFileHash(string filePath)
    {
        using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[8192];
        XXH64 state = new XXH64();
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            fixed (byte* ptr = buffer)
            {
                state.Update((void*)ptr, read);
            }
        }
        return state.Digest();
    }

    // uses meta-hash first, then streamed content hash if needed.
    private bool FileNeedsUpdate(string filePath)
    {
        FileInfo fi = new FileInfo(filePath);
        if (!fi.Exists)
            return false;

        UInt64 metaHash = ComputeMetaHash(fi);

        if (!_textureCache.TryGetValue(filePath, out CacheEntry entry))
        {
            // No cache entry: compute full hash, store both, return true
            UInt64 contentHash = ComputeFileHash(filePath);
            CacheEntry newEntry = new CacheEntry { MetaHash = metaHash, ContentHash = contentHash };
            _textureCache[filePath] = newEntry;
            Plugin.Logger.LogDebug($"FileNeedsUpdate: No cache entry for {filePath} (stored content hash).");
            return true;
        }

        // If metadata matches, assume unchanged (fast path)
        if (entry.MetaHash == metaHash)
        {
            return false;
        }

        // Metadata changed: verify by computing full content hash
        UInt64 newContentHash = ComputeFileHash(filePath);
        if (newContentHash == entry.ContentHash)
        {
            // content unchanged, only metadata changed: update metaHash and don't reprocess
            entry.MetaHash = metaHash;
            _textureCache[filePath] = entry;

            Plugin.Logger.LogDebug($"FileNeedsUpdate: Only metadata changed for {filePath}, content identical.");
            return false;
        }
        else
        {
            // content changed: update cache and return true
            entry.MetaHash = metaHash;
            entry.ContentHash = newContentHash;
            _textureCache[filePath] = entry;

            Plugin.Logger.LogDebug($"FileNeedsUpdate: Content changed for {filePath} (updated content hash).");
            return true;
        }
    }

    // Save cache
    private void SaveTextureCache()
    {
        try
        {
            // compactjson fails to work with concurrentdict, so we kind of have to do this
            Dictionary<string, CacheEntry> dict = _textureCache.ToDictionary(kv => kv.Key, kv => kv.Value);
            Plugin.Logger.LogDebug($"SaveTextureCache() - Saving {dict.Count} entries...");
            File.WriteAllText(_cachePath, Serializer.ToString(dict, false));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"SaveTextureCache failed: {ex}");
        }
    }

    // Parallelized thumbnail resizing (uses FileNeedsUpdate which triggers streamed hashing when needed)
    public void FixThumbnailPNGSizes(string[] pngs)
    {
        Plugin.Logger.LogInfo($"TextureResizer - Processing {pngs.Length} PNGs...");
        Stopwatch sw = Stopwatch.StartNew();

        bool invalidateCache = false;
        object dirtyLock = new();

        Parallel.ForEach(pngs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
        body: thumbnailPath =>
        {
            try
            {
                // FileNeedsUpdate now handles metadata + streamed hash verification
                if (!FileNeedsUpdate(thumbnailPath))
                {
                    Plugin.Logger.LogDebug($"Skipping unchanged: {thumbnailPath}");
                    return;
                }

                lock (dirtyLock)
                {
                    invalidateCache = true;
                }

                IntPtr freeImage = FreeImageLibrary.Instance.LoadFromFile(thumbnailPath);
                using Surface surface = new Surface(freeImage);

                // skip if same size (avoid unnecessary resize)
                if (surface.Width == PREFERRED_WIDTH && surface.Height == PREFERRED_HEIGHT)
                {
                    Plugin.Logger.LogDebug($"Already preferred size: {thumbnailPath}");
                    return;
                }

                surface.Resize(PREFERRED_WIDTH, PREFERRED_HEIGHT, ImageFilter.Bilinear);

                if (!surface.SaveToFile(ImageFormat.PNG, thumbnailPath, ImageSaveFlags.PNG_Z_BestSpeed))
                    Plugin.Logger.LogError($"Failed saving PNG file: {thumbnailPath}");
                else
                    Plugin.Logger.LogDebug($"Saved PNG file: {thumbnailPath}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error processing {thumbnailPath}: {ex}");
            }
        });

        if (invalidateCache)
            SaveTextureCache();

        sw.Stop();
        Plugin.Logger.LogInfo($"TextureResizer - Processed {pngs.Length} PNGs. Took ~ {sw.ElapsedMilliseconds}ms / {sw.ElapsedTicks}tks");
    }
}