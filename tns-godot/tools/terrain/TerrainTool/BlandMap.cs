using System;
using System.Collections.Generic;
using System.Linq;

namespace Tns.Tools.Terrain;

/// <summary>Draws the bland survey (BlandSurvey) as a world map, and sums up what fixing it would cost.</summary>
public static class BlandMap
{
    public sealed record Settings
    {
        /// <summary>A square is bland when at least this share of it is smooth at a 2 km scale.</summary>
        public double Bland { get; init; } = 0.7;
        /// <summary>Dune relief: ETOPO ripple of at least this many metres on open ground.</summary>
        public double DuneRippleM { get; init; } = 1.5;
        /// <summary>Fine colour: drawn from the 500 m picture, at least this much less of the square would be smooth.</summary>
        public double ColourGain { get; init; } = 0.2;
        /// <summary>When above 0 and the survey counted patches: a square is bland when at least this many of its patches are, and the sharper picture fixes it when it fixes this many.</summary>
        public int MinPatches { get; init; }
    }

    public enum Fix { NotBland, Dunes, Colour, Both, NoGain, Ice, NotMeasured }

    public static Fix Classify(BlandSurvey.Cell c, Settings s)
    {
        bool count = s.MinPatches > 0 && c.Patches > 1;
        if (count ? c.BlandPatches < s.MinPatches : c.Smooth < s.Bland) return Fix.NotBland;
        if (c.Kind == Cover.Snow) return Fix.Ice;
        bool open = c.Kind is Cover.Bare or Cover.Shrub or Cover.Grass or Cover.Moss;
        bool dunes = open && !c.HasCopernicus && c.EtopoRippleM >= s.DuneRippleM;
        bool measured = !double.IsNaN(c.SharpSmooth);
        bool colour = count ? c.ColourPatches >= s.MinPatches : measured && c.SharpSmooth <= c.Smooth - s.ColourGain;
        if (dunes && colour) return Fix.Both;
        if (dunes) return Fix.Dunes;
        if (colour) return Fix.Colour;
        return measured ? Fix.NoGain : Fix.NotMeasured;
    }

    private static readonly Dictionary<Fix, (byte, byte, byte)> Colours = new()
    {
        [Fix.Dunes] = (242, 140, 40),
        [Fix.Colour] = (60, 150, 240),
        [Fix.Both] = (220, 70, 200),
        [Fix.NoGain] = (150, 150, 150),
        [Fix.Ice] = (235, 240, 250),
        [Fix.NotMeasured] = (140, 175, 205),
    };

    private static readonly Dictionary<Fix, string> Labels = new()
    {
        [Fix.Dunes] = "Bland, dune or rock relief hidden: 90 m heights would show it",
        [Fix.Colour] = "Bland, the 500 m picture has the missing fine colour",
        [Fix.Both] = "Bland, both would help",
        [Fix.NoGain] = "Bland, but the 500 m picture is just as smooth here",
        [Fix.Ice] = "Ice and snow: smooth in reality too",
        [Fix.NotMeasured] = "Bland, no dune relief; 500 m picture not downloaded here",
    };

    public static void Draw(string path, List<BlandSurvey.Cell> cells, Settings s, double[][] measuredBoxes, string title)
    {
        const int scale = 5, margin = 24, top = 44;
        int mapW = 360 * scale, mapH = 144 * scale; // 84 N to 60 S
        int legendH = Labels.Count * 26 + 60;
        var canvas = new Canvas(mapW + margin * 2, top + mapH + legendH, (20, 22, 28));
        (byte, byte, byte) ink = (226, 228, 235), faint = (150, 155, 168);
        canvas.Text(margin, 14, title, ink);
        canvas.FillRect(margin, top, mapW, mapH, (14, 17, 24));

        foreach (var c in cells)
        {
            Fix fix = Classify(c, s);
            (byte, byte, byte) colour;
            if (fix == Fix.NotBland)
            {
                // The square's own ground colour, dimmed.
                colour = (Dim(c.R), Dim(c.G), Dim(c.B));
            }
            else
            {
                colour = Colours[fix];
            }

            int x = margin + (c.Lon + 180) * scale, y = top + (83 - c.Lat) * scale;
            canvas.FillRect(x, y, scale, scale, colour);
        }

        // Outline where the 500 m picture was available to measure.
        foreach (double[] box in measuredBoxes)
        {
            int x0 = margin + (int)((box[0] + 180) * scale), x1 = margin + (int)((box[2] + 180) * scale);
            int y0 = top + (int)((84 - Math.Min(box[3], 84)) * scale), y1 = top + (int)((84 - Math.Max(box[1], -60)) * scale);
            for (int x = x0; x <= x1; x += 2)
            {
                canvas.Set(x, y0, ink);
                canvas.Set(x, y1 - 1, ink);
            }

            for (int y = y0; y <= y1; y += 2)
            {
                canvas.Set(x0, y, ink);
                canvas.Set(x1 - 1, y, ink);
            }
        }

        int row = top + mapH + 16;
        canvas.Text(margin, row, "One cell per 1 degree square of land, judged at the closest zoom. Dotted boxes: where the 500 m picture was measured.", faint);
        row += 26;
        foreach (var (fix, label) in Labels)
        {
            int count = cells.Count(c => Classify(c, s) == fix);
            canvas.FillRect(margin, row, 34, 16, Colours[fix]);
            canvas.Text(margin + 46, row + 1, $"{label}   {count:#,0} squares", ink);
            row += 26;
        }

        canvas.SavePng(path);
    }

    private static byte Dim(float linear) => (byte)(ColourTable.LinearToSrgb(linear) * 0.45);
}
