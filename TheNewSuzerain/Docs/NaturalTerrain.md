# Natural terrain

Both map shaders use `NaturalTerrain.hlsl` to render the project's terrain
artwork, selected by the existing MODIS landcover classes. Blue Marble is no
longer sampled, bound to either material, or needed by the terrain builder.

Close views use geographically anchored, randomized terrain textures. Their
average colors provide each biome's palette. A restrained broad variation layer
comes from coarse mips of the same artwork at a fixed 32.768 km span. The detail
bands are bounded to the closest two levels (base and four times base), rather
than continually enlarging fields and buildings as the camera zooms out.

The distant view uses `terrain_surface.generated.png`: a filtered render of
these same surfaces over the existing global landcover map. Each 8K surface
pixel averages the rendered colors of its 2x2 source landcover texels. Class
numbers are decoded separately, never interpolated. Ordinary color mipmaps then
average larger terrain regions at country/world scales without point-sampled
biome and coastline shimmer. Both the flat map and Cesium share this surface.
This preserves the global landcover geography while using the project's own
terrain palette and patterns; it is not new satellite imagery.

The surface alpha channel stores land coverage for relief shading, not visual
transparency. Water comes from the water artwork and is excluded from relief.
Relief uses the existing elevation raster at runtime, so it is never baked twice
into the albedo. As the view approaches the surface-map resolution, the shaders
crossfade between local terrain and filtered surface colors over 0.25-1 surface
texel per screen pixel. Fully distant views bypass categorical shading.

## Rebuilding

Use **Tools → Map → Build and Validate Natural Terrain** for a full rebuild.
**Generate MODIS Landcover Texture2DArray** now also rebuilds the distant surface,
so changed terrain artwork propagates to all zoom levels. After changing
landcover data or terrain material scale/strength, run **Rebuild Terrain-Derived
Distant Surface**. **Apply Natural Terrain Desktop Settings** configures and
binds the surface, creating it if missing. Rebuild after changing shader albedo
logic as well. Source image and categorical map files remain unchanged.

The surface builder renders in 512 px tiles to bound individual GPU submissions,
preserves the generated PNG's GUID, and assigns the result to both materials.
The generated PNG and its importer metadata should travel with the project.
The landcover arrays use the original artwork plus the urban and cropland
replacements described in [TerrainArtwork.md](TerrainArtwork.md).

## Material controls

Keep matching settings on `FlatmapMat` and `CesiumCustomTerrain`.

| Control | Default | Effect |
| --- | --- | --- |
| Use Natural Terrain | On | Enables the shared terrain-derived surface workflow. |
| Detail Base Span | 2048 m | Per-biome scale: canopy/shrubs 512 m (broadleaf 1024 m), savanna 1024 m, urban 1536 m, grass/farm/wetland 2048 m, rock/snow 4096 m. |
| Detail Strength | 0.75 | Local texture contrast around the artwork's own average color. Rebuild the surface after changing. |
| Close Detail Reference | 15 m/pixel | Start of the bounded handoff to the second detail band. |
| Terrain Boundary Blend | 1 | Blends rendered class colors near categorical boundaries. |
| Vegetation Edge Patches | 0.8 | Geographically fixed canopy/clearing ownership near mixed boundaries. |
| Shoreline Irregularity | 0.8 | Narrow, antialiased shoreline variation within source raster uncertainty. |
| Elevation Relief Strength | 0.35 | Existing height raster provides restrained slope shading, excluded from water. |

The former Blue Marble, close biome tint, and biome tint fade controls were
removed. Biome color no longer changes back to a photographic overlay on zoom.
Boundary refinement cannot introduce a class absent from its neighborhood.
Province selection, geometry, displacement, and camera controls are unchanged.

## Memory and verification

The 18 terrain and four grassland array layers use 1024 px BC7, all 11 mips,
trilinear filtering, anisotropy 8, and no readable CPU copy: about 29.3 MiB.
The shared 8192×4096 BC7 RGBA surface uses 14 mips and about 42.7 MiB. Combined
GPU color data is about **72 MiB**, down from about 114.7 MiB with Blue Marble.
Elevation, landcover IDs, province IDs, and Cesium caches are additional. The
original Blue Marble PNG is retained as an unused authoring asset.

**Validate Natural Terrain** checks dimensions, compression, mipmaps, references,
the texture budget, shader compilation, flat/Cesium parity, texture detail,
boundaries, vegetation, shoreline, and relief. Distant-surface fixtures verify
that the bake averages actual artwork colors, responds to artwork changes,
never averages categorical IDs, and retains the water relief mask. Reports and
PNG previews are written to `Logs/NaturalTerrain/`.

Target: Windows desktop, Ryzen 5 5600 / RTX 3060 12 GB / 16 GB RAM, 1080p at
60 FPS. Offscreen checks do not establish that performance target. Profile a
player build through country/world views, continuous pan/zoom, flat/Cesium
transitions, and a cold tile-cache pass before shipping.
