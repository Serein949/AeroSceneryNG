using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroSceneryNG.Core;
using AeroSceneryNG.Ttc;
using SixLabors.ImageSharp;

namespace AeroSceneryNG.Cli
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var options = Parse(args);
            if (options.ContainsKey("help") || options.Count == 0)
            {
                Help();
                return options.Count == 0 ? 1 : 0;
            }

            if (options.ContainsKey("tth"))
            {
                return DumpTth(Get(options, "tth"), options.ContainsKey("tth-png"), options.ContainsKey("tth-raw"));
            }

            if (options.ContainsKey("tth-gen"))
            {
                return await GenerateTthAsync(options).ConfigureAwait(false);
            }

            if (options.ContainsKey("png"))
            {
                return DumpPng(Get(options, "png"));
            }

            if (options.ContainsKey("bench"))
            {
                return RunBenchmark(GetInt(options, "bench", 16));
            }

            if (options.ContainsKey("verify"))
            {
                return Verify(Get(options, "verify"));
            }

            string bbox = Get(options, "bbox");
            if (string.IsNullOrWhiteSpace(bbox))
            {
                return Fail("缺少 --bbox", 2);
            }

            string[] parts = bbox.Split(',');
            if (parts.Length != 4)
            {
                return Fail("--bbox 需要 north,south,east,west", 2);
            }

            double north = ParseDouble(parts[0]);
            double south = ParseDouble(parts[1]);
            double east = ParseDouble(parts[2]);
            double west = ParseDouble(parts[3]);

            List<int> levels = ParseLevels(Get(options, "levels", "12,13,14"));
            string sourceId = Get(options, "source", "bing");
            MapSources.TiandituKey = Get(options, "key", MapSources.TiandituKey);
            MapSources.MapboxKey = Get(options, "mapbox", MapSources.MapboxKey);
            MapSource source = MapSources.Get(sourceId);
            if (source == null)
            {
                return Fail("未知图源: " + sourceId + "（可用：" + string.Join(", ", MapSources.All.Select(s => s.Id)) + "）", 2);
            }

            string output = Get(options, "out");
            if (string.IsNullOrWhiteSpace(output))
            {
                return Fail("缺少 --out", 2);
            }

            SceneryTarget target = ParseTarget(Get(options, "target", "fs4"));
            int levelsMax = target.MaxAfLevel();
            levels = levels.Where(l => l >= 8 && l <= levelsMax).Distinct().OrderBy(l => l).ToList();
            if (levels.Count == 0)
            {
                return Fail("没有目标支持的层级（FSG 最高 " + levelsMax + "）。", 2);
            }

            int zoom = GetInt(options, "zoom", 0);
            var levelZooms = new Dictionary<int, int>();
            foreach (string pair in (Get(options, "level-zooms", "") ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split(':');
                int lv, z;
                if (kv.Length == 2 && int.TryParse(kv[0], out lv) && int.TryParse(kv[1], out z)) levelZooms[lv] = z;
            }

            int count = 0;
            foreach (int level in levels)
            {
                count += Afs2Grid.TilesForBounds(north, south, east, west, level).Count;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "图源 {0} · 目标 {1} · 区域 {2:F4},{3:F4} → {4:F4},{5:F4} · 级别 {6} · 共 {7} 个区块",
                source.Name, target.DisplayName(), north, west, south, east,
                string.Join("/", levels), count));

            if (options.ContainsKey("dry-run"))
            {
                foreach (int level in levels)
                {
                    int effectiveZoom = levelZooms.TryGetValue(level, out int lz) ? lz : zoom > 0 ? zoom : MosaicComposer.AutoZoom(level);
                    effectiveZoom = Math.Clamp(effectiveZoom, 0, target.MaxImageZoom());
                    Console.WriteLine("  级别 " + level + " · 影像 Z" + effectiveZoom);
                    foreach (Afs2Tile tile in Afs2Grid.TilesForBounds(north, south, east, west, level))
                    {
                        Console.WriteLine("    " + tile.FileName);
                    }
                }
                return 0;
            }

            string install = Get(options, "install", null);
            var job = new SceneryJob
            {
                Source = source,
                North = north,
                South = south,
                East = east,
                West = west,
                Levels = levels,
                LevelZooms = levelZooms,
                ImageZoom = zoom > 0 ? zoom : -1,
                OutputDirectory = output,
                Target = target,
                SceneryDirectory = install,
                InstallIntoScenery = !string.IsNullOrWhiteSpace(install),
                FsgDirectory = Get(options, "fsg-dir", null),
                PackageTme = !options.ContainsKey("no-tme"),
                GenerateElevation = options.ContainsKey("elevation"),
                SaveOriginalImages = options.ContainsKey("originals"),
                OriginalImagesDirectory = Get(options, "originals-dir",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AeroSceneryNG", "originals")),
                SceneryName = Get(options, "name", "MyScenery"),
                SkipExisting = !options.ContainsKey("force"),
                DownloadThreads = GetInt(options, "download-threads", 64),
                WorkerThreads = GetInt(options, "workers", 0),
                Codec = ParseCodec(Get(options, "ttc-codec", "lzham")),
                Tuning = new ImageTuningOptions
                {
                    Enabled = options.ContainsKey("tune") || options.ContainsKey("brightness") || options.ContainsKey("contrast")
                        || options.ContainsKey("saturation") || options.ContainsKey("sharpness") || options.ContainsKey("red")
                        || options.ContainsKey("green") || options.ContainsKey("blue"),
                    Brightness = GetInt(options, "brightness", 0),
                    Contrast = GetInt(options, "contrast", 0),
                    Saturation = GetInt(options, "saturation", 0),
                    Sharpness = GetInt(options, "sharpness", 0),
                    Red = GetInt(options, "red", 0),
                    Green = GetInt(options, "green", 0),
                    Blue = GetInt(options, "blue", 0),
                    WaterEnabled = options.ContainsKey("water"),
                    WaterColor = Get(options, "water-color", "#1B4F8A"),
                    WaterStrength = GetInt(options, "water-strength", 100)
                }
            };

            string cacheRoot = Get(options, "cache", ImageryCache.DefaultCacheDirectory);
            using (var cache = new ImageryCache(cacheRoot, job.DownloadThreads))
            {
                var builder = new SceneryBuilder(cache);
                using (var cancellation = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancellation.Cancel(); };
                    var progress = new Progress<SceneryProgress>(p => Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[{0,6:F1}%] {1}", p.Fraction * 100, p.Message)));
                    try
                    {
                        ScenerySummary summary = await builder.RunAsync(job, progress, cancellation.Token).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "完成：{0} 个计划，{1} 个失败，{2:F1} MB，用时 {3:F1}s，下载 {4:F1} MB",
                            summary.Tiles, summary.Failed, summary.Bytes / 1048576.0,
                            summary.Elapsed.TotalSeconds, cache.DownloadedBytes / 1048576.0));
                        if (summary.Fs4ImagesDirectory != null) Console.WriteLine("FS4 目录: " + summary.Fs4ImagesDirectory);
                        if (summary.FsgImagesDirectory != null) Console.WriteLine("FSG 目录: " + summary.FsgImagesDirectory);
                        if (summary.TmePath != null) Console.WriteLine("FSG 包: " + summary.TmePath);
                        foreach (string error in summary.Errors.Take(10)) Console.WriteLine("  错误: " + error);
                        return summary.Failed == 0 ? 0 : 5;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("\n已取消");
                        return 130;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("\n失败: " + ex);
                        return 4;
                    }
                }
            }
        }

        private static void Help()
        {
            Console.WriteLine("AeroScenery NG CLI — 在线影像 → Aerofly FS4 / FSG 地景");
            Console.WriteLine();
            Console.WriteLine("  --bbox n,s,e,w              区域（必填）");
            Console.WriteLine("  --levels 9,11,12,13,14      编译层级（默认 12,13,14；FSG 最高 14）");
            Console.WriteLine("  --source bing|google|esri|esri_clarity|tianditu|amap|tencent|sentinel2|mapbox_sat|yandex_sat");
            Console.WriteLine("  --zoom 11..19|auto          统一影像级别（默认自动 = 层级+3，最大 17/19）");
            Console.WriteLine("  --level-zooms 12:15,13:16   逐层覆盖影像级别");
            Console.WriteLine("  --target fs4|fsg|both       输出 DXT1 / ETC2 / 两者（默认 fs4）");
            Console.WriteLine("  --out <目录>                输出目录（必填）");
            Console.WriteLine("  --name <名称>               FSG 包名（默认 MyScenery）");
            Console.WriteLine("  --install <目录>            复制 FS4 结果到 Aerofly 场景目录");
            Console.WriteLine("  --fsg-dir <目录>            复制 fsg_scenery_* 到手机工作目录");
            Console.WriteLine("  --no-tme                    不生成 _images.tme");
            Console.WriteLine("  --elevation                 同时生成 Level 10 地形高程 .tth（Terrarium DEM）");
            Console.WriteLine("  --originals                 同时保存未处理 2048² 原图 PNG（默认关闭）");
            Console.WriteLine("  --originals-dir <目录>      原图输出目录（默认 文档\\AeroSceneryNG\\originals）");
            Console.WriteLine("  --workers N                 编码线程（默认 CPU/2，最多 6）");
            Console.WriteLine("  --download-threads N        下载并发（默认 64）");
            Console.WriteLine("  --force                     不跳过已存在的瓦片");
            Console.WriteLine("  --cache <目录>              瓦片缓存目录（默认 %LOCALAPPDATA%\\AeroSceneryNG\\tilecache）");
            Console.WriteLine("  --key <天地图密钥>  --mapbox <令牌>");
            Console.WriteLine("  --dry-run                   只列出区块");
            Console.WriteLine("  --bench [瓦片数]            合成影像编码基准");
            Console.WriteLine("  --verify <目录|文件>        解析并校验 .ttc");
            Console.WriteLine("  --ttc-codec zlib|lzham       TTC 压缩方式（默认 lzham；zlib 兼容性最好）");
            Console.WriteLine("  --png <.ttc>                把瓦片解码为同名 .png 预览");
            Console.WriteLine("  --tth <file>                查看官方 .tth 高程瓦片（可加 --tth-png / --tth-raw）");
            Console.WriteLine("  --tth-gen                   从 Terrarium DEM 生成高程瓦片");
            Console.WriteLine("  --level 10 --grid 291,631   配合 --tth-gen 指定层级与 AFS 网格索引");
            Console.WriteLine("  --out <file.tth>            配合 --tth-gen 指定输出（默认当前目录）");
            Console.WriteLine("  --tune                      启用影像处理（也可单独给下面任意一项）");
            Console.WriteLine("  --brightness N --contrast N --saturation N --sharpness N   色彩校正，-100..100");
            Console.WriteLine("  --red N --green N --blue N  通道增益，-100..100");
            Console.WriteLine("  --water                     统一偏蓝水体颜色；--water-color #RRGGBB --water-strength 0..100");
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--")) continue;
                string key = arg.Substring(2);
                string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
                options[key] = value;
            }
            return options;
        }

        private static TtcCodec ParseCodec(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "lzham": return TtcCodec.Lzham;
                case "stored":
                case "raw": return TtcCodec.Stored;
                default: return TtcCodec.Zlib;
            }
        }

        private static int DumpPng(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Fail("--png 需要一个存在的 .ttc 文件", 2);
            }

            try
            {
                string png = Path.ChangeExtension(path, ".png");
                TtcReader.Info info = TtcReader.Read(path);
                TtcImage.SavePng(path, png);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "已解码 {0}  L{1} {2}x{3} fmt={4} → {5}",
                    Path.GetFileName(path), info.Level, info.Width, info.Height,
                    TtcWriter.FormatName(info.FormatId), png));
                return 0;
            }
            catch (Exception ex)
            {
                return Fail("解码失败: " + ex.Message, 1);
            }
        }

        private static async Task<int> GenerateTthAsync(Dictionary<string, string> options)
        {
            int level = GetInt(options, "level", 10);
            string grid = Get(options, "grid", "");
            string[] parts = grid.Split(',');
            if (parts.Length != 2)
                return Fail("--grid 需要 gx,gy（AFS 网格索引），例如 --grid 291,631", 2);
            if (!int.TryParse(parts[0], out int gx) || !int.TryParse(parts[1], out int gy))
                return Fail("--grid 不是有效的整数对", 2);
            if (gx < 0 || gy < 0 || gx >= (1 << level) || gy >= (1 << level))
                return Fail("--grid 超出该层级的范围", 2);

            string cache = Get(options, "elev-cache");
            if (string.IsNullOrWhiteSpace(cache))
                cache = Path.Combine(Path.GetTempPath(), "AeroSceneryNG", "elevation");

            string output = Get(options, "out");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine(Directory.GetCurrentDirectory(), Afs2Grid.TileName(level, gx, gy).Replace("map_", "maph_") + ".tth");

            var watch = Stopwatch.StartNew();
            ushort[] samples = await Tth.TerrariumDem.BuildTileAsync(level, gx, gy, cache).ConfigureAwait(false);
            ushort min = ushort.MaxValue, max = ushort.MinValue;
            foreach (ushort h in samples) { if (h < min) min = h; if (h > max) max = h; }
            Tth.TthWriter.Write(output, level, samples, Tth.TthWriter.GridSize, Tth.TthWriter.GridSize);

            Tth.TthFile check = Tth.TthFile.Read(output);
            bool same = check.Samples.Length == samples.Length;
            if (same)
            {
                for (int i = 0; i < samples.Length; i++)
                    if (check.Samples[i] != samples[i]) { same = false; break; }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "高程 → {0}  L{1}  {2}x{3}  {4:0}..{5:0} m  {6:N0} B  {7:0.0} s  回读={8}",
                output, level, check.Width, check.Height,
                -500f + min / 65535f * 9500f, -500f + max / 65535f * 9500f,
                new FileInfo(output).Length, watch.Elapsed.TotalSeconds, same ? "一致" : "不一致"));
            return same ? 0 : 1;
        }

        private static int DumpTth(string path, bool writePng, bool writeRaw)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Fail("--tth 需要一个存在的 .tth 文件", 2);
            }

            try
            {
                Tth.TthFile tth = Tth.TthFile.Read(path);
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int y = 0; y < tth.Height; y++)
                {
                    for (int x = 0; x < tth.Width; x++)
                    {
                        float h = tth.MetresAt(x, y);
                        if (h < min) min = h;
                        if (h > max) max = h;
                    }
                }

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "TTH {0}  L{1}  {2}x{3}  {4} bytes/sample  header={5}..{6} m  actual={7:0}..{8:0} m  payload={9:N0} B",
                    Path.GetFileName(path), tth.Level, tth.Width, tth.Height, tth.BytesPerSample,
                    tth.MinElevation, tth.MaxElevation, min, max, tth.DeclaredUncompressed));

                if (writePng)
                {
                    string png = Path.Combine(Directory.GetCurrentDirectory(),
                        Path.GetFileNameWithoutExtension(path) + ".png");
                    SaveHeightPng(tth, min, max, png);
                    Console.WriteLine("高度图 → " + png);
                }
                if (writeRaw)
                {
                    string raw = Path.Combine(Directory.GetCurrentDirectory(),
                        Path.GetFileNameWithoutExtension(path) + ".raw");
                    using (var fs = File.Create(raw))
                    using (var bw = new BinaryWriter(fs))
                    {
                        foreach (short h in tth.Samples) bw.Write(h);
                    }
                    Console.WriteLine("原始高度 → " + raw);
                }
                return 0;
            }
            catch (Exception ex)
            {
                return Fail("解析 TTH 失败: " + ex.Message, 1);
            }
        }

        private static void SaveHeightPng(Tth.TthFile tth, float min, float max, string png)
        {
            float range = Math.Max(1f, max - min);
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(tth.Width, tth.Height))
            {
                for (int y = 0; y < tth.Height; y++)
                {
                    for (int x = 0; x < tth.Width; x++)
                    {
                        float h = tth.MetresAt(x, y);
                        byte value = (byte)Math.Clamp((int)Math.Round((h - min) * 255f / range), 0, 255);
                        image[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(value, value, value, 255);
                    }
                }
                image.SaveAsPng(png);
            }
        }

        private static int Verify(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Fail("--verify 需要一个文件或目录", 2);
            }

            var files = new List<string>();
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*.ttc", SearchOption.AllDirectories));
            if (files.Count == 0) return Fail("没有找到 .ttc 文件: " + path, 2);

            int bad = 0;
            foreach (string file in files)
            {
                try
                {
                    TtcReader.Info info = TtcReader.Read(file);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "OK  {0}  L{1} {2}x{3} mips={4} fmt={5} payload={6:N0}B raw={7:N0}B ratio={8:F2}x",
                        Path.GetFileName(file), info.Level, info.Width, info.Height, info.MipCount,
                        TtcWriter.FormatName(info.FormatId), info.PayloadBytes, info.UncompressedBytes,
                        info.UncompressedBytes / (double)Math.Max(1, info.PayloadBytes)));
                }
                catch (Exception ex)
                {
                    bad++;
                    Console.WriteLine("BAD " + file + "  " + ex.Message);
                }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} 个文件，{1} 个失败", files.Count, bad));
            return bad == 0 ? 0 : 6;
        }

        private static SceneryTarget ParseTarget(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "fsg":
                case "mobile":
                    return SceneryTarget.Fsg;
                case "both":
                case "all":
                    return SceneryTarget.Both;
                default:
                    return SceneryTarget.Fs4;
            }
        }

        private static List<int> ParseLevels(string text)
        {
            var levels = new List<int>();
            foreach (string piece in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int value;
                if (int.TryParse(piece.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) levels.Add(value);
            }
            return levels.Count > 0 ? levels : new List<int> { 12, 13, 14 };
        }

        private static string Get(Dictionary<string, string> options, string key, string fallback = null)
        {
            return options.TryGetValue(key, out string value) ? value : fallback;
        }

        private static int GetInt(Dictionary<string, string> options, string key, int fallback)
        {
            return int.TryParse(Get(options, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        private static double ParseDouble(string text)
        {
            return double.Parse(text.Trim(), CultureInfo.InvariantCulture);
        }

        private static int Fail(string message, int code)
        {
            Console.WriteLine(message);
            return code;
        }

        /// <summary>Encoding benchmark on synthetic imagery: RGBA → DXT1/ETC2 + mips + LZHAM + TTC.</summary>
        private static int RunBenchmark(int tiles)
        {
            const int size = Afs2Tile.PixelSize;
            byte[] rgba = SyntheticImage(size);
            tiles = Math.Max(1, tiles);

            string directory = Path.Combine(Path.GetTempPath(), "asng-bench");
            try { Directory.CreateDirectory(directory); }
            catch
            {
                directory = Path.Combine(Directory.GetCurrentDirectory(), "asng-bench");
                Directory.CreateDirectory(directory);
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "编码基准：{0} 张 {1}x{1} · DXT1 + ETC2 · 12 级 mip · LZHAM · CPU {2} 线程",
                tiles, size, Environment.ProcessorCount));
            Console.WriteLine();

            foreach (int workers in new[] { 1, Math.Max(2, Environment.ProcessorCount / 2), Environment.ProcessorCount })
            {
                Measure("DXT1", workers, tiles, directory, rgba, size, false);
                Measure("ETC2", workers, tiles, directory, rgba, size, true);
            }

            Console.WriteLine();
            Console.WriteLine("输出目录: " + directory);
            return 0;
        }

        private static void Measure(string label, int workers, int tiles, string directory, byte[] rgba, int size, bool etc)
        {
            var watch = Stopwatch.StartNew();
            Parallel.For(0, tiles, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                string path = Path.Combine(directory, string.Format("bench_{0}_{1}_{2}.ttc", label, workers, i));
                if (etc) TtcWriter.WriteEtc2(path, rgba, size, size, 14);
                else TtcWriter.WriteDxt1(path, rgba, size, size, 14);
            });
            watch.Stop();

            long bytes = 0;
            for (int i = 0; i < tiles; i++)
                bytes += Length(Path.Combine(directory, string.Format("bench_{0}_{1}_{2}.ttc", label, workers, i)));

            double perTile = watch.Elapsed.TotalMilliseconds / tiles;
            double inputMb = tiles * (size * (double)size * 4) / 1048576.0;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-4} {1,3} 线程  {2,7:F1} ms/瓦片  {3,7:F1} 瓦片/分钟  {4,7:F1} MB/s 输入  平均输出 {5,7:F1} KB",
                label, workers, perTile, 60000.0 / perTile, inputMb / watch.Elapsed.TotalSeconds, bytes / (double)tiles / 1024.0));
        }

        private static long Length(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private static byte[] SyntheticImage(int size)
        {
            var rgba = new byte[size * size * 4];
            var random = new Random(12345);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    int checker = ((x / 128) + (y / 128)) % 2 == 0 ? 40 : 0;
                    rgba[i] = (byte)Math.Min(255, x * 255 / size + checker + random.Next(0, 12));
                    rgba[i + 1] = (byte)Math.Min(255, y * 255 / size + checker + random.Next(0, 12));
                    rgba[i + 2] = (byte)Math.Min(255, 128 + checker);
                    rgba[i + 3] = 255;
                }
            }
            return rgba;
        }
    }
}
