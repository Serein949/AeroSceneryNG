using System;
using System.IO;
using System.IO.Compression;
using AeroSceneryNG.Ttc.Lzham;

namespace AeroSceneryNG.Tth
{
    /// <summary>
    /// Reader for Aerofly elevation tiles (<c>maph_10_xxxx_yyyy.tth</c>).
    /// Layout reverse-engineered from shipped FS 4 scenery:
    ///   256-byte header, then a 4-byte chunk-header length (64), a 64-byte
    ///   tmcompress-style header, then an LZHAM stream holding a 1028x1028
    ///   grid of little-endian uint16 samples, linearly mapped to
    ///   [MinElevation, MaxElevation] metres.
    /// </summary>
    public sealed class TthFile
    {
        public const int HeaderSize = 256;
        public const uint Magic = 0x0009FBF1;
        public const int ChunkPrefixSize = 4;
        public const int ChunkHeaderSize = 60;
        public const int DataOffset = HeaderSize + ChunkPrefixSize + ChunkHeaderSize;

        public int Level { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int BytesPerSample { get; private set; }
        public int MinElevation { get; private set; }
        public int MaxElevation { get; private set; }
        public int DeclaredUncompressed { get; private set; }
        public ushort[] Samples { get; private set; }

        public static TthFile Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        public static TthFile Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < DataOffset)
                throw new InvalidDataException("TTH file is too short.");

            uint magic = ReadU32(bytes, 0);
            if (magic != Magic)
                throw new InvalidDataException("Bad TTH magic 0x" + magic.ToString("X8"));

            uint version = ReadU32(bytes, 4);
            if (version != 0x100)
                throw new InvalidDataException("Unsupported TTH version 0x" + version.ToString("X8"));

            var file = new TthFile
            {
                Level = (int)ReadU32(bytes, 8),
                DeclaredUncompressed = (int)ReadU32(bytes, 16),
                MinElevation = (int)ReadU32(bytes, 20),
                MaxElevation = (int)ReadU32(bytes, 24),
                Width = (int)ReadU32(bytes, 28),
                BytesPerSample = (int)ReadU32(bytes, 32)
            };
            file.Height = file.Width;

            int chunkHeaderSize = (int)ReadU32(bytes, HeaderSize);
            if (chunkHeaderSize != ChunkPrefixSize + ChunkHeaderSize)
                throw new InvalidDataException("Unexpected TTH chunk header size " + chunkHeaderSize + ".");

            uint chunkMagic = ReadU32(bytes, HeaderSize + ChunkPrefixSize);
            if (chunkMagic != 0xA810BEF4)
                throw new InvalidDataException("Bad TTH chunk magic 0x" + chunkMagic.ToString("X8"));

            int compressedLength = bytes.Length - DataOffset;
            if (compressedLength <= 0)
                throw new InvalidDataException("TTH has no payload.");

            byte[] raw;
            try
            {
                raw = LzhamCodec.Decompress(
                    new ReadOnlySpan<byte>(bytes, DataOffset, compressedLength),
                    file.DeclaredUncompressed);
            }
            catch
            {
                // Fall back to a plain zlib payload for tools that write it that way.
                raw = DecompressZlib(new ReadOnlySpan<byte>(bytes, DataOffset, compressedLength).ToArray());
            }

            int expected = file.Width * file.Height * file.BytesPerSample;
            if (raw.Length < expected)
                throw new InvalidDataException("TTH payload is " + raw.Length + " bytes, expected " + expected + ".");

            file.Samples = new ushort[file.Width * file.Height];
            for (int i = 0; i < file.Samples.Length; i++)
            {
                file.Samples[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
            }
            return file;
        }

        /// <summary>Raw normalised sample (0..65535).</summary>
        public ushort SampleAt(int x, int y)
        {
            return Samples[y * Width + x];
        }

        /// <summary>Height in metres, mapped linearly between the header's min/max elevation.</summary>
        public float MetresAt(int x, int y)
        {
            float range = MaxElevation - MinElevation;
            return MinElevation + SampleAt(x, y) / 65535f * range;
        }

        private static byte[] DecompressZlib(byte[] payload)
        {
            using (var input = new MemoryStream(payload, writable: false))
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(payload.Length * 3 + 1024))
            {
                zlib.CopyTo(output, 81920);
                return output.ToArray();
            }
        }

        private static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }
    }
}
