using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Tns.Tools.Terrain;

/// <summary>
/// The colour of each kind of ground (Cover class) everywhere in the world, on a grid of quarter
/// degrees. It is measured from a satellite picture of the world (Blue Marble): for every class,
/// the average colour of the picture's pixels that are mostly that class, within about 30 km.
///
/// Averaging over many pixels does two things. Slopes facing the sun and slopes facing away
/// are averaged together, so the picture's own shadows cancel out and the game's lighting is the
/// only lighting. And the colour still varies the way the real world does: desert in the Sahara
/// is a different colour from desert in Arabia, forest in Siberia from forest in the Amazon.
/// Where a class is rare, its colour leans on a wider area, then on the world average.
///
/// All colours are linear RGB (light intensity), not sRGB, so averaging them is correct.
/// </summary>
public sealed class ColourTable
{
    public const int CellsPerDegree = 4;
    public const int Width = 360 * CellsPerDegree;
    public const int Height = 180 * CellsPerDegree;
    private const double KmPerDegree = 111.195;

    /// <summary>[class][(row * Width + column) * 3 + channel], row 0 at the north edge.</summary>
    private readonly float[][] _colours;

    private ColourTable(float[][] colours) => _colours = colours;

    public static readonly float[] SrgbToLinear = MakeSrgbTable();

    private static float[] MakeSrgbTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double s = i / 255.0;
            table[i] = (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));
        }

        return table;
    }

    public static byte LinearToSrgb(float linear)
    {
        double l = Math.Clamp(linear, 0f, 1f);
        double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255.0);
    }

    /// <summary>A class's colour at a place, blended from the four grid cells round it.</summary>
    public (float R, float G, float B) Sample(int cover, double latitude, double longitude)
    {
        double x = (longitude + 180.0) * CellsPerDegree - 0.5;
        double y = (90.0 - latitude) * CellsPerDegree - 0.5;
        y = Math.Clamp(y, 0.0, Height - 1.0);
        int x0 = (int)Math.Floor(x), y0 = Math.Min((int)y, Height - 2);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        int xa = ((x0 % Width) + Width) % Width, xb = (xa + 1) % Width;
        float[] grid = _colours[cover];
        int a = (y0 * Width + xa) * 3, b = (y0 * Width + xb) * 3;
        int c = ((y0 + 1) * Width + xa) * 3, d = ((y0 + 1) * Width + xb) * 3;
        float Mix(int channel) =>
            (grid[a + channel] * (1 - fx) + grid[b + channel] * fx) * (1 - fy) +
            (grid[c + channel] * (1 - fx) + grid[d + channel] * fx) * fy;
        return (Mix(0), Mix(1), Mix(2));
    }

    /// <summary>Replaces a class's colour everywhere (a recipe override). sRGB 0-1 in, as people pick colours.</summary>
    public void SetEverywhere(int cover, double r, double g, double b)
    {
        float lr = SrgbToLinearValue(r), lg = SrgbToLinearValue(g), lb = SrgbToLinearValue(b);
        float[] grid = _colours[cover];
        for (int i = 0; i < grid.Length; i += 3)
        {
            grid[i] = lr;
            grid[i + 1] = lg;
            grid[i + 2] = lb;
        }
    }

    private static float SrgbToLinearValue(double s) =>
        (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));

    // ---- measuring ----

    /// <summary>
    /// Measures the table. <paramref name="picture"/> is an RGB image of the whole world in plain
    /// latitude/longitude (row 0 at 90° N).
    /// </summary>
    public static ColourTable Measure(LandCover cover, byte[] picture, int pictureWidth, int pictureHeight,
        double radiusKm, int threads)
    {
        int cells = Width * Height;
        var sums = new float[Cover.Count][];
        var weights = new float[Cover.Count][];
        for (int c = 0; c < Cover.Count; c++)
        {
            sums[c] = new float[cells * 3];
            weights[c] = new float[cells];
        }

        // Work through the world in 3 x 3 degree blocks, the size of a WorldCover file. Blocks
        // line up with the grid, so two threads never add to the same cell.
        double picturePerDegree = pictureWidth / 360.0;
        // Land cover pixels per picture pixel. Need not be a whole number (12.5 at 160 m), so
        // each picture pixel's share of land cover is worked out from its exact edges.
        double ratio = cover.PixelsPerDegree / picturePerDegree;
        if (Math.Abs(pictureHeight * 2 - pictureWidth) > 2) throw new InvalidDataException("The colour picture must be twice as wide as it is tall.");
        if (ratio < 2) throw new InvalidDataException("The land cover must be finer than the colour picture.");
        long Edge(long picturePixel) => (long)Math.Floor(picturePixel * ratio + 1e-9);

        int blocks = 120 * 60, done = 0;
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, block =>
        {
            int blockColumn = block % 120, blockRow = block / 120;
            double west = -180.0 + blockColumn * 3, north = 90.0 - blockRow * 3;
            if (!cover.Covers(west, north - 3, west + 3, north)) return;

            int px0 = (int)Math.Round((west + 180.0) * picturePerDegree);
            int py0 = (int)Math.Round((90.0 - north) * picturePerDegree);
            int pixels = (int)Math.Round(3 * picturePerDegree);
            int stripRows = 8;
            long cx0 = Edge(px0);
            int width = (int)(Edge(px0 + pixels) - cx0);
            var classes = new byte[width * ((int)Math.Ceiling(stripRows * ratio) + 2)];
            Span<int> counts = stackalloc int[Cover.Count + 1];

            for (int strip = 0; strip < pixels; strip += stripRows)
            {
                int rows = Math.Min(stripRows, pixels - strip);
                long cy0 = Edge(py0 + strip);
                cover.Read(cx0, cy0, width, (int)(Edge(py0 + strip + rows) - cy0), classes);

                for (int r = 0; r < rows; r++)
                {
                    int py = py0 + strip + r;
                    int cellRow = (int)((py + 0.5) / picturePerDegree * CellsPerDegree);
                    int y0 = (int)(Edge(py) - cy0), y1 = (int)(Edge(py + 1) - cy0);
                    for (int p = 0; p < pixels; p++)
                    {
                        counts.Clear();
                        int x0 = (int)(Edge(px0 + p) - cx0), x1 = (int)(Edge(px0 + p + 1) - cx0);
                        for (int y = y0; y < y1; y++)
                        {
                            int at = y * width;
                            for (int x = x0; x < x1; x++)
                            {
                                byte k = classes[at + x];
                                counts[k == Cover.Unknown ? Cover.Count : k]++;
                            }
                        }

                        int total = (x1 - x0) * (y1 - y0);
                        int known = total - counts[Cover.Count];
                        if (known * 2 < total) continue;

                        int px = px0 + p;
                        long at3 = ((long)py * pictureWidth + px) * 3;
                        byte sr = picture[at3], sg = picture[at3 + 1], sb = picture[at3 + 2];
                        // Snow and cloud are bright and grey. Only snow may take their colour, so
                        // a winter picture does not whiten the grass under the snow.
                        int high = Math.Max(sr, Math.Max(sg, sb)), low = Math.Min(sr, Math.Min(sg, sb));
                        bool snowy = low > 150 && high - low < 32;
                        float lr = SrgbToLinear[sr], lg = SrgbToLinear[sg], lb = SrgbToLinear[sb];
                        int cell = cellRow * Width + (int)((px + 0.5) / picturePerDegree * CellsPerDegree);

                        for (int c = 0; c < Cover.Count; c++)
                        {
                            if (counts[c] == 0 || (snowy && c != Cover.Snow)) continue;
                            // Mostly-one-class pixels count far more than mixed ones, which
                            // separates the colours of classes that sit side by side.
                            float f = counts[c] / (float)known;
                            float w = f * f * f;
                            sums[c][cell * 3] += w * lr;
                            sums[c][cell * 3 + 1] += w * lg;
                            sums[c][cell * 3 + 2] += w * lb;
                            weights[c][cell] += w;
                        }
                    }
                }
            }

            int now = System.Threading.Interlocked.Increment(ref done);
            if (now % 50 == 0) Console.Write($"\r  measuring colours: {now} land blocks   ");
        });
        Console.WriteLine();

        // Near: about radiusKm. Wide: six times that. Then the world average.
        double nearCells = radiusKm / KmPerDegree * CellsPerDegree;
        var colours = new float[Cover.Count][];
        Parallel.For(0, Cover.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
        {
            double totalWeight = 0;
            var world = new double[3];
            for (int i = 0; i < cells; i++)
            {
                totalWeight += weights[c][i];
                for (int k = 0; k < 3; k++) world[k] += sums[c][i * 3 + k];
            }

            var fallback = Cover.Fallback[c];
            float[] worldColour = totalWeight > 50
                ? new[] { (float)(world[0] / totalWeight), (float)(world[1] / totalWeight), (float)(world[2] / totalWeight) }
                : new[] { fallback.R, fallback.G, fallback.B };

            float[] wideSums = Blur(sums[c], 3, nearCells * 6), wideWeights = Blur(weights[c], 1, nearCells * 6);
            float[] nearSums = Blur(sums[c], 3, nearCells), nearWeights = Blur(weights[c], 1, nearCells);
            const float wideTrust = 40f, nearTrust = 15f;
            var result = new float[cells * 3];
            for (int i = 0; i < cells; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    float wide = (wideSums[i * 3 + k] + wideTrust * worldColour[k]) / (wideWeights[i] + wideTrust);
                    result[i * 3 + k] = (nearSums[i * 3 + k] + nearTrust * wide) / (nearWeights[i] + nearTrust);
                }
            }

            colours[c] = result;
        });

        return new ColourTable(colours);
    }

    /// <summary>Gaussian blur on the globe: sigma is in cells north-south, and widens east-west toward the poles.</summary>
    private static float[] Blur(float[] values, int channels, double sigma)
    {
        var across = new float[values.Length];
        var result = new float[values.Length];

        // North-south.
        float[] kernel = Kernel(sigma, out int reach);
        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                for (int k = 0; k < channels; k++)
                {
                    float sum = 0f;
                    for (int t = -reach; t <= reach; t++)
                    {
                        int r = Math.Clamp(row + t, 0, Height - 1);
                        sum += kernel[t + reach] * values[(r * Width + column) * channels + k];
                    }

                    across[(row * Width + column) * channels + k] = sum;
                }
            }
        }

        // East-west, wrapping round the world.
        for (int row = 0; row < Height; row++)
        {
            double latitude = 90.0 - (row + 0.5) / CellsPerDegree;
            double rowSigma = Math.Min(sigma / Math.Max(Math.Cos(latitude * Math.PI / 180.0), 0.02), Width / 6.0);
            float[] rowKernel = Kernel(rowSigma, out int rowReach);
            for (int column = 0; column < Width; column++)
            {
                for (int k = 0; k < channels; k++)
                {
                    float sum = 0f;
                    for (int t = -rowReach; t <= rowReach; t++)
                    {
                        int c = ((column + t) % Width + Width) % Width;
                        sum += rowKernel[t + rowReach] * across[(row * Width + c) * channels + k];
                    }

                    result[(row * Width + column) * channels + k] = sum;
                }
            }
        }

        return result;
    }

    private static float[] Kernel(double sigma, out int reach)
    {
        sigma = Math.Max(sigma, 0.3);
        reach = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[reach * 2 + 1];
        float total = 0f;
        for (int i = -reach; i <= reach; i++) total += kernel[i + reach] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        return kernel;
    }

    // ---- saving ----

    public void Save(string path)
    {
        using var file = File.Create(path);
        using var zlib = new ZLibStream(file, CompressionLevel.Fastest);
        using var writer = new BinaryWriter(zlib);
        writer.Write("TNSCOLOURS1");
        writer.Write(Cover.Count);
        foreach (float[] grid in _colours)
        {
            foreach (float value in grid) writer.Write(value);
        }
    }

    public static ColourTable Load(string path)
    {
        using var file = File.OpenRead(path);
        using var zlib = new ZLibStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(zlib);
        if (reader.ReadString() != "TNSCOLOURS1" || reader.ReadInt32() != Cover.Count)
        {
            throw new InvalidDataException($"{path} is not a colour table this tool can read. Delete it to measure again.");
        }

        var colours = new float[Cover.Count][];
        for (int c = 0; c < Cover.Count; c++)
        {
            colours[c] = new float[Width * Height * 3];
            for (int i = 0; i < colours[c].Length; i++) colours[c][i] = reader.ReadSingle();
        }

        return new ColourTable(colours);
    }

    /// <summary>The game's copy of the colours, in the land cover pack.</summary>
    public const string AtlasFileName = "cover_colours.png";
    public const int AtlasWidth = 720, AtlasHeight = 360;

    /// <summary>
    /// Saves the colours for the game: one world map per kind of ground, half a degree per pixel,
    /// stacked top to bottom in Cover order (tree first), sRGB. Modders can repaint it in any
    /// image editor to change what a kind of ground looks like anywhere in the world.
    /// </summary>
    public void SaveAtlas(string path)
    {
        const int scale = Width / AtlasWidth;
        var canvas = new Canvas(AtlasWidth, AtlasHeight * Cover.Count, (0, 0, 0));
        for (int c = 0; c < Cover.Count; c++)
        {
            for (int y = 0; y < AtlasHeight; y++)
            {
                for (int x = 0; x < AtlasWidth; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int i = ((y * scale + dy) * Width + x * scale + dx) * 3;
                            r += _colours[c][i];
                            g += _colours[c][i + 1];
                            b += _colours[c][i + 2];
                        }
                    }

                    float k = 1f / (scale * scale);
                    canvas.Set(x, c * AtlasHeight + y, (LinearToSrgb(r * k), LinearToSrgb(g * k), LinearToSrgb(b * k)));
                }
            }
        }

        canvas.SavePng(path);
    }

    /// <summary>A picture of every class's colour round the world, for checking and for modders.</summary>
    public void SavePreview(string path)
    {
        const int scale = 2, columns = 3, gap = 24;
        int mapWidth = Width / scale, mapHeight = Height / scale;
        int rows = (Cover.Count + columns - 1) / columns;
        var canvas = new Canvas(columns * (mapWidth + gap) + gap, rows * (mapHeight + gap + 22) + gap, (20, 22, 28));
        for (int c = 0; c < Cover.Count; c++)
        {
            int ox = gap + c % columns * (mapWidth + gap), oy = gap + c / columns * (mapHeight + gap + 22);
            canvas.Text(ox, oy, Cover.Names[c], (226, 228, 235));
            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    int i = ((y * scale) * Width + x * scale) * 3;
                    canvas.Set(ox + x, oy + 22 + y,
                        (LinearToSrgb(_colours[c][i]), LinearToSrgb(_colours[c][i + 1]), LinearToSrgb(_colours[c][i + 2])));
                }
            }
        }

        canvas.SavePng(path);
    }
}
