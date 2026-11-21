using System;
using System.Text;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Web-Mercator (EPSG:3857) tile math for standard XYZ / "slippy map" tiles:
    /// 256 px tiles, z = 0 is a single tile covering the world, and the y axis grows
    /// towards the south (Google / OSM / Esri convention).
    /// </summary>
    /// <remarks>
    /// Latitudes are clamped to ±<see cref="MaxLatitude"/> because Web-Mercator cannot
    /// represent the poles; longitudes are clamped to ±180.
    /// </remarks>
    public static class TileMath
    {
        /// <summary>Highest latitude representable in Web-Mercator (top / bottom edge of the square map).</summary>
        public const double MaxLatitude = 85.05112878;

        /// <summary>Highest zoom level supported by the int-based tile indexing below.</summary>
        public const int MaxZoomLevel = 30;

        /// <summary>
        /// Longitude (in degrees, -180..180) of the <b>western edge</b> of tile <paramref name="x"/>
        /// at <paramref name="z"/>. This is the OSM "tile2lon" convention, so the tile's centre is
        /// half a tile further east. x may equal 2^z to obtain the eastern map edge (180°).
        /// </summary>
        public static double TileXToLon(int x, int z)
        {
            z = ClampZoom(z);
            double n = 1L << z;
            return x / n * 360.0 - 180.0;
        }

        /// <summary>
        /// Latitude (in degrees) of the <b>northern edge</b> of tile <paramref name="y"/>
        /// at <paramref name="z"/> (OSM "tile2lat" convention). y may equal 2^z to obtain the
        /// southern map edge.
        /// </summary>
        public static double TileYToLat(int y, int z)
        {
            z = ClampZoom(z);
            double n = 1L << z;
            double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n)));
            return latRad * 180.0 / Math.PI;
        }

        /// <summary>Column (x) of the tile containing <paramref name="lon"/> at zoom <paramref name="z"/>.</summary>
        public static int LonToTileX(double lon, int z)
        {
            z = ClampZoom(z);
            double n = 1L << z;
            double x = (ClampLongitude(lon) + 180.0) / 360.0 * n;
            return ClampTile((int)Math.Floor(x), n);
        }

        /// <summary>Row (y, growing southward) of the tile containing <paramref name="lat"/> at zoom <paramref name="z"/>.</summary>
        public static int LatToTileY(double lat, int z)
        {
            z = ClampZoom(z);
            double n = 1L << z;
            double latRad = ClampLatitude(lat) * Math.PI / 180.0;
            double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
            return ClampTile((int)Math.Floor(y), n);
        }

        /// <summary>
        /// Geographic bounds of one tile. North/west are the tile's top-left corner;
        /// south/east are the bottom-right corner.
        /// </summary>
        public static void TileBounds(int x, int y, int z, out double north, out double south, out double west, out double east)
        {
            z = ClampZoom(z);
            west = TileXToLon(x, z);
            east = TileXToLon(x + 1, z);
            north = TileYToLat(y, z);
            south = TileYToLat(y + 1, z);
        }

        /// <summary>
        /// Converts an XYZ tile address to the Bing Maps "quadkey" string (one base-4 digit per
        /// zoom level, most significant bit of x/y first). z = 0 yields an empty string.
        /// </summary>
        public static string ToQuadKey(int x, int y, int z)
        {
            z = ClampZoom(z);
            if (z == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(z);
            for (int i = z; i > 0; i--)
            {
                int digit = 0;
                int mask = 1 << (i - 1);
                if ((x & mask) != 0)
                {
                    digit |= 1;
                }

                if ((y & mask) != 0)
                {
                    digit |= 2;
                }

                sb.Append((char)('0' + digit));
            }

            return sb.ToString();
        }

        /// <summary>Clamps a latitude to the Web-Mercator range (±85.05112878) and rejects NaN.</summary>
        public static double ClampLatitude(double lat)
        {
            if (double.IsNaN(lat))
            {
                return 0.0;
            }

            if (lat > MaxLatitude)
            {
                return MaxLatitude;
            }

            if (lat < -MaxLatitude)
            {
                return -MaxLatitude;
            }

            return lat;
        }

        /// <summary>Wraps a longitude into -180..180 (NaN becomes 0).</summary>
        public static double ClampLongitude(double lon)
        {
            if (double.IsNaN(lon) || double.IsInfinity(lon))
            {
                return 0.0;
            }

            if (lon >= -180.0 && lon <= 180.0)
            {
                return lon;
            }

            // Normalise into [-180, 180).
            lon = (lon + 180.0) % 360.0;
            if (lon < 0)
            {
                lon += 360.0;
            }

            return lon - 180.0;
        }

        private static int ClampZoom(int z)
        {
            if (z < 0)
            {
                return 0;
            }

            return z > MaxZoomLevel ? MaxZoomLevel : z;
        }

        private static int ClampTile(int value, double n)
        {
            int max = (int)(n - 1);
            if (value < 0)
            {
                return 0;
            }

            return value > max ? max : value;
        }
    }
}
