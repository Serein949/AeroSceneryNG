using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AeroSceneryNG.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AeroSceneryNG.Tth
{
    /// <summary>
    /// Builds AFS4 elevation grids from the keyless Terrarium DEM tile set
    /// (s3.amazonaws.com/elevation-tiles-prod/terrarium).  Heights are RGB-encoded metres:
    /// height = R*256 + G + B/256 - 32768.
    /// </summary>
    public static class TerrariumDem
    {
        private const string UrlTemplate = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        private const int TileSize = 256;
        private const int MinElevation = -500;
        private const int MaxElevation = 9000;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AeroSceneryNG/2.0 (+elevation prototype)");
            return client;
        }

        public static async Task<ushort[]> BuildTileAsync(int level, int gridX, int gridY,
            string cacheRoot, CancellationToken ct = default)
        {
            if (level < 0 || level > 15) throw new ArgumentOutOfRangeException(nameof(level));

            int zoom = Math.Min(15, level + 2);
            double west = Afs2Grid.LonAtGridX(gridX, level);
            double east = Afs2Grid.LonAtGridX(gridX + 1, level);
            double north = Afs2Grid.LatAtGridY(gridY + 1, level);
            double south = Afs2Grid.LatAtGridY(gridY, level);

            double px0 = LonToPixelX(west, zoom);
            double px1 = LonToPixelX(east, zoom);
            double py0 = LatToPixelY(north, zoom);
            double py1 = LatToPixelY(south, zoom);

            int tx0 = Math.Max(0, (int)Math.Floor((Math.Min(px0, px1) - 2) / TileSize));
            int tx1 = Math.Min((1 << zoom) - 1, (int)Math.Floor((Math.Max(px0, px1) + 2) / TileSize));
            int ty0 = Math.Max(0, (int)Math.Floor((Math.Min(py0, py1) - 2) / TileSize));
            int ty1 = Math.Min((1 << zoom) - 1, (int)Math.Floor((Math.Max(py0, py1) + 2) / TileSize));

            var tiles = new Dictionary<long, Rgba32[]>();
            var keys = new List<(int X, int Y)>();
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                    keys.Add((tx, ty));

            await Parallel.ForEachAsync(keys, new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = ct
            }, async (key, token) =>
            {
                Rgba32[] pixels = await LoadTileAsync(zoom, key.X, key.Y, cacheRoot, token).ConfigureAwait(false);
                lock (tiles) tiles[Key(zoom, key.X, key.Y)] = pixels;
            }).ConfigureAwait(false);

            ushort[] samples = new ushort[TthWriter.GridSize * TthWriter.GridSize];
            const int interior = TthWriter.InteriorSize;
            const int skirt = TthWriter.Skirt;

            for (int y = 0; y < TthWriter.GridSize; y++)
            {
                // AFS elevation tiles store row 0 at the southern edge (verified by comparing
                // a generated Beijing tile with the shipped one: north-up needed a vertical flip).
                double gy = gridY + (y - skirt) / (double)interior;
                double lat = Afs2Grid.LatAtGridY(gy, level);
                double py = LatToPixelY(lat, zoom);
                for (int x = 0; x < TthWriter.GridSize; x++)
                {
                    double gx = gridX + (x - skirt) / (double)interior;
                    double lon = Afs2Grid.LonAtGridX(gx, level);
                    double px = LonToPixelX(lon, zoom);
                    float h = SampleBilinear(tiles, zoom, px, py);
                    float normalised = (h - MinElevation) / (MaxElevation - MinElevation) * 65535f;
                    int value = (int)Math.Round(normalised);
                    if (value < 0) value = 0;
                    if (value > 65535) value = 65535;
                    samples[y * TthWriter.GridSize + x] = (ushort)value;
                }
            }

            return samples;
        }

        private static async Task<Rgba32[]> LoadTileAsync(int zoom, int x, int y, string cacheRoot, CancellationToken ct)
        {
            string dir = Path.Combine(cacheRoot, "terrarium", zoom.ToString(), x.ToString());
            string file = Path.Combine(dir, y + ".png");
            byte[] bytes;
            if (File.Exists(file) && new FileInfo(file).Length > 256)
            {
                bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            }
            else
            {
                string url = string.Format(UrlTemplate, zoom, x, y);
                using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Directory.CreateDirectory(dir);
                string tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, file, true);
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
            if (image.Width != TileSize || image.Height != TileSize)
                throw new InvalidDataException("Unexpected Terrarium tile size " + image.Width + "x" + image.Height + ".");

            var pixels = new Rgba32[TileSize * TileSize];
            image.CopyPixelDataTo(pixels);
            return pixels;
        }

        private static float SampleBilinear(Dictionary<long, Rgba32[]> tiles, int zoom, double px, double py)
        {
            double fx = px - 0.5;
            double fy = py - 0.5;
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            double ax = fx - x0;
            double ay = fy - y0;

            float h00 = HeightAt(tiles, zoom, x0, y0);
            float h10 = HeightAt(tiles, zoom, x0 + 1, y0);
            float h01 = HeightAt(tiles, zoom, x0, y0 + 1);
            float h11 = HeightAt(tiles, zoom, x0 + 1, y0 + 1);

            float top = h00 + (h10 - h00) * (float)ax;
            float bottom = h01 + (h11 - h01) * (float)ax;
            return top + (bottom - top) * (float)ay;
        }

        private static float HeightAt(Dictionary<long, Rgba32[]> tiles, int zoom, int x, int y)
        {
            int max = (1 << zoom) - 1;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > max * TileSize + TileSize - 1) x = max * TileSize + TileSize - 1;
            if (y > max * TileSize + TileSize - 1) y = max * TileSize + TileSize - 1;

            int tx = x / TileSize;
            int ty = y / TileSize;
            if (!tiles.TryGetValue(Key(zoom, tx, ty), out Rgba32[] pixels))
                return 0f;

            Rgba32 p = pixels[(y % TileSize) * TileSize + (x % TileSize)];
            return p.R * 256f + p.G + p.B / 256f - 32768f;
        }

        private static long Key(int zoom, int x, int y) => ((long)zoom << 42) ^ ((long)x << 21) ^ (uint)y;

        private static double LonToPixelX(double lon, int zoom) => (lon + 180.0) / 360.0 * (1 << zoom) * TileSize;

        private static double LatToPixelY(double lat, int zoom)
        {
            if (lat > 85.05112878) lat = 85.05112878;
            if (lat < -85.05112878) lat = -85.05112878;
            double rad = lat * Math.PI / 180.0;
            double merc = Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad));
            return (1.0 - merc / Math.PI) / 2.0 * (1 << zoom) * TileSize;
        }
    }
}
