using System;

namespace Tns.Core.Terrain;

/// <summary>
/// Address of a terrain tile. The world is cut into a quadtree in plain latitude and longitude:
/// level 0 is two square tiles side by side (west and east hemispheres), and each level down
/// splits every tile into four. X counts east from 180° W, Y counts north from the south pole.
/// </summary>
public readonly record struct TileId(int Level, int X, int Y)
{
    public const int MaxSupportedLevel = 20;

    public static int ColumnsAt(int level) => 2 << level;
    public static int RowsAt(int level) => 1 << level;

    /// <summary>Width and height of the tile in degrees. Tiles are square in degrees.</summary>
    public double SizeDeg => 180.0 / (1 << Level);
    public double West => -180.0 + X * SizeDeg;
    public double South => -90.0 + Y * SizeDeg;
    public double East => West + SizeDeg;
    public double North => South + SizeDeg;

    public bool IsValid =>
        Level >= 0 && Level <= MaxSupportedLevel && X >= 0 && Y >= 0 && X < ColumnsAt(Level) && Y < RowsAt(Level);

    public TileId Parent => new(Level - 1, X >> 1, Y >> 1);

    /// <summary>One of the four tiles below this one. childX: 0 west, 1 east. childY: 0 south, 1 north.</summary>
    public TileId Child(int childX, int childY) => new(Level + 1, X * 2 + childX, Y * 2 + childY);

    /// <summary>Where the tile file sits inside a terrain pack.</summary>
    public string RelativePath => PathWith(HeightTile.FileExtension);

    /// <summary>Where the tile file sits inside a pack whose files have this extension.</summary>
    public string PathWith(string extension) => $"{Level}/{X}/{Y}{extension}";

    public override string ToString() => $"{Level}/{X}/{Y}";
}
