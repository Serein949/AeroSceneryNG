using System;
using System.IO;

namespace AeroSceneryNG.Ttc
{
    /// <summary>Payload codec used for a TTC mip chain.</summary>
    public enum TtcCodec
    {
        /// <summary>RFC 1950 zlib at 0x100. Written by the reference converter; loads in FS4 and FSG.</summary>
        Zlib = 0,
        /// <summary>64 byte tmcompress header + LZHAM. Smaller on some imagery, slower to encode.</summary>
        Lzham = 1,
        /// <summary>Raw mip chain, both header sizes equal. Always loads, never shrinks.</summary>
        Stored = 2
    }

    /// <summary>
    /// Writer for Aerofly FS TTC texture containers.
    ///
    /// Layout (little endian):
    ///   [0..256)  header
    ///     u32 [0]  magic 0x303A
    ///     u32 [4]  version 0x100
    ///     u32 [8]  extra (AFS2 zoom level)
    ///     u32 [12] payload byte length
    ///     u32 [16] uncompressed pixel/mipmap byte length
    ///     u32 [20] width
    ///     u32 [24] height
    ///     u32 [28] mipmap count
    ///     u32 [32] format id (10 = DXT1, 12 = DXT5, 21 = ETC2 RGB)
    ///     u32 [36], [40] zero (preview offset/length)
    ///     remainder zero filled
    ///   [256..)   payload: zlib stream, tmcompress+LZHAM blob, or the raw mip chain
    ///
    /// FS4 (desktop) reads DXT1/DXT5; FSG (mobile) reads ETC2. Writing DXT1 for mobile is the
    /// reason mobile scenery used to disappear above the lowest levels.
    /// </summary>
    public static class TtcWriter
    {
        /// <summary>Size of the TTC container header in bytes.</summary>
        public const int HeaderSize = 256;

        /// <summary>Container magic ("0x303A") as a little endian u32.</summary>
        public const uint Magic = 0x303A;

        /// <summary>Container version word written at offset 4.</summary>
        public const uint Version = 0x100;

        public const int FormatDxt1 = 10;
        public const int FormatDxt5 = 12;
        public const int FormatEtc2 = 21;

        /// <summary>Writes a DXT1 (BC1) TTC with a full mipmap chain (Aerofly FS 4).</summary>
        public static void WriteDxt1(string path, byte[] rgba, int width, int height, int level,
            bool flip = true, TtcCodec codec = TtcCodec.Zlib)
        {
            Write(path, rgba, width, height, level, "dxt1", true, flip, codec);
        }

        /// <summary>Writes an ETC2 RGB TTC with a full mipmap chain (Aerofly FS Global).</summary>
        public static void WriteEtc2(string path, byte[] rgba, int width, int height, int level,
            bool flip = true, TtcCodec codec = TtcCodec.Zlib)
        {
            Write(path, rgba, width, height, level, "etc2", true, flip, codec);
        }

        /// <summary>
        /// Writes a TTC file choosing the block format and whether a mipmap chain is generated.
        /// </summary>
        /// <param name="format">"dxt1"/"bc1", "dxt5"/"bc3" or "etc2" (case insensitive).</param>
        public static void Write(string path, byte[] rgba, int width, int height, int level,
            string format, bool mipmaps, bool flip = true, TtcCodec codec = TtcCodec.Zlib)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (rgba == null) throw new ArgumentNullException("rgba");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width", "Width and height must be positive.");
            if (rgba.Length < width * height * 4) throw new ArgumentException("RGBA buffer is smaller than width * height * 4.", "rgba");

            int formatId = ParseFormat(format);
            int blockBytes = BlockBytes(formatId);

            byte[] pixels = flip ? TtcMipChain.FlipVertical(rgba, width, height) : rgba;

            int mipCount = mipmaps ? TtcMipChain.Count(width, height) : 1;
            long expected = GpuMipBytes(formatId, width, height, mipCount);
            if (expected > int.MaxValue) throw new NotSupportedException("Image is too large for a single TTC file.");

            byte[] encoded = new byte[(int)expected];
            int offset = 0;

            int levelWidth = width;
            int levelHeight = height;
            byte[] current = pixels;

            for (int mip = 0; mip < mipCount; mip++)
            {
                int encWidth = levelWidth;
                int encHeight = levelHeight;
                byte[] levelPadded = current;
                if ((levelWidth & 3) != 0 || (levelHeight & 3) != 0)
                {
                    levelPadded = TtcMipChain.PadToMultiple(current, levelWidth, levelHeight, 4, out encWidth, out encHeight);
                }

                int levelBytes = ((encWidth + 3) / 4) * ((encHeight + 3) / 4) * blockBytes;

                switch (formatId)
                {
                    case FormatDxt1:
                        TtcBcEncoder.EncodeBc1(levelPadded, encWidth, encHeight, encoded, offset);
                        break;
                    case FormatDxt5:
                        TtcBcEncoder.EncodeBc3(levelPadded, encWidth, encHeight, encoded, offset);
                        break;
                    default:
                        EtcCodec.EncodeEtc2Rgb(levelPadded, encWidth, encHeight,
                            new Span<byte>(encoded, offset, levelBytes));
                        break;
                }
                offset += levelBytes;

                if (mip + 1 < mipCount)
                {
                    int nextWidth, nextHeight;
                    byte[] next = TtcMipChain.Halve(current, levelWidth, levelHeight, out nextWidth, out nextHeight);
                    current = next;
                    levelWidth = nextWidth;
                    levelHeight = nextHeight;
                }
            }

            if (offset != encoded.Length)
            {
                throw new InvalidOperationException("Mipmap byte count mismatch: wrote " + offset + " but expected " + encoded.Length + ".");
            }

            byte[] payload;
            switch (codec)
            {
                case TtcCodec.Lzham:
                    payload = TtcCompress.Compress(encoded);
                    break;
                case TtcCodec.Stored:
                    payload = encoded;
                    break;
                default:
                    payload = TtcCompress.CompressZlib(encoded);
                    break;
            }

            byte[] header = new byte[HeaderSize];
            TtcBinary.WriteU32(header, 0, Magic);
            TtcBinary.WriteU32(header, 4, Version);
            TtcBinary.WriteU32(header, 8, (uint)level);
            TtcBinary.WriteU32(header, 12, (uint)payload.Length);
            TtcBinary.WriteU32(header, 16, (uint)encoded.Length);
            TtcBinary.WriteU32(header, 20, (uint)width);
            TtcBinary.WriteU32(header, 24, (uint)height);
            TtcBinary.WriteU32(header, 28, (uint)mipCount);
            TtcBinary.WriteU32(header, 32, (uint)formatId);
            TtcBinary.WriteU32(header, 36, 0);
            TtcBinary.WriteU32(header, 40, 0);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
            }
        }

        /// <summary>Total size in bytes of the encoded mipmap chain, matching header word [16].</summary>
        public static long GpuMipBytes(string format, int width, int height, int mipmaps)
        {
            return GpuMipBytes(ParseFormat(format), width, height, mipmaps);
        }

        public static long GpuMipBytes(int formatId, int width, int height, int mipmaps)
        {
            int blockBytes = BlockBytes(formatId);
            long total = 0;
            int w = width;
            int h = height;
            int levels = mipmaps < 1 ? 1 : mipmaps;

            for (int i = 0; i < levels; i++)
            {
                total += (long)((w + 3) / 4) * ((h + 3) / 4) * blockBytes;
                if (w > 1) w = Math.Max(1, w / 2);
                if (h > 1) h = Math.Max(1, h / 2);
            }

            return total;
        }

        public static int BlockBytes(int formatId)
        {
            switch (formatId)
            {
                case FormatDxt1:
                case FormatEtc2:
                    return 8;
                case FormatDxt5:
                    return 16;
                default:
                    throw new ArgumentOutOfRangeException("formatId", "Unknown TTC format id " + formatId + ".");
            }
        }

        /// <summary>Maps a format name to the TTC format id (10 = DXT1, 12 = DXT5, 21 = ETC2).</summary>
        public static int ParseFormat(string format)
        {
            if (format == null) throw new ArgumentNullException("format");

            string f = format.Trim().ToLowerInvariant();
            if (f == "dxt1" || f == "bc1" || f == "dxt1a") return FormatDxt1;
            if (f == "dxt5" || f == "bc3") return FormatDxt5;
            if (f == "etc2" || f == "etc2rgb" || f == "etc2_rgb") return FormatEtc2;

            throw new ArgumentException("Unsupported TTC format '" + format + "'. Use dxt1, dxt5 or etc2.", "format");
        }

        public static string FormatName(int formatId)
        {
            switch (formatId)
            {
                case FormatDxt1: return "DXT1";
                case FormatDxt5: return "DXT5";
                case FormatEtc2: return "ETC2";
                default: return "0x" + formatId.ToString("X");
            }
        }
    }
}
