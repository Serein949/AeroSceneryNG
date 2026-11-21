using System;

namespace AeroSceneryNG.Ttc
{
    /// <summary>
    /// Small, allocation-free BC1 (DXT1) and BC3 (DXT5) block encoders used by the TTC writer.
    ///
    /// The colour blocks use the classic bounding-box min/max RGB -&gt; RGB565 endpoints followed by
    /// nearest-palette-index assignment.  That is the cheap encoder the TTC port asks for: it is
    /// fast (well under a second for a 2048x2048 level) and every block is a normal 4-colour BC1
    /// block.  The BC3 alpha blocks use the 8-value interpolation mode.
    /// </summary>
    internal static class TtcBcEncoder
    {
        // Palette weights: colour = (wa * c0 + wb * c1) / 3 for the 4-colour BC1 mode.
        private static readonly int[] PaletteWa = new int[] { 3, 0, 2, 1 };
        private static readonly int[] PaletteWb = new int[] { 0, 3, 1, 2 };

        private const int BlockRgbaBytes = 64;

        private static int Expand5(int v)
        {
            return (v << 3) | (v >> 2);
        }

        private static int Expand6(int v)
        {
            return (v << 2) | (v >> 4);
        }

        private static int Quant5(int v)
        {
            return (v * 31 + 127) / 255;
        }

        private static int Quant6(int v)
        {
            return (v * 63 + 127) / 255;
        }

        private static int Quant565(int r, int g, int b)
        {
            return (Quant5(r) << 11) | (Quant6(g) << 5) | Quant5(b);
        }

        // ------------------------------------------------------------------ BC1 (DXT1)

        public static void EncodeBc1(byte[] rgba, int width, int height, byte[] dest, int destOffset)
        {
            if ((width & 3) != 0 || (height & 3) != 0)
            {
                throw new ArgumentException("BC1 encode requires width and height to be multiples of 4.");
            }
            if (width <= 0 || height <= 0)
            {
                return;
            }
            if (rgba.Length < width * height * 4)
            {
                throw new ArgumentException("RGBA source is too small.", "rgba");
            }

            int blocksW = width >> 2;
            int blocksH = height >> 2;
            if (dest.Length < destOffset + blocksW * blocksH * 8)
            {
                throw new ArgumentException("BC1 destination is too small.", "dest");
            }

            byte[] block = new byte[BlockRgbaBytes];
            int d = destOffset;

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    LoadBlock(rgba, width, bx, by, block);

                    int q0, q1;
                    uint indices;
                    EncodeColorBlock(block, out q0, out q1, out indices);

                    dest[d] = (byte)q0;
                    dest[d + 1] = (byte)(q0 >> 8);
                    dest[d + 2] = (byte)q1;
                    dest[d + 3] = (byte)(q1 >> 8);
                    dest[d + 4] = (byte)indices;
                    dest[d + 5] = (byte)(indices >> 8);
                    dest[d + 6] = (byte)(indices >> 16);
                    dest[d + 7] = (byte)(indices >> 24);
                    d += 8;
                }
            }
        }

        // ------------------------------------------------------------------ BC3 (DXT5)

        public static void EncodeBc3(byte[] rgba, int width, int height, byte[] dest, int destOffset)
        {
            if ((width & 3) != 0 || (height & 3) != 0)
            {
                throw new ArgumentException("BC3 encode requires width and height to be multiples of 4.");
            }
            if (width <= 0 || height <= 0)
            {
                return;
            }
            if (rgba.Length < width * height * 4)
            {
                throw new ArgumentException("RGBA source is too small.", "rgba");
            }

            int blocksW = width >> 2;
            int blocksH = height >> 2;
            if (dest.Length < destOffset + blocksW * blocksH * 16)
            {
                throw new ArgumentException("BC3 destination is too small.", "dest");
            }

            byte[] block = new byte[BlockRgbaBytes];
            int d = destOffset;

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    LoadBlock(rgba, width, bx, by, block);

                    EncodeAlphaBlock(block, dest, d);

                    int q0, q1;
                    uint indices;
                    EncodeColorBlock(block, out q0, out q1, out indices);

                    dest[d + 8] = (byte)q0;
                    dest[d + 9] = (byte)(q0 >> 8);
                    dest[d + 10] = (byte)q1;
                    dest[d + 11] = (byte)(q1 >> 8);
                    dest[d + 12] = (byte)indices;
                    dest[d + 13] = (byte)(indices >> 8);
                    dest[d + 14] = (byte)(indices >> 16);
                    dest[d + 15] = (byte)(indices >> 24);
                    d += 16;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void LoadBlock(byte[] src, int width, int bx, int by, byte[] block)
        {
            int x0 = bx * 4;
            int y0 = by * 4;
            for (int y = 0; y < 4; y++)
            {
                int so = ((y0 + y) * width + x0) * 4;
                int d = y * 16;
                block[d] = src[so];
                block[d + 1] = src[so + 1];
                block[d + 2] = src[so + 2];
                block[d + 3] = src[so + 3];
                block[d + 4] = src[so + 4];
                block[d + 5] = src[so + 5];
                block[d + 6] = src[so + 6];
                block[d + 7] = src[so + 7];
                block[d + 8] = src[so + 8];
                block[d + 9] = src[so + 9];
                block[d + 10] = src[so + 10];
                block[d + 11] = src[so + 11];
                block[d + 12] = src[so + 12];
                block[d + 13] = src[so + 13];
                block[d + 14] = src[so + 14];
                block[d + 15] = src[so + 15];
            }
        }

        private static void Order(ref int q0, ref int q1)
        {
            if (q0 < q1)
            {
                int t = q0;
                q0 = q1;
                q1 = t;
            }
        }

        /// <summary>
        /// Picks RGB565 endpoints for one 4x4 block and assigns palette indices.
        /// The result always uses the 4-colour BC1 mode (q0 &gt; q1) so no pixel decodes as
        /// transparent black.
        /// </summary>
        private static void EncodeColorBlock(byte[] block, out int outQ0, out int outQ1, out uint outIndices)
        {
            int minR = 255, minG = 255, minB = 255;
            int maxR = 0, maxG = 0, maxB = 0;

            for (int i = 0; i < 16; i++)
            {
                int o = i * 4;
                int r = block[o];
                int g = block[o + 1];
                int b = block[o + 2];
                if (r < minR) minR = r;
                if (g < minG) minG = g;
                if (b < minB) minB = b;
                if (r > maxR) maxR = r;
                if (g > maxG) maxG = g;
                if (b > maxB) maxB = b;
            }

            int q0 = Quant565(maxR, maxG, maxB);
            int q1 = Quant565(minR, minG, minB);
            Order(ref q0, ref q1);

            int errA;
            uint indices = Evaluate(block, q0, q1, out errA);

            // Least-squares refit of the endpoints for the winning palette assignment.  It is one
            // extra palette evaluation per block and typically buys ~0.5 dB of RGB PSNR.
            int rr0, rg0, rb0, rr1, rg1, rb1;
            RefitEndpoints(block, indices, q0, q1, out rr0, out rg0, out rb0, out rr1, out rg1, out rb1);
            int rq0 = Quant565(rr0, rg0, rb0);
            int rq1 = Quant565(rr1, rg1, rb1);
            Order(ref rq0, ref rq1);

            int errB;
            uint refitIndices = Evaluate(block, rq0, rq1, out errB);
            if (errB < errA)
            {
                q0 = rq0;
                q1 = rq1;
                indices = refitIndices;
            }

            // Force the 4-colour mode.  A single-colour block collapses to q0 == q1; nudging q1 down
            // by one keeps the palette legal while entry 0 still reproduces the block exactly.
            if (q0 == q1)
            {
                if (q0 > 0)
                {
                    q1 = q0 - 1;
                }
                else
                {
                    q0 = 1;
                    q1 = 0;
                }
                indices = 0;
            }

            outQ0 = q0;
            outQ1 = q1;
            outIndices = indices;
        }

        /// <summary>Builds the 4-colour palette and returns the packed 2-bit indices plus the error.</summary>
        private static uint Evaluate(byte[] block, int q0, int q1, out int error)
        {
            int r0 = Expand5((q0 >> 11) & 31);
            int g0 = Expand6((q0 >> 5) & 63);
            int b0 = Expand5(q0 & 31);
            int r1 = Expand5((q1 >> 11) & 31);
            int g1 = Expand6((q1 >> 5) & 63);
            int b1 = Expand5(q1 & 31);

            int r2, g2, b2, r3, g3, b3;
            if (q0 > q1)
            {
                r2 = (2 * r0 + r1) / 3;
                g2 = (2 * g0 + g1) / 3;
                b2 = (2 * b0 + b1) / 3;
                r3 = (r0 + 2 * r1) / 3;
                g3 = (g0 + 2 * g1) / 3;
                b3 = (b0 + 2 * b1) / 3;
            }
            else
            {
                r2 = (r0 + r1) / 2;
                g2 = (g0 + g1) / 2;
                b2 = (b0 + b1) / 2;
                r3 = 0;
                g3 = 0;
                b3 = 0;
            }

            uint bits = 0;
            int err = 0;

            for (int i = 0; i < 16; i++)
            {
                int o = i * 4;
                int r = block[o];
                int g = block[o + 1];
                int b = block[o + 2];

                int dr = r - r0, dg = g - g0, db = b - b0;
                int best = dr * dr + dg * dg + db * db;
                int idx = 0;

                dr = r - r1; dg = g - g1; db = b - b1;
                int d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; idx = 1; }

                dr = r - r2; dg = g - g2; db = b - b2;
                d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; idx = 2; }

                dr = r - r3; dg = g - g3; db = b - b3;
                d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; idx = 3; }

                err += best;
                bits |= (uint)idx << (2 * i);
            }

            error = err;
            return bits;
        }

        /// <summary>Least-squares endpoint refit for a given palette assignment (4-colour mode only).</summary>
        private static void RefitEndpoints(byte[] block, uint indices, int q0, int q1,
            out int r0, out int g0, out int b0, out int r1, out int g1, out int b1)
        {
            r0 = Expand5((q0 >> 11) & 31);
            g0 = Expand6((q0 >> 5) & 63);
            b0 = Expand5(q0 & 31);
            r1 = Expand5((q1 >> 11) & 31);
            g1 = Expand6((q1 >> 5) & 63);
            b1 = Expand5(q1 & 31);

            if (q0 <= q1)
            {
                return;
            }

            double saa = 0, sab = 0, sbb = 0;
            double savR = 0, savG = 0, savB = 0;
            double sbvR = 0, sbvG = 0, sbvB = 0;

            for (int i = 0; i < 16; i++)
            {
                int k = (int)((indices >> (2 * i)) & 3);
                double a = PaletteWa[k];
                double b = PaletteWb[k];
                int o = i * 4;
                double r = block[o];
                double g = block[o + 1];
                double bl = block[o + 2];

                saa += a * a;
                sab += a * b;
                sbb += b * b;
                savR += a * r; sbvR += b * r;
                savG += a * g; sbvG += b * g;
                savB += a * bl; sbvB += b * bl;
            }

            double det = saa * sbb - sab * sab;
            if (Math.Abs(det) < 1e-9)
            {
                return;
            }

            double inv = 1.0 / det;
            double p0r = (savR * sbb - sbvR * sab) * inv * 3.0;
            double p0g = (savG * sbb - sbvG * sab) * inv * 3.0;
            double p0b = (savB * sbb - sbvB * sab) * inv * 3.0;
            double p1r = (sbvR * saa - savR * sab) * inv * 3.0;
            double p1g = (sbvG * saa - savG * sab) * inv * 3.0;
            double p1b = (sbvB * saa - savB * sab) * inv * 3.0;

            r0 = ClampByte((int)Math.Round(p0r));
            g0 = ClampByte((int)Math.Round(p0g));
            b0 = ClampByte((int)Math.Round(p0b));
            r1 = ClampByte((int)Math.Round(p1r));
            g1 = ClampByte((int)Math.Round(p1g));
            b1 = ClampByte((int)Math.Round(p1b));
        }

        private static int ClampByte(int v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }

        // ------------------------------------------------------------------ BC3 alpha

        private static void EncodeAlphaBlock(byte[] block, byte[] dst, int d)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = block[i * 4 + 3];
                if (a < min) min = a;
                if (a > max) max = a;
            }

            if (min == max)
            {
                // Constant alpha: a0 == a1 selects the 6-value palette; index 0 is a0 exactly.
                dst[d] = (byte)min;
                dst[d + 1] = (byte)min;
                for (int i = 2; i < 8; i++)
                {
                    dst[d + i] = 0;
                }
                return;
            }

            byte a0 = (byte)max;
            byte a1 = (byte)min;
            int error;
            ulong bits = EvaluateAlpha(block, a0, a1, out error);

            dst[d] = a0;
            dst[d + 1] = a1;
            for (int i = 0; i < 6; i++)
            {
                dst[d + 2 + i] = (byte)(bits >> (8 * i));
            }
        }

        private static ulong EvaluateAlpha(byte[] block, byte a0, byte a1, out int error)
        {
            byte[] pal = new byte[8];
            pal[0] = a0;
            pal[1] = a1;

            if (a0 > a1)
            {
                pal[2] = (byte)((6 * a0 + a1) / 7);
                pal[3] = (byte)((5 * a0 + 2 * a1) / 7);
                pal[4] = (byte)((4 * a0 + 3 * a1) / 7);
                pal[5] = (byte)((3 * a0 + 4 * a1) / 7);
                pal[6] = (byte)((2 * a0 + 5 * a1) / 7);
                pal[7] = (byte)((a0 + 6 * a1) / 7);
            }
            else
            {
                pal[2] = (byte)((4 * a0 + a1) / 5);
                pal[3] = (byte)((3 * a0 + 2 * a1) / 5);
                pal[4] = (byte)((2 * a0 + 3 * a1) / 5);
                pal[5] = (byte)((a0 + 4 * a1) / 5);
                pal[6] = 0;
                pal[7] = 255;
            }

            ulong bits = 0;
            int err = 0;

            for (int i = 0; i < 16; i++)
            {
                int v = block[i * 4 + 3];
                int best = v - pal[0];
                best *= best;
                int idx = 0;

                for (int k = 1; k < 8; k++)
                {
                    int d = v - pal[k];
                    d *= d;
                    if (d < best)
                    {
                        best = d;
                        idx = k;
                    }
                }

                err += best;
                bits |= (ulong)idx << (3 * i);
            }

            error = err;
            return bits;
        }
    }
}
