using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroSceneryNG.Ttc;
using AeroSceneryNG.Tth;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AeroSceneryNG.Core
{
    /// <summary>Everything needed to build one scenery package.</summary>
    public sealed class SceneryJob
    {
        public MapSource Source { get; set; }
        public double North { get; set; }
        public double South { get; set; }
        public double East { get; set; }
        public double West { get; set; }

        public List<int> Levels { get; set; } = new List<int>();
        public Dictionary<int, int> LevelZooms { get; set; } = new Dictionary<int, int>();

        /// <summary>Fine-grained selection cells; when non-empty they override the bounding box.</summary>
        public List<SelectionCell> SelectedCells { get; set; } = new List<SelectionCell>();
        public int SelectionLevel { get; set; } = 12;

        /// <summary>Write each untuned 2048px mosaic as a PNG for external editing/archival.</summary>
        public bool SaveOriginalImages { get; set; }
        public string OriginalImagesDirectory { get; set; }

        /// <summary>Global zoom override (-1 = automatic). Per-level entries win over this.</summary>
        public int ImageZoom { get; set; } = -1;

        public string OutputDirectory { get; set; }
        public SceneryTarget Target { get; set; } = SceneryTarget.Fs4;

        public bool InstallIntoScenery { get; set; }
        public string SceneryDirectory { get; set; }

        /// <summary>Optional mounted FSG working folder; the fsg_scenery_* folder is copied here.</summary>
        public string FsgDirectory { get; set; }

        public bool PackageTme { get; set; } = true;

        /// <summary>Also build AFS4 terrain elevation (.tth) from the Terrarium DEM.</summary>
        public bool GenerateElevation { get; set; }

        public string SceneryName { get; set; } = "MyScenery";

        public bool SkipExisting { get; set; } = true;
        public int DownloadThreads { get; set; } = 16;
        public int WorkerThreads { get; set; }

        /// <summary>TTC payload codec. zlib is what the reference converter writes.</summary>
        public TtcCodec Codec { get; set; } = TtcCodec.Lzham;

        /// <summary>Optional colour grading / water unification applied to each mosaic.</summary>
        public ImageTuningOptions Tuning { get; set; } = new ImageTuningOptions();

        public int ResolveZoom(int level)
        {
            int z;
            if (LevelZooms != null && LevelZooms.TryGetValue(level, out z) && z > 0)
                return Math.Clamp(z, 0, Target.MaxImageZoom());
            if (ImageZoom > 0)
                return Math.Clamp(ImageZoom, 0, Target.MaxImageZoom());
            return Math.Clamp(MosaicComposer.AutoZoom(level), 0, Target.MaxImageZoom());
        }
    }

    public sealed class SceneryProgress
    {
        public double Fraction { get; set; }
        public string Message { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public long BytesDownloaded { get; set; }
        public int Failed { get; set; }

        /// <summary>准备 / 下载并转换 / 安装 / 打包 / 完成.</summary>
        public string Stage { get; set; } = "";
        public string CurrentTile { get; set; } = "";
        public long DownloadedBytes { get; set; }
        public int DownloadedTiles { get; set; }
        public double ElapsedSeconds { get; set; }
        public int Workers { get; set; }
    }

    public sealed class ScenerySummary
    {
        public int Tiles;
        public int Failed;
        public long Bytes;
        public int ElevationTiles;
        public string ElevationDirectory;
        public string Fs4ImagesDirectory;
        public string FsgImagesDirectory;
        public string TmePath;
        public TimeSpan Elapsed;
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Orchestrates the whole pipeline: plan AFS tiles, download Web-Mercator imagery, compose
    /// 2048x2048 mosaics, encode DXT1 and/or ETC2, write TTC files into level-9 folders, install
    /// into the simulator and package the FSG <c>.tme</c>.
    /// </summary>
    public sealed class SceneryBuilder
    {
        private readonly ImageryCache cache;

        public SceneryBuilder(ImageryCache cache)
        {
            this.cache = cache ?? throw new ArgumentNullException("cache");
        }

        public static int RecommendedWorkers(int requested)
        {
            if (requested > 0) return Math.Clamp(requested, 1, 16);
            int cpu = Environment.ProcessorCount;
            // LZHAM needs roughly 8-12 MB of tables per worker; keep low-end laptops responsive.
            return Math.Clamp(cpu / 2, 2, 6);
        }

        public async Task<ScenerySummary> RunAsync(SceneryJob job, IProgress<SceneryProgress> progress,
            CancellationToken ct)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (job.Source == null) throw new ArgumentException(Loc.T("请选择一个图源。"), "job");
            if (job.Levels == null || job.Levels.Count == 0) throw new ArgumentException(Loc.T("请至少选择一个 AFS 层级。"), "job");
            if (string.IsNullOrWhiteSpace(job.OutputDirectory)) throw new ArgumentException(Loc.T("请选择输出目录。"), "job");

            Stopwatch watch = Stopwatch.StartNew();
            string name = AppSettings.SanitiseName(job.SceneryName);
            string output = Path.GetFullPath(job.OutputDirectory);

            string fs4Images = job.Target.WantsFs4()
                ? Path.Combine(output, "fs4", "scenery", "images")
                : null;
            string fsgRoot = job.Target.WantsFsg()
                ? Path.Combine(output, "fsg", "fsg_scenery_" + name, "fsg_scenery_" + name + "_images")
                : null;
            string fsgImages = fsgRoot != null ? Path.Combine(fsgRoot, "scenery", "images") : null;

            List<TilePlan> plans = Plan(job, fs4Images, fsgImages);
            if (plans.Count == 0) throw new InvalidOperationException(Loc.T("选择范围内没有可生成的区块。"));

            int workers = RecommendedWorkers(job.WorkerThreads);
            int done = 0;
            int failed = 0;
            long written = 0;
            long downloadBase = cache.DownloadedBytes;
            int downloadTileBase = cache.DownloadedTiles;
            var errors = new ConcurrentQueue<string>();
            var reportLock = new object();
            DateTime lastReport = DateTime.MinValue;

            Action<int, string, string, string, bool> report = (doneNow, message, stage, tile, force) =>
            {
                if (progress == null) return;
                DateTime now = DateTime.UtcNow;
                lock (reportLock)
                {
                    if (!force && (now - lastReport).TotalMilliseconds < 120) return;
                    lastReport = now;
                }
                Report(progress, plans.Count == 0 ? 0 : (double)doneNow / plans.Count, plans.Count,
                    Interlocked.Read(ref written), failed, message, stage, tile,
                    cache.DownloadedBytes - downloadBase, cache.DownloadedTiles - downloadTileBase,
                    watch.Elapsed.TotalSeconds, workers);
            };

            Action<string> noteError = message =>
            {
                if (errors.Count > 200) return;
                errors.Enqueue(message);
                Interlocked.Increment(ref failed);
            };

            report(0, Loc.F("准备中：共 {0} 个区块 · {1} 个转换线程 · {2} 个下载线程…",
                plans.Count, workers, Math.Max(1, job.DownloadThreads)), "准备", "", true);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(plans, options, async (plan, token) =>
            {
                token.ThrowIfCancellationRequested();

                bool needFs4 = plan.Fs4Path != null && !(job.SkipExisting && IsComplete(plan.Fs4Path));
                bool needFsg = plan.FsgPath != null && !(job.SkipExisting && IsComplete(plan.FsgPath));

                if (!needFs4 && !needFsg)
                {
                    int d = Interlocked.Increment(ref done);
                    report(d, Loc.F("跳过已完成 {0}", plan.Tile.FileName), "下载并转换", plan.Tile.FileName, false);
                    return;
                }

                report(Volatile.Read(ref done), Loc.F("下载 {0}", plan.Tile.FileName), "下载并转换",
                    plan.Tile.FileName, true);

                try
                {
                    byte[] mosaic = await MosaicComposer.ComposeAsync(cache, job.Source, plan.Tile, plan.Zoom, token)
                        .ConfigureAwait(false);

                    if (job.SaveOriginalImages && !string.IsNullOrWhiteSpace(job.OriginalImagesDirectory))
                    {
                        SaveOriginalPng(mosaic, job, plan, token);
                    }

                    ImageTuning.Apply(mosaic, MosaicComposer.TileSize, MosaicComposer.TileSize, job.Tuning);

                    if (needFs4)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(plan.Fs4Path));
                        TtcWriter.WriteDxt1(plan.Fs4Path, mosaic, MosaicComposer.TileSize, MosaicComposer.TileSize,
                            plan.Tile.Level, true, job.Codec);
                        Interlocked.Add(ref written, new FileInfo(plan.Fs4Path).Length);
                    }

                    if (needFsg)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(plan.FsgPath));
                        TtcWriter.WriteEtc2(plan.FsgPath, mosaic, MosaicComposer.TileSize, MosaicComposer.TileSize,
                            plan.Tile.Level, true, job.Codec);
                        Interlocked.Add(ref written, new FileInfo(plan.FsgPath).Length);
                    }

                    int d = Interlocked.Increment(ref done);
                    report(d, plan.Tile.FileName + " · ZL" + plan.Zoom + " · " + (plan.Fs4Path == null ? "ETC2" : "DXT1")
                        + (plan.FsgPath != null && plan.Fs4Path != null ? "+ETC2" : ""), "下载并转换",
                        plan.Tile.FileName, false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    noteError(Loc.F("失败 {0}：{1}", plan.Tile.FileName, ex.Message));
                    int d = Interlocked.Increment(ref done);
                    report(d, Loc.F("失败 {0}：{1}", plan.Tile.FileName, ex.Message), "下载并转换", plan.Tile.FileName, false);
                }
            }).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            int elevationTiles = 0;
            string elevationDirectory = null;
            if (job.GenerateElevation)
            {
                List<Afs2Tile> tiles = Afs2Grid.TilesForBounds(job.North, job.South, job.East, job.West, 10);
                if (tiles.Count > 0)
                {
                    elevationDirectory = job.Target.WantsFs4()
                        ? Path.Combine(output, "fs4", "scenery", "elevation")
                        : Path.Combine(output, "elevation");
                    Directory.CreateDirectory(elevationDirectory);
                    string elevationCache = Path.Combine(output, ".elevation-cache");
                    int elevationDone = 0;
                    report(plans.Count, Loc.F("生成地形高程：0 / {0}…", tiles.Count), "高程", "", true);

                    await Parallel.ForEachAsync(tiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 2,
                        CancellationToken = ct
                    }, async (tile, token) =>
                    {
                        string fileName = Afs2Grid.TileName(10, tile.GridX, tile.GridY)
                            .Replace("map_", "maph_") + ".tth";
                        string path = Path.Combine(elevationDirectory, fileName);
                        try
                        {
                            if (!(job.SkipExisting && IsComplete(path)))
                            {
                                ushort[] samples = await TerrariumDem.BuildTileAsync(10, tile.GridX, tile.GridY,
                                    elevationCache, token).ConfigureAwait(false);
                                TthWriter.Write(path, 10, samples, TthWriter.GridSize, TthWriter.GridSize);
                            }
                            int d = Interlocked.Increment(ref elevationDone);
                            report(plans.Count, Loc.F("高程 {0} / {1}：{2}", d, tiles.Count, fileName),
                                "高程", fileName, true);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            noteError(Loc.F("高程 {0}：{1}", fileName, ex.Message));
                            Interlocked.Increment(ref elevationDone);
                        }
                    }).ConfigureAwait(false);

                    elevationTiles = tiles.Count;
                    try { Directory.Delete(elevationCache, true); } catch { }

                    if (job.InstallIntoScenery && job.Target.WantsFs4() &&
                        !string.IsNullOrWhiteSpace(job.SceneryDirectory))
                    {
                        string sceneryRoot = Path.GetDirectoryName(job.SceneryDirectory);
                        if (!string.IsNullOrWhiteSpace(sceneryRoot))
                        {
                            report(plans.Count, Loc.T("安装高程到 Aerofly FS 4…"), "安装", "", true);
                            CopyTree(elevationDirectory, Path.Combine(sceneryRoot, "elevation"));
                        }
                    }
                }
            }

            ScenerySummary summary = new ScenerySummary
            {
                Tiles = plans.Count,
                Failed = failed,
                Bytes = Interlocked.Read(ref written),
                ElevationTiles = elevationTiles,
                ElevationDirectory = elevationDirectory,
                Fs4ImagesDirectory = fs4Images,
                FsgImagesDirectory = fsgImages
            };

            if (fs4Images != null && Directory.Exists(fs4Images))
            {
                WriteManifest(Path.Combine(output, "fs4", "scenery", "aeroscenery.json"), job, plans, "fs4");
                if (job.InstallIntoScenery && !string.IsNullOrWhiteSpace(job.SceneryDirectory))
                {
                    report(plans.Count, Loc.T("安装到 Aerofly FS 4…"), "安装", "", true);
                    Directory.CreateDirectory(job.SceneryDirectory);
                    CopyTree(fs4Images, job.SceneryDirectory);
                }
            }

            if (fsgImages != null && Directory.Exists(fsgImages))
            {
                WriteManifest(Path.Combine(output, "fsg", "aeroscenery.json"), job, plans, "fsg");

                if (job.PackageTme)
                {
                    report(plans.Count, Loc.T("打包 FSG _images.tme…"), "打包", "", true);
                    string tme = Path.Combine(output, name + "_images.tme");
                    TmePackager.PackImages(fsgRoot, tme);
                    summary.TmePath = tme;
                }

                if (!string.IsNullOrWhiteSpace(job.FsgDirectory))
                {
                    try
                    {
                        string sourceRoot = Path.Combine(output, "fsg", "fsg_scenery_" + name);
                        string destRoot = Path.Combine(job.FsgDirectory, "fsg_scenery_" + name);
                        Directory.CreateDirectory(job.FsgDirectory);
                        CopyTree(sourceRoot, destRoot);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(Loc.F("复制到 FSG 目录失败：{0}", ex.Message));
                    }
                }
            }

            summary.Elapsed = watch.Elapsed;
            summary.Errors = errors.ToList();
            report(plans.Count, Loc.F("完成：{0} 个区块，{1:F1} MB，用时 {2:F0} 秒{3}",
                plans.Count, summary.Bytes / 1048576.0, summary.Elapsed.TotalSeconds,
                failed > 0 ? Loc.F("，{0} 个失败", failed) : ""),
                "完成", "", true);
            return summary;
        }

        private static List<TilePlan> Plan(SceneryJob job, string fs4Images, string fsgImages)
        {
            var plans = new List<TilePlan>();
            int maxLevel = job.Target.MaxAfLevel();
            var levels = job.Levels.Distinct().Where(l => l >= 8 && l <= maxLevel).OrderBy(l => l).ToList();

            List<Afs2Tile> selected = job.SelectedCells != null && job.SelectedCells.Count > 0
                ? TileSelection.Expand(job.SelectedCells, levels)
                : null;

            foreach (int level in levels)
            {
                int zoom = job.ResolveZoom(level);
                IEnumerable<Afs2Tile> levelTiles = selected != null
                    ? selected.Where(t => t.Level == level)
                    : Afs2Grid.TilesForBounds(job.North, job.South, job.East, job.West, level);

                foreach (Afs2Tile tile in levelTiles)
                {
                    double centerLat = (tile.North + tile.South) * 0.5;
                    double centerLon = (tile.East + tile.West) * 0.5;
                    int gx9 = Afs2Grid.TileX(centerLon, 9);
                    int gy9 = Afs2Grid.TileY(centerLat, 9);
                    string square = Afs2Grid.TileName(9, gx9, gy9);
                    string file = tile.FileName;

                    plans.Add(new TilePlan
                    {
                        Tile = tile,
                        Zoom = zoom,
                        Fs4Path = fs4Images == null ? null : Path.Combine(fs4Images, square, file),
                        FsgPath = fsgImages == null ? null : Path.Combine(fsgImages, square, file)
                    });
                }
            }

            return plans;
        }

        private static bool IsComplete(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length > 512;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static void Report(IProgress<SceneryProgress> progress, double fraction, int total,
            long bytes, int failed, string message, string stage, string currentTile,
            long downloadedBytes, int downloadedTiles, double elapsedSeconds, int workers)
        {
            if (progress == null) return;
            progress.Report(new SceneryProgress
            {
                Fraction = Math.Clamp(fraction, 0, 1),
                Done = (int)Math.Round(Math.Clamp(fraction, 0, 1) * total),
                Total = total,
                BytesDownloaded = bytes,
                Failed = failed,
                Message = message,
                Stage = stage,
                CurrentTile = currentTile,
                DownloadedBytes = downloadedBytes,
                DownloadedTiles = downloadedTiles,
                ElapsedSeconds = elapsedSeconds,
                Workers = workers
            });
        }

        private static void SaveOriginalPng(byte[] rgba, SceneryJob job, TilePlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string name = AppSettings.SanitiseName(job.SceneryName);
            string directory = Path.Combine(Path.GetFullPath(job.OriginalImagesDirectory), name,
                "L" + plan.Tile.Level);
            string path = Path.Combine(directory, Path.GetFileNameWithoutExtension(plan.Tile.FileName) + ".png");

            if (job.SkipExisting && IsComplete(path))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            using (Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgba,
                       MosaicComposer.TileSize, MosaicComposer.TileSize))
            {
                image.SaveAsPng(path);
            }
        }

        private static void CopyTree(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, rel));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void WriteManifest(string path, SceneryJob job, List<TilePlan> plans, string kind)
        {
            try
            {
                var manifest = new
                {
                    generator = "AeroScenery NG",
                    version = "2.0",
                    kind,
                    target = job.Target.ToString(),
                    scenery = job.SceneryName,
                    source = job.Source != null ? job.Source.Id : "",
                    bounds = new { north = job.North, south = job.South, east = job.East, west = job.West },
                    levels = plans.GroupBy(p => p.Tile.Level).Select(g => new
                    {
                        level = g.Key,
                        zoom = g.First().Zoom,
                        tiles = g.Count()
                    }).ToList(),
                    tiles = plans.Count,
                    generatedUtc = DateTime.UtcNow.ToString("o")
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private sealed class TilePlan
        {
            public Afs2Tile Tile;
            public int Zoom;
            public string Fs4Path;
            public string FsgPath;
        }
    }
}
