using System;

namespace AeroSceneryNG.Core
{
    /// <summary>Simple geographic rectangle in degrees (North &gt;= South, East &gt;= West).</summary>
    public struct GeoBounds
    {
        public double North;
        public double South;
        public double East;
        public double West;

        public GeoBounds(double north, double south, double east, double west)
        {
            North = north;
            South = south;
            East = east;
            West = west;
        }

        public bool IsValid
        {
            get { return North > South && East > West && Math.Abs(North) <= 90 && Math.Abs(South) <= 90; }
        }

        public double CenterLat { get { return (North + South) * 0.5; } }
        public double CenterLon { get { return (East + West) * 0.5; } }

        public override string ToString()
        {
            return string.Format("{0:F4}°N  {1:F4}°S  {2:F4}°E  {3:F4}°W", North, South, East, West);
        }
    }
}
