using System;
using System.Collections.Generic;
using System.IO;

namespace Tns.Core.Terrain;

/// <summary>
/// The terrain the game is running with: an ordered list of packs. A pack is a folder of tile
/// files laid out as level/x/y.tnsh (or .tnsc, .tnsw for surface packs). Later packs win, so a
/// mod replaces a tile simply by containing a file with the same level/x/y.
/// </summary>
public sealed class TerrainPackSet
{
    /// <summary>The file extension of this kind of tile, such as ".tnsh".</summary>
    public string Extension { get; }

    public TerrainPackSet(string extension = HeightTile.FileExtension) => Extension = extension;

    private readonly List<string> _roots = new();
    // Which pack supplies each tile. Later packs overwrite earlier entries.
    private readonly Dictionary<TileId, int> _owner = new();
    private readonly Dictionary<TileId, int> _branchLevels = new();

    public IReadOnlyList<string> Roots => _roots;
    public int TileCount => _owner.Count;
    public int MaxLevel { get; private set; } = -1;

    /// <summary>
    /// Adds a pack on top of the ones already added, by scanning its folder for tiles.
    /// Returns how many tiles it holds.
    /// </summary>
    public int AddPack(string root)
    {
        int packIndex = _roots.Count;
        _roots.Add(root);
        int found = 0;
        if (!Directory.Exists(root)) return 0;

        foreach (string levelFolder in Directory.EnumerateDirectories(root))
        {
            if (!int.TryParse(Path.GetFileName(levelFolder), out int level)) continue;
            foreach (string columnFolder in Directory.EnumerateDirectories(levelFolder))
            {
                if (!int.TryParse(Path.GetFileName(columnFolder), out int x)) continue;
                foreach (string file in Directory.EnumerateFiles(columnFolder, "*" + Extension))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int y)) continue;
                    var id = new TileId(level, x, y);
                    if (!id.IsValid) continue;
                    _owner[id] = packIndex;
                    for (TileId ancestor = id; ancestor.Level >= 0; ancestor = ancestor.Parent)
                    {
                        if (_branchLevels.TryGetValue(ancestor, out int deepest) && deepest >= id.Level) break;
                        _branchLevels[ancestor] = id.Level;
                    }
                    if (level > MaxLevel) MaxLevel = level;
                    found++;
                }
            }
        }

        return found;
    }

    public bool Has(TileId id) => _owner.ContainsKey(id);

    /// <summary>Deepest stored detail on this branch, including a coarser tile used as fallback.</summary>
    public int MaxDetailLevelFor(TileId id)
    {
        if (_branchLevels.TryGetValue(id, out int level)) return level;
        while (id.Level > 0)
        {
            id = id.Parent;
            if (Has(id)) return id.Level;
        }

        return -1;
    }

    /// <summary>Full path of the file that supplies a tile, or null if no pack has it.</summary>
    public string PathOf(TileId id) =>
        _owner.TryGetValue(id, out int pack) ? Path.Combine(_roots[pack], id.PathWith(Extension)) : null;

    public SurfaceTile LoadSurface(TileId id, Func<byte[], byte[]> decodeColour = null)
    {
        string path = PathOf(id);
        return path == null ? null : SurfaceTile.Decode(File.ReadAllBytes(path), decodeColour);
    }

    public HeightTile Load(TileId id)
    {
        string path = PathOf(id);
        return path == null ? null : HeightTile.Decode(File.ReadAllBytes(path));
    }

    public IEnumerable<TileId> AllTiles => _owner.Keys;
}
