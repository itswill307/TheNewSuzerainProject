using System;
using Godot;
using Tns.Map;

namespace Tns.Dev;

/// <summary>
/// On-screen readout of the map view, for checking the fold.
/// F3 shows or hides it. It starts hidden when <see cref="ShowAtStart"/> is off, or when the game
/// is started with <c>-- --no-hud</c>.
/// </summary>
public partial class MapDebugHud : Label
{
    [Export] public MapView Map;

    /// <summary>Whether the readout is on screen when the game starts. F3 toggles it either way.</summary>
    [Export] public bool ShowAtStart = true;

    /// <summary>When set, replaces the readout with one large line. Used by the capture tool.</summary>
    public string Caption = "";

    private const string Help =
        "Drag: pan   Wheel: zoom   Right drag: tilt (eases back on release)   Wheel up: zoom to cursor\n" +
        "G: grid   T: circles   R: target ring   L: tile outlines   P: ground textures   O: fast loading\n" +
        "Space: play fold   F: manual fold (Q/E)   1-5: places   F3: hide this";

    public override void _Ready()
    {
        AddThemeColorOverride("font_outline_color", Colors.Black);
        AddThemeConstantOverride("outline_size", 6);
        Visible = ShowAtStart && Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-hud") < 0;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F3 })
        {
            Visible = !Visible;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (Caption != "")
        {
            AddThemeFontSizeOverride("font_size", 34);
            Text = Caption;
            return;
        }

        AddThemeFontSizeOverride("font_size", 18);
        string lat = $"{Math.Abs(Map.LatitudeDeg):0.0}° {(Map.LatitudeDeg >= 0 ? "N" : "S")}";
        string lon = $"{Math.Abs(Map.LongitudeDeg):0.0}° {(Map.LongitudeDeg >= 0 ? "E" : "W")}";
        string mode = Map.ManualFold ? "manual" : "from zoom";
        Text =
            $"Looking at {lat}, {lon}   Visible height {Map.VisibleHeightKm:#,0} km\n" +
            $"Fold {Map.Fold * 100.0:0}% ({mode})   East-west stretch {Map.EastWestStretch:0.00}x (globe = 1.00x)\n" +
            $"Terrain: {Map.Terrain.TilesDrawn} tiles to level {Map.Terrain.DeepestLevelDrawn}, " +
            $"{Map.Terrain.LoadedTiles} loaded ({Map.Terrain.MemoryMb:0} MB reserved)   " +
            $"Mountain height {Map.ElevationScale * 100:0}% of true scale   " +
            $"Ground textures {(Map.Terrain.HasGroundTextures ? (Map.Terrain.ShowGroundTextures ? "on" : "off") : "none")}   " +
            $"Loading: {(Map.Terrain.FastLoading ? "fast" : "full detail")}\n" +
            Help;
    }
}
