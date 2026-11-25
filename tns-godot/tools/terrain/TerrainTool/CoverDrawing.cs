using System;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// How the game draws land cover up close, repeated here so the baker and the preview agree with
/// the shader (shaders/map/map_terrain.gdshader, cover_pick).
///
/// Kinds of ground are names, not amounts, so they cannot be blended like colours. At any point
/// the four samples round it each vote for their kind, weighted by how close they are, and the
/// kind with most votes wins. Edges between kinds come out as smooth, sharp lines through the
/// samples instead of blurred stripes or square blocks. Near an edge the two leading kinds are
/// blended across about a pixel, so edges do not shimmer.
///
/// Water is left out of the vote: coasts come from the water tiles, which place them more
/// precisely, so the land cover only decides what kind of land meets the water.
/// </summary>
public static class CoverDrawing
{
    /// <summary>
    /// Smooth weights for the four samples at offsets -1, 0, 1, 2 from a point a fraction f past
    /// sample 0 (a cubic B-spline). They fall off gently, so edges between kinds come out as
    /// smooth curves instead of following the sample grid, and a lone sample of one kind inside
    /// another fades away instead of showing as a square.
    /// </summary>
    public static void SmoothWeights(float f, Span<float> w)
    {
        float f2 = f * f, f3 = f2 * f;
        w[0] = (1 - f) * (1 - f) * (1 - f) / 6f;
        w[1] = (3 * f3 - 6 * f2 + 4) / 6f;
        w[2] = (-3 * f3 + 3 * f2 + 3 * f + 1) / 6f;
        w[3] = f3 / 6f;
    }

    /// <summary>
    /// The smooth vote over the 4 x 4 samples round a point. <paramref name="kindAt"/> gives the
    /// kind at a sample (column, row), which may lie one outside the tile. Water does not vote.
    /// </summary>
    /// <summary>Each kind's share of the smooth 4 x 4 vote round a point (water left out).</summary>
    public static void SmoothVotes(Func<int, int, int> kindAt, double column, double row, Span<float> votes)
    {
        int c0 = (int)Math.Floor(column), r0 = (int)Math.Floor(row);
        Span<float> wx = stackalloc float[4];
        Span<float> wy = stackalloc float[4];
        SmoothWeights((float)(column - c0), wx);
        SmoothWeights((float)(row - r0), wy);
        votes.Clear();
        for (int j = 0; j < 4; j++)
        {
            for (int i = 0; i < 4; i++)
            {
                int kind = kindAt(c0 - 1 + i, r0 - 1 + j);
                if (kind >= 0 && kind < Cover.Count && kind != Cover.Water) votes[kind] += wx[i] * wy[j];
            }
        }
    }

    public static (int Best, int Second, float Lead) SmoothVote(Func<int, int, int> kindAt, double column, double row)
    {
        Span<float> votes = stackalloc float[Cover.Count];
        SmoothVotes(kindAt, column, row, votes);

        int best = -1, second = -1;
        for (int k = 0; k < Cover.Count; k++)
        {
            if (votes[k] <= 0f) continue;
            if (best < 0 || votes[k] > votes[best])
            {
                second = best;
                best = k;
            }
            else if (second < 0 || votes[k] > votes[second])
            {
                second = k;
            }
        }

        if (best < 0) return (-1, -1, 0f);
        if (second < 0) return (best, best, 1f);
        // As a share of the land votes, so the lead means the same next to water.
        float land = 0f;
        foreach (float v in votes) land += v;
        return (best, second, (votes[best] - votes[second]) / land);
    }

    /// <summary>The winning kind of land at a position in sample units (column, row), or -1 if only water is near.</summary>
    public static int Pick(float[] kinds, double column, double row)
    {
        var (best, _, _) = Vote(kinds, column, row, Cover.Water);
        return best;
    }

    /// <summary>
    /// The two leading kinds and how far the first leads (0 = tied, 1 = unopposed). Votes for
    /// <paramref name="ignore"/> do not count; Best is -1 if no other kind is near.
    /// </summary>
    public static (int Best, int Second, float Lead) Vote(float[] kinds, double column, double row, int ignore = -1)
    {
        const int n = HeightTile.Samples;
        column = Math.Clamp(column, 0.0, HeightTile.Intervals);
        row = Math.Clamp(row, 0.0, HeightTile.Intervals);
        int c0 = Math.Min((int)column, HeightTile.Intervals - 1), r0 = Math.Min((int)row, HeightTile.Intervals - 1);
        float fx = (float)(column - c0), fy = (float)(row - r0);

        Span<int> kind = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        kind[0] = (int)kinds[r0 * n + c0];
        kind[1] = (int)kinds[r0 * n + c0 + 1];
        kind[2] = (int)kinds[(r0 + 1) * n + c0];
        kind[3] = (int)kinds[(r0 + 1) * n + c0 + 1];
        weight[0] = (1 - fx) * (1 - fy);
        weight[1] = fx * (1 - fy);
        weight[2] = (1 - fx) * fy;
        weight[3] = fx * fy;

        for (int i = 0; i < 4; i++)
        {
            if (kind[i] == ignore) weight[i] = -1;
        }

        // Merge votes for the same kind.
        for (int i = 0; i < 4; i++)
        {
            if (weight[i] < 0) continue;
            for (int j = 0; j < i; j++)
            {
                if (kind[j] != kind[i] || weight[j] < 0) continue;
                weight[j] += weight[i];
                weight[i] = -1;
                break;
            }
        }

        int best = -1, second = -1;
        for (int i = 0; i < 4; i++)
        {
            if (weight[i] < 0) continue;
            if (best < 0 || weight[i] > weight[best])
            {
                second = best;
                best = i;
            }
            else if (second < 0 || weight[i] > weight[second])
            {
                second = i;
            }
        }

        if (best < 0) return (-1, -1, 0f);
        float lead = second < 0 ? 1f : weight[best] - weight[second];
        return (kind[best], second < 0 ? kind[best] : kind[second], lead);
    }
}
