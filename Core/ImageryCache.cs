using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AeroSceneryNG.Core
{
    /// <summary>A decoded, top-down RGBA32 imagery tile.</summary>
    public sealed class RgbaTile
    {
        public RgbaTile(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>RGBA32, row-major, length = Width * Height * 4.</summary>
        public byte[] Pixels { get; private set; }
    }

    /// <summary>
    /// Thread-safe imagery downloader with memory + disk cache.
    ///
    /// * Several downloads run at once (default 16), independent of the CPU-bound encoder.
    /// * Every tile is cached on disk as <c>{root}\{sourceId}\{z}\{x}\{y}.{ext}</c>, so a cancelled
    ///   or crashed run resumes without re-downloading.
    /// * Interrupted single-tile downloads keep a <c>.part</c> file and continue with an HTTP
    ///   Range request instead of restarting from zero.
    /// </summary>
    public sealed class ImageryCache : IDisposable
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly string rootDirectory;
        private readonly SemaphoreSlim downloadSlots;
        private readonly object sync = new object();
        private readonly Dictionary<string, LinkedListNode<MemoryEntry>> memory =
            new Dictionary<string, LinkedListNode<MemoryEntry>>(StringComparer.Ordinal);
        private readonly LinkedList<MemoryEntry> memoryLru = new LinkedList<MemoryEntry>();

        private const int MemoryEntries = 512;
        private long memoryBytes;
        private const long MemoryByteLimit = 96L * 1024 * 1024;

        private long downloadedBytes;
        private int downloadedTiles;
        private int failedTiles;

        public ImageryCache(string rootDirectory)
            : this(rootDirectory, 16)
        {
        }

        public ImageryCache(string rootDirectory, int downloadThreads)
        {
            this.rootDirectory = EnsureWritable(rootDirectory ?? DefaultCacheDirectory);
            downloadSlots = new SemaphoreSlim(Math.Clamp(downloadThreads, 1, 128), 128);
        }

        /// <summary>
        /// Verifies that the cache root can actually be written. Some sandboxed or locked-down
        /// profiles deny <c>%LOCALAPPDATA%</c>; fall back to the temp directory instead of letting
        /// every tile download fail.
        /// </summary>
        private static string EnsureWritable(string preferred)
        {
            string[] candidates =
            {
                preferred,
                Path.Combine(Path.GetTempPath(), "AeroSceneryNG", "tilecache"),
                Path.Combine(AppContext.BaseDirectory, "tilecache")
            };

            Exception last = null;
            foreach (string candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return candidate;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new IOException("没有可写的影像缓存目录。", last);
        }

        public string RootDirectory { get { return rootDirectory; } }

        public static string DefaultCacheDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AeroSceneryNG", "tilecache");
            }
        }

        public long DownloadedBytes { get { return Interlocked.Read(ref downloadedBytes); } }
        public int DownloadedTiles { get { return Volatile.Read(ref downloadedTiles); } }
        public int FailedTiles { get { return Volatile.Read(ref failedTiles); } }

        private static HttpClient CreateHttpClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 256,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                UseProxy = true
            };

            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            return client;
        }

        /// <summary>Downloads (or reads from cache) one tile and decodes it to RGBA32.</summary>
        public async Task<RgbaTile> GetTileAsync(MapSource source, int zoom, int x, int y, CancellationToken ct)
        {
            if (source == null) throw new ArgumentNullException("source");

            string key = source.Id + "/" + zoom + "/" + x + "/" + y;
            RgbaTile cached = TryGetMemory(key);
            if (cached != null) return cached;

            byte[] bytes = await GetTileBytesAsync(source, zoom, x, y, ct).ConfigureAwait(false);
            RgbaTile tile = Decode(bytes, source.FileExtension);
            if (tile != null) PutMemory(key, tile);
            return tile;
        }

        /// <summary>Downloads one tile with resume support and returns the encoded bytes.</summary>
        public async Task<byte[]> GetTileBytesAsync(MapSource source, int zoom, int x, int y, CancellationToken ct)
        {
            string path = TilePathForInstance(source, zoom, x, y);
            if (File.Exists(path))
            {
                try
                {
                    byte[] onDisk = File.ReadAllBytes(path);
                    if (onDisk.Length >= 200) return onDisk;
                }
                catch (IOException) { }
            }

            await downloadSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another worker may have completed the same tile while we were queued.
                if (File.Exists(path))
                {
                    try
                    {
                        byte[] onDisk = File.ReadAllBytes(path);
                        if (onDisk.Length >= 200) return onDisk;
                    }
                    catch (IOException) { }
                }

                return await DownloadWithRetryAsync(source, zoom, x, y, path, ct).ConfigureAwait(false);
            }
            finally
            {
                downloadSlots.Release();
            }
        }

        private async Task<byte[]> DownloadWithRetryAsync(MapSource source, int zoom, int x, int y,
            string path, CancellationToken ct)
        {
            string url = source.BuildUrl(x, y, zoom);
            Exception last = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    byte[] bytes = await DownloadOnceAsync(source, url, path, ct).ConfigureAwait(false);
                    if (bytes == null || bytes.Length < 200)
                        throw new IOException("Tile response was too small (" + (bytes == null ? 0 : bytes.Length) + " bytes).");

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string temp = path + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);

                    Interlocked.Add(ref downloadedBytes, bytes.Length);
                    Interlocked.Increment(ref downloadedTiles);
                    return bytes;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Interlocked.Increment(ref failedTiles);
                    if (attempt < 2)
                        await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }

            throw new IOException("下载瓦片失败 " + zoom + "/" + x + "/" + y + " " + (last != null ? last.Message : "未知错误"), last);
        }

        private async Task<byte[]> DownloadOnceAsync(MapSource source, string url, string path, CancellationToken ct)
        {
            string part = path + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            long have = 0;
            try
            {
                if (File.Exists(part)) have = new FileInfo(part).Length;
            }
            catch (IOException) { }

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(source.UserAgent))
                    request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
                if (!string.IsNullOrEmpty(source.Referer))
                    request.Headers.TryAddWithoutValidation("Referer", source.Referer);
                if (have > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

                HttpResponseMessage response;
                try
                {
                    response = await Http.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("下载超时（" + Math.Round(Http.Timeout.TotalSeconds) + " 秒）");
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
                    {
                        // The .part file already holds the complete tile.
                        byte[] done = File.ReadAllBytes(part);
                        File.Delete(part);
                        return done;
                    }

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);

                    byte[] payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                    if (have > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        byte[] combined = new byte[have + payload.Length];
                        using (FileStream fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int read = 0;
                            while (read < have)
                            {
                                int n = fs.Read(combined, read, (int)Math.Min(int.MaxValue, have - read));
                                if (n <= 0) break;
                                read += n;
                            }
                        }
                        Buffer.BlockCopy(payload, 0, combined, (int)have, payload.Length);
                        File.Delete(part);
                        return combined;
                    }

                    // Server ignored Range (200) — start over.
                    if (File.Exists(part)) File.Delete(part);
                    return payload;
                }
            }
        }

        /// <summary>Writes a partial response body for the next resume. Only used when the caller
        /// aborts mid-download; normal downloads are written atomically above.</summary>
        internal void SavePartial(string path, byte[] prefix)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path + ".part", prefix);
            }
            catch { }
        }

        public static RgbaTile Decode(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                using (Image<Rgba32> image = Image.Load<Rgba32>(bytes))
                {
                    byte[] pixels = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(pixels);
                    return new RgbaTile(image.Width, image.Height, pixels);
                }
            }
            catch
            {
                // Some providers return HTML/error images; skip that tile instead of failing the job.
                return null;
            }
        }

        private static string TilePath(MapSource source, int zoom, int x, int y)
        {
            string ext = string.IsNullOrEmpty(source.FileExtension) ? "jpg" : source.FileExtension.TrimStart('.');
            return Path.Combine(DefaultRootFor(source), source.Id, zoom.ToString(), x.ToString(), y + "." + ext);
        }

        private static string DefaultRootFor(MapSource source)
        {
            return DefaultCacheDirectory;
        }

        private string TilePathForInstance(MapSource source, int zoom, int x, int y)
        {
            string ext = string.IsNullOrEmpty(source.FileExtension) ? "jpg" : source.FileExtension.TrimStart('.');
            return Path.Combine(rootDirectory, source.Id, zoom.ToString(), x.ToString(), y + "." + ext);
        }

        private RgbaTile TryGetMemory(string key)
        {
            lock (sync)
            {
                LinkedListNode<MemoryEntry> node;
                if (!memory.TryGetValue(key, out node)) return null;
                memoryLru.Remove(node);
                memoryLru.AddFirst(node);
                return node.Value.Tile;
            }
        }

        private void PutMemory(string key, RgbaTile tile)
        {
            lock (sync)
            {
                LinkedListNode<MemoryEntry> existing;
                if (memory.TryGetValue(key, out existing))
                {
                    memoryLru.Remove(existing);
                    memoryBytes -= existing.Value.Tile.Pixels.Length;
                    memory.Remove(key);
                }

                LinkedListNode<MemoryEntry> node = new LinkedListNode<MemoryEntry>(
                    new MemoryEntry { Key = key, Tile = tile });
                memoryLru.AddFirst(node);
                memory[key] = node;
                memoryBytes += tile.Pixels.Length;

                while (memory.Count > MemoryEntries || memoryBytes > MemoryByteLimit)
                {
                    LinkedListNode<MemoryEntry> last = memoryLru.Last;
                    if (last == null) break;
                    memoryLru.RemoveLast();
                    memory.Remove(last.Value.Key);
                    memoryBytes -= last.Value.Tile.Pixels.Length;
                }
            }
        }

        public void ClearMemory()
        {
            lock (sync)
            {
                memory.Clear();
                memoryLru.Clear();
                memoryBytes = 0;
            }
        }

        public long CacheSizeBytes()
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(rootDirectory)) return 0;
                foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            return total;
        }

        /// <summary>
        /// Deletes the oldest cached files until the cache fits into <paramref name="maxBytes"/>.
        /// A value of zero (or less) disables the limit. Returns the number of bytes freed.
        /// </summary>
        public long EnforceLimit(long maxBytes)
        {
            if (maxBytes <= 0) return 0;

            long total = CacheSizeBytes();
            if (total <= maxBytes) return 0;

            long freed = 0;
            try
            {
                var files = new List<FileInfo>();
                foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(info);
                        }
                    }
                    catch (IOException) { }
                }

                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (FileInfo info in files)
                {
                    if (total - freed <= maxBytes) break;
                    try
                    {
                        long length = info.Length;
                        info.Delete();
                        freed += length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                RemoveEmptyDirectories(rootDirectory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            ClearMemory();
            return freed;
        }

        private static void RemoveEmptyDirectories(string root)
        {
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory);
                        }
                    }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void ClearDiskCache()
        {
            ClearMemory();
            try
            {
                if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true);
                Directory.CreateDirectory(rootDirectory);
            }
            catch (IOException) { }
        }

        public void Dispose()
        {
            ClearMemory();
            downloadSlots.Dispose();
        }

        private sealed class MemoryEntry
        {
            public string Key;
            public RgbaTile Tile;
        }
    }
}
