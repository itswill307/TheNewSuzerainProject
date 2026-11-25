using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Works out where finer elevation data is worth having. The world is split into 1 x 1 degree
/// squares, the size high-resolution elevation data is published in. Each square is scored by how
/// steep its ground is on average, and the steepest squares are listed, up to a chosen number.
/// Plains and open sea score low and keep the coarse data.
/// </summary>
public static class RuggedCells
{
    // Rough sizes per square, measured on mountain squares: the download, and the baked tiles
    // at one sample every 150 m.
    private const double DownloadMbPerSquare = 4.5;
    private const double BakedMbPerSquare = 0.65;

    public static int Run(string packFolder, string outputPath, int maxSquares, double minLatitude, int? levelOption)
    {
        var source = new PackElevationSource(packFolder);
        int level = Math.Min(levelOption ?? 5, source.NativeLevel);
        var slopeSum = new double[180, 360];
        var slopeCount = new int[180, 360];
        var highest = new float[180, 360];

        // Read the whole world at one level. For every pair of neighbouring samples, add the
        // slope between them (metres of climb per km) to the square they fall in.
        double step = 180.0 / (1 << level) / HeightTile.Intervals;
        double stepKm = step * 111.32;
        var heights = new float[HeightTile.SampleCount];
        const int n = HeightTile.Samples;
        for (int y = 0; y < TileId.RowsAt(level); y++)
        {
            for (int x = 0; x < TileId.ColumnsAt(level); x++)
            {
                var tile = new TileId(level, x, y);
                Array.Fill(heights, float.NaN);
                source.Fill(tile, heights);
                for (int row = 0; row < n - 1; row++)
                {
                    double lat = tile.South + row * step;
                    int cellLat = Math.Clamp((int)Math.Floor(lat + 90.0), 0, 179);
                    double eastKm = stepKm * Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0));
                    for (int column = 0; column < n - 1; column++)
                    {
                        int i = row * n + column;
                        float h = heights[i];
                        if (float.IsNaN(h)) continue;
                        int cellLon = Math.Clamp((int)Math.Floor(tile.West + column * step + 180.0), 0, 359);
                        slopeSum[cellLat, cellLon] += Math.Abs(heights[i + 1] - h) / eastKm + Math.Abs(heights[i + n] - h) / stepKm;
                        slopeCount[cellLat, cellLon] += 2;
                        if (h > highest[cellLat, cellLon]) highest[cellLat, cellLon] = h;
                    }
                }
            }
        }

        var scored = new List<(double Slope, int Lat, int Lon)>();
        int landSquares = 0;
        for (int lat = 0; lat < 180; lat++)
        {
            for (int lon = 0; lon < 360; lon++)
            {
                if (highest[lat, lon] <= 0f || slopeCount[lat, lon] == 0) continue;
                landSquares++;
                if (lat - 90 < minLatitude) continue;
                scored.Add((slopeSum[lat, lon] / slopeCount[lat, lon], lat - 90, lon - 180));
            }
        }

        scored.Sort((a, b) => b.Slope.CompareTo(a.Slope));
        int take = Math.Min(maxSquares, scored.Count);
        File.WriteAllLines(outputPath, scored.Take(take).Select(s => CellName(s.Lat, s.Lon)));

        Console.WriteLine($"{landSquares:#,0} of 64,800 squares contain land; {scored.Count:#,0} of those are north of {minLatitude}°.");
        Console.WriteLine("If the steepest squares get finer data:");
        Console.WriteLine("  squares   gentlest slope included   download   baked at 150 m");
        foreach (int count in new[] { 500, 1000, 1500, 2000, 3000, 4000, 6000 })
        {
            if (count > scored.Count) break;
            Console.WriteLine($"  {count,7:#,0}   {scored[count - 1].Slope,10:0.0} m per km        {count * DownloadMbPerSquare / 1000.0,5:0.0} GB   {count * BakedMbPerSquare / 1000.0,7:0.0} GB");
        }

        Console.WriteLine($"Wrote the {take:#,0} steepest squares to {outputPath}");
        Console.WriteLine("Slopes are measured from the pack given, so a coarse pack understates them; the order is what matters.");
        return 0;
    }

    /// <summary>Copernicus names a square by its south-west corner, for example N46_00_E010_00.</summary>
    public static string CellName(int southLat, int westLon)
    {
        string lat = (southLat >= 0 ? "N" : "S") + Math.Abs(southLat).ToString("00", CultureInfo.InvariantCulture);
        string lon = (westLon >= 0 ? "E" : "W") + Math.Abs(westLon).ToString("000", CultureInfo.InvariantCulture);
        return $"{lat}_00_{lon}_00";
    }
}
