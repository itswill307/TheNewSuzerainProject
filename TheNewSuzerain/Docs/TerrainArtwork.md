# Terrain artwork

The built-in image generation tool produced these representative terrain albedos
on 2026-10-17. They are not satellite observations of a specific location. The
original MODIS-named source artwork is retained; the array builder uses the two
new sibling files for classes 12 and 13. Source ID maps are unchanged.

The generated sources are 1254 px square and are resampled to the existing 1024 px
BC7 array layers during rebuilding. Runtime layer count and color texture memory
are unchanged. The shader's geographic anchoring, stochastic blending, and detail
bands apply to these layers just like the other terrain classes.

## Urban

Asset: `Assets/Map/Textures/natural_urban_v2.png`

Final generation prompt:

Use case: photorealistic-natural. Asset type: square 1024x1024 seamless repeating albedo terrain texture for a satellite-style strategy game. Primary request: a realistic strictly vertical orthographic aerial image covering approximately 1.5 kilometers square of mixed urban development. Include varied but consistently small real-world scale buildings, modest dense blocks mixed with lower density residential clusters, small courtyards, scattered tree canopy and gardens, a few low industrial roofs, narrow connected local streets that follow several related imperfect grids. Neutral weathered gray and warm muted stone roofs, subdued olive vegetation, restrained contrast. Surface must look like clear satellite photography with crisp fine detail at this scale, no blur. Even diffuse daylight with very soft short shadows, no directional dramatic shadows, no baked vignetting or gradients. Distribute interest across whole image without one landmark or focal point. Seamlessly tile on all four edges, connecting streets and canopy at borders. Avoid highways, giant roads, skyscrapers, perspective, horizon, clouds, labels, text, watermark, bright white roof repetition, checkerboard uniform blocks, decorative illustration. This replaces an overly uniform dense city texture; noticeably vary building density while keeping a coherent urban surface.

## Cropland

Asset: `Assets/Map/Textures/natural_croplands_v2.png`

Final generation prompt:

Use case: photorealistic-natural. Asset type: square 1024x1024 seamless repeating albedo terrain texture for a satellite-style strategy game. Primary request: realistic strictly vertical orthographic aerial image covering approximately 2 kilometers square of cultivated agricultural land. A natural irregular patchwork of roughly 35-60 fields in different sizes and shapes, mostly irregular quadrilaterals with some narrow strip fields and gently curved boundaries. Different subtle crop stages: muted sage and olive greens, straw ochre, earthy brown fallow patches. Fine cultivation rows inside fields, occasional grassy margins, thin hedgerows, occasional small groups of trees. Boundaries are narrow natural earth tracks, no bright thick outlines. Crisp satellite photography detail, even diffuse daylight with very soft short shadows. Uniform exposure across the tile. Seamlessly tile on all four edges, no large continuous color seam. Avoid a uniform diagonal grid, quilt illustration, oversaturated green/yellow, repeated checkerboards, large buildings, central focal point, roads wider than narrow farm tracks, rivers, perspective, horizon, clouds, labels, text, watermark, blur, vignette. Field sizes should correspond to tens to hundreds of meters and show clear local variation.
