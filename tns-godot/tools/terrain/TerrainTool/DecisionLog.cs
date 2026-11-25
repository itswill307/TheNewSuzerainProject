using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Why a bake kept or dropped each tile it looked at: the measured change against the tile above
/// (how much ground would look different if the tile were left out), the limit it was compared
/// with, and the outcome. Saved beside the pack as decisions.csv and drawn by "terrain decisions".
///
/// Columns: level, x, y, changed_km2, limit_km2, kept (0/1), why:
///   m  the measure decided (kept when changed_km2 is over limit_km2)
///   b  kept because a finer tile below it was kept (the game needs every tile above a kept one)
///   f  kept whatever it measured: a level that is always complete, a keepSquares square, a
///      detail area with minLevel, or (land cover) the first cover level wherever there is land
///   c  never stored: a detail area caps the level
/// changed_km2 is -1 where nothing was measured.
/// </summary>
public sealed class DecisionLog
{
    public const string FileName = "decisions.csv";

    private readonly string _path;
    private readonly ConcurrentDictionary<TileId, string> _rows = new();

    /// <param name="keepExisting">Start from the rows already saved (an update bake rewrites only its boxes).</param>
    public DecisionLog(string packFolder, bool keepExisting)
    {
        _path = Path.Combine(packFolder, FileName);
        if (!keepExisting || !File.Exists(_path)) return;
        foreach (string line in File.ReadLines(_path).Skip(1))
        {
            string[] parts = line.Split(',', 4);
            if (parts.Length < 4) continue;
            var id = new TileId(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            _rows[id] = parts[3];
        }
    }

    public void Add(TileId id, double changedKm2, double limitKm2, bool kept, char why)
    {
        _rows[id] = string.Create(CultureInfo.InvariantCulture,
            $"{(changedKm2 < 0 ? -1 : Math.Round(changedKm2, 4))},{Math.Round(limitKm2, 4)},{(kept ? 1 : 0)},{why}");
    }

    public void Save()
    {
        var text = new StringBuilder("level,x,y,changed_km2,limit_km2,kept,why\n");
        foreach (var (id, row) in _rows.OrderBy(r => r.Key.Level).ThenBy(r => r.Key.Y).ThenBy(r => r.Key.X))
        {
            text.Append(id.Level).Append(',').Append(id.X).Append(',').Append(id.Y).Append(',').Append(row).Append('\n');
        }

        File.WriteAllText(_path, text.ToString());
    }

    public sealed record Row(TileId Id, double ChangedKm2, double LimitKm2, bool Kept, char Why);

    public static List<Row> Read(string path)
    {
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] p = line.Split(',');
            if (p.Length < 7) continue;
            rows.Add(new Row(new TileId(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])),
                double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture),
                p[5] == "1", p[6].Length > 0 ? p[6][0] : '?'));
        }

        return rows;
    }
}
