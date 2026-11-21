using System;
using System.Collections.Generic;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Optional colour grading applied to each 2048x2048 mosaic before texture encoding.
    /// The values mirror the image-processing page of the original AeroScenery: brightness,
    /// contrast, saturation, sharpness and per-channel trim, plus a deliberately simple
    /// "unify water colour" pass for imagery whose lakes and sea come in mismatched hues.
    /// </summary>
    public sealed class ImageTuningOptions
    {
        public bool Enabled { get; set; }
        public int Brightness { get; set; }
        public int Contrast { get; set; }
        public int Saturation { get; set; }
        public int Sharpness { get; set; }
        public int Red { get; set; }
        public int Green { get; set; }
        public int Blue { get; set; }

        /// <summary>Blend water-like pixels towards <see cref="WaterColor"/>.</summary>
        public bool WaterEnabled { get; set; }
        public string WaterColor { get; set; } = "#1B4F8A";
        public int WaterStrength { get; set; } = 100;

        public bool HasWater
        {
            get { return WaterEnabled && WaterStrength > 0; }
        }

        public static ImageTuningOptions FromSettings(AppSettings s)
        {
            return new ImageTuningOptions
            {
                Enabled = s.EnableImageProcessing,
                Brightness = s.BrightnessAdjustment,
                Contrast = s.ContrastAdjustment,
                Saturation = s.SaturationAdjustment,
                Sharpness = s.SharpnessAdjustment,
                Red = s.RedAdjustment,
                Green = s.GreenAdjustment,
                Blue = s.BlueAdjustment,
                WaterEnabled = s.WaterTintEnabled,
                WaterColor = s.WaterTintColor,
                WaterStrength = s.WaterTintStrength
            };
        }
    }

    /// <summary>Fast per-pixel grading for a single 2048x2048 RGBA mosaic.</summary>
    public static class ImageTuning
    {
        public static unsafe void Apply(byte[] rgba, int width, int height, ImageTuningOptions options)
        {
            if (rgba == null || options == null) return;
            if (!options.Enabled && !options.HasWater) return;
            if (rgba.Length < width * height * 4) return;

            double brightness = Math.Clamp(options.Brightness, -100, 100) * 2.55;
            double contrastValue = Math.Clamp(options.Contrast, -100, 100) * 2.55;
            double contrast = (259.0 * (contrastValue + 255.0)) / (255.0 * (259.0 - contrastValue));
            double saturation = 1.0 + Math.Clamp(options.Saturation, -100, 100) / 100.0;
            double redGain = 1.0 + Math.Clamp(options.Red, -100, 100) / 100.0;
            double greenGain = 1.0 + Math.Clamp(options.Green, -100, 100) / 100.0;
            double blueGain = 1.0 + Math.Clamp(options.Blue, -100, 100) / 100.0;
            double sharpness = Math.Clamp(options.Sharpness, 0, 100) / 100.0 * 1.2;

            byte[] source = null;
            if (sharpness > 0.01)
            {
                source = new byte[rgba.Length];
                Buffer.BlockCopy(rgba, 0, source, 0, rgba.Length);
            }

            byte waterR = 27, waterG = 79, waterB = 138;
            ParseColor(options.WaterColor, ref waterR, ref waterG, ref waterB);
            double waterMix = Math.Clamp(options.WaterStrength, 0, 100) / 100.0;
            bool waterOn = options.HasWater;
            bool[] waterMask = waterOn ? BuildWaterMask(rgba, width, height) : null;

            fixed (byte* basePtr = rgba)
            fixed (byte* srcPtr = source)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* row = basePtr + y * width * 4;
                    byte* srcRow = source == null ? null : srcPtr + y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        byte* p = row + x * 4;
                        double r = p[0];
                        double g = p[1];
                        double b = p[2];
                        bool water = waterMask != null && waterMask[y * width + x];

                        if (options.Enabled)
                        {
                            if (sharpness > 0.01 && srcRow != null)
                            {
                                int lx = x > 0 ? x - 1 : x;
                                int rx = x + 1 < width ? x + 1 : x;
                                int uy = y > 0 ? y - 1 : y;
                                int dy = y + 1 < height ? y + 1 : y;
                                byte* l = srcRow + lx * 4;
                                byte* rr = srcRow + rx * 4;
                                byte* u = srcPtr + (uy * width + x) * 4;
                                byte* d = srcPtr + (dy * width + x) * 4;
                                double lr = (l[0] + rr[0] + u[0] + d[0] + srcRow[x * 4]) / 5.0;
                                double lg = (l[1] + rr[1] + u[1] + d[1] + srcRow[x * 4 + 1]) / 5.0;
                                double lb = (l[2] + rr[2] + u[2] + d[2] + srcRow[x * 4 + 2]) / 5.0;
                                r += (r - lr) * sharpness;
                                g += (g - lg) * sharpness;
                                b += (b - lb) * sharpness;
                            }

                            r = (r - 128.0) * contrast + 128.0 + brightness;
                            g = (g - 128.0) * contrast + 128.0 + brightness;
                            b = (b - 128.0) * contrast + 128.0 + brightness;

                            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                            r = luma + (r - luma) * saturation;
                            g = luma + (g - luma) * saturation;
                            b = luma + (b - luma) * saturation;

                            r *= redGain;
                            g *= greenGain;
                            b *= blueGain;
                        }

                        if (water)
                        {
                            r = r * (1.0 - waterMix) + waterR * waterMix;
                            g = g * (1.0 - waterMix) + waterG * waterMix;
                            b = b * (1.0 - waterMix) + waterB * waterMix;
                        }

                        p[0] = ClampByte(r);
                        p[1] = ClampByte(g);
                        p[2] = ClampByte(b);
                        p[3] = 255;
                    }
                }
            }
        }

        /// <summary>
        /// Colour-based water test. Real satellite lakes range from deep blue to cyan and
        /// green-teal, so a strict blue test misses most Chinese urban lakes. This accepts
        /// blue-dominant and teal pixels, plus very dark water, while requiring enough
        /// blue/green separation from red to avoid grey roofs and most vegetation.
        /// The UI labels the option as approximate rather than an OSM water mask.
        /// </summary>
        private static bool IsWaterLike(double r, double g, double b)
        {
            double luma = (r + g + b) / 3.0;
            if (luma > 178) return false;

            bool blue = b > 28 && (b - r) > 6 && b >= g - 22;
            bool teal = g > 24 && (g - r) > 10 && b > r + 2 && Math.Abs(g - b) <= 46 && luma < 150;
            bool dark = luma < 58 && b >= g - 10 && b >= r - 8;
            return blue || teal || dark;
        }

        /// <summary>
        /// Builds a water mask from colour candidates, then drops connected blobs smaller than
        /// a lake/pond. Blue roofs, painted lines and cars are small isolated specks; lakes and
        /// wide rivers survive. The threshold scales with resolution (about 1,200 px at 2048²).
        /// </summary>
        private static unsafe bool[] BuildWaterMask(byte[] rgba, int width, int height)
        {
            int count = width * height;
            var candidate = new bool[count];
            fixed (byte* basePtr = rgba)
            {
                byte* ptr = basePtr;
                for (int i = 0; i < count; i++, ptr += 4)
                {
                    candidate[i] = IsWaterLike(ptr[0], ptr[1], ptr[2]);
                }
            }

            var keep = new bool[count];
            var visited = new bool[count];
            var stack = new int[count];
            var component = new List<int>(4096);
            int minimumArea = Math.Max(600, count / 3500);

            for (int start = 0; start < count; start++)
            {
                if (!candidate[start] || visited[start]) continue;

                component.Clear();
                int top = 0;
                stack[top++] = start;
                visited[start] = true;

                while (top > 0)
                {
                    int index = stack[--top];
                    component.Add(index);
                    int x = index % width;
                    int y = index / width;

                    if (x > 0)
                    {
                        int n = index - 1;
                        if (candidate[n] && !visited[n]) { visited[n] = true; stack[top++] = n; }
                    }
                    if (x + 1 < width)
                    {
                        int n = index + 1;
                        if (candidate[n] && !visited[n]) { visited[n] = true; stack[top++] = n; }
                    }
                    if (y > 0)
                    {
                        int n = index - width;
                        if (candidate[n] && !visited[n]) { visited[n] = true; stack[top++] = n; }
                    }
                    if (y + 1 < height)
                    {
                        int n = index + width;
                        if (candidate[n] && !visited[n]) { visited[n] = true; stack[top++] = n; }
                    }
                }

                if (component.Count >= minimumArea)
                {
                    for (int i = 0; i < component.Count; i++) keep[component[i]] = true;
                }
            }

            return keep;
        }

        private static byte ClampByte(double value)
        {
            if (value <= 0) return 0;
            if (value >= 255) return 255;
            return (byte)(value + 0.5);
        }

        public static void ParseColor(string text, ref byte r, ref byte g, ref byte b)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string value = text.Trim().TrimStart('#');
            if (value.Length != 6) return;
            try
            {
                r = Convert.ToByte(value.Substring(0, 2), 16);
                g = Convert.ToByte(value.Substring(2, 2), 16);
                b = Convert.ToByte(value.Substring(4, 2), 16);
            }
            catch
            {
                // Keep the default deep blue.
            }
        }
    }
}
