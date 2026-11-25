using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// A bake recipe: which elevation sources to stack and how to turn them into a terrain pack.
/// Stored as JSON (comments allowed). Paths are relative to the recipe file.
/// </summary>
public sealed class Recipe
{
    /// <summary>Shown in the pack's pack.json.</summary>
    public string Name { get; set; } = "Terrain";

    /// <summary>Folder the tiles are written to.</summary>
    public string Output { get; set; }

    /// <summary>Elevation sources from bottom to top: later entries cover earlier ones.</summary>
    public List<SourceSettings> Sources { get; set; } = new();

    /// <summary>Deepest tile level to bake. Leave out to use the full detail of the sources.</summary>
    public int? MaxLevel { get; set; }

    /// <summary>Levels 0 to this are always complete, so the whole world is covered.</summary>
    public int FullLevel { get; set; } = 3;

    /// <summary>
    /// Anything lower than this is raised to it. 0 flattens the sea bed to sea level, which
    /// reduces the pack size. Set to null to preserve negative heights.
    /// </summary>
    public double? MinElevationM { get; set; } = 0.0;

    /// <summary>Height used where no source has data.</summary>
    public double FillElevationM { get; set; } = 0.0;

    /// <summary>
    /// How much detail to keep. A tile is stored only when it would visibly change the slope
    /// shading: when the ground's slope differs from the tile above it by more than this
    /// (rise over run, so 0.06 is 6 m in 100 m) over more than DetailAreaKm2 of ground.
    /// Larger values give a smaller pack with softer mountains. 0 keeps everything.
    /// </summary>
    public double DetailSlope { get; set; } = 0.06;

    /// <summary>
    /// How much ground must show that slope change before a tile is stored, in square km. The
    /// same area counts at every level, so a fine tile needs most of its ground to be rugged
    /// while a coarse tile, which covers far more, is kept for a single range of hills.
    /// </summary>
    public double DetailAreaKm2 { get; set; } = 800.0;

    /// <summary>Boxes where the detail is set by hand instead: capped, or always kept.</summary>
    public List<DetailArea> DetailAreas { get; set; } = new();

    /// <summary>
    /// A file of 1 x 1 degree squares (as for a source's "squares") where every tile is kept down to
    /// KeepSquaresLevel, however gentle: the slope shading shows dunes and low relief that the
    /// detail rule would drop.
    /// </summary>
    public string KeepSquares { get; set; }
    public int KeepSquaresLevel { get; set; } = 9;

    /// <summary>Heights are rounded to this many metres. Coarser rounding compresses better.</summary>
    public double HeightStepM { get; set; } = 1.0;

    /// <summary>
    /// Only rebuild tiles touching this box: [west, south, east, north]. Used for mods that
    /// change one region; tiles outside it are left out of the pack.
    /// </summary>
    public double[] OnlyBounds { get; set; }

    [JsonIgnore] public string FilePath { get; private set; }

    public static Recipe Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        Recipe recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("The recipe file is empty.");
        recipe.FilePath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(recipe.Output)) throw new InvalidDataException("The recipe needs an \"output\" folder.");
        if (recipe.Sources.Count == 0) throw new InvalidDataException("The recipe needs at least one entry in \"sources\".");
        return recipe;
    }

    public string Resolve(string path) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath)!, path));
}

/// <summary>A box where the recipe's author overrides the automatic choice of detail.</summary>
public sealed record DetailArea
{
    /// <summary>[west, south, east, north] in degrees.</summary>
    public double[] Bounds { get; set; }

    /// <summary>No tile deeper than this level is stored inside the box.</summary>
    public int? MaxLevel { get; set; }

    /// <summary>Every tile down to this level is stored inside the box, however flat.</summary>
    public int? MinLevel { get; set; }

    /// <summary>A note for people reading the recipe.</summary>
    public string Name { get; set; }
}

/// <summary>Decides which tiles are worth storing. Shared by the baker and the size predictor.</summary>
public sealed class DetailRule
{
    private readonly Recipe _recipe;
    private readonly HashSet<(int Lat, int Lon)> _keep;

    public DetailRule(Recipe recipe)
    {
        _recipe = recipe;
        if (!string.IsNullOrWhiteSpace(recipe.KeepSquares) && recipe.FilePath != null)
        {
            _keep = SquareLimitedSource.ReadSquares(recipe.Resolve(recipe.KeepSquares));
        }
        foreach (DetailArea area in recipe.DetailAreas)
        {
            if (area.Bounds is not { Length: 4 }) throw new InvalidDataException("A detail area needs \"bounds\": [west, south, east, north].");
        }
    }

    /// <summary>True if a detail area caps the level above this tile, so it is never stored.</summary>
    public bool Capped(TileId id)
    {
        foreach (DetailArea area in _recipe.DetailAreas)
        {
            if (area.MaxLevel is not { } cap || id.Level <= cap) continue;
            double[] box = area.Bounds;
            if (id.West >= box[0] && id.East <= box[2] && id.South >= box[1] && id.North <= box[3]) return true;
        }

        return false;
    }

    /// <summary>True if a detail area says this tile is stored whatever it looks like.</summary>
    public bool Forced(TileId id)
    {
        if (id.Level <= _recipe.FullLevel) return true;
        if (_keep != null && id.Level <= _recipe.KeepSquaresLevel && TouchesKept(id)) return true;
        foreach (DetailArea area in _recipe.DetailAreas)
        {
            if (area.MinLevel is not { } floor || id.Level > floor) continue;
            double[] box = area.Bounds;
            if (id.East > box[0] && id.West < box[2] && id.North > box[1] && id.South < box[3]) return true;
        }

        return false;
    }

    private bool TouchesKept(TileId tile)
    {
        int lat0 = (int)Math.Floor(tile.South), lat1 = (int)Math.Ceiling(tile.North) - 1;
        int lon0 = (int)Math.Floor(tile.West), lon1 = (int)Math.Ceiling(tile.East) - 1;
        if ((long)(lat1 - lat0 + 1) * (lon1 - lon0 + 1) > _keep.Count) return _keep.Any(s => s.Lat >= lat0 && s.Lat <= lat1 && s.Lon >= lon0 && s.Lon <= lon1);
        for (int lat = lat0; lat <= lat1; lat++)
        {
            for (int lon = lon0; lon <= lon1; lon++)
            {
                if (_keep.Contains((lat, lon))) return true;
            }
        }

        return false;
    }

    /// <summary>True if enough ground changes the shading. <paramref name="fractionOver"/> is the
    /// share of the tile whose slope differs from the tile above by more than DetailSlope.</summary>
    public bool Visible(TileId id, double fractionOver) =>
        _recipe.DetailSlope <= 0.0 ? fractionOver > 0.0 : fractionOver * AreaKm2(id) > _recipe.DetailAreaKm2;

    /// <summary>Ground a tile covers. Tiles get narrower toward the poles.</summary>
    public static double AreaKm2(TileId id)
    {
        double sideKm = id.SizeDeg * 111.195;
        return sideKm * sideKm * Math.Cos((id.South + id.SizeDeg * 0.5) * Math.PI / 180.0);
    }
}

public sealed record SourceSettings
{
    /// <summary>A GeoTIFF file, or a folder of GeoTIFFs.</summary>
    public string Path { get; set; }

    /// <summary>An existing terrain pack folder to build on, instead of a path.</summary>
    public string Pack { get; set; }

    /// <summary>[west, south, east, north] in degrees, for images with no georeference.</summary>
    public double[] Bounds { get; set; }

    /// <summary>Height in metres = pixel value x valueScale + valueOffset.</summary>
    public double ValueScale { get; set; } = 1.0;
    public double ValueOffset { get; set; }

    /// <summary>
    /// Deepest tile level to take from this source, even if it has finer detail. Lets a
    /// whole-world source stay coarse while regional sources go deeper.
    /// </summary>
    public int? MaxLevel { get; set; }

    /// <summary>Pixel value that means "no data here". Read from the file when left out.</summary>
    public double? NoData { get; set; }

    /// <summary>
    /// A file listing 1 x 1 degree squares ("lat,lon" of the south-west corner, or names like
    /// N24_00_W003_00); the source is only used inside them.
    /// </summary>
    public string Squares { get; set; }
}
