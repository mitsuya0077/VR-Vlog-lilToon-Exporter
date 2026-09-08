using System;

namespace VRVlog.LilToonExporter
{
    // Byte-domain area filtering makes resizing independent of Editor color
    // space. Color maps filter premultiplied linear light; data maps filter
    // their numeric channels directly. The source array is never written.
    internal static class TextureResizePolicy
    {
        internal static Tuple<int, int> Size(int width, int height, int maximum = LilToonMobileProfile.DefaultMaximumTextureSize)
        {
            if (width <= 0 || height <= 0 || maximum <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
            if (width <= maximum && height <= maximum) return Tuple.Create(width, height);
            var scale = maximum / (double)Math.Max(width, height);
            return Tuple.Create(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static readonly double[] Linear = CreateLinearLookup();
        private static double[] CreateLinearLookup()
        {
            var result = new double[256];
            for (var i = 0; i < result.Length; i++)
            {
                var value = i / 255.0;
                result[i] = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return result;
        }
        private static byte Byte(double value) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * 255)));
        private static byte Srgb(double value) => Byte(value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055);

        internal static byte[] Resize(byte[] rgba, int width, int height, bool srgb, int maximum = LilToonMobileProfile.DefaultMaximumTextureSize)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            var size = Size(width, height, maximum);
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA byte count does not match dimensions.", nameof(rgba));
            if (size.Item1 == width && size.Item2 == height) return (byte[])rgba.Clone();
            var output = new byte[checked(size.Item1 * size.Item2 * 4)];
            var sx = width / (double)size.Item1;
            var sy = height / (double)size.Item2;
            for (var y = 0; y < size.Item2; y++)
            for (var x = 0; x < size.Item1; x++)
            {
                var left = x * sx; var right = (x + 1) * sx;
                var bottom = y * sy; var top = (y + 1) * sy;
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                for (var iy = (int)bottom; iy < Math.Min(height, (int)Math.Ceiling(top)); iy++)
                for (var ix = (int)left; ix < Math.Min(width, (int)Math.Ceiling(right)); ix++)
                {
                    var weight = (Math.Min(ix + 1, right) - Math.Max(ix, left)) * (Math.Min(iy + 1, top) - Math.Max(iy, bottom));
                    var p = (iy * width + ix) * 4;
                    var alpha = rgba[p + 3] / 255.0;
                    if (srgb)
                    {
                        r += Linear[rgba[p]] * alpha * weight;
                        g += Linear[rgba[p + 1]] * alpha * weight;
                        b += Linear[rgba[p + 2]] * alpha * weight;
                    }
                    else
                    {
                        r += rgba[p] / 255.0 * weight;
                        g += rgba[p + 1] / 255.0 * weight;
                        b += rgba[p + 2] / 255.0 * weight;
                    }
                    a += alpha * weight;
                    total += weight;
                }
                var o = (y * size.Item1 + x) * 4;
                output[o] = srgb ? Srgb(a > 0 ? r / a : 0) : Byte(r / total);
                output[o + 1] = srgb ? Srgb(a > 0 ? g / a : 0) : Byte(g / total);
                output[o + 2] = srgb ? Srgb(a > 0 ? b / a : 0) : Byte(b / total);
                output[o + 3] = Byte(a / total);
            }
            return output;
        }
    }
}
