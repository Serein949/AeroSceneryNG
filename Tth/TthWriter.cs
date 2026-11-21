using System;
using System.Buffers.Binary;
using System.IO;
using AeroSceneryNG.Ttc.Lzham;

namespace AeroSceneryNG.Tth
{
    /// <summary>
    /// Writes AeroFly FS4 elevation tiles (maph_LL_xxxx_yyyy.tth).
    /// Payload is a 1028x1028 grid of little-endian int16 metres: a 1024x1024 interior plus a
    /// two-pixel skirt on every side.  The 60-byte chunk header is the same tmcompress wrapper
    /// used by TTC files; the compressed stream itself is raw LZHAM.
    /// </summary>
    public static class TthWriter
    {
        public const uint Magic = 0x0009FBF1;
        public const uint Version = 0x100;
        public const int HeaderSize = 256;
        public const int ChunkPrefixSize = 4;
        public const int ChunkHeaderSize = 60;
        public const int DataOffset = HeaderSize + ChunkPrefixSize + ChunkHeaderSize;
        public const int GridSize = 1028;
        public const int InteriorSize = 1024;
        public const int Skirt = 2;

        public static void Write(string path, int level, ushort[] samples, int width, int height,
            short minElevation = -500, short maxElevation = 9000)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (width <= 0 || height <= 0 || samples.Length != width * height)
                throw new ArgumentException("Height grid size does not match the sample array.", nameof(samples));

            byte[] raw = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2, 2), samples[i]);

            byte[] payload = LzhamCodec.CompressRaw(raw, 21, 20);
            int chunkLength = ChunkPrefixSize + ChunkHeaderSize + payload.Length;

            byte[] header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)level);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)chunkLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)raw.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), minElevation);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), maxElevation);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 2);

            byte[] chunk = new byte[ChunkPrefixSize + ChunkHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0), (uint)(ChunkPrefixSize + ChunkHeaderSize));
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), 0xA810BEF4);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8), (uint)raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(16), (uint)chunkLength);
            BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(40), 0x17F34DF32797945CUL);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(48), 21);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(52), 20);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(chunk, 0, chunk.Length);
                fs.Write(payload, 0, payload.Length);
            }
        }
    }
}
