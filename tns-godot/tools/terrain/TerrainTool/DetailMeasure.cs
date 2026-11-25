using System;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Measures how much a tile changes the picture compared with drawing the same ground from the
/// tile above it.
///
/// At true scale the shape of the ground barely moves on screen when a finer tile arrives: even a
/// 100 m height error is under a pixel at the closest zoom. What the eye picks up is the slope
/// shading. So the measure is the change in ground slope, taken the same way the game's shader
/// takes it: from the samples either side of each point, in the tile being drawn.
/// </summary>
public static class DetailMeasure
{
    private const int N = HeightTile.Samples;
    private const int Fine = HeightTile.Intervals * 2 + 1;
    private const double MetresPerDegree = 111_195.0;

    /// <summary>
    /// A tile's heights at twice the resolution, as the game shows them when it draws the four
    /// quarters of the tile in place of the missing tiles below. 513 x 513, row 0 south.
    /// </summary>
    public static float[] Upsample(float[] parent)
    {
        var fine = new float[Fine * Fine];
        for (int row = 0; row < N; row++)
        {
            for (int column = 0; column < N; column++) fine[row * 2 * Fine + column * 2] = parent[row * N + column];
        }

        for (int row = 0; row < Fine; row += 2)
        {
            for (int column = 1; column < Fine; column += 2)
            {
                fine[row * Fine + column] = 0.5f * (fine[row * Fine + column - 1] + fine[row * Fine + column + 1]);
            }
        }

        for (int row = 1; row < Fine; row += 2)
        {
            for (int column = 0; column < Fine; column++)
            {
                fine[row * Fine + column] = 0.5f * (fine[(row - 1) * Fine + column] + fine[(row + 1) * Fine + column]);
            }
        }

        return fine;
    }

    /// <summary>
    /// For each slope in <paramref name="slopes"/>, the fraction of the tile where the ground's
    /// slope differs from the tile above by more than that. Slopes are rise over run: 0.03 is a
    /// change of 3 m in 100 m. Returns the typical (root mean square) slope change.
    /// </summary>
    public static double SlopeChange(
        float[] parentUpsampled, float[] child, TileId childId, ReadOnlySpan<float> slopes, Span<double> fractionOver)
    {
        int offsetX = (childId.X & 1) * HeightTile.Intervals;
        int offsetY = (childId.Y & 1) * HeightTile.Intervals;
        double metresNorth = childId.SizeDeg / HeightTile.Intervals * MetresPerDegree;
        Span<int> counts = stackalloc int[slopes.Length];
        Span<float> squared = stackalloc float[slopes.Length];
        for (int i = 0; i < slopes.Length; i++) squared[i] = slopes[i] * slopes[i];
        double sum = 0.0;

        for (int row = 0; row < N; row++)
        {
            double latitude = (childId.South + childId.SizeDeg * row / HeightTile.Intervals) * Math.PI / 180.0;
            double metresEast = metresNorth * Math.Max(Math.Cos(latitude), 0.05);
            int south = Math.Max(row - 1, 0), north = Math.Min(row + 1, N - 1);
            // The tile above is read one of its own samples either side, which is two of ours.
            int fineRow = offsetY + row;
            int fineSouth = Math.Max(fineRow - 2, 0), fineNorth = Math.Min(fineRow + 2, Fine - 1);
            float perNorth = (float)(1.0 / ((north - south) * metresNorth));
            float finePerNorth = (float)(1.0 / ((fineNorth - fineSouth) * metresNorth));

            for (int column = 0; column < N; column++)
            {
                int west = Math.Max(column - 1, 0), east = Math.Min(column + 1, N - 1);
                int fineColumn = offsetX + column;
                int fineWest = Math.Max(fineColumn - 2, 0), fineEast = Math.Min(fineColumn + 2, Fine - 1);

                float ownX = (child[row * N + east] - child[row * N + west]) / (float)((east - west) * metresEast);
                float ownY = (child[north * N + column] - child[south * N + column]) * perNorth;
                float aboveX = (parentUpsampled[fineRow * Fine + fineEast] - parentUpsampled[fineRow * Fine + fineWest])
                               / (float)((fineEast - fineWest) * metresEast);
                float aboveY = (parentUpsampled[fineNorth * Fine + fineColumn] - parentUpsampled[fineSouth * Fine + fineColumn])
                               * finePerNorth;

                float dx = ownX - aboveX, dy = ownY - aboveY;
                float change = dx * dx + dy * dy;
                sum += change;
                for (int i = 0; i < squared.Length; i++)
                {
                    if (change > squared[i]) counts[i]++;
                }
            }
        }

        for (int i = 0; i < slopes.Length; i++) fractionOver[i] = counts[i] / (double)HeightTile.SampleCount;
        return Math.Sqrt(sum / HeightTile.SampleCount);
    }

    /// <summary>Fraction of the tile where the slope changes by more than <paramref name="slope"/>.</summary>
    public static double FractionOver(float[] parentUpsampled, float[] child, TileId childId, double slope)
    {
        Span<float> slopes = stackalloc float[] { (float)slope };
        Span<double> fraction = stackalloc double[1];
        SlopeChange(parentUpsampled, child, childId, slopes, fraction);
        return fraction[0];
    }
}
