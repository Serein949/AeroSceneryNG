using System;
using System.IO;

namespace AeroSceneryNG.Ttc
{
    /// <summary>Reads a TTC back, validates its header, and inflates the LZHAM payload.</summary>
    public static class TtcReader
    {
        public sealed class Info
        {
            public int Level;
            public int Width;
            public int Height;
            public int MipCount;
            public int FormatId;
            public int PayloadBytes;
            public int UncompressedBytes;
            public byte[] Encoded;
        }

        public static Info Read(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            return Read(all);
        }

        public static Info Read(byte[] all)
        {
            if (all == null || all.Length < TtcWriter.HeaderSize + TtcCompress.HeaderSize)
                throw new InvalidDataException("TTC is too short.");

            uint magic = TtcBinary.ReadU32(all, 0);
            if (magic != TtcWriter.Magic)
                throw new InvalidDataException("Bad TTC magic 0x" + magic.ToString("X8") + ".");

            Info info = new Info
            {
                Level = (int)TtcBinary.ReadU32(all, 8),
                PayloadBytes = (int)TtcBinary.ReadU32(all, 12),
                UncompressedBytes = (int)TtcBinary.ReadU32(all, 16),
                Width = (int)TtcBinary.ReadU32(all, 20),
                Height = (int)TtcBinary.ReadU32(all, 24),
                MipCount = (int)TtcBinary.ReadU32(all, 28),
                FormatId = (int)TtcBinary.ReadU32(all, 32)
            };

            if (info.PayloadBytes <= 0 || TtcWriter.HeaderSize + info.PayloadBytes > all.Length)
                throw new InvalidDataException("TTC payload length is out of range.");

            byte[] payload = new byte[info.PayloadBytes];
            Buffer.BlockCopy(all, TtcWriter.HeaderSize, payload, 0, payload.Length);

            if (TtcCompress.IsTmBlob(payload))
            {
                info.Encoded = TtcCompress.Decompress(payload);
            }
            else if (payload.Length >= 2 && payload[0] == 0x78)
            {
                info.Encoded = TtcCompress.DecompressZlib(payload);
            }
            else if (info.UncompressedBytes == payload.Length)
            {
                info.Encoded = payload;
            }
            else
            {
                throw new InvalidDataException("Unrecognised TTC payload (not zlib, LZHAM or raw).");
            }

            if (info.UncompressedBytes > 0 && info.Encoded.Length != info.UncompressedBytes)
                throw new InvalidDataException("TTC payload inflated to " + info.Encoded.Length +
                    " bytes, header says " + info.UncompressedBytes + ".");

            return info;
        }

        /// <summary>Cheap structural check used by the resume logic and tests.</summary>
        public static bool IsValid(string path)
        {
            try
            {
                Info info = Read(path);
                return info.Width > 0 && info.Height > 0 && info.MipCount > 0 && info.Encoded.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
