using System;

namespace AeroSceneryNG.Ttc
{
    /// <summary>
    /// RGBA8 mip chain helpers for the TTC writer: area-average ("box") downsampling of a level,
    /// vertical flip and padding up to whole 4x4 blocks.  Every buffer is tightly packed top-down
    /// RGBA, i.e. one pixel is four consecutive bytes in R,G,B,A order.
    /// </summary>
    internal static class TtcMipChain
    {
        /// <summary>Number of mip levels down to 1x1.</summary>
        public static int Count(int width, int height)
        {
            int n = 1;
            int m = Math.Max(width, height);
            while (m > 1)
            {
                m /= 2;
                n++;
            }
            return n;
        }

        /// <summary>Area-average ("box") downscale by a factor of two, rounding to nearest.</summary>
        public static byte[] Halve(byte[] rgba, int width, int height, out int newWidth, out int newHeight)
        {
            newWidth = Math.Max(1, width / 2);
            newHeight = Math.Max(1, height / 2);
            byte[] dest = new byte[newWidth * newHeight * 4];

            for (int dy = 0; dy < newHeight; dy++)
            {
                int sy0 = dy * height / newHeight;
                int sy1 = Math.Max(sy0 + 1, (dy + 1) * height / newHeight);
                for (int dx = 0; dx < newWidth; dx++)
                {
                    int sx0 = dx * width / newWidth;
                    int sx1 = Math.Max(sx0 + 1, (dx + 1) * width / newWidth);

                    int sumR = 0, sumG = 0, sumB = 0, sumA = 0, n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int rowBase = (sy * width + sx0) * 4;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int o = rowBase + (sx - sx0) * 4;
                            sumR += rgba[o];
                            sumG += rgba[o + 1];
                            sumB += rgba[o + 2];
                            sumA += rgba[o + 3];
                            n++;
                        }
                    }

                    int d = (dy * newWidth + dx) * 4;
                    dest[d] = (byte)((sumR + n / 2) / n);
                    dest[d + 1] = (byte)((sumG + n / 2) / n);
                    dest[d + 2] = (byte)((sumB + n / 2) / n);
                    dest[d + 3] = (byte)((sumA + n / 2) / n);
                }
            }

            return dest;
        }

        /// <summary>Flips an RGBA image vertically (the converter stores TTC rows bottom-up).</summary>
        public static byte[] FlipVertical(byte[] rgba, int width, int height)
        {
            int row = width * 4;
            byte[] dest = new byte[rgba.Length];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * row, dest, (height - 1 - y) * row, row);
            }
            return dest;
        }

        /// <summary>Pads an RGBA image up to a multiple of <paramref name="align"/> by edge clamping.</summary>
        public static byte[] PadToMultiple(byte[] rgba, int width, int height, int align,
            out int paddedWidth, out int paddedHeight)
        {
            paddedWidth = (width + align - 1) / align * align;
            paddedHeight = (height + align - 1) / align * align;

            if (paddedWidth == width && paddedHeight == height)
            {
                byte[] copy = new byte[rgba.Length];
                Buffer.BlockCopy(rgba, 0, copy, 0, rgba.Length);
                return copy;
            }

            byte[] dest = new byte[paddedWidth * paddedHeight * 4];
            for (int y = 0; y < paddedHeight; y++)
            {
                int sy = y < height ? y : height - 1;
                int srcRow = sy * width * 4;
                int dstRow = y * paddedWidth * 4;
                for (int x = 0; x < paddedWidth; x++)
                {
                    int sx = x < width ? x : width - 1;
                    int so = srcRow + sx * 4;
                    int d = dstRow + x * 4;
                    dest[d] = rgba[so];
                    dest[d + 1] = rgba[so + 1];
                    dest[d + 2] = rgba[so + 2];
                    dest[d + 3] = rgba[so + 3];
                }
            }
            return dest;
        }
    }
}
