using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Builds one 2048x2048 AFS texture from a grid of web-map tiles.
    ///
    /// Longitude is linear in both projections. Latitude is not: AFS uses its own grid constant,
    /// so each destination row is converted through the AFS inverse projection and then into
    /// Web-Mercator world pixels. Sampling is bilinear, which keeps the result smooth when the
    /// chosen image zoom differs from the natural one.
    /// </summary>
    public static class MosaicComposer
    {
        public const int TileSize = 2048;

        public static async Task<byte[]> ComposeAsync(ImageryCache cache, MapSource source,
            Afs2Tile tile, int imageZoom, CancellationToken ct)
        {
            if (cache == null) throw new ArgumentNullException("cache");
            if (source == null) throw new ArgumentNullException("source");
            if (tile == null) throw new ArgumentNullException("tile");

            int zoom = Math.Clamp(imageZoom, 0, 19);
            double worldSize = 256.0 * (1L << zoom);

            double west = Math.Max(-180.0, Math.Min(180.0, tile.West));
            double east = Math.Max(-180.0, Math.Min(180.0, tile.East));
            double north = Math.Clamp(tile.North, -85.05112878, 85.05112878);
            double south = Math.Clamp(tile.South, -85.05112878, 85.05112878);

            double x0 = (west + 180.0) / 360.0 * worldSize;
            double x1 = (east + 180.0) / 360.0 * worldSize;
            double y0 = MercatorY(north) * worldSize;
            double y1 = MercatorY(south) * worldSize;

            if (x1 <= x0 || y1 <= y0) throw new InvalidOperationException("选择范围太小，无法生成一个 AFS 区块。");

            int maxTile = (1 << zoom) - 1;
            int tx0 = Math.Clamp((int)Math.Floor(x0 / 256.0), 0, maxTile);
            int tx1 = Math.Clamp((int)Math.Floor((x1 - 1e-6) / 256.0), 0, maxTile);
            int ty0 = Math.Clamp((int)Math.Floor(y0 / 256.0), 0, maxTile);
            int ty1 = Math.Clamp((int)Math.Floor((y1 - 1e-6) / 256.0), 0, maxTile);

            int count = (tx1 - tx0 + 1) * (ty1 - ty0 + 1);
            if (count <= 0 || count > 16384)
                throw new InvalidOperationException("缩放级别 " + zoom + " 需要 " + count + " 张瓦片，超出合理范围。");

            var jobs = new List<Task<RgbaTile>>(count);
            var keys = new List<long>(count);
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int cx = tx, cy = ty;
                    keys.Add(Key(cx, cy));
                    jobs.Add(cache.GetTileAsync(source, zoom, cx, cy, ct));
                }
            }

            RgbaTile[] results = await Task.WhenAll(jobs).ConfigureAwait(false);

            var tiles = new Dictionary<long, RgbaTile>(count);
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null) tiles[keys[i]] = results[i];

            byte[] dest = new byte[TileSize * TileSize * 4];
            for (int i = 3; i < dest.Length; i += 4) dest[i] = 255;

            double spanX = x1 - x0;
            double lonStep = (east - west) / TileSize;

            Parallel.For(0, TileSize, new ParallelOptions { CancellationToken = ct }, dy =>
            {
                // Row 0 is the northern edge; AFS grid Y grows southwards.
                double gy = tile.GridY + 1.0 - (dy + 0.5) / TileSize;
                double lat = Afs2Grid.LatAtGridY(gy, tile.Level);
                lat = Math.Clamp(lat, -85.05112878, 85.05112878);
                double worldY = MercatorY(lat) * worldSize;

                int ty = Math.Clamp((int)Math.Floor(worldY / 256.0), 0, maxTile);
                int rowBase = dy * TileSize * 4;

                int lastTx = int.MinValue;
                RgbaTile lastTile = null;
                double lastV = 0;
                int lastTy = int.MinValue;

                for (int dx = 0; dx < TileSize; dx++)
                {
                    double lon = west + (dx + 0.5) * lonStep;
                    double worldX = (lon + 180.0) / 360.0 * worldSize;
                    int tx = Math.Clamp((int)Math.Floor(worldX / 256.0), 0, maxTile);

                    if (tx != lastTx || ty != lastTy)
                    {
                        RgbaTile found;
                        tiles.TryGetValue(Key(tx, ty), out found);
                        lastTile = found;
                        if (found != null)
                        {
                            double u = (worldX - tx * 256.0) / 256.0 * found.Width - 0.5;
                            double v = (worldY - ty * 256.0) / 256.0 * found.Height - 0.5;
                            lastV = v;
                            SampleBilinear(found, u, v, dest, rowBase + dx * 4);
                        }
                        lastTx = tx;
                        lastTy = ty;
                    }
                    else if (lastTile != null)
                    {
                        double u = (worldX - tx * 256.0) / 256.0 * lastTile.Width - 0.5;
                        SampleBilinear(lastTile, u, lastV, dest, rowBase + dx * 4);
                    }
                }
            });

            return dest;
        }

        private static long Key(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        private static void SampleBilinear(RgbaTile tile, double u, double v, byte[] dest, int offset)
        {
            int w = tile.Width;
            int h = tile.Height;
            byte[] src = tile.Pixels;

            if (u < 0) u = 0;
            if (v < 0) v = 0;
            if (u > w - 1) u = w - 1;
            if (v > h - 1) v = h - 1;

            int x = (int)u;
            int y = (int)v;
            double fx = u - x;
            double fy = v - y;
            int x1 = x + 1 < w ? x + 1 : x;
            int y1 = y + 1 < h ? y + 1 : y;

            int i00 = (y * w + x) * 4;
            int i10 = (y * w + x1) * 4;
            int i01 = (y1 * w + x) * 4;
            int i11 = (y1 * w + x1) * 4;

            double w00 = (1 - fx) * (1 - fy);
            double w10 = fx * (1 - fy);
            double w01 = (1 - fx) * fy;
            double w11 = fx * fy;

            for (int c = 0; c < 3; c++)
            {
                double value = src[i00 + c] * w00 + src[i10 + c] * w10 + src[i01 + c] * w01 + src[i11 + c] * w11;
                dest[offset + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
            dest[offset + 3] = 255;
        }

        private static double MercatorY(double latitude)
        {
            if (latitude >= 89.9) latitude = 89.9;
            if (latitude <= -89.9) latitude = -89.9;
            double lat = latitude * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0;
        }

        /// <summary>Web-Mercator zoom that makes one 2048 px AFS tile one pixel per source pixel.</summary>
        public static int AutoZoom(int afsLevel)
        {
            return Math.Clamp(afsLevel + 3, 0, 19);
        }
    }
}
