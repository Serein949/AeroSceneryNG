using System;
using System.IO;
using System.IO.Compression;
using AeroSceneryNG.Ttc.Lzham;

namespace AeroSceneryNG.Ttc
{
    /// <summary>
    /// Builds the <c>tmcompress</c> wrapper used by TTC payloads: a fixed 64 byte header followed by
    /// an official-compatible LZHAM stream.
    /// </summary>
    internal static class TtcCompress
    {
        public const int HeaderSize = 64;
        public const uint Magic = 0xA810BEF4;
        public const ulong ConstValue = 0x17F34DF32797945C;
        public const int LzhamDictLog2 = 21;
        public const int LzhamTableRate = 20;

        public static byte[] BuildHeader(long uncompressed, long compressed)
        {
            byte[] header = new byte[HeaderSize];
            TtcBinary.WriteU32(header, 0, HeaderSize);
            TtcBinary.WriteU32(header, 4, Magic);
            TtcBinary.WriteU64(header, 8, (ulong)uncompressed);
            TtcBinary.WriteU64(header, 16, (ulong)compressed);
            TtcBinary.WriteU64(header, 40, ConstValue);
            TtcBinary.WriteU32(header, 48, (uint)LzhamDictLog2);
            TtcBinary.WriteU32(header, 52, (uint)LzhamTableRate);
            return header;
        }

        /// <summary>Compresses a payload into a complete tmcompress blob (header + real LZHAM stream).</summary>
        public static byte[] Compress(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            byte[] stream = LzhamCodec.CompressRaw(payload, LzhamDictLog2, LzhamTableRate);
            byte[] header = BuildHeader(payload.Length, HeaderSize + stream.Length);
            byte[] blob = new byte[header.Length + stream.Length];
            Buffer.BlockCopy(header, 0, blob, 0, header.Length);
            Buffer.BlockCopy(stream, 0, blob, header.Length, stream.Length);
            return blob;
        }

        /// <summary>Inflates a tmcompress blob back to the raw mip chain (used by the verifier).</summary>
        public static byte[] Decompress(byte[] blob)
        {
            if (blob == null) throw new ArgumentNullException("blob");
            if (blob.Length < HeaderSize) throw new InvalidOperationException("tmcompress blob is too short.");
            uint magic = TtcBinary.ReadU32(blob, 4);
            if (magic != Magic) throw new InvalidOperationException("Bad tmcompress magic 0x" + magic.ToString("X8"));
            long expected = (long)TtcBinary.ReadU64(blob, 8);
            return LzhamCodec.Decompress(
                new ReadOnlySpan<byte>(blob, HeaderSize, blob.Length - HeaderSize),
                expected > 0 && expected < int.MaxValue ? (int)expected : -1);
        }

        /// <summary>True when the buffer starts with the 64 byte tmcompress header.</summary>
        public static bool IsTmBlob(byte[] blob)
        {
            return blob != null && blob.Length >= HeaderSize && TtcBinary.ReadU32(blob, 4) == Magic;
        }

        /// <summary>
        /// RFC 1950 zlib stream. This is what the reference converter writes at 0x100 and what
        /// both FS4 and FSG are known to load, so it is the default.
        /// </summary>
        public static byte[] CompressZlib(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            using (var output = new MemoryStream(payload.Length / 2 + 64))
            {
                using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    zlib.Write(payload, 0, payload.Length);
                }
                return output.ToArray();
            }
        }

        public static byte[] DecompressZlib(byte[] payload)
        {
            using (var input = new MemoryStream(payload, writable: false))
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(payload.Length * 3 + 1024))
            {
                zlib.CopyTo(output, 81920);
                return output.ToArray();
            }
        }
    }

    /// <summary>Little-endian helpers.</summary>
    internal static class TtcBinary
    {
        public static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static void WriteU64(byte[] buffer, int offset, ulong value)
        {
            WriteU32(buffer, offset, (uint)value);
            WriteU32(buffer, offset + 4, (uint)(value >> 32));
        }

        public static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }

        public static ulong ReadU64(byte[] buffer, int offset)
        {
            return ReadU32(buffer, offset) | ((ulong)ReadU32(buffer, offset + 4) << 32);
        }
    }
}
