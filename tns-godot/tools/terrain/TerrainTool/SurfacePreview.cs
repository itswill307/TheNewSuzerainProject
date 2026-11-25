using System;
using System.Collections.Generic;
using System.IO;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Draws a region of the map from the surface packs much as the game shader does, as a PNG. For
/// checking a bake without starting the game, and for comparing settings.
///
/// Up close the land cover is drawn kind by kind with sharp edges (CoverDrawing), each kind
/// coloured from the colour map. Far away, where land cover samples are smaller than a pixel,
/// the blended colour and water tiles are drawn instead.
/// </summary>
public static class SurfacePreview
{
    // The same defaults as the game shader (sRGB).
    private static readonly (float R, float G, float B) Shallow = (0.23f, 0.45f, 0.52f);
    private static readonly (float R, float G, float B) Shelf = (0.10f, 0.25f, 0.38f);
    private static readonly (float R, float G, float B) Deep = (0.04f, 0.09f, 0.20f);
    private static readonly (float R, float G, float B) Lake = (0.08f, 0.17f, 0.23f);

    private sealed class Layer
    {
        public TerrainPackSet Packs;
        public readonly Dictionary<TileId, object> Loaded = new();
    }

    public static int Run(string surfaceFolder, string output, double west, double south, double east, double north,
        int width, double reliefStrength, string heightPack)
    {
        // surfaceFolder holds the cover, surface and water packs side by side (data/base).
        var cover = new Layer { Packs = new TerrainPackSet(SurfaceTile.CoverExtension) };
        var colour = new Layer { Packs = new TerrainPackSet(SurfaceTile.ColourExtension) };
        var water = new Layer { Packs = new TerrainPackSet(SurfaceTile.WaterExtension) };
        cover.Packs.AddPack(Path.Combine(surfaceFolder, "cover"));
        colour.Packs.AddPack(Path.Combine(surfaceFolder, "surface"));
        water.Packs.AddPack(Path.Combine(surfaceFolder, "water"));
        var detail = new Layer { Packs = new TerrainPackSet(SurfaceTile.DetailExtension) };
        detail.Packs.AddPack(Path.Combine(surfaceFolder, "detail"));
        Layer heights = null;
        if (heightPack != null)
        {
            heights = new Layer { Packs = new TerrainPackSet() };
            heights.Packs.AddPack(heightPack);
        }

        byte[] atlas = null;
        string atlasPath = Path.Combine(surfaceFolder, "cover", ColourTable.AtlasFileName);
        if (cover.Packs.TileCount > 0) atlas = PngImage.ReadRgb(atlasPath, out _, out _);

        double midLatitude = (south + north) * 0.5 * Math.PI / 180.0;
        int height = (int)Math.Round(width * (north - south) / ((east - west) * Math.Cos(midLatitude)));
        double degreesPerPixel = (east - west) / width;
        double metresPerPixel = degreesPerPixel * 111_195.0;
        // Land cover tiles no finer than the screen can show, as the game picks them.
        int coverLevel = (int)Math.Floor(Math.Log2(180.0 / (HeightTile.Intervals * degreesPerPixel * 0.8)));
        var canvas = new Canvas(width, height, (0, 0, 0));
        Span<float> votes = stackalloc float[Cover.Count];

        for (int y = 0; y < height; y++)
        {
            double latitude = north - (y + 0.5) * (north - south) / height;
            for (int x = 0; x < width; x++)
            {
                double longitude = west + (x + 0.5) * degreesPerPixel;
                float[] c = SampleSurface(colour, latitude, longitude, 4, 99);
                float shade = 1f;
                if (heights != null && reliefStrength > 0)
                {
                    float s = Shade(heights, latitude, longitude, degreesPerPixel, metresPerPixel);
                    shade = 1f + (Math.Clamp(s, 0.35f, 1.6f) - 1f) * (float)reliefStrength;
                }

                var sea = WaterColour(c[3]);
                (float R, float G, float B) land = (c[0] * shade, c[1] * shade, c[2] * shade);
                TileId coverTile = atlas == null ? default : Deepest(cover.Packs, latitude, longitude, coverLevel);
                if (atlas != null && cover.Packs.Has(coverTile))
                {
                    // Up close: the kind of land, with sharp edges.
                    var tile = (float[])Load(cover, coverTile, id => ToFloats(cover.Packs.LoadSurface(id).Data));
                    double column = (longitude - coverTile.West) / coverTile.SizeDeg * HeightTile.Intervals;
                    double row = (latitude - coverTile.South) / coverTile.SizeDeg * HeightTile.Intervals;
                    double pixelInSamples = degreesPerPixel / (coverTile.SizeDeg / HeightTile.Intervals);
                    int best, second;
                    float lead, blendWidth;
                    if (Sharpness > 0)
                    {
                        // Each kind's colour, weighted by its smooth share raised to a power:
                        // where one kind dominates it wins outright (crisp), where kinds are
                        // mixed they blend (soft). Higher powers are crisper.
                        TileId at = coverTile;
                        CoverDrawing.SmoothVotes((c, r) => KindNear(cover, at, tile, c, r), column, row, votes);
                        float total = 0, r0 = 0, g0 = 0, b0 = 0;
                        for (int k = 0; k < Cover.Count; k++)
                        {
                            if (votes[k] <= 0) continue;
                            float w = MathF.Pow(votes[k], (float)Sharpness);
                            var kindColour = KindColour(atlas, k, latitude, longitude, shade);
                            r0 += w * kindColour.R;
                            g0 += w * kindColour.G;
                            b0 += w * kindColour.B;
                            total += w;
                        }

                        if (total > 0) land = (r0 / total, g0 / total, b0 / total);
                        best = -1;
                        second = -1;
                        lead = 0;
                        blendWidth = 1;
                    }
                    else if (Smooth)
                    {
                        TileId at = coverTile;
                        (best, second, lead) = CoverDrawing.SmoothVote((c, r) => KindNear(cover, at, tile, c, r), column, row);
                        blendWidth = (float)Math.Max(pixelInSamples * 0.75, Softness);
                    }
                    else
                    {
                        (best, second, lead) = CoverDrawing.Vote(tile, column, row, Cover.Water);
                        blendWidth = (float)Math.Max(pixelInSamples, 1e-3);
                    }

                    if (best >= 0)
                    {
                        float t = 0.5f + 0.5f * SmoothStep(0f, blendWidth, lead);
                        land = Mix(KindColour(atlas, second, latitude, longitude, shade), KindColour(atlas, best, latitude, longitude, shade), t);
                    }
                }

                // The real colour of the ground relative to its kinds (DetailBaker), applied in linear light.
                if (detail.Packs.TileCount > 0)
                {
                    float[] d = SampleSurface(detail, latitude, longitude, 3, 99);
                    land = (Detailed(land.R, d[0]), Detailed(land.G, d[1]), Detailed(land.B, d[2]));
                }

                // The coast where the water share crosses one half, softened over about a pixel.
                float wet = SampleSurface(water, latitude, longitude, 1, 99)[0];
                var pixel = Mix(land, sea, Math.Clamp((wet - 0.5f) * 6f + 0.5f, 0f, 1f));

                canvas.Set(x, y, (ToByte(pixel.R), ToByte(pixel.G), ToByte(pixel.B)));
            }
        }

        canvas.SavePng(output);
        Console.WriteLine($"Wrote {output} ({width} x {height}, {metresPerPixel:0} m per pixel)");
        return 0;
    }

    /// <summary>Draw land cover with the smooth 4 x 4 vote, and blend kinds over at least this many samples.</summary>
    public static bool Smooth = true;
    public static double Softness = 0.3;

    /// <summary>When above 0, blend kinds by their smooth shares raised to this power instead of voting.</summary>
    public static double Sharpness = 2;

    /// <summary>Repeat a tile's edge samples instead of reading the neighbouring tile (to check seams).</summary>
    public static bool ClampEdges;

    /// <summary>The kind at a sample of a tile, reaching into the neighbouring tile at the same level when just outside it.</summary>
    private static int KindNear(Layer cover, TileId id, float[] tile, int column, int row)
    {
        if (column >= 0 && row >= 0 && column <= HeightTile.Intervals && row <= HeightTile.Intervals)
        {
            return (int)tile[row * HeightTile.Samples + column];
        }

        int dx = column < 0 ? -1 : column > HeightTile.Intervals ? 1 : 0, dy = row < 0 ? -1 : row > HeightTile.Intervals ? 1 : 0;
        int columns = TileId.ColumnsAt(id.Level);
        var neighbour = new TileId(id.Level, ((id.X + dx) % columns + columns) % columns, id.Y + dy);
        if (!ClampEdges && neighbour.IsValid && cover.Packs.Has(neighbour))
        {
            var other = (float[])Load(cover, neighbour, t => ToFloats(cover.Packs.LoadSurface(t).Data));
            return (int)other[(row - dy * HeightTile.Intervals) * HeightTile.Samples + column - dx * HeightTile.Intervals];
        }

        return (int)tile[Math.Clamp(row, 0, HeightTile.Intervals) * HeightTile.Samples + Math.Clamp(column, 0, HeightTile.Intervals)];
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var values = new float[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) values[i] = bytes[i];
        return values;
    }

    private static (float R, float G, float B) KindColour(byte[] atlas, int kind, double latitude, double longitude, float shade)
    {
        double x = (longitude + 180.0) / 360.0 * ColourTable.AtlasWidth - 0.5;
        double y = Math.Clamp((90.0 - latitude) / 180.0 * ColourTable.AtlasHeight - 0.5, 0, ColourTable.AtlasHeight - 1.001);
        int x0 = (int)Math.Floor(x), y0 = (int)y;
        float fx = (float)(x - x0), fy = (float)(y - y0);
        float Channel(int ch)
        {
            float At(int px, int py)
            {
                px = ((px % ColourTable.AtlasWidth) + ColourTable.AtlasWidth) % ColourTable.AtlasWidth;
                return atlas[((kind * ColourTable.AtlasHeight + py) * ColourTable.AtlasWidth + px) * 3 + ch] / 255f;
            }

            return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
        }

        return (Channel(0) * shade, Channel(1) * shade, Channel(2) * shade);
    }

    /// <summary>An sRGB channel times a detail multiplier stored as value / 128, in linear light.</summary>
    private static float Detailed(float srgb, float stored)
    {
        double linear = srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        linear = Math.Clamp(linear * stored * 255.0 / 128.0, 0, 1);
        return (float)(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055);
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte[] DecodeJpeg(byte[] file) => Jpeg.Decode(file, out _, out _);

    private static byte ToByte(float v) => (byte)Math.Clamp(Math.Round(v * 255f), 0, 255);

    public static (float R, float G, float B) WaterColour(float tone)
    {
        if (tone > 0.998f) return Lake;
        float toShelf = SmoothStep(0.02f, 0.2f, tone), toDeep = SmoothStep(0.2f, 0.6f, tone);
        return Mix(Mix(Shallow, Shelf, toShelf), Deep, toDeep);
    }

    private static (float R, float G, float B) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static object Load(Layer layer, TileId id, Func<TileId, object> load)
    {
        if (!layer.Loaded.TryGetValue(id, out object loaded)) layer.Loaded[id] = loaded = load(id);
        return loaded;
    }

    /// <summary>Channels 0-1 at a place, from the deepest tile that covers it, blended bilinearly.</summary>
    private static float[] SampleSurface(Layer layer, double latitude, double longitude, int channels, int maxLevel)
    {
        TileId id = Deepest(layer.Packs, latitude, longitude, maxLevel);
        if (!layer.Packs.Has(id)) return new float[channels];
        var tile = (SurfaceTile)Load(layer, id, t => SurfaceTile.Decode(File.ReadAllBytes(layer.Packs.PathOf(t)), DecodeJpeg));
        double column = (longitude - id.West) / id.SizeDeg * HeightTile.Intervals;
        double row = (latitude - id.South) / id.SizeDeg * HeightTile.Intervals;
        int c0 = Math.Clamp((int)column, 0, HeightTile.Intervals - 1), r0 = Math.Clamp((int)row, 0, HeightTile.Intervals - 1);
        float fc = (float)Math.Clamp(column - c0, 0, 1), fr = (float)Math.Clamp(row - r0, 0, 1);
        var result = new float[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            int at = ch * HeightTile.SampleCount + r0 * HeightTile.Samples + c0;
            float s = tile.Data[at] + (tile.Data[at + 1] - tile.Data[at]) * fc;
            float n = tile.Data[at + HeightTile.Samples] + (tile.Data[at + HeightTile.Samples + 1] - tile.Data[at + HeightTile.Samples]) * fc;
            result[ch] = (s + (n - s) * fr) / 255f;
        }

        return result;
    }

    private static float Height(Layer layer, double latitude, double longitude)
    {
        TileId id = Deepest(layer.Packs, latitude, longitude, 99);
        var tile = (HeightTile)Load(layer, id, t => layer.Packs.Load(t));
        return tile.SampleBilinear((longitude - id.West) / id.SizeDeg * HeightTile.Intervals,
            (latitude - id.South) / id.SizeDeg * HeightTile.Intervals);
    }

    /// <summary>Slope shading as the game draws it: lit from the north-west, slopes steepened three times.</summary>
    private static float Shade(Layer heights, double latitude, double longitude, double stepDeg, double stepM)
    {
        double eastM = stepM * Math.Max(Math.Cos(latitude * Math.PI / 180.0), 0.05);
        float west = Height(heights, latitude, longitude - stepDeg), east = Height(heights, latitude, longitude + stepDeg);
        float southH = Height(heights, latitude - stepDeg, longitude), northH = Height(heights, latitude + stepDeg, longitude);
        double sx = (east - west) / (2 * eastM) * 3.0, sy = (northH - southH) / (2 * stepM) * 3.0;
        double length = Math.Sqrt(sx * sx + sy * sy + 1);
        double nx = -sx / length, ny = -sy / length, nz = 1 / length;
        const double lx = -0.5, ly = 0.5, lz = 0.70710678;
        return (float)((nx * lx + ny * ly + nz * lz) / lz);
    }

    /// <summary>The deepest tile that a pack holds at a place, no deeper than maxLevel. May not exist if the pack holds nothing there.</summary>
    private static TileId Deepest(TerrainPackSet packs, double latitude, double longitude, int maxLevel)
    {
        longitude = ((longitude + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        latitude = Math.Clamp(latitude, -89.9999, 89.9999);
        var id = new TileId(0, longitude < 0 ? 0 : 1, 0);
        TileId found = id;
        while (id.Level < maxLevel)
        {
            int childX = longitude >= id.West + id.SizeDeg * 0.5 ? 1 : 0;
            int childY = latitude >= id.South + id.SizeDeg * 0.5 ? 1 : 0;
            id = id.Child(childX, childY);
            if (packs.Has(id)) found = id;
            else if (packs.MaxDetailLevelFor(id) <= id.Level) break;
        }

        return found;
    }
}
