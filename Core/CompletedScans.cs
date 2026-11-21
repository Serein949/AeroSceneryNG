using System;
using System.Collections.Generic;
using System.IO;

namespace AeroSceneryNG.Core
{
    /// <summary>Finds already generated TTC tiles so the map can show them as a coverage overlay.</summary>
    public static class CompletedScans
    {
        public static HashSet<SelectionCell> Scan(string outputDirectory, SceneryTarget target)
        {
            var cells = new HashSet<SelectionCell>();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return cells;
            }

            string root;
            try
            {
                root = Path.GetFullPath(outputDirectory);
            }
            catch
            {
                return cells;
            }

            var directories = new List<string>();
            if (target.WantsFs4())
            {
                directories.Add(Path.Combine(root, "fs4", "scenery", "images"));
            }

            if (target.WantsFsg())
            {
                string fsgRoot = Path.Combine(root, "fsg");
                if (Directory.Exists(fsgRoot))
                {
                    try
                    {
                        foreach (string scenery in Directory.EnumerateDirectories(fsgRoot, "fsg_scenery_*"))
                        {
                            directories.Add(Path.Combine(scenery, "scenery", "images"));
                        }
                    }
                    catch (IOException) { }
                }
            }

            // Old/legacy flat output layout.
            directories.Add(Path.Combine(root, "scenery", "images"));

            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "map_*.ttc",
                                 SearchOption.AllDirectories))
                    {
                        if (TileSelection.TryParseTtcName(file, out int parsedLevel, out int gridX, out int gridY) &&
                            parsedLevel >= 0 && parsedLevel <= Afs2Grid.MaxLevel)
                        {
                            cells.Add(new SelectionCell(parsedLevel, gridX, gridY));
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return cells;
        }
    }
}
