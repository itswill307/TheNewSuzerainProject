using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tns.Core.Geo;
using Tns.Core.Terrain;
using Tns.Map;
using Tns.Map.Terrain;

namespace Tns.Dev;

/// <summary>
/// Takes a fixed set of screenshots of the fold and measures the distortion in each, then quits.
/// Does nothing unless the game is started with a capture folder:
///
///   godot --path . --resolution 1280x720 -- --capture=C:/some/folder
///
/// Each sheet is a grid of shots. The ring round the view target is a true circle on the ground,
/// so its width divided by its height on screen is the distortion at that spot. That is measured
/// from the rendered pixels and compared with what the CPU-side maths predicts.
/// </summary>
public partial class CaptureRunner : Node
{
    [Export] public MapView Map;
    [Export] public MapDebugHud Hud;

    private const int CellWidth = 640;
    private const int CellHeight = 360;
    private const double RingTolerance = 0.04;

    private string _folder;
    private string _views;
    private int _failures;

    public override async void _Ready()
    {
        bool detailCompare = false, surfaceTour = false, proceduralCompare = false, desertTest = false, memoryReport = false, panTest = false, tiltSweep = false, loadTest = false;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--capture=")) _folder = arg["--capture=".Length..];
            if (arg == "--memory-report") memoryReport = true;
            // Anti-aliasing for comparing its look and cost: --no-msaa, --msaa2, --msaa4.
            if (arg == "--no-msaa") GetViewport().Msaa3D = Viewport.Msaa.Disabled;
            if (arg == "--msaa2") GetViewport().Msaa3D = Viewport.Msaa.Msaa2X;
            if (arg == "--msaa4") GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
            // Leave a pack's part out of the picture, to show what it adds.
            if (arg == "--no-cover") Map.Terrain.Material.SetShaderParameter("show_cover", false);
            if (arg == "--no-detail") Map.Terrain.Material.SetShaderParameter("show_detail", false);
            if (arg == "--no-distant-colour") Map.Terrain.Material.SetShaderParameter("show_distant_colour", false);
            if (arg == "--no-ground-textures") Map.Terrain.ShowGroundTextures = false;
            if (arg == "--fast-loading") Map.Terrain.FastLoading = true;
            if (arg == "--ground-plain") Map.Terrain.Material.SetShaderParameter("ground_hexagons", false);
            if (arg.StartsWith("--cover-sharpness=") && float.TryParse(arg["--cover-sharpness=".Length..],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float coverSharpness))
            {
                Map.Terrain.Material.SetShaderParameter("cover_sharpness", coverSharpness);
            }
            if (arg.StartsWith("--ground-strength=") && float.TryParse(arg["--ground-strength=".Length..],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float strength))
            {
                Map.Terrain.Material.SetShaderParameter("ground_texture_strength", strength);
            }
            if (arg == "--no-relief") Map.ReliefStrength = 0.0;
            if (arg == "--detail-compare") detailCompare = true;
            if (arg == "--surface-tour") surfaceTour = true;
            if (arg == "--pan-test") panTest = true;
            if (arg == "--tilt-sweep") tiltSweep = true;
            if (arg == "--load-test") loadTest = true;
            if (arg == "--procedural-compare") proceduralCompare = true;
            if (arg == "--desert-test") desertTest = true;
            if (arg.StartsWith("--views=")) { desertTest = true; _views = arg["--views=".Length..]; }
        }

        if (_folder == null)
        {
            QueueFree();
            return;
        }

        try
        {
            DirAccess.MakeDirRecursiveAbsolute(_folder);
            Map.SetProcess(false);
            Map.GetNode("Input").SetProcess(false);
            await NextFrame();
            await NextFrame();

            if (memoryReport)
            {
                await MemoryReport();
            }
            else if (panTest)
            {
                await PanTest();
            }
            else if (loadTest)
            {
                await LoadTest();
            }
            else if (tiltSweep)
            {
                await TiltSweep();
            }
            else if (detailCompare)
            {
                Map.LimitToPoles = false;
                await DetailCompareSheet();
            }
            else if (desertTest)
            {
                Map.LimitToPoles = false;
                await DesertTestSheet();
            }
            else if (proceduralCompare)
            {
                Map.LimitToPoles = false;
                await ProceduralCompareSheet();
            }
            else if (surfaceTour)
            {
                Map.LimitToPoles = false;
                await SurfaceTourSheet();
            }
            else
            {
                await HudToggleCheck();
                await FastZoomOutCheck();
                await PoleLimitSheet();
                await ZoomToCursorSheet();

                // The remaining sheets look at places the pole limit would not allow.
                Map.LimitToPoles = false;
                Map.MaxVisibleHeightKm = 60000.0;
                await FoldByLatitudeSheet();
                await ZoomSequenceSheet();
                await TiltedSheet();
                await TerrainSheet();
            }

            // Leave a quarter of the room spare, for tiles still in use while the view moves on.
            foreach (TerrainRenderer.Layer layer in Map.Terrain.Layers)
            {
                bool roomy = layer.PeakInUse <= layer.Cache.Layers * 3 / 4;
                if (!roomy) _failures++;
                GD.Print($"TERRAIN memory, {layer.Name}: most tiles needed for one picture {layer.PeakInUse}, " +
                    $"room for {layer.Cache.Layers} ({layer.MemoryMb:0} MB)  {(roomy ? "ok" : "FAIL")}");
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"capture failed: {e}");
            _failures++;
        }

        GD.Print($"MEMORY: RAM {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1e6:0} MB (Godot's own count), " +
                 $"most {Performance.GetMonitor(Performance.Monitor.MemoryStaticMax) / 1e6:0} MB; graphics card " +
                 $"{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1e6:0} MB " +
                 $"(textures {Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1e6:0} MB, " +
                 $"buffers {Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1e6:0} MB)");
        GD.Print(_failures == 0 ? "CAPTURE OK" : $"CAPTURE FAILED ({_failures} problems)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// --memory-report: memory at a world, a country and a close view, as the game counts it. Add
    /// --no-msaa to see what anti-aliasing costs.
    /// </summary>
    private async Task MemoryReport()
    {
        Vector2I window = DisplayServer.WindowGetSize();
        GD.Print($"MEMORY report at {window.X} x {window.Y} pixels, anti-aliasing {GetViewport().Msaa3D}");
        Rid viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        foreach (var (name, lat, lon, km) in new[] { ("world", 20.0, 10.0, 30000.0), ("country", 46.5, 10.0, 1500.0), ("close", 46.5, 8.0, 150.0) })
        {
            Map.SetView(lat, lon, km);
            await Shoot();
            double gpu = 0;
            for (int i = 0; i < 30; i++)
            {
                await NextFrame();
                gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport) / 30;
            }

            GD.Print($"MEMORY {name,-8} {Map.Terrain.TilesDrawn,4} tiles, GPU {gpu:0.00} ms a frame   RAM {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1e6,5:0} MB   graphics card " +
                     $"{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1e6,5:0} MB: textures " +
                     $"{Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1e6:0}, buffers " +
                     $"{Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1e6:0}, terrain tiles " +
                     $"{Map.Terrain.MemoryMb * 1.048576:0}");
        }
    }

    /// <summary>
    /// Browses a few close-ups, which pushes the coarser tiles out of memory, then jumps straight
    /// out to the whole world and checks that no frame on the way draws a blank (grey) tile:
    /// every place must show at least a coarse picture while its own tile loads. Saves the first
    /// frame after the jump as zoom_out_first_frame.png.
    /// </summary>
    /// <summary>
    /// Pans east steadily, about a screen width every three seconds, at a country and the closest
    /// zoom in a few places, and prints how long each frame took: the mean, the slowest 5% and the
    /// slowest, and how many frames took over 25 ms (a visible hitch).
    /// </summary>
    /// <summary>
    /// Tilts the camera sideways and up across mountains and plains at several zooms, and checks
    /// that every spot of ground on screen is covered by a tile the renderer chose to draw. A spot
    /// with no tile is a hole: background showing where ground should be.
    /// </summary>
    private async Task TiltSweep()
    {
        var places = new[] { ("Alps", 46.5, 10.0), ("Himalaya", 28.0, 86.0), ("Andes", -22.0, -67.0), ("Rockies", 39.5, -106.0), ("Ukraine", 49.0, 32.0) };
        Vector2 size = GetViewport().GetVisibleRect().Size;
        int views = 0, viewsWithHoles = 0, viewsOverLimit = 0, worstHoles = 0;
        string worst = "";
        foreach (double km in new[] { 150.0, 300.0, 600.0, 1200.0 })
        {
            foreach (var (name, lat, lon) in places)
            {
                foreach (double pitch in new[] { 0.0, 45.0, 75.0 })
                {
                    for (double side = -75.0; side <= 75.0; side += 15.0)
                    {
                        Map.SideTiltDeg = side;
                        Map.PitchDeg = pitch;
                        Map.SetView(lat, lon, km);
                        IReadOnlyList<TileId> tiles = Map.Terrain.SelectedTiles;
                        int holes = 0, checkedPoints = 0;
                        Vector2 firstHole = default;
                        for (int gy = 0; gy < 54; gy++)
                        {
                            for (int gx = 0; gx < 96; gx++)
                            {
                                var point = new Vector2((gx + 0.5f) * size.X / 96f, (gy + 0.5f) * size.Y / 54f);
                                if (!Map.TryScreenToGeo(point, out double plat, out double plon)) continue;
                                checkedPoints++;
                                bool covered = false;
                                foreach (TileId id in tiles)
                                {
                                    if (plat < id.South - 1e-9 || plat > id.North + 1e-9) continue;
                                    double d = ((plon - id.West) % 360.0 + 360.0) % 360.0;
                                    if (d <= id.SizeDeg + 1e-9) { covered = true; break; }
                                }

                                if (covered) continue;
                                if (holes == 0)
                                {
                                    firstHole = point;
                                    GD.Print($"HOLE {name} {km:0} km side {side:0} pitch {pitch:0} at screen {point.X:0},{point.Y:0} = {plat:0.000}, {plon:0.000}\n" + Map.Terrain.ExplainPlace(plat, plon));
                                }
                                holes++;
                            }
                        }

                        views++;
                        bool overLimit = tiles.Count > Map.Terrain.MaxTilesDrawn || Map.Terrain.TilesDrawn >= Map.Terrain.MaxTilesDrawn;
                        if (overLimit) viewsOverLimit++;
                        if (holes > 0) viewsWithHoles++;
                        if (holes > worstHoles) { worstHoles = holes; worst = $"{name} {km:0} km, side {side:0}, pitch {pitch:0}"; }
                        if (holes > 0 || overLimit)
                        {
                            GD.Print($"TILT {name,-9} {km,5:0} km  side {side,4:0}  pitch {pitch,3:0}  tiles {tiles.Count,4} drawn {Map.Terrain.TilesDrawn,4}"
                                     + $"  holes {holes,4} of {checkedPoints}{(holes > 0 ? $" (first at {firstHole.X:0},{firstHole.Y:0})" : "")}{(overLimit ? "  OVER TILE LIMIT" : "")}");
                        }
                    }
                }
            }
        }

        Map.SideTiltDeg = 0.0;
        Map.PitchDeg = 0.0;
        GD.Print($"TILT sweep: {views} views, {viewsWithHoles} with holes, {viewsOverLimit} over the tile limit; worst {worstHoles} holes ({worst})");
        await NextFrame();
    }

    /// <summary>
    /// How quickly tiles load. Jumps to places far apart (so little is loaded already) and counts
    /// the time until every tile the view needs is on the graphics card, then pans steadily and
    /// counts how often a tile is drawn from a coarser stand-in because its own hasn't arrived.
    /// Prints, for each kind of tile, how long reading and decoding took and how long tiles waited.
    /// </summary>
    private async Task LoadTest()
    {
        var jumps = new[] { ("Alps", 46.5, 10.0, 150.0), ("Himalaya", 28.0, 86.0, 150.0), ("Amazon", -3.0, -62.0, 150.0),
            ("Iowa", 42.0, -94.0, 150.0), ("Sahara", 25.0, 5.0, 300.0), ("Japan", 36.0, 138.0, 300.0), ("Andes", -20.0, -68.0, 600.0) };
        foreach (var layer in Map.Terrain.Layers) layer.Cache.ResetTimes();
        double totalSettle = 0.0;
        foreach (var (name, lat, lon, km) in jumps)
        {
            Map.SetView(lat, lon, km);
            ulong start = Time.GetTicksMsec();
            int frames = 0;
            do { Map.Apply(); await NextFrame(); frames++; } while (!Map.Terrain.Settled && Time.GetTicksMsec() - start < 15000);
            double ms = Time.GetTicksMsec() - start;
            totalSettle += ms;
            GD.Print($"LOAD jump {name,-9} {km,4:0} km: all tiles in after {ms,5:0} ms ({frames} frames), {Map.Terrain.TilesDrawn} tiles drawn");
            GD.Print("LOAD   threads " + System.Threading.ThreadPool.ThreadCount + ", pending work " + System.Threading.ThreadPool.PendingWorkItemCount + ", " + string.Join(", ", Map.Terrain.Layers.Select(l => $"{l.Name} in flight {l.Cache.InFlight}, waiting {l.Cache.WaitingForUpload}, loaded {l.Cache.LoadedTiles}")));
        }

        GD.Print($"LOAD mode: {(Map.Terrain.FastLoading ? "fast loading" : "full detail")}; {Map.Terrain.Layers.Sum(l => l.Cache.Loads)} tiles read for the jumps");
        GD.Print($"LOAD jumps: {totalSettle / jumps.Length:0} ms on average to load a new place");
        foreach (var layer in Map.Terrain.Layers)
        {
            var c = layer.Cache;
            if (c.Loads == 0) continue;
            GD.Print($"LOAD {layer.Name,-8} {c.Loads,5} tiles: read {c.ReadMs / c.Loads,5:0.0} ms, decode {c.DecodeMs / c.Loads,5:0.0} ms, queued {c.QueueMs / c.Loads,6:0} ms a tile; waited {c.WaitMs / c.Loads,6:0} ms on average, longest {c.LongestWaitMs,6:0} ms");
        }

        foreach (var (km, pitch) in new[] { (150.0, 0.0), (300.0, 0.0), (150.0, 60.0) })
        {
            Map.PitchDeg = pitch;
            Map.SetView(30.0, 70.0, km);
            for (int i = 0; i < 600 && !Map.Terrain.Settled; i++) { Map.Apply(); await NextFrame(); }
            double step = km * 16.0 / 9.0 / 60.0; // a screen width a second
            long stand = 0, windows = 0, drawn = 0;
            int readBefore = Map.Terrain.Layers.Sum(l => l.Cache.Loads);
            for (int frame = 0; frame < 300; frame++)
            {
                Map.PanByKm(step, 0.0);
                Map.Apply();
                await NextFrame();
                stand += Map.Terrain.FallbackWindows;
                windows += Map.Terrain.TilesDrawn * Map.Terrain.Layers.Count;
                drawn += Map.Terrain.TilesDrawn;
            }

            GD.Print($"LOAD pan {km,4:0} km, pitch {pitch:0}, a screen a second: {100.0 * stand / Math.Max(windows, 1):0.0}% of tiles drawn from a coarser stand-in, {drawn / 300} tiles drawn, {Map.Terrain.Layers.Sum(l => l.Cache.Loads) - readBefore} tiles read");
            Map.PitchDeg = 0.0;
        }

        // Zooming in from a continent to the closest zoom in two seconds, somewhere new each time.
        foreach (var (name, lat, lon) in new[] { ("Caucasus", 42.5, 44.0), ("Tibet", 32.0, 90.0), ("Patagonia", -48.0, -71.0), ("Ethiopia", 9.0, 39.0) })
        {
            Map.SetView(lat, lon, 4000.0);
            for (int i = 0; i < 600 && !Map.Terrain.Settled; i++) { Map.Apply(); await NextFrame(); }
            long stand = 0, windows = 0;
            ulong start = Time.GetTicksMsec();
            for (int frame = 0; frame <= 120; frame++)
            {
                Map.SetView(lat, lon, 4000.0 * Math.Pow(150.0 / 4000.0, frame / 120.0));
                await NextFrame();
                stand += Map.Terrain.FallbackWindows;
                windows += Map.Terrain.TilesDrawn * Map.Terrain.Layers.Count;
            }

            ulong zoomed = Time.GetTicksMsec();
            int frames = 0;
            while (!Map.Terrain.Settled && frames < 900) { Map.Apply(); await NextFrame(); frames++; }
            GD.Print($"LOAD zoom {name,-10} 4000 to 150 km in {zoomed - start} ms: {100.0 * stand / Math.Max(windows, 1):0.0}% stand-ins on the way, all in {Time.GetTicksMsec() - zoomed} ms after");
        }
    }

    private async Task PanTest()
    {
        var places = new[] { ("Europe", 48.0, 5.0), ("Sahara", 25.0, 0.0), ("India", 25.0, 75.0) };
        foreach (double km in new[] { 600.0, 150.0 })
        {
            foreach (var (name, lat, lon) in places)
            {
                Map.SetView(lat, lon, km);
                await Shoot();
                var times = new List<double>();
                ulong last = Time.GetTicksUsec();
                double step = km * 16.0 / 9.0 / 180.0;
                for (int frame = 0; frame < 240; frame++)
                {
                    Map.PanByKm(step, 0.0);
                    Map.Apply();
                    await NextFrame();
                    ulong now = Time.GetTicksUsec();
                    times.Add((now - last) / 1000.0);
                    last = now;
                }

                times.Sort();
                GD.Print($"PAN {name,-7} {km,4:0} km: mean {times.Average():0.0} ms, slowest 5% {times[(int)(times.Count * 0.95)]:0.0} ms, " +
                         $"slowest {times[^1]:0.0} ms, frames over 25 ms {times.Count(t => t > 25)}");
            }
        }
    }

    private async Task FastZoomOutCheck()
    {
        var places = new[]
        {
            (46.5, 8.0), (28.0, 86.9), (61.5, 6.5), (-33.9, 18.5), (35.4, 138.7), (-13.2, -72.5),
            (40.0, -105.0), (-25.0, 134.0), (5.0, 20.0), (55.0, 90.0), (-40.0, -70.0), (25.0, 45.0),
        };
        foreach (var (lat, lon) in places)
        {
            Map.SetView(lat, lon, 600.0);
            await Shoot();
            Map.SetView(lat, lon, 80.0);
            await Shoot();
        }

        Map.SetView(20.0, 10.0, 30000.0);
        int worst = 0;
        Image first = null;
        for (int frame = 0; frame < 30; frame++)
        {
            Map.Apply();
            await NextFrame();
            worst = Math.Max(worst, Map.Terrain.BlankTiles);
            first ??= GetViewport().GetTexture().GetImage();
        }

        first?.SavePng(_folder.PathJoin("zoom_out_first_frame.png"));
        bool ok = worst == 0;
        if (!ok) _failures++;
        GD.Print($"ZOOM OUT fast: at most {worst} blank tile layers in a frame  {(ok ? "ok" : "FAIL")}");
    }

    /// <summary>Presses F3 twice, as a player would, and checks the readout hides and comes back.</summary>
    private async Task HudToggleCheck()
    {
        bool before = Hud.Visible;
        var states = new bool[2];
        for (int i = 0; i < 2; i++)
        {
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.F3, Keycode = Key.F3, Pressed = true });
            await NextFrame();
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.F3, Keycode = Key.F3, Pressed = false });
            await NextFrame();
            states[i] = Hud.Visible;
        }

        bool ok = states[0] == !before && states[1] == before;
        if (!ok) _failures++;
        GD.Print($"HUD toggle: F3 {(states[0] ? "showed" : "hid")} it, F3 again {(states[1] ? "showed" : "hid")} it  {(ok ? "ok" : "FAIL")}");
        Hud.Visible = before;
    }

    /// <summary>
    /// Pans as far toward each pole as the view allows at several zoom levels, and checks that
    /// nothing beyond the pole is on screen. The background is turned green so it is easy to find.
    /// </summary>
    private async Task PoleLimitSheet()
    {
        double[] heights = { 20000.0, 12000.0, 8000.0, 6000.0, 4500.0, 3500.0, 1500.0, 400.0 };
        var cells = new List<Image>();
        ResetDebug();
        Map.ShowGraticule = true;
        Godot.Environment environment =
            GetTree().CurrentScene.GetNode<WorldEnvironment>("WorldEnvironment").Environment;
        Color background = environment.BackgroundColor;
        environment.BackgroundColor = new Color(0f, 1f, 0f);

        foreach (double sign in new[] { 1.0, -1.0 })
        {
            foreach (double height in heights)
            {
                Map.SetView(90.0 * sign, 16.0, height);
                string pole = sign > 0 ? "north" : "south";
                Hud.Caption = $"Furthest {pole} at {height:#,0} km visible: {Math.Abs(Map.LatitudeDeg):0.0}°   fold {Map.Fold * 100:0}%";
                Image shot = await Shoot();
                CheckPoleLimit($"{pole} {height:#,0} km", shot, sign);
                if (sign > 0) cells.Add(shot);
            }
        }

        environment.BackgroundColor = background;
        SaveSheet("pole_limit.png", cells, 4);
    }

    /// <summary>
    /// Passes when no background shows in the middle of the screen, and the pole sits just beyond
    /// the screen edge (so the limit is not stricter than it needs to be).
    /// </summary>
    private void CheckPoleLimit(string label, Image shot, double sign)
    {
        byte[] data = shot.GetData();
        int width = shot.GetWidth();
        int height = shot.GetHeight();
        int middle = 0, anywhere = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                if (data[i] > 60 || data[i + 1] < 200 || data[i + 2] > 60) continue;
                anywhere++;
                if (x > width * 2 / 5 && x < width * 3 / 5) middle++;
            }
        }

        Vector2 viewport = Map.GetViewport().GetVisibleRect().Size;
        float poleY = Map.Camera.UnprojectPosition(Map.GeoToWorld(90.0 * sign, Map.LongitudeDeg)).Y;
        double inside = (sign > 0 ? poleY : viewport.Y - poleY) / viewport.Y;

        bool ok = middle == 0 && inside <= 0.0 && inside > -0.02;
        if (!ok) _failures++;
        GD.Print($"POLE {label,-16} limit {Map.LatitudeDeg,6:0.0}°  background in middle {middle} px, " +
            $"anywhere {100.0 * anywhere / (width * height):0.0}%  pole {-inside * 100:0.00}% beyond edge  {(ok ? "ok" : "FAIL")}");
    }

    /// <summary>Every test place at fold 0%, 50% and 100%, with the same zoom.</summary>
    private async Task FoldByLatitudeSheet()
    {
        double[] folds = { 0.0, 0.5, 1.0 };
        var cells = new List<Image>();
        ResetDebug();
        Map.ShowGraticule = true;
        Map.ShowTargetRing = true;
        Map.ManualFold = true;

        foreach (TestPlace place in TestPlaces.All)
        {
            foreach (double fold in folds)
            {
                Map.ManualFoldAmount = fold;
                Map.SetView(place.LatitudeDeg, place.LongitudeDeg, 3000.0);
                Hud.Caption = $"{place.Name} {place.LatitudeDeg:0}°   fold {fold * 100:0}%   stretch {Map.EastWestStretch:0.00}x";
                Image shot = await Shoot();
                CheckRing($"{place.Name} fold {fold * 100:0}%", shot);
                cells.Add(shot);
            }
        }

        SaveSheet("fold_by_latitude.png", cells, folds.Length);
    }

    /// <summary>Zooming in on Svalbard with the fold driven by zoom, as it is in play.</summary>
    private async Task ZoomSequenceSheet()
    {
        double[] heights = { 21500.0, 12000.0, 9000.0, 7000.0, 5500.0, 4500.0, 3500.0, 1500.0 };
        var cells = new List<Image>();
        ResetDebug();
        Map.ShowGraticule = true;
        Map.ShowTissot = true;

        foreach (double height in heights)
        {
            Map.SetView(78.0, 16.0, height);
            Hud.Caption = $"Svalbard   {height:#,0} km visible   fold {Map.Fold * 100:0}%   stretch {Map.EastWestStretch:0.00}x";
            cells.Add(await Shoot());
        }

        SaveSheet("zoom_sequence_svalbard.png", cells, 4);
    }

    /// <summary>The whole map from a tilted, pulled-back camera, to see the 3D shape of the fold.</summary>
    private async Task TiltedSheet()
    {
        double[] folds = { 0.0, 0.33, 0.66, 1.0 };
        var cells = new List<Image>();
        ResetDebug();
        Map.ShowGraticule = true;
        Map.ManualFold = true;
        double worstDifference = 0.0;

        // Top row tilts up and down, bottom row tilts sideways across the central longitude.
        foreach (bool sideways in new[] { false, true })
        {
            Map.PitchDeg = sideways ? 0.0 : 50.0;
            Map.SideTiltDeg = sideways ? 50.0 : 0.0;

            foreach (double fold in folds)
            {
                Map.ManualFoldAmount = fold;
                Map.SetView(49.0, 2.0, 40000.0);
                Hud.Caption = $"Paris, tilted 50° {(sideways ? "sideways" : "up")}   fold {fold * 100:0}%";
                cells.Add(await Shoot());

                // The map's own screen maths must agree with the real camera.
                foreach (TestPlace place in TestPlaces.All)
                {
                    Vector2 own = Map.GeoToScreen(place.LatitudeDeg, place.LongitudeDeg);
                    Vector2 camera = Map.Camera.UnprojectPosition(
                        Map.GeoToWorld(place.LatitudeDeg, place.LongitudeDeg));
                    worstDifference = Math.Max(worstDifference, (own - camera).Length());
                }
            }
        }

        bool ok = worstDifference < 0.5;
        if (!ok) _failures++;
        GD.Print($"SCREEN maths vs camera while tilted: worst difference {worstDifference:0.000} px  {(ok ? "ok" : "FAIL")}");
        SaveSheet("tilted_shape.png", cells, 4);
    }

    private readonly record struct TerrainShot(
        string Name, double LatitudeDeg, double LongitudeDeg, double HeightKm, double PitchDeg, double SideTiltDeg,
        bool TileLevels, bool ExpectNoGaps);

    /// <summary>
    /// Raised terrain from several angles. Views that should be filled edge to edge are first
    /// drawn against a green background and fail if any green shows, which would be a missing
    /// tile or a gap between tiles.
    /// </summary>
    private async Task TerrainSheet()
    {
        TerrainShot[] shots =
        {
            new("Alps from above", 46.3, 10.0, 700.0, 0.0, 0.0, false, true),
            new("Alps, tilted", 46.3, 10.0, 700.0, 40.0, 0.0, false, true),
            new("Alps, tile levels", 46.3, 10.0, 700.0, 40.0, 0.0, true, true),
            new("Himalaya, low angle", 28.5, 86.0, 900.0, 62.0, 0.0, false, false),
            new("Andes from the side", -16.0, -69.0, 900.0, 0.0, 30.0, false, true),
            new("Norway coast", 61.5, 7.0, 400.0, 40.0, 0.0, false, true),
            new("Hindu Kush, close", 35.8, 74.3, 150.0, 30.0, 0.0, false, true),
            new("Sahara, close", 24.0, 14.0, 150.0, 0.0, 0.0, false, true),
            // Coastlines, lakes and rivers from the water tiles, at the closest zoom and further out.
            new("Venice lagoon, close", 45.35, 12.35, 150.0, 0.0, 0.0, false, true),
            new("Alps, close", 46.35, 10.2, 150.0, 0.0, 0.0, false, true),
            new("Alps, close and tilted", 46.2, 10.2, 150.0, 45.0, 0.0, false, true),
            new("Croatian islands", 44.2, 15.2, 400.0, 30.0, 0.0, false, true),
            new("Italy, globe formed", 42.0, 12.5, 2500.0, 0.0, 0.0, false, true),
            // The two views that need the most tiles at once: closest zoom, fully tilted, over
            // the most detailed ground in the pack.
            new("Hengduan, closest, max tilt", 29.5, 98.5, 150.0, 75.0, 0.0, false, false),
            new("Karakoram, closest, side tilt", 36.0, 76.0, 150.0, 40.0, 75.0, false, false),
            new("Europe, globe formed", 47.0, 12.0, 3000.0, 0.0, 0.0, false, true),
            new("World, flat", 20.0, 10.0, 20000.0, 0.0, 0.0, false, false),
        };
        var cells = new List<Image>();
        ResetDebug();
        Godot.Environment environment =
            GetTree().CurrentScene.GetNode<WorldEnvironment>("WorldEnvironment").Environment;
        Color background = environment.BackgroundColor;

        foreach (TerrainShot shot in shots)
        {
            Map.PitchDeg = shot.PitchDeg;
            Map.SideTiltDeg = shot.SideTiltDeg;
            Map.ShowTileLevels = shot.TileLevels;
            Map.SetView(shot.LatitudeDeg, shot.LongitudeDeg, shot.HeightKm);
            Hud.Caption = "";

            string gaps = "";
            if (shot.ExpectNoGaps)
            {
                environment.BackgroundColor = new Color(0f, 1f, 0f);
                Hud.Visible = false;
                int green = CountGreen(await Shoot());
                Hud.Visible = true;
                environment.BackgroundColor = background;
                bool ok = green == 0;
                if (!ok) _failures++;
                gaps = $", gaps {green} px {(ok ? "ok" : "FAIL")}";
            }

            // Each view also at full size without the caption, to judge the detail pixel by pixel.
            Hud.Visible = false;
            Image full = await Shoot();
            Hud.Visible = true;
            full.SavePng(_folder.PathJoin($"terrain_{cells.Count + 1:00}.png"));

            Hud.Caption = $"{shot.Name}   {shot.HeightKm:#,0} km   {Map.Terrain.TilesDrawn} tiles to level {Map.Terrain.DeepestLevelDrawn}   height {Map.ElevationScale * 100:0}%";
            cells.Add(await Shoot());
            GD.Print($"TERRAIN {shot.Name,-22} {Map.Terrain.TilesDrawn,3} tiles, deepest level {Map.Terrain.DeepestLevelDrawn}, " +
                $"{Map.Terrain.LoadedTiles} loaded{gaps}");
        }

        Map.ShowTileLevels = false;
        SaveSheet("terrain.png", cells, 2);
    }


    /// <summary>
    /// The same close-up drawn with the finest tiles, then with the finest one and two levels
    /// taken away, as unscaled crops side by side. Shows what each level of detail adds, which is
    /// how the bake recipe's detail settings were chosen. Run with --detail-compare.
    /// </summary>
    /// <summary>Desert close-ups, saved full size as desert_NN.png. Run with --desert-test.</summary>
    private async Task DesertTestSheet()
    {
        (string Name, double Lat, double Lon, double Km, double Pitch)[] views =
        {
            ("Erg Chech", 24.8, -1.5, 150.0, 0.0),
            ("Namib Sand Sea", -24.1, 15.0, 150.0, 0.0),
            ("Namib Sand Sea, tilted", -24.5, 15.0, 150.0, 50.0),
            ("Kalahari", -23.5, 22.0, 150.0, 0.0),
        };
        // --views=Name:lat:lon:km:pitch|... replaces the list.
        if (_views != null)
        {
            views = _views.Split('|').Select(v => v.Split(':')).Select(p => (p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                p.Length > 4 ? double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) : 0.0)).ToArray();
        }

        var cells = new List<Image>();
        ResetDebug();
        for (int i = 0; i < views.Length; i++)
        {
            Map.PitchDeg = views[i].Pitch;
            Map.SetView(views[i].Lat, views[i].Lon, views[i].Km);
            Hud.Visible = false;
            Image full = await Shoot();
            Hud.Visible = true;
            full.SavePng(_folder.PathJoin($"desert_{i + 1:00}.png"));
            cells.Add(full);
            GD.Print($"DESERT {views[i].Name,-24} {Map.Terrain.TilesDrawn,3} tiles, deepest level {Map.Terrain.DeepestLevelDrawn}");
        }

        Map.PitchDeg = 0.0;
        SaveSheet("desert_test.png", cells, 2);
    }

    private async Task ProceduralCompareSheet()
    {
        (string Name, double Lat, double Lon, double Km, double Pitch)[] views =
        {
            ("Sahara, Erg Chech", 24.5, -2.5, 150.0, 0.0),
            ("Sahara, Tibesti", 21.0, 18.0, 150.0, 0.0),
            ("Kalahari", -23.5, 22.0, 150.0, 0.0),
            ("Namib dunes", -24.6, 15.3, 150.0, 30.0),
            ("Arabia, Empty Quarter", 20.0, 50.0, 300.0, 0.0),
            ("Australia, outback", -25.0, 132.0, 150.0, 0.0),
            ("Alps, close", 46.35, 10.2, 150.0, 0.0),
            ("US Great Plains", 40.0, -98.0, 150.0, 0.0),
            ("Siberian taiga", 62.0, 95.0, 150.0, 0.0),
            ("Amazon", -3.0, -62.0, 150.0, 0.0),
        };
        var cells = new List<Image>();
        ResetDebug();
        ShaderMaterial material = Map.Terrain.Material;
        Rid viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        double offTotal = 0, onTotal = 0;
        for (int i = 0; i < views.Length; i++)
        {
            var view = views[i];
            Map.PitchDeg = view.Pitch;
            Map.SetView(view.Lat, view.Lon, view.Km);
            foreach (bool on in new[] { false, true })
            {
                material.SetShaderParameter("procedural_strength", on ? 1.0f : 0.0f);
                Hud.Visible = false;
                Image full = await Shoot();
                full.SavePng(_folder.PathJoin($"proc_{i + 1:00}_{(on ? "on" : "off")}.png"));

                // Graphics card time per frame, averaged over a few frames.
                double gpu = 0;
                for (int f = 0; f < 20; f++)
                {
                    await NextFrame();
                    gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                }

                gpu /= 20;
                if (on) onTotal += gpu; else offTotal += gpu;
                Hud.Visible = true;
                Hud.Caption = $"{view.Name}   {view.Km:#,0} km   detail {(on ? "on" : "off")}   {gpu:0.00} ms";
                cells.Add(await Shoot());
                GD.Print($"PROCEDURAL {view.Name,-24} {(on ? "on " : "off")} GPU {gpu:0.00} ms");
            }
        }

        material.SetShaderParameter("procedural_strength", 0.0f);
        Map.PitchDeg = 0.0;
        GD.Print($"PROCEDURAL average GPU time: off {offTotal / views.Length:0.00} ms, on {onTotal / views.Length:0.00} ms");
        SaveSheet("procedural_compare.png", cells, 2);
    }

    /// <summary>
    /// The ground round the world, from the closest zoom out to a continent, to check the land
    /// cover, colour and water everywhere. Run with --surface-tour. Each view is also saved full
    /// size as tour_NN.png.
    /// </summary>
    private async Task SurfaceTourSheet()
    {
        (string Name, double Lat, double Lon, double Km, double Pitch)[] views =
        {
            ("Nile delta", 30.8, 31.0, 300.0, 0.0),
            ("Sahara, Tibesti", 21.0, 18.0, 600.0, 0.0),
            ("Amazon and Rio Negro", -3.0, -60.0, 300.0, 0.0),
            ("US Great Plains", 40.0, -98.0, 300.0, 0.0),
            ("Rocky Mountains", 39.6, -106.0, 200.0, 40.0),
            ("Netherlands", 52.3, 5.0, 250.0, 0.0),
            ("Norway fjords", 61.0, 6.0, 200.0, 0.0),
            ("Finland lakes", 62.0, 27.0, 300.0, 0.0),
            ("Siberia, Yenisei", 62.0, 90.0, 400.0, 0.0),
            ("Greenland coast", 64.5, -50.0, 400.0, 0.0),
            ("Ganges delta", 22.3, 89.5, 300.0, 0.0),
            ("Himalaya, Everest", 28.0, 86.9, 200.0, 45.0),
            ("Java and Bali", -8.0, 113.0, 400.0, 0.0),
            ("Australia, outback", -25.0, 132.0, 800.0, 0.0),
            ("Patagonia ice fields", -49.0, -73.5, 300.0, 0.0),
            ("Antarctic Peninsula", -64.5, -61.0, 400.0, 0.0),
            ("Sahara, zoomed out", 22.0, 12.0, 3000.0, 0.0),
            ("Southern Africa, zoomed out", -26.0, 22.0, 3000.0, 0.0),
            ("Europe", 50.0, 10.0, 3000.0, 0.0),
            ("Africa", 0.0, 20.0, 9000.0, 0.0),
        };
        var cells = new List<Image>();
        ResetDebug();
        foreach (var view in views)
        {
            Map.PitchDeg = view.Pitch;
            Map.SetView(view.Lat, view.Lon, view.Km);
            Hud.Visible = false;
            Image full = await Shoot();
            Hud.Visible = true;
            full.SavePng(_folder.PathJoin($"tour_{cells.Count + 1:00}.png"));
            Hud.Caption = $"{view.Name}   {view.Km:#,0} km   {Map.Terrain.TilesDrawn} tiles to level {Map.Terrain.DeepestLevelDrawn}";
            cells.Add(await Shoot());
            GD.Print($"TOUR {view.Name,-24} {Map.Terrain.TilesDrawn,3} tiles, deepest level {Map.Terrain.DeepestLevelDrawn}");
        }

        Map.PitchDeg = 0.0;
        SaveSheet("surface_tour.png", cells, 3);
    }

    private async Task DetailCompareSheet()
    {
        (string Name, double Lat, double Lon, double Pitch)[] sites =
        {
            ("2% Andean foothills", -40.25, -70.49, 0.0),
            ("7% southern Italy", 39.90, 15.29, 0.0),
            ("13% north Norway", 69.08, 21.27, 0.0),
            ("21% Norway", 61.35, 8.96, 0.0),
            ("37% Altai", 46.58, 92.64, 0.0),
            ("46% Alborz", 36.04, 53.61, 0.0),
            ("55% Patagonia", -42.36, -71.54, 0.0),
            ("76% Laos", 20.57, 104.24, 0.0),
            ("95% Kunlun", 37.79, 76.11, 0.0),
            ("37% Altai, tilted", 46.58, 92.64, 60.0),
            ("95% Kunlun, tilted", 37.79, 76.11, 60.0),
        };
        var cells = new List<Image>();
        ResetDebug();
        Hud.Visible = false;
        int deepest = Map.Terrain.Packs.MaxLevel;

        foreach (var site in sites)
        {
            for (int cap = deepest; cap > deepest - 3; cap--)
            {
                Map.Terrain.MaxLevel = cap;
                Map.PitchDeg = site.Pitch;
                Map.SetView(site.Lat, site.Lon, Map.MinVisibleHeightKm);
                Image shot = await Shoot();
                cells.Add(shot.GetRegion(new Rect2I(
                    (shot.GetWidth() - CellWidth) / 2, (shot.GetHeight() - CellHeight) / 2, CellWidth, CellHeight)));
                GD.Print($"DETAIL {site.Name,-22} up to level {cap}: {Map.Terrain.TilesDrawn} tiles drawn");
            }
        }

        Map.Terrain.MaxLevel = TileId.MaxSupportedLevel;
        Hud.Visible = true;
        // One file per place as well, so each can be looked at pixel for pixel.
        for (int i = 0; i < sites.Length; i++) SaveSheet($"detail_compare_{i + 1:00}.png", cells.GetRange(i * 3, 3), 3);
        SaveSheet("detail_compare.png", cells, 3);
    }

    private static int CountGreen(Image shot)
    {
        byte[] data = shot.GetData();
        int count = 0;
        for (int i = 0; i < data.Length; i += 3)
        {
            if (data[i] < 60 && data[i + 1] > 200 && data[i + 2] < 60) count++;
        }

        return count;
    }

    private readonly record struct ZoomCase(
        string Name, double LatitudeDeg, double LongitudeDeg, double FromKm, double ToKm,
        float CursorX, float CursorY);

    /// <summary>
    /// Zooms on a cursor position with the real per-frame animation running. Checks that the place
    /// under the cursor stays under it, and that the pole limit is never broken on the way. A
    /// magenta dot marks the place and a cyan cross marks the cursor.
    /// </summary>
    private async Task ZoomToCursorSheet()
    {
        ZoomCase[] cases =
        {
            new("Flat to globe", 0.0, 10.0, 20000.0, 2500.0, 0.70f, 0.38f),
            new("Toward the Arctic", 0.0, 10.0, 20000.0, 4000.0, 0.45f, 0.10f),
            new("On the globe", 45.0, 10.0, 3000.0, 500.0, 0.20f, 0.75f),
            new("Southern hemisphere", 0.0, -60.0, 15000.0, 1500.0, 0.55f, 0.88f),
            // Zooming out near a pole: the limit has to push the view south, so the place under
            // the cursor is allowed to slip. Only the limit is checked.
            new("Zoom out near the pole", 70.0, 10.0, 3000.0, 15000.0, 0.50f, 0.20f),
        };
        var cells = new List<Image>();
        ResetDebug();
        Map.ShowGraticule = true;
        Vector2 viewport = Map.GetViewport().GetVisibleRect().Size;
        Control crosshair = AddCrosshair();

        foreach (ZoomCase zoom in cases)
        {
            Map.SetView(zoom.LatitudeDeg, zoom.LongitudeDeg, zoom.FromKm);
            Vector2 cursor = new Vector2(zoom.CursorX, zoom.CursorY) * viewport;
            crosshair.Position = cursor;
            if (!Map.TryScreenToGeo(cursor, out double latitudeDeg, out double longitudeDeg))
            {
                GD.PrintErr($"ZOOM {zoom.Name}: no map under the cursor");
                _failures++;
                continue;
            }

            Map.MarkerLatitudeDeg = latitudeDeg;
            Map.MarkerLongitudeDeg = longitudeDeg;
            Map.MarkerRadiusKm = Map.VisibleHeightKm * 0.012;
            Hud.Caption = $"{zoom.Name}: start, {zoom.FromKm:#,0} km";
            cells.Add(await Shoot());

            Map.ZoomAt(cursor, zoom.ToKm / zoom.FromKm);
            Map.SetProcess(true);
            double worstSlip = 0.0;
            double worstOverLimit = 0.0;
            Image halfway = null;
            double halfwayKm = Math.Sqrt(zoom.FromKm * zoom.ToKm);

            for (int frame = 0; frame < 600; frame++)
            {
                await NextFrame();
                Map.MarkerRadiusKm = Map.VisibleHeightKm * 0.012;
                double overLimit = Math.Abs(Map.LatitudeDeg) - Map.MaxLatitudeDegNow;
                worstOverLimit = Math.Max(worstOverLimit, overLimit);
                if (overLimit < -0.01)
                {
                    worstSlip = Math.Max(worstSlip, (Map.GeoToScreen(latitudeDeg, longitudeDeg) - cursor).Length());
                }

                bool pastHalfway = zoom.ToKm < zoom.FromKm
                    ? Map.VisibleHeightKm <= halfwayKm
                    : Map.VisibleHeightKm >= halfwayKm;
                if (halfway == null && pastHalfway)
                {
                    Hud.Caption = $"{zoom.Name}: halfway, {Map.VisibleHeightKm:#,0} km";
                    halfway = await Shoot();
                }

                if (Math.Abs(Map.VisibleHeightKm / zoom.ToKm - 1.0) < 1e-3) break;
            }

            Map.SetProcess(false);
            Hud.Caption = $"{zoom.Name}: end, {Map.VisibleHeightKm:#,0} km";
            Image end = await Shoot();
            cells.Add(halfway ?? end);
            cells.Add(end);

            bool limited = Math.Abs(Map.LatitudeDeg) > Map.MaxLatitudeDegNow - 0.01;
            Vector2? dot = MeasureMarkerCentre(end);
            Vector2 cursorPixels = new Vector2(zoom.CursorX * end.GetWidth(), zoom.CursorY * end.GetHeight());
            double dotError = dot == null ? double.NaN : (dot.Value - cursorPixels).Length();

            bool ok = worstOverLimit < 1e-6 && worstSlip < 1.0 && (limited || dotError < 2.0);
            if (!ok) _failures++;
            GD.Print($"ZOOM {zoom.Name,-24} slip while free {worstSlip:0.00} px, dot at end {dotError:0.0} px from cursor, " +
                $"limit {(worstOverLimit < 1e-6 ? "kept" : "BROKEN")}{(limited ? " (view held at limit)" : "")}  {(ok ? "ok" : "FAIL")}");
        }

        crosshair.QueueFree();
        Map.MarkerRadiusKm = 0.0;
        SaveSheet("zoom_to_cursor.png", cells, 3);
    }

    private Control AddCrosshair()
    {
        var crosshair = new Control();
        foreach (Rect2 arm in new[] { new Rect2(-14f, -1f, 28f, 2f), new Rect2(-1f, -14f, 2f, 28f) })
        {
            crosshair.AddChild(new ColorRect { Color = new Color(0f, 1f, 1f), Position = arm.Position, Size = arm.Size });
        }

        Hud.GetParent().AddChild(crosshair);
        return crosshair;
    }

    /// <summary>Centre of the magenta marker dot in a screenshot, in pixels.</summary>
    private static Vector2? MeasureMarkerCentre(Image shot)
    {
        byte[] data = shot.GetData();
        int width = shot.GetWidth();
        int height = shot.GetHeight();
        double sumX = 0.0, sumY = 0.0;
        int count = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                if (data[i] > 200 && data[i + 1] < 70 && data[i + 2] > 200)
                {
                    sumX += x + 0.5;
                    sumY += y + 0.5;
                    count++;
                }
            }
        }

        if (count == 0) return null;
        return new Vector2((float)(sumX / count), (float)(sumY / count));
    }

    private void ResetDebug()
    {
        Map.ShowGraticule = false;
        Map.ShowTissot = false;
        Map.ShowTargetRing = false;
        Map.ManualFold = false;
        Map.PitchDeg = 0.0;
        Map.SideTiltDeg = 0.0;
    }

    private async Task<Image> Shoot()
    {
        // Terrain tiles load in the background: keep the view updating until they stop changing.
        bool settled = false;
        var loading = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        for (int frame = 0; frame < 900 && !settled; frame++)
        {
            Map.Apply();
            await NextFrame();
            frames++;
            settled = Map.Terrain.Settled;
        }

        // How long the tiles took to load for this view (a view near the last one loads little).
        GD.Print($"LOADED in {frames} frames, {loading.ElapsedMilliseconds} ms");

        if (!settled)
        {
            GD.PrintErr("TERRAIN did not finish loading for a screenshot");
            _failures++;
        }

        Map.Apply();
        await NextFrame();
        await NextFrame();
        Image image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        return image;
    }

    private async Task NextFrame()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    /// <summary>Compares the target ring's measured width / height with the predicted value.</summary>
    private void CheckRing(string label, Image shot)
    {
        double? measured = MeasureRingAspect(shot);
        double expected = ExpectedRingAspect();
        if (measured == null)
        {
            GD.PrintErr($"RING {label}: not found in screenshot");
            _failures++;
            return;
        }

        bool ok = Math.Abs(measured.Value - expected) <= RingTolerance * expected;
        if (!ok) _failures++;
        GD.Print($"RING {label,-28} measured {measured:0.000}  predicted {expected:0.000}  {(ok ? "ok" : "MISMATCH")}");
    }

    private static double? MeasureRingAspect(Image shot)
    {
        byte[] data = shot.GetData();
        int width = shot.GetWidth();
        int height = shot.GetHeight();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                if (data[i] > 200 && data[i + 1] < 70 && data[i + 2] > 200)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0 || maxY <= minY) return null;
        return (double)(maxX - minX) / (maxY - minY);
    }

    private double ExpectedRingAspect()
    {
        double lat = Mathf.DegToRad(Map.LatitudeDeg);
        double radius = Map.TargetRingRadiusKm / MapProjection.EarthRadiusKm;
        Vector2 viewport = Map.GetViewport().GetVisibleRect().Size;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

        for (int step = 0; step < 7200; step++)
        {
            var (ringLat, ringDLon) = MapProjection.Destination(lat, Mathf.DegToRad(step * 0.05), radius);
            Vector3 world = Map.GeoToWorld(Mathf.RadToDeg(ringLat), Map.LongitudeDeg + Mathf.RadToDeg(ringDLon));
            // The screenshot can only show the part of the ring that is on screen.
            Vector2 screen = Map.Camera.UnprojectPosition(world);
            if (screen.X < 0f || screen.Y < 0f || screen.X > viewport.X || screen.Y > viewport.Y) continue;
            minX = Math.Min(minX, screen.X);
            maxX = Math.Max(maxX, screen.X);
            minY = Math.Min(minY, screen.Y);
            maxY = Math.Max(maxY, screen.Y);
        }

        return (maxX - minX) / (maxY - minY);
    }

    private void SaveSheet(string fileName, List<Image> cells, int columns)
    {
        int rows = (cells.Count + columns - 1) / columns;
        Image sheet = Image.CreateEmpty(columns * CellWidth, rows * CellHeight, false, Image.Format.Rgb8);

        for (int i = 0; i < cells.Count; i++)
        {
            Image cell = cells[i];
            cell.Resize(CellWidth, CellHeight, Image.Interpolation.Lanczos);
            sheet.BlitRect(cell, new Rect2I(0, 0, CellWidth, CellHeight),
                new Vector2I(i % columns * CellWidth, i / columns * CellHeight));
        }

        string path = _folder.PathJoin(fileName);
        Error error = sheet.SavePng(path);
        GD.Print($"SHEET {path} {error}");
        if (error != Error.Ok) _failures++;
    }
}
