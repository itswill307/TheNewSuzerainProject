# Terrain tools

If you're new to this, read `../../docs/terrain.md` first. It explains the whole process from
start to finish, and this file will make a lot more sense afterwards.

Everything in this folder exists to turn raw elevation and satellite data into the terrain the
game draws, and to let mods change that terrain. The main piece is a small command-line program
that needs nothing but .NET 8.

Run it from this folder:

```
dotnet run -c Release --project TerrainTool -- <command>
```

Baking and the scripts here (`*.ps1`, run with `powershell -ExecutionPolicy Bypass -File
script.ps1`) are meant for Windows, and many of them need GDAL in `C:\Program Files\GDAL`.

| Command | What it does |
| --- | --- |
| `bake <recipe.json> [--clean] [--output folder]` | Builds a height pack from the sources listed in a recipe |
| `map <pack folder> <out.png>` | Draws a world map showing how much detail is stored at each place, for any kind of pack |
| `decisions <pack folder> <out.png>` | Draws, level by level, how much each tile changed the picture compared with the tile above it, measured against its limit (read from the pack's `decisions.csv`) |
| `plan <pack folder> <recipe.json> [--map out.png]` | Predicts what pack a recipe's detail settings would produce, without actually baking it |
| `info <file.tif \| tile.tnsh \| pack folder>` | Describes an elevation file, a single tile, or a whole pack |
| `height <pack folder> <lat> <lon>` | Prints the ground height a pack gives at a place |
| `export <pack folder> <out.tif> <west> <south> <east> <north>` | Saves a region as a height image you can look at or edit |
| `rugged <pack folder> <out.txt> [--squares 2000]` | Lists the steepest 1° squares, the ones that deserve sharper data |
| `surface <surface recipe.json> [--region w,s,e,n] [--update w,s,e,n]` | Paints the ground: builds the land cover, colour, water and detail packs. `--update` rebakes just one area of the existing packs |
| `preview <data folder> <out.png> <west> <south> <east> <north>` | Draws a region from the surface packs, lit the way the game lights it |
| `bland <surface recipe> <data folder> <out.csv> [--sharp folder] [--patches 3] [--detail-level 6]` | Scores every 1° square of land for how bland it looks up close, and what data would help |
| `blandmap <survey.csv> <out.png> [--min-patches 2] [--list out.csv]` | Draws that survey as a world map. `--list` writes out the squares the 500 m picture fixes |
| `groundtex <samples folder> <out folder> [--blur-m 500] [--pixel-m 43] [--soften 2.5] [--pick kind=sample,...]` | Makes the close-up ground textures from satellite samples (normally run by `make_ground_textures.ps1`) |
| `samplepick <worldcover folder> <sites.csv> <out.csv> [--size-km 88]` | Finds the plainest square of each kind of ground near each sample place, for the ground texture samples |
| `compare <pack> <pack>` | Shows how two packs differ, tile by tile, which is handy for checking a rebake: heights, or cover, colour, water or detail, level by level |
| `selftest` | Checks the tool's own reading and writing code |

## How terrain is stored

A **terrain pack** is just a folder of tile files:

```
<pack>/pack.json          what the pack is and how it was baked
<pack>/<level>/<x>/<y>.tnsh
```

- The world is cut into square tiles by latitude and longitude. Level 0 is two tiles, the western
  and eastern hemispheres, and each level down splits every tile into four, doubling the detail.
- Every tile is a grid of 257 by 257 heights in metres. North to south, that's one height every
  611 m at level 7, 305 m at level 8 and 153 m at level 9.
- A finer tile is only stored where it would visibly change the picture (see the next section),
  or where a tile below it is stored. Everywhere else it's left out, and the game draws that area
  from the coarser tile above. That's how the mountains stay sharp without paying to store the
  whole world in detail.
- The file layout is written up at the top of `src/Core/Terrain/HeightTile.cs`.

`docs/detail_maps` has two maps for each pack (heights, land cover, distant colour, water and
detail):

| Map | What it shows | How to make it |
| --- | --- | --- |
| `<pack>.png` | The result: the deepest tile stored at each place | `terrain map ../../data/base/water ../../docs/detail_maps/water.png` |
| `<pack>_decisions.png` | The reasoning: one world map per level, with each tile coloured by how much it changed the picture compared with the tile above, divided by its limit | `terrain decisions ../../data/base/water ../../docs/detail_maps/water_decisions.png` |

Every bake writes a `decisions.csv` into each pack it makes. There's one row for every tile it
looked at, whether it kept it or not. Each row has the measured change (how many square km of
ground would look different without the tile), the limit it was compared with, whether it was
kept, and why: `m` means the measurement decided, `b` means a finer tile below it was kept, `f`
means it's always stored, and `c` means it was capped by a detail area. An `--update` bake only
rewrites the rows it decided again. The game never reads this file, so leave it out of a release.

How to read a decisions map: the colour is the measured change divided by the limit, on a smooth
log scale from 1% up to 100 times, with a break at the limit itself. Blue tiles were under the
limit and left out (dark blue is far under, light blue just under), unless they're striped, which
means they were kept for some other reason. Yellow, orange and red tiles were over the limit and
kept (yellow just over, red far over). Light blue and yellow are the borderline cases that a small
change in the setting would flip. At any place, the deepest level that's warm-coloured or striped
is the level the `<pack>.png` map shows there.

### What each pack measures

Every pack asks the same question about each tile, T. If T were left out, the game would draw its
ground from the tile above, P (its parent, which covers the same ground with half the detail). So
how much ground would then look different? That much ground is the tile's **measured change**, C,
in square kilometres. T is kept when C is over the pack's limit, or when something else keeps it.

For each of T's 257 by 257 points i, the tool reads P's value at the same spot the way the game
would draw it. For heights, colour, water and detail, that means blending between P's nearest
points; for land cover, it means drawing P kind by kind, with the nearest kinds voting. Each point
covers this much ground:

$$A_i = \left(\frac{S}{256} \times 111.2\ \text{km}\right)^2 \cos\varphi_i$$

where S is the tile's size in degrees and φᵢ is the point's latitude. A point counts towards C
when it differs by more than the pack's tolerance. In the formulas below, [ ] is 1 when what's
inside it is true and 0 when it isn't.

| Level | Tile size | Tile area at 45° | One point at 45° |
| --- | --- | --- | --- |
| 4 | 11.25° | 1,106,000 km² | 16.9 km² |
| 5 | 5.6° | 276,600 km² | 4.2 km² |
| 6 | 2.8° | 69,100 km² | 1.05 km² |
| 7 | 1.4° | 17,300 km² | 0.26 km² |
| 8 | 0.7° | 4,320 km² | 0.066 km² |
| 9 | 0.35° | 1,080 km² | 0.016 km² |

**Heights (`terrain`): ground whose slope changes.** The slope, g, is worked out the same way the
shader does it: the height difference between the points on either side, divided by the distance
between them, east to west and north to south. P's slope is taken over P's own point spacing. Here
C counts points rather than adding up their areas, which comes to the same thing within one tile,
since a tile is nearly the same size everywhere inside it:

$$C = \frac{\#\{\, i : \lVert g_T(i) - g_P(i) \rVert > 0.06 \,\}}{257^2} \times A_T \qquad \text{kept if } C > 800\ \text{km}^2$$

- *Tolerance 0.06 (`detailSlope`)*: a change in slope of 6 m in every 100 m. The shading makes
  slopes three times steeper and is drawn at half strength (`ReliefStrength` 0.5), so that changes
  how bright a slope looks on screen by up to about 9%, which your eye notices on a hillside.
- *Limit 800 km² (`detailAreaKm2`)*: that's 5% of a level 7 tile, 19% of a level 8 tile and 74% of
  a level 9 tile. Because the area is fixed, it gets harder to reach at each finer level, so the
  finest tiles are only kept where the slopes change across most of the tile: real mountain
  country.
- *Why these numbers*: they were chosen on 2026-11-13 to cut storage without any visible loss.
  `terrain plan` predicted the pack for several settings: 0.06 / 800 gave 714 MB, down from 1.7 GB
  if you keep everything; 0.06 / 1200 gave 460 MB with almost nothing left at level 9; and
  0.08 / 300 gave 1 GB. The `--detail-compare` screenshots showed the mountains with and without
  the finest levels side by side. 0.06 / 800 was the balance that was picked: most of the mountain detail
  for under half the size. The `keepSquares` list (dunes and rocky plateaus) overrides it, which is
  why most of the level 9 tiles on the map are striped.

**Land cover (`cover`): ground drawn as a different kind of land.** k_T(i) is the kind of ground at
the point, and K_P(i) is the kind the game would draw there from P. Water points don't count,
because the water pack draws the coast:

$$C = \sum_i A_i \,\big[\, k_T(i) \neq K_P(i) \,\big] \qquad \text{kept if } C > 20\ \text{km}^2$$

- *Limit 20 km² (`coverAreaKm2`)*: that's 0.5% of a level 8 tile and 1.9% of a level 9 tile, or
  about 1,200 points at level 9. Picture a patch of forest or snow about 4.5 km across being drawn
  wrong.
- *Why*: land cover is what you see up close, so the limit is small. It was set when the land
  cover pack was first built, as a judgement call. It was tested on 2026-11-18 (below), and twice
  the limit already starts thinning out small clearings in forests. Level 7, the first land cover
  level, is kept wherever there's any land at all, so the sharp close-up drawing exists
  everywhere; that's why the Sahara and Antarctica are striped at level 7.

**Distant colour (`surface`): ground whose blended colour changes.** c is the land colour (red,
green and blue, from 0 to 1 in linear light), compared as square roots, which is close to how
different colours actually look to the eye. t is the sea's tone. Only points that are at least a
quarter land count for colour, and only points at least a quarter water count for tone:

$$C = \sum_i A_i \,\Big[\, \max_{c \in \{r,g,b\}} \big|\sqrt{c_T(i)} - \sqrt{c_P(i)}\big| > 0.04 \ \ \text{or}\ \ |t_T(i) - t_P(i)| > 0.08 \,\Big] \qquad \text{kept if } C > 100\ \text{km}^2$$

- *Tolerance 0.04 (`colourTolerance`)*: in the mid tones, that's about 10 of the 255 brightness
  levels on screen.
- *Limit 100 km² (`colourAreaKm2`)*: 0.15% of a level 6 tile, or 0.6% of a level 7 tile, about 380
  points at level 7. Level 6 is the finest colour level, except on open ground that isn't
  mountains (`openSquares`), where it goes to level 7 (a point every 610 m).
- *Why*: until 2026-11-18 it was 400 km², a value picked while building the colour pack, with no
  test behind it. It was lowered to 100 km² along with the deeper level on open ground, so the
  blended colour meets the land cover better as you zoom in (see "More distant colour and detail"
  below).

**Water (`water`): ground that ends up on the other side of the coast.** w is the share of water at
the point, and the coast is drawn where it crosses one half:

$$C = \sum_i A_i \,\big[\, (w_T(i) \ge 0.5) \neq (w_P(i) \ge 0.5) \,\big] \qquad \text{kept if } C > 1\ \text{km}^2$$

- *Limit 1 km² (`waterAreaKm2`)*: about 60 points at level 9. For example, a coast that shifts by
  150 m along 7 km of shore.
- *Why*: crisp coastlines were one of the very first goals for the ground, so almost any visible
  shift of the coast keeps the tile. That makes water the second biggest pack (331 MB). It was tested
  on 2026-11-18 (below), and four times the limit makes small rivers break up.

**Detail (`detail`): ground whose colour detail changes.** m is the detail multiplier for red, green
and blue, where 1 means no change to the colour:

$$C = \sum_i A_i \,\Big[\, \max_{c \in \{r,g,b\}} \big|m_{c,T}(i) - m_{c,P}(i)\big| > 0.05 \,\Big] \qquad \text{kept if } C > 100\ \text{km}^2$$

- *Tolerance 0.05 (`detailTolerance`)*: a 5% change in brightness or tint.
- *Limit 100 km² (`detailAreaKm2` in the surface recipe)*: 0.6% of a level 7 tile, or 2.3% of a
  level 8 tile. It's the same area as for distant colour, since you see both from about the same
  distances. Level 6 is the finest detail level, except in the bland squares, on open ground and in
  the filled-in forests (`detailFineSquares`, `openSquares`, `detailFillSquares`), where it goes
  to level 8 from the 500 m picture.
- *Why*: until 2026-11-18 it was 400 km² (the tool's default). It was lowered along with the
  colour limit (see "More distant colour and detail" below).

Only the height limits were chosen by comparing predicted sizes and screenshots. The other four
started out as judgement calls, and were tested on 2026-11-18 (below).

Every tile down to each pack's finest level gets measured, whether it's kept or not, so the
decisions files can tell you exactly what a different limit would keep, without baking anything.
`terrain decisions ../../data/base/water --what-if 0.25,0.5,1,2,4` prints the tiles and size for
each multiple of the limit. And `terrain decisions <a copy of a pack> --prune 4` turns a copy into
the pack that four times the limit would make. That only works for raising a limit; lowering one
needs a bake, for example `--update` on a copy, with the lower limit in a recipe.

### The limit test (2026-11-18)

Each pack was tried at a quarter, twice and four times its limit, at 2560 by 1440, in 15 views. The
views were picked where the decisions files showed the most tiles near the limit: rivers in Yakutia
and Russia, the Alaska coast, forest edges in Montana and Mongolia, the Kazakh steppe, deserts and
coasts at country zoom, and Europe. The higher limits were made by pruning copies of the packs, and
the quarter by baking those views again. Each picture was compared with the current one, pixel by
pixel and by eye at 2.5 to 3 times magnification (`docs/limit_test/limit_test.png`).

| Pack | ¼ of the limit | At the time | 2x | 4x | Largest change in a test view (2x / 4x) |
| --- | --- | --- | --- | --- | --- |
| Water | +54 MB | 332 MB | -9 MB | -28 MB | 1% / 7% of pixels; at 4x small rivers break up and vanish |
| Land cover | +34 MB | 415 MB | -12 MB | -51 MB | 1.5% / 6.5%; at 2x small forest clearings thin out, at 4x edges turn into steps |
| Distant colour | +12 MB | 67 MB | -8 MB | -22 MB | 0.14% / 0.7%, with almost none by more than 32 of 255 levels |
| Detail | +22 MB | 205 MB | -15 MB | -53 MB | 1.1% / 1.7%, and faint (the land cover changed in the same views too) |

A quarter of each limit changed at most 0.8% of the pixels in any view, so lower limits add size
without adding much you'd see. Water and land cover are right where their limits should be: higher
limits visibly lose rivers and small patches, and save very little. Distant colour and detail could
be raised (to about 1,600 km²) to save 20 to 50 MB each. The pixel differences were faint, but
they haven't yet been checked by eye on their own, separately from the other packs.

Both kinds of map are drawn by the terrain tool itself (`DetailPlan.cs` and `DecisionMap.cs`). It
colours one pixel at a time into an image in memory (`Canvas.cs`) and saves it as a PNG with its
own writer (`PngImage.cs`). The land outline comes from the height pack next to the one being drawn
(ground above sea level at level 5), and the text uses Windows' Segoe UI font.

### More distant colour and detail (test, 2026-11-18)

The other direction was tried too, so the blended colour would meet the sharp land cover better as
you zoom in: distant colour down to level 7 (a point every 610 m, instead of 1.2 km at level 6) and
detail down to level 8 (300 m, from the 500 m picture), both with a 100 km² limit instead of 400.
At the time, only the bland squares in `lists/detail_fine_squares.csv` had detail past level 6.
Each try was baked with `--update` into a copy of the packs, only inside ten test views (3 minutes
each), and compared with the current packs at 2560 by 1440.

**Everywhere** (`docs/limit_test/more_detail.png`):

- Flat open ground, seen from 300 to 500 km, gained the most. The soft blobs over the Sahara, the
  Po plain, the Montana plains and the Nile delta turned into fine mottling that matches the land
  cover patches next to them. Up to 2% of pixels changed in deserts and 5 to 7% on farmland, with
  almost none by more than 32 of 255 levels.
- Mountains hardly changed to the eye, even though they cost the most: 74 KB of new detail per 1°
  square in the test views, against 44 KB on flat ground and 22 KB in deserts.
- The Kazakh steppe didn't change, because its squares were already bland squares with the 500 m
  detail. Only the level 7 colour was new there.

Why does flat ground gain and mountains don't? In mountains, the slope shading (from heights down
to 150 m) already draws fine light and dark everywhere, and your eye reads that, not small changes
of colour underneath it. The detail also fades out on steep ground, so the picture's own shadows
don't clash with the game's, and mountain land cover changes kind every few hundred metres (forest,
meadow, rock, snow), which the land cover already draws sharply. On flat open ground there's no
shading to speak of, and one kind of ground (sand, fields, grass) covers whole regions, so all of
its variety has to come from the distant colour and the detail. When those are coarse, it looks
blurred. Mountains still store a lot because the finer slopes move where the detail fades out,
which changes the tiles without it showing.

**Open ground only** (`docs/limit_test/open_ground_rule.png`): the same change, but only in the
7,330 squares that were in `lists/open_squares.csv` at the time. That list is made by
`make_open_squares.ps1` from the bland survey: squares whose most common kind of ground is bare
ground, cropland, grassland, shrub, tundra, wetland or town, and whose shading varies by less than
0.06 (plains are below 0.02, the Alps about 0.15). Forest, ice and mountains are left out. In a
recipe it looks like this:

```
"openSquares": "../lists/open_squares.csv",   // detail to detailFineMaxLevel and
"colourOpenMaxLevel": 7,                      // colour to this level, in these squares
```

- The Sahara, Kazakh steppe and Nile delta views came out identical to "everywhere", and the Po and
  Montana plains nearly so. The Alps, the Rockies and the Amazon forest looked the same as before.
- What it misses: farmland mixed with forest, such as the Swiss plateau, southern Germany and the
  clearings in the Amazon, where forest is the most common kind in the survey patch. Those looked
  the same as before, not like "everywhere".

**Size, estimated for the whole world** (the new tiles per 1° square in the test views, by kind of
ground, times the world's squares of that kind; the views were picked for busy ground, so these
lean high):

| | Distant colour | Detail | Total |
| --- | --- | --- | --- |
| Before | 67 MB | 205 MB | 272 MB |
| Everywhere | +125 MB | +860 MB | about +1.0 GB |
| Open ground only | +50 MB | +295 MB | about +0.35 GB |

An earlier estimate of +0.4 to 0.6 GB for "everywhere" was scaled from tile counts and missed that
forest is both common and expensive (81 KB a square, across 2,946 flat forest squares).

**Adopted (2026-11-18).** `base_surface.json` now uses the open squares alongside the bland
squares. The whole-world bake took 26 minutes, and this is what came out:

| | Distant colour | Detail | Total |
| --- | --- | --- | --- |
| Before | 67 MB | 205 MB | 272 MB |
| After | 147 MB | 535 MB | 682 MB |
| Change | +80 MB | +330 MB | +410 MB |

So the estimate for open ground was actually a little low, not high. The graphics card holds the
same number of tiles as before (the screenshot test still passes with the same `ColourLayers` and
`DetailLayers`), so memory didn't change: 753 MB of graphics memory at 2560 by 1440 with either
bake. The packs from before are kept in `data/base` as `*-before-open`.

The first bake had a fault, fixed before this one. Wherever a tile reached into a listed square,
the baker went down to level 8 across the whole tile and stored the parts outside the squares too,
made from the coarse picture, and with the lower limit nearly all of them were kept. That put
level 8 detail over forests that weren't on either list and cost 48 MB. Those parts are now built
only so the tile above can be averaged, and never stored (`Built.Beyond` in `DetailBaker.cs`).

**Straight lines at the edges of the lists (fixed the same day).** In the Congo, the bland squares
were patches inside one big forest. A square only makes the list if the 20 km patch at its centre
passes the survey, so its neighbour, with a river or a clearing at its centre, might not. Where
they met, the detail switched from the 500 m picture to the 2 km one along the square's edge,
drawing a straight line through uniform forest. Half of the Congo's unlisted forest squares were
bland, but the 500 m picture didn't make their centre patch any less smooth; the other half didn't
score as bland, often because of a river or clearing in the patch. Neither is a good reason for
the forest to change.

The first fix tried, and dropped, faded the sharp detail out over the last 50 km inside the
lists. The line went away, but the forest near every edge got the blurry 2 km detail, which looked
worse.

The real fix is a third list, `lists/fill_squares.csv`, made by `make_fill_squares.ps1`
(`detailFillSquares` in `base_surface.json`; see `docs/limit_test/congo_fill.png`):

- Whole forest regions. A region is a patch of touching squares with the same most common kind of
  ground. Any forest region that's already at least 15% listed gets all its squares: the Amazon
  (513 more), the Congo (299), West Africa (50), Borneo (24), New Guinea (22), north Queensland
  (22) and two small patches.
- Gaps. Three times over, any square with at least 5 of its 8 neighbours listed joins in, which
  added 829 squares. A looser rule (4 of 8) kept on spreading and would have swallowed whole
  forests.

That came to 1,771 squares and 48 MB (detail 583 MB). The Siberian taiga and the forests of
Canada and the eastern US were left as they are, because they're only 3 to 4% listed (the bland survey's
patches in them still meet the rest along square edges), and listing them whole would add over
3,000 squares. The two packs together were then 730 MB, +458 MB on before.

**After the whole-square survey (2026-11-19).** The bland survey now scores 9 patches per square and
lists a square when 2 of them are bland and helped by the sharper picture (see "Finding bland
ground"). That gives 2,083 bland squares, the open list from both surveys (8,769), and the fill list
rebuilt from the whole-square kinds of ground (2,496; the rainforests come out as bigger regions,
and Indochina's forest qualifies too). The bake took 30 minutes: distant colour 153 MB, detail
655 MB, 808 MB for the two together, +536 MB on before the open-ground change.

## Which tiles are stored

The game draws mountains at their true height. At that scale, a finer tile hardly moves the
outline of the ground: even a 100 m height error is less than a pixel at the closest zoom. What a
finer tile does change is the slope shading. So the baker compares the slopes in each tile with the
slopes the game would show from the coarser tile above it, measured the same way the shader
measures them (`TerrainTool/DetailMeasure.cs`), and stores the tile when:

> the slope differs by more than `detailSlope` over more than `detailAreaKm2` of ground.

- `detailSlope` is rise over run: `0.06` means 6 m in 100 m, which changes the shading by up to a
  tenth of its brightness.
- `detailAreaKm2` is the same at every level. A level 9 tile covers about 1,500 sq km at the
  equator, so it's only stored when most of it is rugged. A level 7 tile covers 24,000 sq km, so a
  single range of hills is enough.

Raise either number for a smaller pack with softer mountains. To see what a change would do before
spending half an hour on a bake:

```
terrain plan ../../data/base/terrain recipes/base_earth.json --slope 0.08 --area 600 --map plan.png
```

`plan` measures every tile of an existing pack once, then predicts the tiles and megabytes per level
for the recipe (or for the `--slope` and `--area` you give it), and it can draw the map you'd get.
It only knows about the tiles the existing pack has, so it can predict settings that keep less, but
not more. To try settings that keep more, add `--stats` with the measurements saved from a fuller
pack: `terrain-sources/detail_survey.csv` holds those of a 1.7 GB bake.

`detailAreas` in a recipe lets you set the detail by hand inside boxes, overriding the rule:

```json
"detailAreas": [
  { "name": "Alps", "bounds": [5, 44, 16, 48], "minLevel": 9 },
  { "name": "Antarctica", "bounds": [-180, -90, 180, -60], "maxLevel": 5 }
]
```

`minLevel` stores every tile down to that level inside the box, however flat it is (as long as the
sources go that far). `maxLevel` stores nothing deeper than that level inside the box.

To judge by eye what a level adds, the game can draw the same close-ups side by side with the
finest levels taken away:

```
godot --path . --resolution 1920x1080 -- --capture=<output folder> --detail-compare
```

## Recipes

A recipe (`recipes/*.json`) lists the elevation sources from bottom to top, and says where to
write the pack. Later sources cover earlier ones wherever they have data, so the coarse data for
the whole world goes first and the fine data for particular regions goes after it.

```json
{
  "name": "Base Earth",
  "output": "../../../data/base/terrain",
  "minElevationM": 0,
  "detailSlope": 0.06,
  "detailAreaKm2": 800,
  "sources": [
    { "path": "../../../../terrain-sources/etopo2022_15s", "maxLevel": 7 },
    { "path": "../../../../terrain-sources/copernicus_glo90", "maxLevel": 9 }
  ]
}
```

| Setting | What it means |
| --- | --- |
| `output` | The folder to write the tiles to, relative to the recipe |
| `sources` | From bottom to top. Each is either a `path` (a GeoTIFF or a folder of them) or a `pack` (an existing pack) |
| `minElevationM` | Anything lower is raised to this. `0` also lifts land below sea level. Use `null` to keep negative heights |
| `detailSlope`, `detailAreaKm2` | Which tiles get stored; see above. A `detailSlope` of 0 stores everything |
| `detailAreas` | Boxes where you set the detail by hand; see above |
| `maxLevel` | The deepest level to bake. Leave it out to use all the detail the sources have |
| `heightStepM` | Heights are rounded to this. `2` or `4` compresses better than `1` |
| `onlyBounds` | `[west, south, east, north]`: only rebuild the tiles touching this box (for mods) |

Each source can also have `bounds`, to give the extent of an image that has no location stored in
it; `valueScale` and `valueOffset`, to turn pixel values into metres (`metres = value x scale +
offset`); and `maxLevel`, to stop that source at a level even if its data is finer. `squares`
names a file of 1° squares (the `lat,lon` of the south-west corner, or `N24_00_W003_00`), and the
source is then used only inside those squares. The base recipe uses this to take ETOPO one level
finer only where the bland survey found relief that the coarser level hides.

Sources have to be GeoTIFFs in plain latitude and longitude. Uncompressed, LZW and Deflate files
are read directly; for anything else, convert them first with
`gdal_translate -co COMPRESS=DEFLATE in.tif out.tif`.

## Sharing the baked packs

The five baked packs (2.7 GB, over 250,000 files) are too big for git: GitHub refuses any file over
100 MB, and every rebake would add another 2.7 GB to the history for good. So they're shared as
files attached to a GitHub release, which sit outside the git history and can each be up to 2 GB.
- **Getting them:** on GitHub, open the newest `terrain-<date>` release, download the five zips and
  unzip each into `data/base` (see "Running" in the game's README). The release's `packs.txt` lists
  each pack's file count and size, to check against.
- **Publishing them after a rebake:** `make_pack_release.ps1` zips each pack on its own into
  `terrain-sources/pack_release` (stored without compressing again, since the tiles already are;
  that's much quicker and only about 1% bigger), writes `packs.txt` listing each pack's file count
  and size, and creates a release tagged `terrain-<date>` with all of them attached. It needs the
  GitHub command-line tool (`winget install GitHub.cli`), signed in once with `gh auth login`.
  `-ZipOnly` makes the zips without uploading them, and `-ReuseZips` uploads zips made earlier.

Each zip has to stay under 2 GB; the biggest, `terrain`, is about 1.2 GB.

## Getting the data

- **The whole world, coarse:** `download_etopo2022.ps1` fetches ETOPO 2022 (15″, ice surface) into
  `terrain-sources/etopo2022_15s`, about 5 GB. The base recipe stops it at level 7, about 610 m
  between points north to south, and at level 8 in the relief squares.
- **Mountains, sharp:** [Copernicus GLO-90](https://registry.opendata.aws/copernicus-dem/) comes as
  1° by 1° GeoTIFFs. The recipe stops it at level 9, about 153 m north to south.
- **Land cover in the far north:** WorldCover has no data north of about 82.8° N.
  `make_arctic_cover.ps1` fills that gap from the Natural Earth glacier outlines (1.6 MB; see the
  top of the script): ice inside them, bare ground outside. The recipe reads it as `landCoverFill`.
- **Land cover:** `download_worldcover.ps1` fetches ESA WorldCover 2021 at 160 m, about 1 GB in
  2,651 files, in 10 to 20 minutes. It needs GDAL. You only need it for baking the surface, and you
  can delete it afterwards.
- **The colour picture:** NASA Blue Marble, July 2004, 21,600 by 10,800, in
  `terrain-sources/bluemarble_2km` (copied from the Unity project).
- **The sharper colour picture:** `download_bluemarble500.ps1` fetches the 500 m Blue Marble
  (351 MB) and splits it into GeoTIFFs for `detailImages`. Again, you only need it for baking.
- **Ground texture samples:** see "Close-up ground textures" below.

To reproduce the base pack, point the ETOPO source in `recipes/base_earth.json` at your files. If
you need a new list of mountain squares, first bake a copy of the recipe with only the ETOPO source
and run `terrain rugged` on that pack. Then run `download_copernicus90.ps1` with the list to fetch
those squares, and bake the full recipe. You can add particular mountain or coastal squares to the
list before downloading.

The raw downloads go in `terrain-sources/`, next to the game projects, and aren't committed. When
you're replacing an existing pack, bake with `--output` pointing at a new folder, so the current
pack keeps working while the baker runs. Then move the finished pack into `data/base/terrain` to
switch to it.

## The surface: land cover, colour and water

What the ground looks like comes from four more packs, all baked together from
`recipes/base_surface.json`:

```
terrain surface recipes/base_surface.json
terrain preview ../../data/base venice.png 11.6 44.8 13.6 45.9 --heights ../../data/base/terrain
```

| Pack | Files | What each point holds | Levels |
| --- | --- | --- | --- |
| `data/base/cover` | `.tnsk` | What kind of ground it is: forest, shrub, grass, crop, city, bare, snow, water, wetland, mangrove or moss | 7 and 8, plus 9 where the heights are at level 9 |
| `data/base/water` | `.tnsw` | How much of the ground around it is water | 0 to 9 |
| `data/base/surface` | `.tnsc` | The blended colour of the land, and a tone for the water (how deep the sea is, or "lake") | 0 to 6, plus 7 on open ground |
| `data/base/detail` | `.tnsr` | How much the real colour of the ground differs from its kinds, as a multiplier | 0 to 6, plus 7 and 8 in the listed squares |

**Up close,** the game draws the land cover kind by kind. Each of the 4 by 4 points around a spot
gives its kind a smooth share depending on how near it is, and each kind's colour counts by its
share raised to `cover_sharpness` (2, set on the map material). Where one kind dominates, it's
drawn pure with a clean, curving edge. Where kinds are mixed, like rock, meadow and snow on a
mountainside, they blend. And a single odd point fades in rather than showing up as a square
(`CoverDrawing.cs`, and `cover_land` in the shader; `terrain preview` draws the same way). Each
kind's colour comes from `data/base/cover/cover_colours.png`.

**Coasts, lakes and rivers** come from the water tiles, which store how much of each spot is
water. The coast is drawn where that crosses one half, which places it more precisely than a
single point could. **Far away,** where many points share one pixel, the game fades to the blended
colour tiles. Those are the average of the same kinds, so zooming only ever sharpens the picture
and never changes its colours.

All of them use the same tile grid as the heights and only store a finer tile where it changes the
picture. Land cover goes down to level 8 (305 m) over plains, and one level finer than the heights
wherever the heights are finer than plains (`coverMatchHeights`, `coverLevelsBeyondHeights`). That
means level 9 (153 m) in hill and mountain country, where the patches of forest, meadow, rock and
snow are small. The water goes down to level 9 everywhere, because a stepped coast is where it
would show most. Over Italy and the Alps, the cover, water and colour packs take up 6.3 MB.

**Where the colours come from.** Each kind's colour is measured from the colour picture of the world
(NASA Blue Marble): it's the average colour of the picture's pixels that are mostly that kind,
within about 30 km. That keeps the real world's variety (the Sahara is redder than Arabia, and
Siberian forest is darker than the Amazon), while the picture's own shadows average away, so the
game's slope shading is the only light. The measured colours are kept next to the downloads, in
`surface_colours.bin` with a picture of them in `surface_colours.png`, and `--recolour` measures
them again. For the game they're saved as `cover_colours.png`: eleven world maps, one per kind, in
the same order as the table above. Repaint it in any image editor (or ship a repainted copy in a
mod's `cover` folder) to change what a kind of ground looks like, everywhere or just in one region.

**Detail.** Each kind's colour is an average over about 30 km, so wide areas of one kind, deserts
most of all, would come out as smooth blobs: the Sahara's dark plateaus, red sand seas and wind
streaks are all just "bare". `data/base/detail` (`.tnsr`, `DetailBaker.cs`) holds how much the
colour picture differs from what the kinds of ground predict, at least every 1.2 km and down to
305 m in the listed squares. It's a multiplier the game applies to the land both near and far, so
zooming never shifts the colours. It fades out on steep ground, where the picture shows its own
shadows. `terrain surface ... --detail-only` bakes just this pack, in a few minutes.

**The sea** is coloured by its depth (from ETOPO): light over shallows and shelves, dark over the
deep ocean. Water higher than `lakeAboveM` above the sea is a lake or river and gets the lake
colour. The colours themselves are set on the map material (`sea_shallow`, `sea_shelf`, `sea_deep`,
`lake_colour`), so you can change them without a bake.

| Setting | What it means |
| --- | --- |
| `coverOutput`, `colourOutput`, `waterOutput`, `detailOutput` | The folders for the four packs |
| `detailMaxLevel`, `detailTolerance`, `detailAreaKm2` | The deepest detail tiles (6), and when a finer one is stored |
| `detailImages`, `detailFineSquares`, `detailFineMaxLevel` | Sharper pictures for the detail, and the squares where they're used, down to `detailFineMaxLevel` (8) |
| `openSquares`, `colourOpenMaxLevel` | Squares of open ground (from `make_open_squares.ps1`) where the detail goes to `detailFineMaxLevel` and the blended colour to `colourOpenMaxLevel` (7) |
| `detailFillSquares` | More squares for the sharper detail: whole forest regions and gaps (from `make_fill_squares.ps1`) |
| `detailSlopeStart`, `detailSlopeEnd` | The range of slopes over which the detail fades out, where the picture shows its own shadows |
| `landCover` | The folder of WorldCover GeoTIFFs (`download_worldcover.ps1`, 160 m) |
| `landCoverFill` | A folder of land cover in the same form, used only where WorldCover has no data (`make_arctic_cover.ps1`: north of 82.8° N) |
| `pictureNorthLimit` | The latitude where Blue Marble stops having real pictures (80); the detail fades out half a degree before it |
| `colourImage` | The colour picture of the whole world, as a plain latitude and longitude PNG |
| `elevation` | Whole-world elevation including the sea floor, for the sea colour and for Antarctica |
| `maxLevel` | The deepest water and land cover tiles |
| `coverMinLevel`, `coverMaxLevel` | The land cover levels; it's drawn sharp from `coverMinLevel` down |
| `coverMatchHeights` | A height pack: the land cover goes as deep as its heights wherever they're finer than `coverMaxLevel` |
| `coverAreaKm2` | A finer land cover tile is stored where it changes the kind of land over more than this |
| `colourMaxLevel`, `waterMaxLevel` | The deepest blended colour and water tiles |
| `colourTolerance`, `colourAreaKm2` | A colour tile is stored where it changes colours by more than the tolerance over more than the area |
| `waterAreaKm2`, `waterSteps` | A water tile is stored where it moves the coast across more than this; the water share is rounded to this many steps |
| `colourQuality` | The JPEG quality of colour tiles (85) |
| `colourRadiusKm` | How far around each kind's colour is averaged |
| `classColours` | Fixed colours for kinds of ground, replacing the measured ones, for example `"built": [0.45, 0.43, 0.41]` |

`--region west,south,east,north` paints just one box into empty folders, so you can try out
settings in a minute instead of waiting for a full bake. Its coarse tiles are only right inside the
box, so it's for experiments only, never for the packs the game uses.

`--update west,south,east,north` rebakes one area of the existing packs after its data or settings
have changed (a land cover fix, a repainted region), in a minute or two instead of half an hour:

```
terrain surface recipes/base_surface.json --update -60,80,-10,84
```

The box is widened to whole level 6 tiles (2.8 degrees). Everything inside it is rebuilt and
written again, and any old tiles there that are no longer worth keeping are removed. The coarser
tiles that reach outside the box are rebuilt too, but keep their stored values outside it, so
nothing else on the map changes. You can do several boxes at once by separating them with
semicolons. Only a change that affects the whole world (a new colour table, or a new detail or
water setting) needs a full bake. It was tested against a full bake over the Alps: the same tiles,
identical inside the box, and the coarse tiles above it within a few levels (from JPEG rounding at
the edge of the box). `terrain compare` on two cover, colour, water or detail packs shows
differences like these level by level.

WorldCover leaves out Antarctica, so there the land and coast come from the elevation instead, and
the coastline is softer.

## Modding the terrain

The game loads `data/base/terrain` first, then `mods/<name>/terrain` for each mod, in order of
name. A tile in a later pack replaces the same tile in an earlier one, so a mod only needs to ship
the tiles it changes.

To reshape a region:

1. Export it: `terrain export ../../data/base/terrain alps.tif 5 43 17 49`
2. Edit `alps.tif` in any program that handles 32-bit float TIFFs, such as QGIS, GIMP, Krita or
   Photoshop. The pixel values are heights in metres.
3. Write a recipe that lays your edit over the base pack and only rebuilds that box (the export
   prints the exact `onlyBounds` to use):

   ```json
   {
     "name": "Taller Alps",
     "output": "../../../mods/taller_alps/terrain",
     "minElevationM": 0,
     "onlyBounds": [5, 43, 17, 49],
     "sources": [
       { "pack": "../../../data/base/terrain" },
       { "path": "alps.tif" }
     ]
   }
   ```

4. Bake it: `terrain bake taller_alps.json`. Start the game and it will use the mod's tiles.

A whole new world works the same way: give `bake` a single height image covering
`[-180, -90, 180, 90]` and write the result to a mod folder, or over the base pack.

## Finding bland ground

`terrain bland` scores every 1° square of land the way the game draws it at the closest zoom: how
much of a 20 km patch looks like a smooth wash at a 2 km scale. With `--patches 3`, it measures a 3
by 3 grid of those patches across the square instead of just one at the centre, and counts how many
are bland and how many of those the 500 m picture fixes. `--detail-level 6` judges the map with no
detail deeper than level 6, the way it looks without the sharp detail, so squares that already have
it aren't dropped for looking fine. For the bland squares, it also measures what better data would
add: relief that ETOPO's own heights have at fine scales (dunes, rocky plateaus), and fine colour
from the 500 m Blue Marble wherever that's been downloaded. `terrain blandmap` draws the result (see
`docs/bland_survey`). The base packs use it for these lists:

- `lists/relief_squares_all.csv`: bland open ground with hidden relief. ETOPO goes one level finer
  there (`squares` on a source in `base_earth.json`).
- `lists/relief_squares.txt`: the strongest of those, fetched from Copernicus 90 m with
  `download_copernicus90.ps1 -List`.
- `lists/detail_fine_squares.csv`: squares where at least 2 of the 9 patches are bland and the
  500 m picture makes them less smooth (`blandmap --min-patches 2 --list`, from
  `docs/bland_survey/bland_survey_squares.csv`). The detail tiles use the 500 m picture there, down
  to level 8 (`detailFineSquares` in `base_surface.json`). The first survey only looked at the
  centre patch, so a river or a field there decided the whole square. Now there are 2,083 squares,
  against 1,484 before, and 344 of the old ones were only bland in that one patch.
- `lists/open_squares.csv`: open ground that isn't mountains, picked from the survey's kind of
  ground and shading (`make_open_squares.ps1`). The colour and detail go deeper there
  (`openSquares` in `base_surface.json`; see "More distant colour and detail").
- `lists/fill_squares.csv`: whole forest regions that the lists already reach into, plus gaps in
  them (`make_fill_squares.ps1`, `detailFillSquares`). Run it again after changing either of the
  lists above.

Run the survey again after changing the packs, to see what's still bland.

## Close-up ground textures

Up close, each kind of ground gets a texture cut from a real satellite picture of a typical place
(decision 16 in `docs/terrain.md` explains why, and how it looks). Making them takes four steps,
all run from this folder, and you only need to rerun a step when its input changes.

**1. Choose where to cut each sample.** A texture gets laid over every place of its kind, so its
sample has to be plain, typical ground. A single lake or town in it would show up on every steppe or
every field in the world. `sample_sites.csv` lists a typical place for each sample, for example
`grass_kazakh,48.5,69.5,3,` (the Kazakh steppe, searched up to 3° around it). Then run:

```
dotnet TerrainTool/bin/Release/net8.0/terrain.dll samplepick ../../../terrain-sources/worldcover_160m sample_sites.csv sample_windows.csv
```

It looks at the land cover map around each place and picks the plainest 88 km square. It prints
what it found, like this:

```
grass_kazakh  47.70  71.80  belongs 99.8% (least part 99.7%)  grass alone 99.8%  water 0.00%  built 0.00%  towns 0  lakes 0
```

- **belongs:** how much of the square is that kind of ground, or a kind that looks the same from
  above (grass among fields, grass among bushes, bushes in grassland).
- **least part:** the same thing for the worst of its 16 parts (4 by 4), so a square that's all
  farmland on one side and all forest on the other scores badly.
- **water** and **built:** these can be at most 0.15% and 5%. Built-up is allowed more, because the
  map counts every village and road as built-up, and that's 1 to 3% of most farmland.
- **towns** and **lakes:** 2 km patches that are mostly buildings or mostly water. Each one lowers
  the score, so the square with the fewest wins.

A sixth column in `sample_sites.csv` raises the water limit for one place (Thailand's rice country
has ponds everywhere), and a search radius of 0 fixes a place by hand, so the search only measures
it.

The land cover map is coarse (160 m), so **look at every square** before you use it. Six of the
search's first picks had something in them that would repeat (strips of forest, woods, a canal,
canyons, mine scars) and were fixed by hand; the list is in decision 16.

**2. Download the samples.** `download_s2_windows.ps1` reads each square in `sample_windows.csv`
from ESA's WorldCover Sentinel-2 picture of 2021 (it's public, no account needed) into
`terrain-sources/sentinel2_samples_88km`. Each one is 1024 by 1024 pixels, about 86 m a pixel,
about 6 MB, and takes about two minutes. A square crosses several of the picture's 1° files, so
each file's part is read on its own at the right size and the parts are joined together on your
computer. (Reading them as one joined file made GDAL read the full 10 m picture, which was many
times slower.)

The older, smaller samples (44 km, about 43 m a pixel) come from `download_s2_samples.ps1` into
`terrain-sources/sentinel2_samples`. Only wetland still uses one. The desert samples there are just
being kept in case the desert textures are switched back on; deserts don't have a texture now.

**3. Make the textures.** `make_ground_textures.ps1` turns the samples into
`data/base/ground/<kind>.png` using `terrain groundtex`. For each sample, it:
- keeps only the fine light and dark, by dividing the picture by a copy of itself blurred over
  500 m (`--blur-m`). A field 15% brighter than its surroundings comes out as 1.15, and a whole
  valley that's a bit greener comes out as 1;
- keeps half of the colour of that light and dark (`--chroma 0.5`), so a reddish field stays a
  little reddish;
- tones down standout spots (`--soften 2.5`): any light or dark much stronger than the sample's
  usual variation gets eased back. In the Kazakh sample, for example, ordinary ground varies by
  about ±8%, and a white salt pan 80% brighter than its surroundings comes out only about 20%
  brighter. Set it to 0 to turn this off;
- scales it so it averages exactly 1, which means the texture never changes the map's colour.

Each kind uses its large sample if it has one, and the small one if not. `-Pick` says which sample
each kind uses (`kind=none` gives it no texture, which is how deserts get `bare=none`). The script
also writes `data/base/ground/sizes.txt`, which says how much ground each kind's texture covers
(`crop 88`, `wetland 44`); the game reads that.

If the script says a file is in use, wait a few seconds and run it again. Something, like the Godot
editor re-importing a texture that just changed, can hold on to a file for a moment.

**4. Regional versions.** `make_ground_regions.ps1` writes `data/base/ground/regions.png` and
`regions.txt`: one pixel per 1° square, saying which region it's in (there are 10, roughly
continents) and which zone (the northern forest belt, north of 52° N). Then
`make_ground_textures.ps1 -Regional "crop_europe=crop_beauce,..."` copies the chosen samples'
textures to `data/base/ground/regional/<kind>_<region>.png`. For example, `crop_europe=crop_beauce`
means farmland in Europe uses the texture made from the Beauce sample. The script's default list is
the table in decision 16. A regional version has to be the same size as its kind's standard texture,
and the script leaves out any that aren't. `groundtex --all <folder>` makes every sample into a
texture named after it, and that's where the regional ones are copied from.

**Check the result.** `draw_ground_textures.ps1` draws `docs/ground_textures/textures_and_sources.png`
(each texture next to its sample, with the place and size) and `regions_map.png` (the region map in
colour). In the game, press **P** to turn the textures on and off and compare.
