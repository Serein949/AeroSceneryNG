using System;
using System.Collections.Generic;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// One cell of the interactive selection grid. The cell is always stored at the grid level the
    /// user is editing; output levels are expanded from it when the job starts.
    /// </summary>
    public readonly struct SelectionCell : IEquatable<SelectionCell>
    {
        public SelectionCell(int level, int gridX, int gridY)
        {
            Level = level;
            GridX = gridX;
            GridY = gridY;
        }

        public int Level { get; }
        public int GridX { get; }
        public int GridY { get; }

        public double West => Afs2Grid.LonAtGridX(GridX, Level);
        public double East => Afs2Grid.LonAtGridX(GridX + 1, Level);
        public double South => Afs2Grid.LatAtGridY(GridY, Level);
        public double North => Afs2Grid.LatAtGridY(GridY + 1, Level);

        public Afs2Tile ToTile()
        {
            return new Afs2Tile
            {
                Level = Level,
                GridX = GridX,
                GridY = GridY,
                West = West,
                East = East,
                South = South,
                North = North
            };
        }

        public bool Equals(SelectionCell other)
        {
            return Level == other.Level && GridX == other.GridX && GridY == other.GridY;
        }

        public override bool Equals(object obj)
        {
            return obj is SelectionCell other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Level * 73856093;
                hash ^= GridX * 19349663;
                hash ^= GridY * 83492791;
                return hash;
            }
        }

        public override string ToString()
        {
            return string.Format("L{0}/{1}/{2}", Level, GridX, GridY);
        }
    }

    /// <summary>
    /// Converts between the interactive grid and the actual AFS2 output tiles.
    /// </summary>
    public static class TileSelection
    {
        public static SelectionCell CellAt(double latitude, double longitude, int level)
        {
            int maxIndex = (1 << level) - 1;
            int gx = Math.Max(0, Math.Min(maxIndex, Afs2Grid.TileX(longitude, level)));
            int gy = Math.Max(0, Math.Min(maxIndex, Afs2Grid.TileY(latitude, level)));
            return new SelectionCell(level, gx, gy);
        }

        public static List<SelectionCell> CellsForBounds(double north, double south, double east, double west, int level)
        {
            var cells = new List<SelectionCell>();
            foreach (Afs2Tile tile in Afs2Grid.TilesForBounds(north, south, east, west, level))
            {
                cells.Add(new SelectionCell(level, tile.GridX, tile.GridY));
            }
            return cells;
        }

        /// <summary>
        /// Expands every grid cell to the requested output levels. Higher levels become a block of
        /// descendants; lower levels become the single ancestor that contains the cell.
        /// </summary>
        public static List<Afs2Tile> Expand(IEnumerable<SelectionCell> cells, IEnumerable<int> levels)
        {
            var seen = new HashSet<long>();
            var tiles = new List<Afs2Tile>();

            foreach (int level in levels)
            {
                if (level < 0 || level > Afs2Grid.MaxLevel)
                {
                    continue;
                }

                int maxIndex = (1 << level) - 1;

                foreach (SelectionCell cell in cells)
                {
                    if (cell.Level < 0 || cell.Level > Afs2Grid.MaxLevel)
                    {
                        continue;
                    }

                    if (level >= cell.Level)
                    {
                        int factor = 1 << (level - cell.Level);
                        int x0 = cell.GridX * factor;
                        int y0 = cell.GridY * factor;

                        for (int dy = 0; dy < factor; dy++)
                        {
                            for (int dx = 0; dx < factor; dx++)
                            {
                                int gx = x0 + dx;
                                int gy = y0 + dy;
                                if (gx < 0 || gy < 0 || gx > maxIndex || gy > maxIndex)
                                {
                                    continue;
                                }

                                AddTile(tiles, seen, level, gx, gy);
                            }
                        }
                    }
                    else
                    {
                        int shift = cell.Level - level;
                        int gx = cell.GridX >> shift;
                        int gy = cell.GridY >> shift;
                        if (gx < 0 || gy < 0 || gx > maxIndex || gy > maxIndex)
                        {
                            continue;
                        }

                        AddTile(tiles, seen, level, gx, gy);
                    }
                }
            }

            tiles.Sort((a, b) =>
            {
                int byLevel = a.Level.CompareTo(b.Level);
                if (byLevel != 0) return byLevel;
                int byY = a.GridY.CompareTo(b.GridY);
                return byY != 0 ? byY : a.GridX.CompareTo(b.GridX);
            });

            return tiles;
        }

        public static int Count(IEnumerable<SelectionCell> cells, IEnumerable<int> levels)
        {
            return Expand(cells, levels).Count;
        }

        public static bool TryParseTtcName(string path, out int level, out int gridX, out int gridY)
        {
            level = 0;
            gridX = 0;
            gridY = 0;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name) || !name.StartsWith("map_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] parts = name.Split('_');
            if (parts.Length != 4 ||
                !int.TryParse(parts[1], out level) ||
                !int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out int xValue) ||
                !int.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out int yValue))
            {
                return false;
            }

            if (level < 0 || level > Afs2Grid.MaxLevel)
            {
                return false;
            }

            int scale = 65536 >> level;
            gridX = (int)Math.Round((double)xValue / scale);
            gridY = (int)Math.Round((double)yValue / scale);

            int maxIndex = (1 << level) - 1;
            gridX = Math.Max(0, Math.Min(maxIndex, gridX));
            gridY = Math.Max(0, Math.Min(maxIndex, gridY));
            return true;
        }

        private static void AddTile(List<Afs2Tile> tiles, HashSet<long> seen, int level, int gx, int gy)
        {
            long key = ((long)level << 40) ^ ((long)gx << 20) ^ (uint)gy;
            if (!seen.Add(key))
            {
                return;
            }

            tiles.Add(new Afs2Tile
            {
                Level = level,
                GridX = gx,
                GridY = gy,
                West = Afs2Grid.LonAtGridX(gx, level),
                East = Afs2Grid.LonAtGridX(gx + 1, level),
                South = Afs2Grid.LatAtGridY(gy, level),
                North = Afs2Grid.LatAtGridY(gy + 1, level)
            });
        }
    }
}
