using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tns.Tools.Terrain;

/// <summary>
/// Makes the close-up ground textures (terrain groundtex): one per kind of ground, from a
/// satellite picture of a place that is typical of it (download_s2_samples.ps1, then
/// make_ground_textures.ps1 turns them into PNGs named &lt;kind&gt;_&lt;place&gt;.png).
///
/// Each texture holds only the fine light and dark of the ground, finer than BlurM: the picture
/// is divided by a blurred copy of itself, so it averages exactly 1 and never changes the colour
/// the map already shows; it only adds texture on top of it up close. Stored as a multiplier
/// times 128, like the detail tiles (128 is 1, no change).
/// </summary>
public static class GroundTextures
{
    public sealed record Settings
    {
        /// <summary>Variation coarser than this is left to the detail tiles; finer is kept.</summary>
        public double BlurM { get; init; } = 500;
        /// <summary>Ground size of one pixel of the samples.</summary>
        public double PixelM { get; init; } = 43;
        /// <summary>How much of the colour of the variation is kept, 0 (brightness only) to 1.</summary>
        public double Chroma { get; init; } = 0.5;
        /// <summary>
        /// Standout spots (a white salt pan, a lake shore, a road) are what the eye picks out when
        /// the texture repeats across a country, so light and dark further from 1 than this many
        /// times the sample's usual spread are eased back (smoothly, on a log scale). 0 leaves them.
        /// </summary>
        public double Soften { get; init; } = 2.5;
        public double Min { get; init; } = 0.5;
        public double Max { get; init; } = 1.8;
    }

    public static void Run(string samples, string output, Settings settings, IReadOnlyDictionary<string, string> pick)
    {
        Directory.CreateDirectory(output);
        var files = Directory.GetFiles(samples, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (string kind in Cover.Names)
        {
            string chosen = pick.TryGetValue(kind, out string p)
                ? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == p)
                : files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(kind + "_", StringComparison.Ordinal));
            if (chosen == null) continue;
            byte[] rgb = PngImage.ReadRgb(chosen, out int w, out int h);
            byte[] texture = Make(rgb, w, h, settings, out double contrast);
            string path = Path.Combine(output, kind + ".png");
            var canvas = new Canvas(w, h, (128, 128, 128));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    canvas.Set(x, y, (texture[i], texture[i + 1], texture[i + 2]));
                }
            }

            canvas.SavePng(path);
            Console.WriteLine($"  {kind,-8} from {Path.GetFileName(chosen),-24} {w} x {h}, variation {100 * contrast:0.0}% -> {path}");
        }
    }

    /// <summary>Every sample made into a texture named after it (for the regional versions).</summary>
    public static void RunAll(string samples, string output, Settings settings, IReadOnlyDictionary<string, double> blurFor = null)
    {
        Directory.CreateDirectory(output);
        foreach (string file in Directory.GetFiles(samples, "*.png").OrderBy(f => f, StringComparer.Ordinal))
        {
            // A sample whose pattern is coarser than BlurM (big dunes) can keep more of it.
            Settings own = blurFor != null && blurFor.TryGetValue(Path.GetFileNameWithoutExtension(file), out double blur) ? settings with { BlurM = blur } : settings;
            byte[] rgb = PngImage.ReadRgb(file, out int w, out int h);
            byte[] texture = Make(rgb, w, h, own, out double contrast);
            var canvas = new Canvas(w, h, (128, 128, 128));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    canvas.Set(x, y, (texture[i], texture[i + 1], texture[i + 2]));
                }
            }

            string path = Path.Combine(output, Path.GetFileName(file));
            canvas.SavePng(path);
            Console.WriteLine($"  {Path.GetFileNameWithoutExtension(file),-20} variation {100 * contrast:0.0}%");
        }
    }

    private static byte[] Make(byte[] rgb, int w, int h, Settings s, out double contrast)
    {
        int n = w * h;
        var lin = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            lin[c] = new float[n];
            // The samples are stored as the square root of reflectance (make_ground_textures.ps1).
            for (int i = 0; i < n; i++)
            {
                float v = rgb[i * 3 + c] / 255f;
                lin[c][i] = v * v;
            }
        }

        float sigma = (float)(s.BlurM / s.PixelM);
        var blurred = lin.Select(plane => Blur(plane, w, h, sigma)).ToArray();
        var m = new float[3][];
        for (int c = 0; c < 3; c++) m[c] = new float[n];
        var lums = new float[n];
        for (int i = 0; i < n; i++)
        {
            float l = 0.2126f * lin[0][i] + 0.7152f * lin[1][i] + 0.0722f * lin[2][i];
            float lb = 0.2126f * blurred[0][i] + 0.7152f * blurred[1][i] + 0.0722f * blurred[2][i];
            lums[i] = l / Math.Max(lb, 1e-5f);
        }

        // The usual spread of light and dark: the median distance from the median, on a log scale
        // (x 1.4826, a standard deviation for a bell curve), which the standout spots barely move.
        float limit = 0;
        if (s.Soften > 0)
        {
            var logs = lums.Select(v => MathF.Log(Math.Max(v, 1e-4f))).ToArray();
            var sorted = (float[])logs.Clone();
            Array.Sort(sorted);
            float median = sorted[n / 2];
            var away = logs.Select(v => Math.Abs(v - median)).ToArray();
            Array.Sort(away);
            limit = (float)(s.Soften * 1.4826 * away[n / 2]);
        }

        for (int i = 0; i < n; i++)
        {
            float lum = lums[i];
            // Eased back past the limit: tanh keeps small changes as they are and levels off.
            float soft = limit > 0 ? MathF.Exp(limit * MathF.Tanh(MathF.Log(Math.Max(lum, 1e-4f)) / limit)) : lum;
            for (int c = 0; c < 3; c++)
            {
                float own = lin[c][i] / Math.Max(blurred[c][i], 1e-5f);
                float v = soft * (1f + (float)s.Chroma * (own / Math.Max(lum, 1e-4f) - 1f));
                m[c][i] = Math.Clamp(v, (float)s.Min, (float)s.Max);
            }
        }

        // Exactly 1 on average, so zooming in never shifts the colour.
        double spread = 0;
        for (int c = 0; c < 3; c++)
        {
            double mean = m[c].Average(v => (double)v);
            for (int i = 0; i < n; i++) m[c][i] = (float)(m[c][i] / mean);
            spread += Math.Sqrt(m[c].Average(v => (v - 1.0) * (v - 1.0))) / 3;
        }

        contrast = spread;
        var bytes = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++) bytes[i * 3 + c] = (byte)Math.Clamp(Math.Round(m[c][i] * 128), 0, 255);
        }

        return bytes;
    }

    /// <summary>A Gaussian blur, edges extended.</summary>
    private static float[] Blur(float[] plane, int w, int h, float sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        float sum = 0;
        for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] = MathF.Exp(-k * k / (2 * sigma * sigma));
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        var across = new float[plane.Length];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int k = -radius; k <= radius; k++) v += kernel[k + radius] * plane[y * w + Math.Clamp(x + k, 0, w - 1)];
                across[y * w + x] = v;
            }
        });
        var result = new float[plane.Length];
        System.Threading.Tasks.Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                float v = 0;
                for (int k = -radius; k <= radius; k++) v += kernel[k + radius] * across[Math.Clamp(y + k, 0, h - 1) * w + x];
                result[y * w + x] = v;
            }
        });
        return result;
    }
}
