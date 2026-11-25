using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Tns.Core.Terrain;

namespace Tns.Map.Terrain;

/// <summary>
/// Draws the map as terrain tiles. Each frame it walks the tile tree from the two top tiles,
/// splitting any tile that looks too big on screen, and draws the tiles it ends up with.
///
/// Three kinds of tile describe the ground, each in its own packs and each only as detailed as
/// that place needs: heights, colour, and water (where the coast is). A drawn tile takes each
/// from its own tile if one exists and has loaded, else from the nearest loaded tile above it,
/// so there are never holes, and a place can have fine coastlines over coarse heights.
///
/// The same tiles serve the flat map and the globe: the shader folds them.
/// </summary>
public partial class TerrainRenderer : Node3D
{
    private const int GridQuads = 64;
    private const int DetailGridQuads = 128;
    private const int DetailGridStartLevel = 8;

    [Export] public ShaderMaterial Material;
    /// <summary>A tile is split into four when it would appear larger than this on screen.</summary>
    [Export] public double TilePixels = 256.0;
    /// <summary>How many tiles of each kind can be held on the graphics card at once.</summary>
    [Export] public int HeightLayers = 448;
    [Export] public int CoverLayers = 800;
    [Export] public int ColourLayers = 512;
    [Export] public int WaterLayers = 512;
    [Export] public int DetailLayers = 512;
    [Export] public int MaxUploadsPerFrame = 6;
    [Export] public int MaxTilesDrawn = 600;
    /// <summary>
    /// Tiles deeper than this level are not used even where they exist. Lower it to see what
    /// each level adds, or as a low-detail setting for weak machines.
    /// </summary>
    [Export] public int MaxLevel = TileId.MaxSupportedLevel;
    /// <summary>
    /// Tiles down to this level are loaded at the start and kept for good (42 of each kind at
    /// level 2), so a place whose own tile is still loading shows a coarse picture, never a blank
    /// one: zooming out fast otherwise flashes grey rectangles.
    /// </summary>
    [Export] public int PinnedLevel = 2;

    /// <summary>
    /// Fast loading: fewer tiles to load, for slower machines or disks, keeping full detail where
    /// it matters most. Two things change:
    /// - Towards the edges of the screen, a tile may look up to <see cref="FastEdgeFactor"/> times
    ///   larger before it is split, so the middle of the screen, where you are looking, stays as
    ///   sharp as ever and the edges and the horizon use coarser tiles.
    /// - The extra-sharp distant colour and colour detail (levels 7 and 8, in the squares listed
    ///   for flat open ground and rainforests) are not used; they stop at
    ///   <see cref="FastColourMaxLevel"/>. Heights, land cover, coasts and the ground textures keep
    ///   their full detail everywhere.
    /// O toggles it in game (MapInput); -- --fast-loading starts with it on.
    /// </summary>
    [Export] public bool FastLoading;

    /// <summary>In fast loading, how many times larger a tile may look at the screen's edge before it is split.</summary>
    [Export] public double FastEdgeFactor = 2.0;

    /// <summary>In fast loading, the deepest distant colour and colour detail tiles used.</summary>
    [Export] public int FastColourMaxLevel = 6;

    /// <summary>The deepest level of a kind of tile used, given the quality setting.</summary>
    private int LevelCap(Layer layer) =>
        FastLoading && (layer == Colour || layer == Detail) ? Math.Min(MaxLevel, FastColourMaxLevel) : MaxLevel;

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>
    /// Use the regional versions of the close-up ground textures (ground/regional, chosen by
    /// ground/regions.png); false, or -- --ground-standard, uses the standard one everywhere.
    /// Kept to compare the two; remove once one is chosen.
    /// </summary>
    [Export] public bool RegionalGroundTextures = true;

    /// <summary>Compress the ground textures on the graphics card (BC7): about a quarter of the memory.</summary>
    [Export] public bool CompressGroundTextures = true;

    /// <summary>One kind of tile: its packs, its texture on the graphics card, and how much of it a picture needs.</summary>
    public sealed class Layer
    {
        public string Name;
        public TerrainTileCache Cache;
        public StringName Uniform;
        /// <summary>True if places may have none of this kind (land cover over open sea); the shader is told with layer -1.</summary>
        public bool Optional;
        /// <summary>Set when the layer cannot be drawn (land cover without its colour map).</summary>
        public bool Disabled;
        public readonly HashSet<int> InUse = new();
        /// <summary>The most different tiles of this kind one picture has needed.</summary>
        public int PeakInUse;
        public TerrainPackSet Packs => Cache.Packs;
        public double MemoryMb => Cache.Layers * (double)Cache.BytesPerLayer / (1 << 20);
    }

    public Layer Heights { get; private set; }
    public Layer Cover { get; private set; }
    public Layer Colour { get; private set; }
    public Layer Water { get; private set; }
    public Layer Detail { get; private set; }
    public IReadOnlyList<Layer> Layers => _layers;

    /// <summary>True when colour and water packs were found; otherwise the map shows an error.</summary>
    public bool HasSurface { get; private set; }

    /// <summary>Base packs with no tiles at all (not installed or not baked); the map shows an error naming them.</summary>
    public IReadOnlyList<string> MissingPacks { get; private set; } = Array.Empty<string>();

    /// <summary>Graphics memory reserved for all kinds of tile.</summary>
    public double MemoryMb => _layers.Sum(l => l.MemoryMb);

    /// <summary>Tiles drawn this frame.</summary>
    public int TilesDrawn { get; private set; }
    /// <summary>How many times this frame a tile had nothing of a kind to show, not even a coarse version (drawn blank grey).</summary>
    public int BlankTiles { get; private set; }
    private int _blankThisFrame;
    private int _fallbackThisFrame;

    /// <summary>How many times this frame a tile was drawn from a coarser stand-in because its own wasn't loaded yet, counting each kind of data separately.</summary>
    public int FallbackWindows { get; private set; }
    /// <summary>Deepest tile level drawn this frame.</summary>
    public int DeepestLevelDrawn { get; private set; }
    /// <summary>True once every tile on screen is showing its own, final heights, colour and water.</summary>
    public bool Settled { get; private set; }
    public TerrainPackSet Packs => Heights.Packs;
    public int LoadedTiles => _layers.Sum(l => l.Cache.LoadedTiles);

    private MapView _view;
    private readonly List<Layer> _layers = new();
    private ArrayMesh _grid;
    private ArrayMesh _detailGrid;
    private readonly List<MeshInstance3D> _pool = new();
    private readonly List<TileId> _selected = new();

    /// <summary>The tiles chosen for this frame (for tests).</summary>
    public IReadOnlyList<TileId> SelectedTiles => _selected;
    private readonly Stack<TileId> _stack = new();
    private readonly Vector4[] _windows = new Vector4[5];
    private Texture2DArray _coverColours;
    private long _frame;
    private int _deepestAvailable;

    private static readonly StringName TileRectName = "tile_rect";
    private static readonly StringName TileLevelName = "tile_level";

    public void Setup(MapView view)
    {
        _view = view;
        Heights = AddLayer("heights", "terrain", HeightTile.FileExtension, HeightLayers, Image.Format.R16,
            TerrainTileFormats.DecodeHeights, TerrainTileFormats.BlankHeights, "height_window", "height_tiles");
        Cover = AddLayer("cover", "cover", SurfaceTile.CoverExtension, CoverLayers, Image.Format.R8,
            TerrainTileFormats.DecodeCover, TerrainTileFormats.BlankWater, "cover_window", "cover_tiles");
        Cover.Optional = true;
        Colour = AddLayer("colour", "surface", SurfaceTile.ColourExtension, ColourLayers, Image.Format.Rgba8,
            TerrainTileFormats.DecodeColour, TerrainTileFormats.BlankColour, "colour_window", "colour_tiles");
        Water = AddLayer("water", "water", SurfaceTile.WaterExtension, WaterLayers, Image.Format.R8,
            TerrainTileFormats.DecodeWater, TerrainTileFormats.BlankWater, "water_window", "water_tiles");
        Detail = AddLayer("detail", "detail", SurfaceTile.DetailExtension, DetailLayers, Image.Format.Rgba8,
            TerrainTileFormats.DecodeDetail, TerrainTileFormats.BlankDetail, "detail_window", "detail_tiles");
        HasSurface = Colour.Packs.TileCount > 0 && Water.Packs.TileCount > 0;
        _coverColours = LoadCoverColours();
        if (_coverColours != null) Material.SetShaderParameter("cover_colours", _coverColours);
        else Cover.Disabled = true;
        LoadGroundTextures();
        _deepestAvailable = _layers.Max(l => l.Packs.MaxLevel);

        _grid = MapMeshBuilder.BuildTileGrid(GridQuads);
        _detailGrid = MapMeshBuilder.BuildTileGrid(DetailGridQuads);
        Material.SetShaderParameter("height_decode", TerrainTileFormats.HeightShaderDecode);
        Material.SetShaderParameter("use_surface", HasSurface);

        MissingPacks = _layers.Where(l => l.Packs.TileCount == 0).Select(l => l.Name).ToArray();
        if (MissingPacks.Count > 0) ShowMissingPacks();

        // -- --pinned-level=N overrides the setting, to compare (-1 keeps nothing loaded for good).
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--pinned-level=") && int.TryParse(arg["--pinned-level=".Length..], out int level)) PinnedLevel = level;
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        int pinned = _layers.Where(l => !l.Disabled).Sum(l => l.Cache.LoadPinned(PinnedLevel));
        GD.Print($"Terrain: {pinned} coarse tiles (levels 0-{PinnedLevel}) loaded for good in {timer.ElapsedMilliseconds} ms.");
    }

    /// <summary>An error on screen, and in the log, naming the packs that are not there.</summary>
    private void ShowMissingPacks()
    {
        string folders = string.Join(", ", MissingPacks.Select(n => n switch
        {
            "heights" => "terrain", "colour" => "surface", _ => n,
        }));
        string message =
            $"Terrain data is missing: no {string.Join(", ", MissingPacks)} tiles were found.\n" +
            $"The game needs these folders in data/base: {folders}.\n" +
            "Bake them with tools/terrain (see docs/terrain.md), or copy them from a build of the game.";
        GD.PushError(message);
        var layer = new CanvasLayer { Layer = 100 };
        var label = new Label
        {
            Text = message,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontSizeOverride("font_size", 24);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 8);
        layer.AddChild(label);
        AddChild(layer);
    }

    /// <summary>
    /// The colour of each kind of ground (cover_colours.png, beside the land cover tiles): one
    /// world map per kind stacked top to bottom. A mod's copy replaces the base one.
    /// </summary>
    private Texture2DArray LoadCoverColours()
    {
        const int width = 720, height = 360, kinds = 11;
        for (int i = Cover.Packs.Roots.Count - 1; i >= 0; i--)
        {
            string path = Path.Combine(Cover.Packs.Roots[i], "cover_colours.png");
            if (!File.Exists(path)) continue;
            Image atlas = Image.LoadFromFile(path);
            if (atlas == null || atlas.GetWidth() != width || atlas.GetHeight() != height * kinds)
            {
                GD.PushWarning($"Terrain: {path} should be {width} x {height * kinds} pixels; land cover is not drawn.");
                return null;
            }

            atlas.Convert(Image.Format.Rgba8);
            var layers = new Godot.Collections.Array<Image>();
            for (int k = 0; k < kinds; k++) layers.Add(atlas.GetRegion(new Rect2I(0, k * height, width, height)));
            var texture = new Texture2DArray();
            texture.CreateFromImages(layers);
            return texture;
        }

        if (Cover.Packs.TileCount > 0) GD.PushWarning("Terrain: land cover tiles found without cover_colours.png; they are not drawn.");
        return null;
    }

    /// <summary>Kinds of ground in land cover order (Cover in the terrain tool).</summary>
    private static readonly string[] KindNames =
        { "tree", "shrub", "grass", "crop", "built", "bare", "snow", "water", "wetland", "mangrove", "moss" };

    private Texture2DArray _groundTextures;
    private bool _showGroundTextures = true;

    /// <summary>Whether ground textures were found and loaded.</summary>
    public bool HasGroundTextures => _groundTextures != null;

    /// <summary>
    /// Whether the close-up ground textures are drawn (P in game, for comparing with and without).
    /// Has no effect when none were loaded.
    /// </summary>
    public bool ShowGroundTextures
    {
        get => _showGroundTextures && HasGroundTextures;
        set
        {
            _showGroundTextures = value;
            Material.SetShaderParameter("use_ground_textures", ShowGroundTextures);
        }
    }

    /// <summary>
    /// The close-up ground textures: ground/&lt;kind&gt;.png beside the cover folder of the base
    /// data or of a mod (a mod's copy of a kind replaces the base one). Each is the fine light and
    /// dark of real ground of that kind, as a multiplier times 128 (make_ground_textures.ps1). Kinds
    /// without one get none. All must be the same size.
    /// </summary>
    private void LoadGroundTextures()
    {
        // -- --ground-standard: the standard textures only, to compare with the regional ones.
        bool regional = RegionalGroundTextures && !OS.GetCmdlineUserArgs().Contains("--ground-standard");
        // -- --ground-uncompressed: to compare compressed with uncompressed.
        bool compress = CompressGroundTextures && !OS.GetCmdlineUserArgs().Contains("--ground-uncompressed");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var images = new Godot.Collections.Array<Image>();
        Vector2I size = default;
        var folders = Cover.Packs.Roots
            .Select(root => Path.Combine(Path.GetDirectoryName(root.TrimEnd('/', '\\')) ?? "", "ground"))
            .Reverse().ToList(); // a mod's first, then the base

        // A texture's layer, loading it the first time; -1 if no folder has it.
        int Load(string relative)
        {
            foreach (string folder in folders)
            {
                string path = Path.Combine(folder, relative);
                if (!File.Exists(path)) continue;
                // Compressing takes about 0.4 s a texture, so the result is kept in the user folder,
                // named after the file's path, size and time, and made again when the file changes.
                var file = new FileInfo(path);
                string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(file.FullName)))[..12];
                string cached = $"user://ground_cache/{key}_{file.Length}_{file.LastWriteTimeUtc.Ticks}.res";
                Image image = compress && File.Exists(ProjectSettings.GlobalizePath(cached)) ? ResourceLoader.Load<Image>(cached) : null;
                if (image == null)
                {
                    image = Image.LoadFromFile(path);
                    if (image == null) return -1;
                    image.Convert(Image.Format.Rgba8);
                    image.GenerateMipmaps();
                    if (compress)
                    {
                        image.Compress(Image.CompressMode.Bptc);
                        Directory.CreateDirectory(ProjectSettings.GlobalizePath("user://ground_cache"));
                        ResourceSaver.Save(image, cached);
                    }
                }

                if (images.Count > 0 && image.GetSize() != size)
                {
                    GD.PushWarning($"Terrain: {path} is {image.GetSize()}, not {size} like the other ground textures; left out.");
                    return -1;
                }

                size = image.GetSize();
                images.Add(image);
                return images.Count - 1;
            }

            return -1;
        }

        var layerOfKind = KindNames.Select(kind => Load(kind + ".png")).ToArray();
        if (images.Count == 0) return;
        int standard = images.Count;

        // How much ground each kind's texture covers: sizes.txt ("<kind> <km>" lines) in the folder
        // its texture came from; 44 km if not given. Its regional versions cover the same.
        var kindKm = KindNames.Select(kind =>
        {
            string folder = folders.FirstOrDefault(f => File.Exists(Path.Combine(f, kind + ".png")));
            string sizes = folder == null ? null : Path.Combine(folder, "sizes.txt");
            if (sizes == null || !File.Exists(sizes)) return 44f;
            foreach (string line in File.ReadLines(sizes))
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && p[0] == kind && float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float km)) return km;
            }

            return 44f;
        }).ToArray();
        Material.SetShaderParameter("ground_kind_km", kindKm);

        // Regional versions: regions.png says which region and zone each square is in, regions.txt
        // names them, and regional/<kind>_<name>.png is the version for that kind there.
        var regionLayer = Enumerable.Repeat(-1, KindNames.Length * 16).ToArray();
        var zoneLayer = Enumerable.Repeat(-1, KindNames.Length * 16).ToArray();
        string mapFolder = regional ? folders.FirstOrDefault(f => File.Exists(Path.Combine(f, "regions.png"))) : null;
        if (mapFolder != null && File.Exists(Path.Combine(mapFolder, "regions.txt")))
        {
            foreach (string line in File.ReadLines(Path.Combine(mapFolder, "regions.txt")))
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 3 || p[0].StartsWith('#') || !int.TryParse(p[1], out int id)) continue;
                bool zone = p[0] == "zone";
                if (!zone && p[0] != "region") continue;
                if (id <= 0 || id >= 16) continue;
                for (int k = 0; k < KindNames.Length; k++)
                {
                    if (layerOfKind[k] < 0) continue; // regional versions only for kinds with a standard one
                    int layer = Load(Path.Combine("regional", $"{KindNames[k]}_{p[2]}.png"));
                    if (zone) zoneLayer[k * 16 + id] = layer;
                    else regionLayer[k * 16 + id] = layer;
                }
            }

            Image map = Image.LoadFromFile(Path.Combine(mapFolder, "regions.png"));
            map.Convert(Image.Format.Rgb8);
            Material.SetShaderParameter("ground_regions", ImageTexture.CreateFromImage(map));
            Material.SetShaderParameter("ground_region_layer", regionLayer);
            Material.SetShaderParameter("ground_zone_layer", zoneLayer);
            Material.SetShaderParameter("use_ground_regions", images.Count > standard);
        }

        _groundTextures = new Texture2DArray();
        _groundTextures.CreateFromImages(images);
        Material.SetShaderParameter("ground_textures", _groundTextures);
        Material.SetShaderParameter("ground_layer", layerOfKind);
        Material.SetShaderParameter("use_ground_textures", _showGroundTextures);
        GD.Print($"Terrain: {standard} standard ground textures ("
                 + string.Join(", ", KindNames.Where((_, k) => layerOfKind[k] >= 0)) + $") and {images.Count - standard} regional, "
                 + $"{size.X} x {size.Y}{(compress ? ", compressed" : "")}, in {timer.ElapsedMilliseconds} ms.");
    }

    private Layer AddLayer(string name, string folder, string extension, int layers, Image.Format format,
        Func<byte[], byte[]> decode, byte[] blank, string windowUniform, string textureUniform, int size = TerrainTileFormats.Texels)
    {
        var layer = new Layer
        {
            Name = name,
            Cache = new TerrainTileCache(FindPacks(folder, extension, name), layers, format, decode, blank, size),
            Uniform = windowUniform,
        };
        Material.SetShaderParameter(textureUniform, layer.Cache.Texture);
        _layers.Add(layer);
        return layer;
    }

    /// <summary>
    /// The base pack, then every mod's pack of the same kind in name order. Later packs replace
    /// tiles of earlier ones. In the editor these sit in the project folder; in an exported
    /// game, beside the executable.
    /// </summary>
    private static TerrainPackSet FindPacks(string folder, string extension, string kind)
    {
        string root = OS.HasFeature("editor")
            ? ProjectSettings.GlobalizePath("res://")
            : OS.GetExecutablePath().GetBaseDir();
        // -- --data-base=<folder> reads the base packs from elsewhere, for testing other bakes.
        string baseFolder = Path.Combine(root, "data", "base");
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--data-base=")) baseFolder = arg["--data-base=".Length..];
        }

        var packs = new TerrainPackSet(extension);
        int baseTiles = packs.AddPack(Path.Combine(baseFolder, folder));
        GD.Print($"Terrain: base {kind} pack has {baseTiles} tiles.");

        string mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods))
        {
            foreach (string mod in Directory.EnumerateDirectories(mods).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                string pack = Path.Combine(mod, folder);
                if (!Directory.Exists(pack)) continue;
                int tiles = packs.AddPack(pack);
                GD.Print($"Terrain: mod \"{Path.GetFileName(mod)}\" adds or replaces {tiles} {kind} tiles.");
            }
        }

        return packs;
    }

    /// <summary>Chooses and positions this frame's tiles. Called by MapView after the view changes.</summary>
    public void Refresh()
    {
        if (_view == null) return;
        _frame++;
        foreach (Layer layer in _layers)
        {
            layer.Cache.Upload(_frame, MaxUploadsPerFrame);
            layer.InUse.Clear();
        }

        SelectTiles();
        _blankThisFrame = 0;
        _fallbackThisFrame = 0;

        bool settled = _layers.All(l => !l.Cache.Busy);
        int used = 0;
        int deepest = 0;
        double viewLon = _view.LongitudeDeg;

        foreach (TileId id in _selected)
        {
            for (int i = 0; i < _layers.Count; i++) _windows[i] = Window(_layers[i], id, ref settled);

            // Longitude relative to the view. A tile crossing the far side of the world from the
            // view is drawn twice, once at each end of the flat map; the shader trims each copy.
            double west = WrapDegrees(id.West - viewLon);
            int copies = west + id.SizeDeg > 180.0 + 1e-9 ? 2 : 1;
            for (int copy = 0; copy < copies; copy++)
            {
                if (used >= MaxTilesDrawn) break;
                MeshInstance3D instance = Instance(used++);
                ArrayMesh mesh = id.Level >= DetailGridStartLevel ? _detailGrid : _grid;
                if (instance.Mesh != mesh) instance.Mesh = mesh;
                double copyWest = west - copy * 360.0;
                instance.SetInstanceShaderParameter(TileRectName, new Vector4(
                    (float)Mathf.DegToRad(copyWest),
                    (float)Mathf.DegToRad(id.South),
                    (float)Mathf.DegToRad(id.SizeDeg),
                    (float)Mathf.DegToRad(id.SizeDeg)));
                for (int i = 0; i < _layers.Count; i++) instance.SetInstanceShaderParameter(_layers[i].Uniform, _windows[i]);
                instance.SetInstanceShaderParameter(TileLevelName, (float)id.Level);
            }

            if (id.Level > deepest) deepest = id.Level;
        }

        for (int i = used; i < _pool.Count; i++) _pool[i].Visible = false;
        foreach (Layer layer in _layers) layer.PeakInUse = Math.Max(layer.PeakInUse, layer.InUse.Count);
        TilesDrawn = used;
        BlankTiles = _blankThisFrame;
        FallbackWindows = _fallbackThisFrame;
        DeepestLevelDrawn = deepest;
        Settled = settled;
    }

    /// <summary>
    /// Where a drawn tile reads one kind of data: (x offset, y offset, scale, texture layer).
    /// The tile's own if loaded, else a corner of the nearest loaded tile above it. Asks for
    /// whatever is missing.
    /// </summary>
    private Vector4 Window(Layer layer, TileId id, ref bool settled)
    {
        var none = new Vector4(0f, 0f, 1f, layer.Optional ? -1f : 0f);
        if (layer.Disabled) return none;
        TileId source = id;
        int found = -1;
        TileId? finestMissing = null, coarsestMissing = null;
        while (true)
        {
            bool exists = source.Level <= LevelCap(layer) && layer.Packs.Has(source);
            if (exists && layer.Cache.TryGetLayer(source, _frame, out int index))
            {
                found = index;
                break;
            }

            if (exists)
            {
                finestMissing ??= source;
                coarsestMissing = source;
            }

            if (source.Level == 0) break;
            source = source.Parent;
        }

        if (coarsestMissing.HasValue)
        {
            // Wanted but not loaded yet. Load the coarsest missing tile first so the picture
            // sharpens step by step, and the tile itself alongside it.
            layer.Cache.Request(coarsestMissing.Value);
            layer.Cache.Request(finestMissing.Value);
            settled = false;
        }

        if (found < 0)
        {
            // Nothing of this kind loaded for this place or anywhere above it: it draws blank.
            if (!layer.Optional && layer.Packs.TileCount > 0) _blankThisFrame++;
            return none;
        }

        // A stand-in only when a finer tile of this kind exists but hasn't arrived yet; where none
        // is stored, drawing the coarser one is the design, not a wait.
        if (finestMissing.HasValue) _fallbackThisFrame++;
        layer.InUse.Add(found);
        double scale = 1.0 / (1 << (id.Level - source.Level));
        return new Vector4(
            (float)((id.West - source.West) / source.SizeDeg * HeightTile.Intervals),
            (float)((id.South - source.South) / source.SizeDeg * HeightTile.Intervals),
            (float)scale,
            found);
    }


    private MeshInstance3D Instance(int index)
    {
        while (_pool.Count <= index)
        {
            float extent = 1e6f;
            var instance = new MeshInstance3D
            {
                Mesh = _grid,
                MaterialOverride = Material,
                // The shader moves every vertex, so the mesh's own bounds mean nothing.
                CustomAabb = new Aabb(Vector3.One * -extent, Vector3.One * extent * 2f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(instance);
            _pool.Add(instance);
        }

        _pool[index].Visible = true;
        return _pool[index];
    }

    private void SelectTiles()
    {
        _selected.Clear();
        _stack.Clear();
        _stack.Push(new TileId(0, 0, 0));
        _stack.Push(new TileId(0, 1, 0));
        int deepestAvailable = Math.Max(Math.Min(_deepestAvailable, MaxLevel), 2);

        while (_stack.Count > 0)
        {
            TileId id = _stack.Pop();
            if (!Measure(id, out double pixels, out double edge)) continue;
            // In fast loading, tiles towards the edge of the screen may look larger before they split.
            double limit = TilePixels * (FastLoading ? 1.0 + (FastEdgeFactor - 1.0) * SmoothStep(0.35, 1.0, edge) : 1.0);

            // Split while the tile looks too big, as long as finer heights, colour or water
            // exist below it. The top levels are always split so the flat map's curved edges
            // stay smooth.
            bool split = id.Level < deepestAvailable
                         && pixels > limit
                         && (id.Level < 2 || FinerBelow(id));
            if (split && _selected.Count + _stack.Count < MaxTilesDrawn)
            {
                for (int i = 0; i < 4; i++) _stack.Push(id.Child(i & 1, i >> 1));
            }
            else
            {
                _selected.Add(id);
            }
        }
    }

    private bool FinerBelow(TileId id)
    {
        foreach (Layer layer in _layers)
        {
            if (!layer.Disabled && id.Level < LevelCap(layer) && layer.Packs.MaxDetailLevelFor(id) > id.Level) return true;
        }

        return false;
    }

    /// <summary>
    /// Decides whether a tile can be seen and roughly how large it appears. Size is judged from
    /// the camera's distance to the nearest part of the tile, so tiles tilted away near the
    /// horizon stay coarse.
    /// </summary>
    /// <summary>For tests: follows the tiles containing a place down from level 0 the way
    /// SelectTiles does, and says where the place was dropped.</summary>
    public string ExplainPlace(double lat, double lon)
    {
        var id = new TileId(0, lon < 0 ? 0 : 1, 0);
        var text = new System.Text.StringBuilder();
        var p = _view.ProjectRelative(lat, WrapDegrees(lon - _view.LongitudeDeg));
        text.Append($"  the place itself projects to {p.X:0},{p.Y:0}, distance {p.Distance:0} km, facing={p.Facing}\n");
        while (true)
        {
            _explain = text;
            bool visible = Measure(id, out double pixels, out _);
            _explain = null;
            text.Append($"  {id} visible={visible} pixels={pixels:0}\n");
            if (!visible) return text.ToString();
            bool split = id.Level < Math.Max(Math.Min(_deepestAvailable, MaxLevel), 2) && pixels > TilePixels && (id.Level < 2 || FinerBelow(id));
            if (!split) return text.Append("  selected here\n").ToString();
            int cx = lon >= id.West + id.SizeDeg * 0.5 ? 1 : 0, cy = lat >= id.South + id.SizeDeg * 0.5 ? 1 : 0;
            id = id.Child(cx, cy);
        }
    }

    private System.Text.StringBuilder _explain;

    private bool Measure(TileId id, out double pixels, out double edge)
    {
        pixels = 0.0;
        edge = 1.0;
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        double marginX = viewport.X * 0.15, marginY = viewport.Y * 0.15;
        double viewLat = _view.LatitudeDeg;
        double west = WrapDegrees(id.West - _view.LongitudeDeg);
        int copies = west + id.SizeDeg > 180.0 + 1e-9 ? 2 : 1;
        bool visible = false;
        double nearest = double.MaxValue;

        for (int copy = 0; copy < copies; copy++)
        {
            // The part of this copy that lies on the map, in longitude relative to the view.
            double w = Math.Max(west - copy * 360.0, -180.0);
            double e = Math.Min(west - copy * 360.0 + id.SizeDeg, 180.0);
            if (e <= w) continue;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool anyFacing = false;

            void Consider(double lat, double dlon)
            {
                var (x, y, distance, facing) = _view.ProjectRelative(lat, dlon);
                // A point beyond the horizon still counts towards where the tile lands on screen.
                // A tile reaching over the horizon shows the strip between its near points and the
                // horizon, and that strip lies between where its near and far points project.
                // Leaving the far ones out wrongly dropped such tiles when the camera was tilted
                // and their near points happened to be off screen.
                if (facing)
                {
                    anyFacing = true;
                    if (distance < nearest) nearest = distance;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            // A 3 x 3 spread of points, plus the point of the tile closest to where the camera
            // is looking. Without that last one, a tile much larger than the screen could have
            // all nine points off screen and be wrongly skipped.
            for (int j = 0; j <= 2; j++)
            {
                for (int i = 0; i <= 2; i++) Consider(id.South + id.SizeDeg * j * 0.5, w + (e - w) * i * 0.5);
            }

            Consider(Math.Clamp(viewLat, id.South, id.North), Math.Clamp(0.0, w, e));

            _explain?.Append($"    copy {copy}: facing={anyFacing} x {minX:0}..{maxX:0} y {minY:0}..{maxY:0} (screen {viewport.X:0} x {viewport.Y:0})\n");
            if (!anyFacing) continue;
            if (maxX < -marginX || minX > viewport.X + marginX || maxY < -marginY || minY > viewport.Y + marginY) continue;
            visible = true;
            // How far from the middle of the screen the tile's nearest part is: 0 in the middle,
            // 1 at a corner.
            double cx = Math.Clamp(viewport.X * 0.5, minX, maxX) - viewport.X * 0.5;
            double cy = Math.Clamp(viewport.Y * 0.5, minY, maxY) - viewport.Y * 0.5;
            edge = Math.Min(edge, Math.Sqrt(cx * cx + cy * cy) / (0.5 * Math.Sqrt(viewport.X * viewport.X + viewport.Y * viewport.Y)));
        }

        if (!visible) return false;
        double tileKm = id.SizeDeg * 111.19;
        pixels = tileKm / Math.Max(nearest, 1e-3) * _view.PixelsPerUnitAtDistanceOne;
        return true;
    }

    private static double WrapDegrees(double degrees)
    {
        degrees = (degrees + 180.0) % 360.0;
        if (degrees < 0.0) degrees += 360.0;
        return degrees - 180.0;
    }
}
