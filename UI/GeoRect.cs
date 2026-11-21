using System;
using System.Globalization;

namespace AeroSceneryNG.UI
{
    /// <summary>
    /// A geographic rectangle expressed in WGS84 degrees.
    /// <para>
    /// <see cref="North"/> is the top edge, <see cref="South"/> the bottom edge,
    /// <see cref="West"/> the left edge and <see cref="East"/> the right edge.
    /// An <see cref="IsEmpty"/> rectangle has no usable area (either unset - all fields
    /// are <see cref="double.NaN"/> - or inverted / zero-sized).
    /// </para>
    /// </summary>
    public struct GeoRect
    {
        /// <summary>Northern (top) latitude in degrees.</summary>
        public double North;

        /// <summary>Southern (bottom) latitude in degrees.</summary>
        public double South;

        /// <summary>Western (left) longitude in degrees.</summary>
        public double West;

        /// <summary>Eastern (right) longitude in degrees.</summary>
        public double East;

        /// <summary>Creates a geographic rectangle from its four edges.</summary>
        public GeoRect(double north, double south, double west, double east)
        {
            North = north;
            South = south;
            West = west;
            East = east;
        }

        /// <summary>An empty (unset) rectangle.</summary>
        public static GeoRect Empty
        {
            get { return new GeoRect(double.NaN, double.NaN, double.NaN, double.NaN); }
        }

        /// <summary>True when the rectangle has no usable area.</summary>
        public bool IsEmpty
        {
            get
            {
                return double.IsNaN(North) || double.IsNaN(South) ||
                       double.IsNaN(West) || double.IsNaN(East) ||
                       North <= South || West >= East;
            }
        }

        /// <summary>Latitude of the centre of the rectangle, in degrees.</summary>
        public double CenterLat
        {
            get { return (North + South) * 0.5; }
        }

        /// <summary>Longitude of the centre of the rectangle, in degrees.</summary>
        public double CenterLon
        {
            get { return (West + East) * 0.5; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (IsEmpty)
            {
                return "GeoRect(empty)";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "GeoRect(N={0:0.######}, S={1:0.######}, W={2:0.######}, E={3:0.######})",
                North, South, West, East);
        }
    }
}
