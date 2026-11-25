# TNS Godot

This is the Godot 4.7 (.NET) version of The New Suzerain. So far it has one thing in it: the
folding world map. You start on a flat map (in the Winkel tripel projection), and as you zoom in
it folds up into a globe around whatever point you're looking at.

## Windows and Mac

The game and its docs work on both Windows and macOS; the scripts in `tools/terrain` are for
Windows only.

| | Windows | Mac |
| --- | --- | --- |
| Godot | The Godot 4.7 .NET build for Windows | The Godot 4.7 .NET build for macOS |
| .NET | The .NET 8 SDK | The .NET 8 SDK |
| Running the game from the editor | F5 | Cmd+B |
| Running Godot from a terminal | `godot` (or the full path to the `.exe`) | `/Applications/Godot_mono.app/Contents/MacOS/Godot` (or wherever you put the app) |
| Graphics | Direct3D 12 | Metal, chosen automatically |
| Getting the baked packs | `download_packs.ps1` (see "Running") | Download the five zips from the GitHub release page by hand and unzip them into `data/base` |
| Baking terrain, making ground textures | The scripts in `tools/terrain` | Not done on a Mac |

The game has nothing Windows-specific in its code, though it hasn't been tried on a Mac yet. The
docs are plain Markdown with PNG pictures, so they read the same in any Markdown viewer (GitHub,
VS Code, Obsidian and so on). The parts about baking are written for Windows.

## Running

The baked terrain the map is drawn from (2.7 GB, over 250,000 files) isn't in git, because it's
far too big. It's attached to a GitHub release instead. After cloning, download it once (on a Mac,
see the table above):

1. Install the GitHub command-line tool (`winget install GitHub.cli`) and sign in to GitHub with it
   (`gh auth login`). The download script uses it to fetch the release.
2. From `tools/terrain`, run `download_packs.ps1`:

   ```
   powershell -ExecutionPolicy Bypass -File download_packs.ps1
   ```

   It downloads the release named in `tools/terrain/pack_release.txt`, unpacks the five packs into
   `data/base` and checks that every file arrived. It takes a few minutes, depending on your
   connection.

Then open the project in the Godot .NET editor and run it (F5 on Windows, Cmd+B on a Mac). Or,
from this folder:

```
dotnet build
godot --path .
```

## Controls

| Input | What it does |
| --- | --- |
| Left or middle drag, WASD, arrow keys | Pan around |
| Mouse wheel | Zoom. Zooming in heads towards the cursor, and the fold follows the zoom |
| Right drag | Tilt the view up and down or side to side; it eases back when you let go |
| Space | Play the fold back and forth |
| F, then Q and E | Turn manual folding on or off, then fold less or more |
| G, T, R | Show the grid, the distortion circles, or the ring around the view target |
| L | Outline the terrain tiles and tint them by level |
| P | Turn the ground textures on or off, to compare the close-up ground with and without them (the debug readout shows which) |
| O | Switch between full detail and fast loading (the debug readout shows which) |
| 1 to 5 | Jump to a test place |
| F3 | Show or hide the debug readout in the top left. To start with it hidden, run the game with `-- --no-hud`, or untick **Show At Start** on the `Dev/Hud` node in `scenes/main.tscn` |

## Where things are

```
assets/      Art and data files that Godot imports (empty for now)
scenes/      Scene files (.tscn)
  main.tscn         The starting scene: the map plus the dev tools
  map/              The map scene on its own
shaders/     Shader code, grouped by feature
  map/
src/         C# code, with namespaces Tns.<Folder>
  Core/      Plain C# with no Godot types: maths now, the simulation later
  Core/Terrain/   The terrain tile format, shared by the game and the terrain tool
  Map/       Drawing the map, the camera and input
  Map/Terrain/    Streaming the terrain: picking tiles, loading them, drawing them
  Dev/       Debug overlays and test tools, which aren't part of the game
tools/       Scripts and programs you run by hand to prepare assets
  terrain/          The terrain tool, its recipes and the download scripts (see its README)
data/        Game data read while the game runs; Godot doesn't import this folder
  base/             The baked terrain packs and ground textures (not committed; rebuilt from recipes)
docs/        Explanations and reference pictures; Godot doesn't import this folder
mods/        One folder per mod; for example mods/<name>/terrain replaces base terrain tiles
```

As the game grows, the simulation code will go in `src/Sim/`, the UI in `src/Ui/` and
`scenes/ui/`, networking in `src/Net/`, and game data like countries and provinces under `data/`.

Don't put the map's tile folders anywhere Godot can see them, because Godot tries to import every
file it finds. Either read them from outside the project, or drop an empty `.gdignore` file into a
folder you want Godot to skip.

## Terrain

The map is drawn from terrain tiles streamed from `data/base`. Once the globe has formed and you
keep zooming in, the mountains rise up, at their true height and never exaggerated, and the slope
shading shows at every zoom. Fine tiles are only stored where they'd actually change the shading,
so the great mountain ranges get a height every 150 m while the plains get one every few km.

For the full story, from the downloads to the screen, read `docs/terrain.md`. It describes each
data source, each pack and every design decision. `docs/detail_maps` has a map for each pack
showing what's stored where, and `tools/terrain/README.md` covers how to bake the terrain, how the
detail is chosen and how a mod can change it.

What the ground looks like comes from four more packs: `data/base/cover` says what kind of ground
each place is (forest, fields, city, rock and so on), `data/base/water` says where the water is,
`data/base/surface` holds the blended colour for distant views, and `data/base/detail` brings back
the real colour of the ground where a single kind can't show it, like in deserts. Up close, the
shader draws each kind of ground with sharp edges, using colours measured from satellite pictures,
so nothing blurs, repeats or shifts as you zoom. Those colours have no shadows in them, so the
slope shading is the only light you see. Coastlines, lakes and rivers stay sharp at every zoom.

The packs aren't in git, so you'll need to download them from the release (see "Running" above),
bake them, or copy them from a build. If any of them are missing, the game puts an error on screen
naming the missing folders. See "The surface" in
`tools/terrain/README.md`.

The coarsest tiles, levels 0 to 2 (42 of each kind), are loaded at the start and never unloaded
(that's `PinnedLevel` on the Terrain node). That way, a place whose own tile is still loading
always shows a coarse picture instead of a blank one.

Each kind of tile has its own texture on the graphics card. `HeightLayers`, `CoverLayers`,
`ColourLayers`, `WaterLayers` and `DetailLayers` on the Terrain node set how many tiles each one
can hold, at 128 KB, 64 KB, 256 KB, 64 KB and 256 KB a tile. Every tile is resampled from its
file's 257 by 257 points to 256 by 256 as it loads, keeping the edges exact. That's because the
graphics card pads a layer 257 wide up to the next size, which was costing about 100 MB. The
numbers are set to the most any test view needs, plus a quarter, for about 410 MB in all, and the
screenshot test fails if a view ever needs more.

Which tiles get drawn depends on the view, not the screen. The game measures the window in its
fixed 1600 by 900 frame (`window/size` in project.godot), so 1080p and 1440p load the same tiles
and show the same area; a bigger screen just draws them larger.

**Memory.** Run the screenshot test with `--memory-report` and it prints the game's memory use and
graphics card time at a world view, a country view and a close view. Add `--no-msaa`, `--msaa2` or
`--msaa4` to compare anti-aliasing. Graphics memory comes to about 790 MB at 2560 by 1440 (787 MB on
2026-11-19), of which 410 MB is tiles and about 25 MB the ground textures. An earlier measurement
gave 706 MB; on 2026-11-18 the old and new packs both measured 753 MB, so the packs aren't the
difference, and the cause isn't known. RAM use is about 420 MB. Anti-aliasing is 2x MSAA, because
4x looked the same and cost 60 MB more.

**Other settings.** `MaxLevel` on the Terrain node ignores the finest levels, as a low-detail
option. The sea colours and `cover_sharpness` are settings on the map material, and so is
`procedural_strength`, which adds made-up fine detail to close-ups. It's fixed to the ground and
costs no storage, but it's off by default (0). Run the screenshot test with `--procedural-compare`
to see close-ups with it off and on, along with the graphics card time of each.

**Fast loading.** For a slower machine or disk, fast loading cuts down how many tiles have to be
loaded, while keeping full detail where it matters most. Press **O** in the game to switch it on
and off, set `FastLoading` on the Terrain node to start with it on, or pass `--fast-loading` to the
screenshot test. It changes two things:
- Towards the edges of the screen and the horizon, a tile may look up to twice as big before the
  game swaps in four finer ones (`FastEdgeFactor`). The middle of the screen, where you're
  looking, stays exactly as sharp as before.
- The extra-sharp distant colour and colour detail that flat open ground and the rainforests get
  (levels 7 and 8) is skipped; both stop at level 6 (`FastColourMaxLevel`). Heights, land cover,
  coasts and the ground textures keep their full detail everywhere.

On 2026-11-24 at 2560 by 1440 (`--load-test`), fast loading read about a quarter fewer tiles when
jumping to a new place (164 ms to load it, against 224 ms), 36% fewer while panning a tilted view,
and during a quick zoom from 4,000 km down to 150 km only 4 to 8% of the tiles waited on a blurrier
stand-in, against 10 to 25% at full detail. The main thing you'd notice is that flat open ground,
like the Kazakh steppe or the Sahara, has slightly softer colour mottling from a distance
(`docs/fast_loading.png`).

**Checking how fast tiles load.** `--load-test` with the screenshot test jumps to seven places far
apart and times how long each takes to load completely, then pans at the closest and a country
zoom (and tilted), then zooms quickly from 4,000 km to 150 km in four new places. It prints how
often a tile had to be drawn from a coarser stand-in because its own hadn't arrived yet, how many
tiles were read, and for each kind of tile how long reading, decoding and waiting took. On
2026-11-24 a new place loaded in about 0.2 seconds, and while panning a screen's width every
second, under 1% of tiles were ever waiting.

**Checking for holes when tilted.** `--tilt-sweep` with the screenshot test tilts the camera
sideways and up in five places (mountains and plains) at four zooms, and checks a grid of spots
across the screen to make sure every bit of ground showing is covered by a tile the game chose to
draw. A spot without one would show the background through a hole. Tiles that reached over the
horizon used to be dropped by mistake when the camera was tilted, which showed as tiles popping in
and out at the edge of the screen; the sweep found holes in 22 of 440 views before the fix and none
after.

**Ground textures.** Up close, each kind of ground (farmland, grassland, forest, bushland and
wetland) also gets the fine light and dark of a real satellite picture of a typical place of that
kind: field edges, gullies, the crowns of trees. For example, farmland in Europe shows the fields
of the Beauce in France, and forest north of 52° N shows Russian forest. The textures live in
`data/base/ground` and are made by `tools/terrain/make_ground_textures.ps1`. Decision 16 in
`docs/terrain.md` explains how they're made and laid.
- They only add light and dark around the colour that's already there, and they average out to no
  change, so the map looks the same from further out. They fade in between about 500 km and 250 km.
- Each texture is laid in hexagons, and each hexagon shows a different piece of it, moved, turned
  a little and mirrored, so the copies never line up in a grid.
- Deserts don't get one, because the slope shading from the heights already shows the dunes.
- Press **P** in the game to turn them on and off and compare.
- On the map material, `ground_texture_strength` sets how strong they are and
  `ground_texture_far_km` sets where they start. On the Terrain node, turning off
  `RegionalGroundTextures` uses one texture per kind everywhere, and `CompressGroundTextures`
  compresses them on the graphics card and caches the result in the user folder.
- The screenshot test takes `--no-ground-textures`, `--ground-strength=N`, `--ground-plain` (laid
  like floor tiles, without hexagons), `--ground-standard` (no regional versions) and
  `--ground-uncompressed`.
- A mod can replace a kind's texture with its own `ground/<kind>.png` next to its `cover` folder,
  plus a line in `sizes.txt` saying how much ground it covers (for example `crop 88`).

**Checking that panning is smooth.** Run the screenshot test with `--pan-test` and it pans steadily
over Europe, the Sahara and India, at a country view (600 km) and at the closest zoom (150 km). For
each it prints the frame times: the average, the slowest 5%, the slowest of all, and how many took
over 25 ms. Use it after a change to make sure panning is still smooth. On 2026-11-19 at 2560 by
1440, no frame anywhere took over 16 ms. The ground textures cost nothing at 600 km and about 4 ms
a frame at the closest zoom (Europe went from 11.6 ms without them to 15.6 ms with them).

**Checking the ground everywhere.** `--surface-tour` with the screenshot test draws 18 places around
the world (deltas, deserts, forests, fjords, ice) into `surface_tour.png`, so you can check the
ground everywhere after a bake.

**Seeing what each pack adds.** "The packs" in `docs/terrain.md` describes what each of the five
holds and what it's made from, and "How the packs are combined" explains how the shader puts them
together. To see what one adds, leave it out of the screenshot test with `--no-cover` (the land
cover), `--no-detail` (the detail), `--no-distant-colour` (replaces the land's distant colour with
grey) or `--no-relief` (the slope shading). Combine it with `--views=Name:lat:lon:km` to pick the
places. And `--data-base=<folder>` reads the base packs from another folder, so you can try out a
new bake without replacing the real one.

The data credits are in `CREDITS.md`.

## How the fold works

Every terrain tile is drawn with the same grid mesh. Its vertices don't hold a real position, just
a position inside the tile. The vertex shader (`shaders/map/map_terrain.gdshader`) works out two
positions for each vertex and blends between them:

1. Flat: the Winkel tripel projection, shifted so the view target sits at the origin.
2. Globe: a sphere turned so the view target faces the camera, again with the target at the
   origin.

Because the target is at the origin in both, it never moves on screen while the map folds.
Panning just changes which place sits at the origin. `src/Core/Geo/MapProjection.cs` does the
same maths on the CPU, so keep it in step with `shaders/map/map_projection.gdshaderinc`.

How far the map is folded depends on the zoom: it's flat while more than `FoldStartKm` is visible,
and a full globe below `FoldEndKm` (both are settings on the Map node).

## Screenshot test

```
godot --path . --resolution 1280x720 -- --capture=<output folder>
```

This takes a fixed set of screenshots, saves them as contact sheets, and checks the fold. The ring
around the view target is a true circle on the ground, so its width divided by its height on
screen tells you how distorted the map is there. The test measures that from the pixels and
compares it with the CPU maths. It prints `CAPTURE OK` and exits with code 0 when everything
matches.
