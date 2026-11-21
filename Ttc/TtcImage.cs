using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AeroSceneryNG.Ttc
{
    /// <summary>
    /// Decodes the largest mip of a TTC back to RGBA. Used by the <c>--png</c> verification
    /// command and by the tile preview; it is intentionally a straightforward reference decoder.
    /// </summary>
    public static class TtcImage
    {
        public static byte[] DecodeMip0(TtcReader.Info info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("TTC has no pixels.");

            int width = info.Width;
            int height = info.Height;
            byte[] rgba = new byte[width * height * 4];

            switch (info.FormatId)
            {
                case TtcWriter.FormatDxt1:
                    DecodeBc(rgba, info.Encoded, width, height, 8, true);
                    break;
                case TtcWriter.FormatDxt5:
                    DecodeBc(rgba, info.Encoded, width, height, 16, false);
                    break;
                case TtcWriter.FormatEtc2:
                    EtcCodec.DecodeEtc2Rgb(info.Encoded, width, height, rgba);
                    break;
                default:
                    throw new InvalidDataException("Unsupported TTC format " + TtcWriter.FormatName(info.FormatId) + ".");
            }

            return rgba;
        }

        public static void SavePng(string ttcPath, string pngPath)
        {
            TtcReader.Info info = TtcReader.Read(ttcPath);
            byte[] rgba = DecodeMip0(info);
            using (var image = Image.LoadPixelData<Rgba32>(rgba, info.Width, info.Height))
            {
                image.SaveAsPng(pngPath);
            }
        }

        private static void DecodeBc(byte[] dst, byte[] src, int width, int height, int blockBytes, bool bc1)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int offset = 0;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    uint color0 = ReadU16(src, offset);
                    uint color1 = ReadU16(src, offset + 2);
                    uint bits = (uint)(src[offset + 4] | (src[offset + 5] << 8) | (src[offset + 6] << 16) | (src[offset + 7] << 24));

                    byte[] palette = new byte[16];
                    Expand565(color0, palette, 0);
                    Expand565(color1, palette, 4);

                    if (!bc1 || color0 > color1)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c] + 1) / 3);
                            palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c] + 1) / 3);
                        }
                        palette[11] = 255;
                        palette[15] = 255;
                    }
                    else
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            palette[8 + c] = (byte)((palette[c] + palette[4 + c]) / 2);
                        }
                        palette[11] = 255;
                        palette[12] = 0;
                        palette[13] = 0;
                        palette[14] = 0;
                        palette[15] = 0;
                    }

                    int alphaOffset = 0;
                    byte[] alpha = null;
                    int colorOffset = offset;
                    if (!bc1)
                    {
                        alpha = DecodeBc3Alpha(src, offset, out alphaOffset);
                        colorOffset = offset + 8;
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;

                            int index = (int)((bits >> (2 * (py * 4 + px))) & 3);
                            int d = (y * width + x) * 4;
                            dst[d] = palette[index * 4];
                            dst[d + 1] = palette[index * 4 + 1];
                            dst[d + 2] = palette[index * 4 + 2];
                            dst[d + 3] = bc1 ? palette[index * 4 + 3] : alpha[py * 4 + px];
                        }
                    }

                    offset += blockBytes;
                }
            }
        }

        private static byte[] DecodeBc3Alpha(byte[] src, int offset, out int end)
        {
            byte[] alpha = new byte[16];
            byte a0 = src[offset];
            byte a1 = src[offset + 1];
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)src[offset + 2 + i] << (8 * i);

            byte[] palette = new byte[8];
            palette[0] = a0;
            palette[1] = a1;
            if (a0 > a1)
            {
                for (int i = 2; i < 8; i++)
                    palette[i] = (byte)(((8 - i) * a0 + (i - 1) * a1 + 3) / 7);
            }
            else
            {
                for (int i = 2; i < 6; i++)
                    palette[i] = (byte)(((6 - i) * a0 + (i - 1) * a1 + 2) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }

            for (int i = 0; i < 16; i++)
                alpha[i] = palette[(int)((bits >> (3 * i)) & 7)];

            end = offset + 8;
            return alpha;
        }

        private static void Expand565(uint c, byte[] dst, int offset)
        {
            int r = (int)((c >> 11) & 0x1F);
            int g = (int)((c >> 5) & 0x3F);
            int b = (int)(c & 0x1F);
            dst[offset] = (byte)((r << 3) | (r >> 2));
            dst[offset + 1] = (byte)((g << 2) | (g >> 4));
            dst[offset + 2] = (byte)((b << 3) | (b >> 2));
            dst[offset + 3] = 255;
        }

        private static uint ReadU16(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8));
        }
    }
}
