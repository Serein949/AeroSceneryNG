using System;
using System.Collections.Generic;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// One AFS2 texture tile: a rectangular piece of the world at a given AFS2 grid level.
    /// Bounds are geographic (degrees). The tile always has <see cref="Afs2Tile.PixelSize"/> pixels per side
    /// and is written to disk as map_LL_xxxx_yyyy.ttc.
    /// </summary>
    public sealed class Afs2Tile
    {
        public const int PixelSize = 2048;

        public int Level { get; set; }
        public int GridX { get; set; }
        public int GridY { get; set; }
        public double North { get; set; }
        public double South { get; set; }
        public double West { get; set; }
        public double East { get; set; }

        public string FileName
        {
            get { return Afs2Grid.TileName(Level, GridX, GridY) + ".ttc"; }
        }

        public override string ToString()
        {
            return FileName;
        }
    }

    /// <summary>
    /// AFS2 world grid maths, reverse engineered from the official geoconvert tool.
    ///
    /// Projection (per axis, degrees):
    ///     nx = 2^L * (0.5 + lon / 360)
    ///     ny = 2^L * (0.5 + 0.5 * tan(C * latRad) / C),  C = 2.3311223704144
    /// Tile (gx, gy) covers nx in [gx, gx+1] and ny in [gy, gy+1], i.e. a fixed 360/2^L degrees of
    /// longitude and a latitude band that stretches towards the poles.
    ///
    /// File names use the south-west (lower-left) corner scaled by 65536 / 2^L:
    ///     map_{level:d2}_{(k*gx):x4}_{(k*gy):x4},  k = 65536 / 2^L
    /// </summary>
    public static class Afs2Grid
    {
        public const double WorldGridConstant = 2.3311223704144;
        public const int MaxLevel = 15;

        public static double GridX(double lon, int level)
        {
            return Math.Pow(2, level) * (0.5 + lon / 360.0);
        }

        public static double GridY(double lat, int level)
        {
            // The official tool works in "degrees / 180" units, not radians.
            double latNormalised = lat / 180.0;
            double ny = 0.5 + 0.5 * Math.Tan(WorldGridConstant * latNormalised) / WorldGridConstant;
            return Math.Pow(2, level) * ny;
        }

        public static int TileX(double lon, int level)
        {
            return (int)Math.Floor(GridX(lon, level));
        }

        public static int TileY(double lat, int level)
        {
            return (int)Math.Floor(GridY(lat, level));
        }

        public static double LonAtGridX(double gx, int level)
        {
            return 360.0 * (gx / Math.Pow(2, level) - 0.5);
        }

        public static double LatAtGridY(double gy, int level)
        {
            double ny = gy / Math.Pow(2, level);
            double y1 = 2.0 * (ny - 0.5);
            // Inverse of GridY; result is in degrees (the projection uses degrees / 180 internally).
            return Math.Atan(WorldGridConstant * y1) / WorldGridConstant * 180.0;
        }

        /// <summary>Naming value used inside map_LL_xxxx_yyyy.ttc (south-west corner, scaled).</summary>
        public static int NameValue(int gridIndex, int level)
        {
            return (int)Math.Round((65536.0 / Math.Pow(2, level)) * gridIndex);
        }

        public static string TileName(int level, int gx, int gy)
        {
            return string.Format("map_{0}_{1}_{2}", level.ToString("d2"),
                NameValue(gx, level).ToString("x4"), NameValue(gy, level).ToString("x4"));
        }

        /// <summary>
        /// Enumerates every AFS2 tile at <paramref name="level"/> that intersects the given geographic box.
        /// </summary>
        public static List<Afs2Tile> TilesForBounds(double north, double south, double east, double west, int level)
        {
            if (level < 0 || level > MaxLevel) throw new ArgumentOutOfRangeException("level");
            if (north < south) { double t = north; north = south; south = t; }
            if (west > east) { double t = west; west = east; east = t; }

            int maxIndex = (int)Math.Pow(2, level) - 1;
            int gx0 = Math.Max(0, Math.Min(maxIndex, TileX(west, level)));
            int gx1 = Math.Max(0, Math.Min(maxIndex, TileX(east, level)));
            int gy0 = Math.Max(0, Math.Min(maxIndex, TileY(south, level)));
            int gy1 = Math.Max(0, Math.Min(maxIndex, TileY(north, level)));

            var tiles = new List<Afs2Tile>((gx1 - gx0 + 1) * (gy1 - gy0 + 1));
            for (int gy = gy0; gy <= gy1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    tiles.Add(new Afs2Tile
                    {
                        Level = level,
                        GridX = gx,
                        GridY = gy,
                        West = LonAtGridX(gx, level),
                        East = LonAtGridX(gx + 1, level),
                        South = LatAtGridY(gy, level),
                        North = LatAtGridY(gy + 1, level)
                    });
                }
            }
            return tiles;
        }

        /// <summary>Approximate ground resolution of a tile at the equator, in metres per pixel.</summary>
        public static double MetresPerPixel(int level, double latitude)
        {
            double tileDegrees = 360.0 / Math.Pow(2, level);
            double metresPerDegree = 111320.0 * Math.Cos(latitude * Math.PI / 180.0);
            return tileDegrees * metresPerDegree / Afs2Tile.PixelSize;
        }
    }
}
