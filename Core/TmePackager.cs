using System;
using System.IO;
using System.IO.Compression;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Packages the FSG scenery folder into the <c>.tme</c> file the mobile simulator imports.
    ///
    /// A scenery TME is a plain ZIP whose entries keep the <c>fsg_scenery_&lt;name&gt;_images/...</c>
    /// prefix. The TTC files inside are already LZHAM-compressed, so entries are stored without
    /// deflate: it is much faster and yields practically the same size.
    /// </summary>
    public static class TmePackager
    {
        /// <summary>
        /// Zips <paramref name="imagesDirectory"/> (the <c>fsg_scenery_x_images</c> folder) into
        /// <paramref name="tmePath"/>, preserving the folder name as the ZIP entry prefix.
        /// </summary>
        public static long PackImages(string imagesDirectory, string tmePath)
        {
            if (string.IsNullOrEmpty(imagesDirectory) || !Directory.Exists(imagesDirectory))
                throw new DirectoryNotFoundException("找不到 FSG 影像目录：" + imagesDirectory);

            string rootName = Path.GetFileName(imagesDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string parent = Path.GetDirectoryName(imagesDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(tmePath));
            if (File.Exists(tmePath)) File.Delete(tmePath);

            using (ZipArchive zip = ZipFile.Open(tmePath, ZipArchiveMode.Create))
            {
                foreach (string file in Directory.EnumerateFiles(imagesDirectory, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    ZipArchiveEntry entry = zip.CreateEntry(relative, CompressionLevel.NoCompression);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using (Stream target = entry.Open())
                    using (FileStream source = File.OpenRead(file))
                    {
                        source.CopyTo(target, 81920);
                    }
                }
            }

            return new FileInfo(tmePath).Length;
        }
    }
}
