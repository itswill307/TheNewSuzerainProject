using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tns.Tools.Terrain;

/// <summary>
/// A surface recipe: how to paint the ground. Stored as JSON (comments allowed). Paths are
/// relative to the recipe file. See tools/terrain/README.md.
/// </summary>
public sealed class SurfaceRecipe
{
    public string Name { get; set; } = "Surface";

    /// <summary>Folder for the colour tiles (.tnsc).</summary>
    public string ColourOutput { get; set; }

    /// <summary>Folder for the water tiles (.tnsw).</summary>
    public string WaterOutput { get; set; }

    /// <summary>Folder for the land cover tiles (.tnsk) and the colour of each kind of ground.</summary>
    public string CoverOutput { get; set; }

    /// <summary>Folder for the detail tiles (.tnsr): the real colour of the ground relative to its kinds, see DetailBaker.</summary>
    public string DetailOutput { get; set; }

    /// <summary>
    /// A folder of sharper colour pictures for the detail, for parts of the world (see TiledPicture);
    /// the colour picture is used everywhere else. With 500 m pictures, set DetailMaxLevel to 8.
    /// </summary>
    public string DetailImages { get; set; }

    /// <summary>Deepest level of detail tiles. Level 6 is a sample every 1.2 km, about the colour picture's own resolution.</summary>
    public int DetailMaxLevel { get; set; } = 6;

    /// <summary>
    /// A file of 1 x 1 degree squares where the sharper pictures (DetailImages) are used, down to
    /// DetailFineMaxLevel. Without it, the sharper pictures are used everywhere at DetailMaxLevel.
    /// </summary>
    public string DetailFineSquares { get; set; }
    public int DetailFineMaxLevel { get; set; } = 8;

    /// <summary>
    /// A file of more squares that get the sharper detail, like DetailFineSquares: whole forest
    /// regions already partly listed, and gaps in the lists (make_fill_squares.ps1), so a uniform
    /// region does not change detail from one square to the next.
    /// </summary>
    public string DetailFillSquares { get; set; }

    /// <summary>A detail tile is stored where it changes the multiplier by more than this over more than DetailAreaKm2.</summary>
    public double DetailTolerance { get; set; } = 0.05;
    public double DetailAreaKm2 { get; set; } = 400;

    /// <summary>On slopes steeper than DetailSlopeStart (rise over run, at 1.2 km) the detail fades, and is gone by DetailSlopeEnd: there the picture shows its own shadows.</summary>
    public double DetailSlopeStart { get; set; } = 0.08;
    public double DetailSlopeEnd { get; set; } = 0.25;

    /// <summary>Folder of ESA WorldCover GeoTIFFs, all at the same resolution.</summary>
    public string LandCover { get; set; }

    /// <summary>
    /// Folder of land cover files in the same form, used only where LandCover has no data: north
    /// of about 82.8 N, which Sentinel-2 (and so WorldCover) never sees (make_arctic_cover.ps1).
    /// </summary>
    public string LandCoverFill { get; set; }

    /// <summary>The Blue Marble pictures hold no real data north of this latitude (they are plain white), so the detail fades out just south of it.</summary>
    public double PictureNorthLimit { get; set; } = 80.0;

    /// <summary>A colour picture of the whole world in plain latitude/longitude (PNG), to measure each class's colour from.</summary>
    public string ColourImage { get; set; }

    /// <summary>Where the measured colours are kept between bakes. Delete it, or bake with --recolour, to measure again.</summary>
    public string ColourCache { get; set; }

    /// <summary>Whole-world elevation, sea bed included, for the colour of the sea and for land with no land cover.</summary>
    public SourceSettings Elevation { get; set; }

    /// <summary>Deepest level of land cover tiles: how sharp coastlines, lakes, rivers, forests and fields get.</summary>
    public int MaxLevel { get; set; } = 9;

    /// <summary>
    /// The first level of land cover tiles. From here down the game draws each kind of ground
    /// with sharp edges; above it, from far away, it draws the blended colour and water tiles.
    /// </summary>
    public int CoverMinLevel { get; set; } = 7;

    /// <summary>A land cover tile is stored where it draws the kinds of land differently from the tile above over more than this much ground.</summary>
    public double CoverAreaKm2 { get; set; } = 20;

    /// <summary>Deepest level of land cover tiles in general.</summary>
    public int CoverMaxLevel { get; set; } = 8;

    /// <summary>
    /// A height pack. Where its heights are finer than CoverMaxLevel (the great mountain ranges),
    /// land cover goes as deep as they do, up to MaxLevel.
    /// </summary>
    public string CoverMatchHeights { get; set; }

    /// <summary>Height tiles this deep or coarser count as plains.</summary>
    public int CoverPlainsHeightLevel { get; set; } = 7;

    /// <summary>Outside the plains, land cover goes this many levels finer than the heights.</summary>
    public int CoverLevelsBeyondHeights { get; set; } = 1;

    /// <summary>Deepest level of the blended colour tiles. Normally one above CoverMinLevel.</summary>
    public int ColourMaxLevel { get; set; } = 6;

    /// <summary>
    /// A file of 1 x 1 degree squares of open ground that is not mountains (deserts, fields, grassland,
    /// shrub, tundra, wetland), where flat colour shows most: there the blended colour goes down to
    /// ColourOpenMaxLevel and the detail, from the sharper pictures, down to DetailFineMaxLevel, as
    /// in DetailFineSquares. Made by make_open_squares.ps1 from the bland survey.
    /// </summary>
    public string OpenSquares { get; set; }
    public int ColourOpenMaxLevel { get; set; } = 7;

    /// <summary>Deepest level of the water share tiles. Normally one above CoverMinLevel.</summary>
    public int WaterMaxLevel { get; set; } = 6;

    /// <summary>Levels 0 to this are always complete.</summary>
    public int FullLevel { get; set; } = 3;

    /// <summary>
    /// A colour tile is stored where it changes the land's colour by more than this (0-1 in each
    /// sRGB channel; 0.03 is about 8 of 255) over more than ColourAreaKm2 of ground.
    /// </summary>
    public double ColourTolerance { get; set; } = 0.03;
    public double ColourAreaKm2 { get; set; } = 150;

    /// <summary>JPEG quality of colour tiles, 1-100. 85 looks the same as lossless on the map at a tenth of the size.</summary>
    public int ColourQuality { get; set; } = 85;

    /// <summary>A water tile is stored where it moves the coast across more than this much ground.</summary>
    public double WaterAreaKm2 { get; set; } = 1.0;

    /// <summary>The water share is rounded to this many steps between land and water. 16 places the coast to within a sixteenth of a sample.</summary>
    public int WaterSteps { get; set; } = 16;

    /// <summary>Each class's colour is the average of the colour picture within about this distance.</summary>
    public double ColourRadiusKm { get; set; } = 30;

    /// <summary>Water whose surface is higher than this above the sea is a lake or river.</summary>
    public double LakeAboveM { get; set; } = 25;

    /// <summary>Sea this deep or deeper gets the darkest water tone.</summary>
    public double DeepestSeaM { get; set; } = 6000;

    /// <summary>
    /// Fixed colours for classes, replacing the measured ones everywhere: class name to sRGB 0-1,
    /// such as "built": [0.45, 0.43, 0.41]. Class names: tree, shrub, grass, crop, built, bare,
    /// snow, water, wetland, mangrove, moss.
    /// </summary>
    public Dictionary<string, double[]> ClassColours { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; }

    public static SurfaceRecipe Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        SurfaceRecipe recipe = JsonSerializer.Deserialize<SurfaceRecipe>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("The recipe file is empty.");
        recipe.FilePath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(recipe.ColourOutput) || string.IsNullOrWhiteSpace(recipe.WaterOutput) || string.IsNullOrWhiteSpace(recipe.CoverOutput)
            || string.IsNullOrWhiteSpace(recipe.DetailOutput))
        {
            throw new InvalidDataException("A surface recipe needs \"coverOutput\", \"colourOutput\", \"waterOutput\" and \"detailOutput\" folders.");
        }

        if (string.IsNullOrWhiteSpace(recipe.LandCover)) throw new InvalidDataException("A surface recipe needs a \"landCover\" folder.");
        if (string.IsNullOrWhiteSpace(recipe.ColourImage)) throw new InvalidDataException("A surface recipe needs a \"colourImage\".");
        if (recipe.Elevation == null) throw new InvalidDataException("A surface recipe needs an \"elevation\" source.");
        recipe.CoverMinLevel = System.Math.Clamp(recipe.CoverMinLevel, 0, recipe.MaxLevel);
        recipe.ColourMaxLevel = System.Math.Min(recipe.ColourMaxLevel, recipe.MaxLevel);
        recipe.ColourOpenMaxLevel = System.Math.Clamp(recipe.ColourOpenMaxLevel, recipe.ColourMaxLevel, recipe.MaxLevel);
        recipe.WaterMaxLevel = System.Math.Min(recipe.WaterMaxLevel, recipe.MaxLevel);
        return recipe;
    }

    public string Resolve(string path) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath)!, path));
}
