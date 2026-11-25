// The world map, drawn as terrain tiles.
//
// Every tile uses the same grid mesh. Its vertices hold a position inside the tile (0..1) rather
// than a real position; this shader places each one from the tile's latitude and longitude,
// folds it between the flat map and the globe, and lifts it by the ground height.
// See src/Map/Terrain/TerrainRenderer.cs.
shader_type spatial;
render_mode unshaded, cull_back;

#include "res://shaders/map/map_projection.gdshaderinc"

// One layer per loaded tile, 256 x 256 texels each (resampled from the files' 257 samples):
//   heights: ground height (see height_decode)
//   cover:   kind of ground (Cover class 0-10: tree, shrub, grass, crop, built, bare, snow,
//            water, wetland, mangrove, moss), for drawing up close with sharp edges
//   colour:  RGB = blended colour of the land for distant views, A = water tone
//   water:   share of the ground that is water; the coast is where it crosses one half
uniform sampler2DArray height_tiles : filter_linear, repeat_disable;
uniform sampler2DArray cover_tiles : filter_nearest, repeat_disable;
uniform sampler2DArray colour_tiles : source_color, filter_linear, repeat_disable;
uniform sampler2DArray water_tiles : filter_linear, repeat_disable;
// The real colour of the ground relative to its kinds: red, green, blue multipliers x 128.
// Applied to the land near and far, so zooming never shifts colours. Brings back what one kind
// of ground cannot show: the Sahara's dark plateaus and sand seas.
uniform sampler2DArray detail_tiles : filter_linear, repeat_disable;
// The colour of each kind of ground round the world: one map per kind, half a degree per pixel.
uniform sampler2DArray cover_colours : source_color, filter_linear, repeat_enable;
uniform bool use_surface = false;
// For comparing what each pack adds (the screenshot test's --no-cover, --no-detail, --no-relief).
uniform bool show_cover = true;
uniform bool show_detail = true;
// false: the land's distant colour becomes a plain grey, so only the detail's variation shows.
uniform bool show_distant_colour = true;

// Close-up ground textures (data/base/ground): the fine light and dark of real ground of each
// kind, from Sentinel-2 pictures, averaging 1 so they only add texture up close. ground_layer
// gives each kind's layer in ground_textures, or -1 for none. Off until the game finds them.
uniform sampler2DArray ground_textures : filter_linear_mipmap, repeat_enable;
uniform int ground_layer[11];
// Regional versions (data/base/ground/regional, chosen by regions.png): the layer for each kind in
// each region (kind x 16 + region) and zone (kind x 16 + zone), or -1 for the standard one.
uniform sampler2D ground_regions : filter_nearest, repeat_enable;
uniform bool use_ground_regions = false;
uniform int ground_region_layer[176];
uniform int ground_zone_layer[176];
uniform bool use_ground_textures = false;
uniform float ground_texture_strength : hint_range(0.0, 2.0) = 1.0;
// Ground covered by one texture of each kind (its regional versions too; ground/sizes.txt), and
// the coarsest view (km a pixel) where the textures still show.
uniform float ground_kind_km[11];
uniform float ground_texture_far_km = 0.35;
// false: lay the texture plainly, repeated, without hexagons (for testing).
uniform bool ground_hexagons = true;
const float COVER_WATER = 7.0;
uniform float radius_km = 6371.0;
uniform float view_lat = 0.0;
uniform float view_lon = 0.0;
uniform float fold : hint_range(0.0, 1.0) = 0.0;
uniform float projection_blend : hint_range(0.0, 1.0) = 0.5;

group_uniforms terrain;
// How much of the ground's real height is applied: 0 draws a smooth surface, 1 is true scale.
uniform float elevation_scale = 0.0;
// Slope shading: 0 = none, 1 = full.
uniform float relief_strength : hint_range(0.0, 1.0) = 0.5;
// Slopes are steepened by this much before shading, so gentle terrain still reads.
uniform float relief_exaggeration = 3.0;

group_uniforms water;
// The sea's colour by depth: shallow (coasts, reefs), shelf (to about 200 m), deep (beyond
// about 2,000 m). The colour tiles store the depth as a tone: tone = sqrt(depth / 6000 m).
uniform vec3 sea_shallow : source_color = vec3(0.23, 0.45, 0.52);
uniform vec3 sea_shelf : source_color = vec3(0.10, 0.25, 0.38);
uniform vec3 sea_deep : source_color = vec3(0.04, 0.09, 0.20);
uniform vec3 lake_colour : source_color = vec3(0.08, 0.17, 0.23);
// How many pixels the coastline is softened over. Higher is softer, lower is crisper.
uniform float coast_softness : hint_range(0.25, 3.0) = 0.75;

group_uniforms land;
// How sharply kinds of ground are told apart up close: 1 blends neighbouring kinds smoothly,
// higher values draw dominant kinds pure with cleaner edges.
uniform float cover_sharpness : hint_range(1.0, 8.0) = 2.0;
// Km a pixel where the land cover starts to fade in over the distant colour (x), and where it has
// taken over (y): 0.7 to 0.2 is about 1,000 to 300 km across at 2560 x 1440. The land cover is a
// little darker than the distant colour (small bright towns and roads are in the 160 m land cover
// it is mixed from, but often lost from the 611 m land cover tiles), so a long fade keeps that
// change, and the sharpening, gradual.
uniform vec2 cover_fade_km = vec2(0.7, 0.2);

// Made-up fine detail up close (see procedural_detail): 0 is off (the default), 1 is normal.
// Before and after pictures are in docs/procedural.
uniform float procedural_strength : hint_range(0.0, 3.0) = 0.0;
// How much the made-up detail brightens and darkens the ground, and how much darker it makes hollows.
uniform float procedural_contrast : hint_range(0.0, 1.0) = 0.2;
uniform float procedural_relief : hint_range(0.0, 0.5) = 0.08;

group_uniforms debug;
uniform bool show_graticule = false;
uniform float graticule_step_deg = 10.0;
uniform bool show_tissot = false;
uniform float tissot_radius_km = 250.0;
uniform bool show_target_ring = false;
uniform float target_ring_radius_km = 600.0;
// A dot at a place: (latitude, longitude, radius in km). Radius 0 hides it.
uniform vec3 marker = vec3(0.0);
// Tints each tile by its level, to see which tiles are being drawn.
uniform bool show_tile_levels = false;

// Where the tile is: (west longitude relative to the view, south latitude, width, height), radians.
instance uniform vec4 tile_rect = vec4(0.0, 0.0, 1.0, 1.0);
// Which heights to use: (x offset, y offset, scale, layer). A tile with no heights of its own
// loaded yet reads a corner of a tile above it; offset and scale pick out that corner.
instance uniform vec4 height_window = vec4(0.0, 0.0, 1.0, 0.0);
// The same for land cover, colour and water, which may come from tiles of other levels. Land
// cover only exists up close; its layer is -1 where there is none.
instance uniform vec4 cover_window = vec4(0.0, 0.0, 1.0, -1.0);
instance uniform vec4 colour_window = vec4(0.0, 0.0, 1.0, 0.0);
instance uniform vec4 water_window = vec4(0.0, 0.0, 1.0, 0.0);
instance uniform vec4 detail_window = vec4(0.0, 0.0, 1.0, 0.0);
instance uniform float tile_level = 0.0;

// Heights are stored as 16-bit numbers: metres = texture value * height_decode.x + height_decode.y.
uniform vec2 height_decode = vec2(32767.5, -12000.0);

// A tile file has 257 samples a side (256 intervals, edges shared with the neighbours). Its layer
// on the graphics card holds 256 texels a side, resampled so texel 0 and texel 255 are exactly the
// edge samples: the driver would pad a 257-wide layer (see TerrainTileFormats.Texels).
const float HEIGHT_INTERVALS = 256.0;
const float TEXELS = 256.0;
const float TEXELS_PER_SAMPLE = (TEXELS - 1.0) / HEIGHT_INTERVALS;

varying vec2 relative_geo; // (longitude relative to the view, latitude)
varying vec2 height_coord; // position in the height layer, in samples
varying vec2 cover_coord;  // ... in the land cover layer
varying vec2 colour_coord; // ... in the colour layer
varying vec2 water_coord;  // ... in the water layer
varying vec2 detail_coord; // ... in the detail layer
varying vec2 tile_uv;      // position inside the tile, 0..1

// Where a position in samples (0..256) is in a layer, as a texture coordinate.
vec2 texel_uv(vec2 coord) {
	return (coord * TEXELS_PER_SAMPLE + 0.5) / TEXELS;
}

float height_at(vec2 coord) {
	return textureLod(height_tiles, vec3(texel_uv(coord), height_window.w), 0.0).r * height_decode.x + height_decode.y;
}

void vertex() {
	vec2 in_tile = VERTEX.xy;
	float skirt = VERTEX.z;
	float dlon = tile_rect.x + in_tile.x * tile_rect.z;
	float lat = tile_rect.y + in_tile.y * tile_rect.w;
	relative_geo = vec2(dlon, lat);
	tile_uv = in_tile;
	height_coord = height_window.xy + in_tile * HEIGHT_INTERVALS * height_window.z;
	cover_coord = cover_window.xy + in_tile * HEIGHT_INTERVALS * cover_window.z;
	colour_coord = colour_window.xy + in_tile * HEIGHT_INTERVALS * colour_window.z;
	water_coord = water_window.xy + in_tile * HEIGHT_INTERVALS * water_window.z;
	detail_coord = detail_window.xy + in_tile * HEIGHT_INTERVALS * detail_window.z;

	float lift_km = height_at(height_coord) * 0.001 * elevation_scale;
	// Skirt: a strip hanging down from the tile's edge. It hides the thin gaps that open where a
	// detailed tile meets a coarser neighbour.
	lift_km -= skirt * tile_rect.w * radius_km * 0.02 * min(elevation_scale, 1.0);

	vec3 surface = fold_project(lat, dlon, view_lat, projection_blend, fold);
	vec3 up = normalize(mix(vec3(0.0, 0.0, 1.0), globe_project(lat, dlon, view_lat) + vec3(0.0, 0.0, 1.0), fold));
	VERTEX = surface * radius_km + up * lift_km;
}

// 1 on a line centred on value == 0. `per_pixel` is how much value changes
// across one pixel; it must come from something continuous, because fwidth()
// of a value that jumps (fract, nearest-centre distance) draws stray marks.
float line_mask(float value, float per_pixel, float half_width_px) {
	return 1.0 - smoothstep(half_width_px, half_width_px + 1.0, abs(value) / max(per_pixel, 1e-9));
}

// Brightness from the slope of the ground, lit from the north-west as on a printed relief map.
// 1 on flat ground.
float relief_shade(float lat) {
	vec2 low = max(height_coord - 1.0, vec2(0.0));
	vec2 high = min(height_coord + 1.0, vec2(HEIGHT_INTERVALS));
	float west = height_at(vec2(low.x, height_coord.y));
	float east = height_at(vec2(high.x, height_coord.y));
	float south = height_at(vec2(height_coord.x, low.y));
	float north = height_at(vec2(height_coord.x, high.y));

	// Ground distance between two neighbouring height samples of the layer being read.
	float metres_north = tile_rect.w / (HEIGHT_INTERVALS * height_window.z) * radius_km * 1000.0;
	float metres_east = metres_north * max(cos(lat), 0.05);
	vec2 slope = vec2(
		(east - west) / ((high.x - low.x) * metres_east),
		(north - south) / ((high.y - low.y) * metres_north));

	vec3 normal = normalize(vec3(-slope * relief_exaggeration, 1.0));
	const vec3 light = vec3(-0.5, 0.5, 0.70710678);
	return dot(normal, light) / light.z;
}

// The colour of one kind of ground at a place.
vec3 kind_colour(float kind, float lon, float lat) {
	vec2 uv = vec2(lon / TAU + 0.5, clamp(0.5 - lat / PI, 0.002, 0.998));
	return textureLod(cover_colours, vec3(uv, kind), 0.0).rgb;
}

// Smooth weights for the four samples at offsets -1, 0, 1, 2 from a point a fraction f past
// sample 0 (a cubic B-spline). They fall off gently, so edges between kinds of ground follow
// smooth curves instead of the sample grid.
vec4 smooth_weights(float f) {
	float f2 = f * f;
	float f3 = f2 * f;
	return vec4(
		(1.0 - f) * (1.0 - f) * (1.0 - f),
		3.0 * f3 - 6.0 * f2 + 4.0,
		-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0,
		f3) / 6.0;
}

// The land up close, drawn kind by kind. Each of the 4 x 4 samples round the point gives its
// kind a smooth share by nearness; each kind's colour then counts by its share raised to
// cover_sharpness. Where one kind dominates it is drawn pure, with a clean edge; where kinds are
// mixed, as on a mountainside of rock, grass and snow, they blend. Lone samples fade instead of
// showing as squares. Water takes no share: the water tiles place the coast. Returns alpha 0
// where only water is near. Matches the preview in tools/terrain/TerrainTool/SurfacePreview.cs.
vec4 cover_land(float lon, float lat, out float dominant) {
	// In texels (0..255), which the B-spline steps over one at a time.
	vec2 p = clamp(cover_coord * TEXELS_PER_SAMPLE, vec2(0.0), vec2(TEXELS - 1.0));
	vec2 base = min(floor(p), vec2(TEXELS - 2.0));
	vec4 wx = smooth_weights(p.x - base.x);
	vec4 wy = smooth_weights(p.y - base.y);
	int layer = int(cover_window.w);
	float shares[11];
	for (int k = 0; k < 11; k++) {
		shares[k] = 0.0;
	}
	for (int j = 0; j < 4; j++) {
		for (int i = 0; i < 4; i++) {
			// Samples just outside the tile repeat its edge; the seam this leaves is not visible.
			ivec2 at = clamp(ivec2(base) + ivec2(i - 1, j - 1), ivec2(0), ivec2(int(TEXELS) - 1));
			int kind = int(round(texelFetch(cover_tiles, ivec3(at, layer), 0).r * 255.0));
			if (kind >= 0 && kind < 11 && kind != int(COVER_WATER)) {
				shares[kind] += wx[i] * wy[j];
			}
		}
	}
	vec3 colour = vec3(0.0);
	float total = 0.0;
	dominant = -1.0;
	float most = 0.0;
	for (int k = 0; k < 11; k++) {
		if (shares[k] > most) {
			most = shares[k];
			dominant = float(k);
		}
		if (shares[k] > 0.0) {
			float w = pow(shares[k], cover_sharpness);
			colour += w * kind_colour(float(k), lon, lat);
			total += w;
		}
	}
	return total > 0.0 ? vec4(colour / total, 1.0) : vec4(0.0);
}

// ---- Procedural detail ----
// Fine variation the data is too coarse to hold, made up in the shader so it costs no storage.
// It is noise computed from the point's position on the globe, so it is fixed to the ground: it
// never repeats like a tiled picture, and never moves as the view pans or zooms. Its layers have
// fixed ground sizes; each fades in once it is several pixels across, so zooming in uncovers
// finer layers beneath the ones already showing. It only brightens and darkens round the real
// colour, averaging out, so a region keeps the colour the data gives it.
//
// The noise is warped, so it flows in organic shapes rather than looking like static. On bare
// ground and shrubland it is ridged, like dune crests and gullies; elsewhere it is soft patches,
// like canopy and fields. Hollows and channels in the terrain come out a little darker.

// A well-mixed hash of three integers (PCG3D, Jarzynski & Olano 2020).
uvec3 pcg3d(uvec3 v) {
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	return v;
}

vec3 lattice_gradient(ivec3 cell) {
	vec3 g = vec3(pcg3d(uvec3(cell))) * (2.0 / 4294967295.0) - 1.0;
	return g;
}

// Smooth gradient noise in 3D, about -1 to 1, varying over about one unit.
float gradient_noise(vec3 p) {
	vec3 cell = floor(p);
	vec3 f = p - cell;
	vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	ivec3 c = ivec3(cell);
	float n000 = dot(lattice_gradient(c), f);
	float n100 = dot(lattice_gradient(c + ivec3(1, 0, 0)), f - vec3(1, 0, 0));
	float n010 = dot(lattice_gradient(c + ivec3(0, 1, 0)), f - vec3(0, 1, 0));
	float n110 = dot(lattice_gradient(c + ivec3(1, 1, 0)), f - vec3(1, 1, 0));
	float n001 = dot(lattice_gradient(c + ivec3(0, 0, 1)), f - vec3(0, 0, 1));
	float n101 = dot(lattice_gradient(c + ivec3(1, 0, 1)), f - vec3(1, 0, 1));
	float n011 = dot(lattice_gradient(c + ivec3(0, 1, 1)), f - vec3(0, 1, 1));
	float n111 = dot(lattice_gradient(c + ivec3(1, 1, 1)), f - vec3(1, 1, 1));
	return 1.6 * mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
		mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

// How each kind of ground varies within itself (Cover order): x = strength, y = how ridged.
vec2 kind_style(float kind) {
	int k = int(kind);
	if (k == 0) return vec2(0.9, 0.0);  // tree: soft canopy patches
	if (k == 1) return vec2(1.0, 0.5);  // shrub
	if (k == 2) return vec2(0.7, 0.2);  // grass
	if (k == 3) return vec2(0.8, 0.0);  // crop
	if (k == 4) return vec2(0.8, 0.0);  // built
	if (k == 5) return vec2(1.1, 1.0);  // bare: dunes, rock and gullies
	if (k == 6) return vec2(0.35, 0.6); // snow
	if (k == 8) return vec2(0.7, 0.0);  // wetland
	if (k == 9) return vec2(0.7, 0.0);  // mangrove
	if (k == 10) return vec2(0.8, 0.3); // moss
	return vec2(0.8, 0.3);
}

// The multiplier for the land's colour at a place. km_per_pixel: ground covered by one pixel.
//
// Two layers of soft patches, 3 km and 1.2 km across, at low contrast: enough to break up the
// smooth, airbrushed look where the data is coarse, without drawing a pattern the eye picks out.
// Each layer fades in once it is several pixels across.
vec3 procedural_detail(float lat, float lon, float km_per_pixel, vec2 style, float hollow) {
	vec3 p = vec3(cos(lat) * cos(lon), cos(lat) * sin(lon), sin(lat)) * radius_km;
	float broad = smoothstep(4.0, 10.0, 3.0 / km_per_pixel);
	if (broad <= 0.0) return vec3(1.0);
	float n = gradient_noise(p / 3.0) * 0.65 * broad;
	float fine = smoothstep(4.0, 10.0, 1.2 / km_per_pixel);
	if (fine > 0.0) n += gradient_noise(p / 1.2 + vec3(7.3)) * 0.35 * fine;
	float strength = procedural_strength * style.x;
	vec3 variation = 1.0 + strength * procedural_contrast * n * vec3(1.0, 0.94, 0.85);
	float relief = 1.0 - strength * procedural_relief * hollow * broad;
	return max(variation * relief, vec3(0.0));
}

vec2 hex_hash(ivec2 p) {
	uint n = uint(p.x) * 1597334673u ^ uint(p.y) * 3812015801u;
	n = (n ^ (n >> 16u)) * 2246822519u;
	n = (n ^ (n >> 13u)) * 3266489917u;
	return vec2(float(n & 65535u), float(n >> 16u)) / 65535.0;
}

// The texture layer for a kind of ground at a place: its zone's version, else its region's, else
// the standard one.
int ground_layer_at(int kind, float lat, float lon) {
	int layer = ground_layer[kind];
	if (!use_ground_regions) return layer;
	int x = ((int(floor(degrees(lon))) + 180) % 360 + 360) % 360;
	int y = clamp(89 - int(floor(degrees(lat))), 0, 179);
	vec2 rz = texelFetch(ground_regions, ivec2(x, y), 0).rg * 255.0;
	int region = int(rz.x + 0.5), zone = int(rz.y + 0.5);
	int by_zone = zone > 0 && zone < 16 ? ground_zone_layer[kind * 16 + zone] : -1;
	int by_region = region > 0 && region < 16 ? ground_region_layer[kind * 16 + region] : -1;
	return by_zone >= 0 ? by_zone : (by_region >= 0 ? by_region : layer);
}

// One hexagon: its centre (c: longitude, latitude, cosine of latitude) and its key for random
// numbers. The cosine comes from the pixel's own (a hexagon is 11 km across, so one step of
// cos(lat_c) ~ cos(lat) + sin(lat) (lat - lat_c) is exact enough), saving a cosine per hexagon.
void hex_centre(ivec2 v, float band_lon, float k, vec3 pixel, out vec3 c, out ivec2 key) {
	key = v + ivec2(int(round(band_lon * 5.729578)) * 7919, 0);
	vec2 centre = vec2(float(v.x), (float(v.y) + 0.57735027 * float(v.x)) / 1.15470054) * 0.25;
	float lat_c = centre.y / k;
	float cos_c = max(pixel.x + pixel.y * (pixel.z - lat_c), 0.05);
	c = vec3(band_lon + centre.x / (k * cos_c), lat_c, cos_c);
}

// Which texture layer a hexagon uses: the version for a place near its centre, up to half a degree
// away at random, so where two regions meet, hexagons of both mix over about 100 km instead of
// changing along one line.
int hex_layer(vec3 c, ivec2 key, int kind) {
	vec2 jitter = (hex_hash(key + ivec2(53, 97)) - 0.5) * 0.0174533;
	return ground_layer_at(kind, c.y + jitter.y, c.x + jitter.x);
}

// One hexagon's patch of a ground texture, laid flat in an east-north frame at the hexagon's
// centre so it keeps its true shape, shifted at random and turned a little. lod: the mipmap level,
// the same for all three patches (the turn does not change the footprint's size).
vec3 ground_patch(vec3 c, ivec2 key, vec2 geo, float k, float lod, float layer) {
	// Only a small turn, at most 10 degrees: dunes and field patterns have a direction, which a
	// full turn would scatter. So cos and sin are their small-angle forms.
	float a = (hex_hash(key).x - 0.5) * 0.35;
	mat2 turn = mat2(vec2(1.0 - 0.5 * a * a, a), vec2(-a, 1.0 - 0.5 * a * a));
	float dlon = geo.x - c.x;
	dlon -= 6.2831853 * round(dlon / 6.2831853);
	vec2 local = vec2(dlon * c.z, geo.y - c.y) * k;
	// So one spot of the sample does not show up again and again looking the same: each hexagon
	// mirrors it east-west, north-south, both or neither (which keeps fields lined up with the
	// compass), and shows it a little larger or smaller, 0.75 to 1.33 times.
	vec2 h = hex_hash(key + ivec2(71, 13));
	vec2 flip = vec2(h.x < 0.5 ? -1.0 : 1.0, fract(h.x * 2.0) < 0.5 ? -1.0 : 1.0);
	float grow = h.y * 0.83 - 0.415;
	vec2 q = turn * local * flip * exp2(grow) + 0.3 + 0.4 * hex_hash(key + ivec2(17, 31));
	return textureLod(ground_textures, vec3(q, layer), lod + grow).rgb * (255.0 / 128.0);
}

// A ground texture laid in hexagons (about a quarter of a texture across), each shifted at random
// and turned a little so no repeat shows. Where hexagons meet, their patterns are blended so the
// contrast stays the same: a plain average would wash the texture out into a blur there.
// geo: longitude and latitude; gdx, gdy: their change from pixel to pixel.
vec3 ground_texture(vec2 geo, vec2 gdx, vec2 gdy, int kind) {
	float layer = float(ground_layer[kind]);
	float k = radius_km / max(ground_kind_km[kind], 1.0);
	// The pixel's latitude, its cosine and sine, worked out once for all three hexagons.
	vec3 pixel = vec3(max(cos(geo.y), 0.05), sin(geo.y), geo.y);
	// The hexagon grid, in texture widths east and north. Longitude is measured from the nearest
	// multiple of 30 degrees (band_lon): measured from 0 the grid would lean more and more away from
	// it (east = longitude x cos latitude changes as you go north). The pattern changes at those
	// meridians.
	float band_lon = round(geo.x / 0.52359878) * 0.52359878;
	vec2 p = vec2((geo.x - band_lon) * pixel.x, geo.y) * k;
	vec2 pdx = vec2(gdx.x * pixel.x, gdx.y) * k, pdy = vec2(gdy.x * pixel.x, gdy.y) * k;
	if (!ground_hexagons) {
		return max(1.0 + (textureGrad(ground_textures, vec3(p, layer), pdx, pdy).rgb * (255.0 / 128.0) - 1.0) * ground_texture_strength, vec3(0.0));
	}
	float texels = float(textureSize(ground_textures, 0).x);
	float lod = log2(max(max(length(pdx), length(pdy)) * texels, 1e-6));
	vec2 uv = p * 4.0;
	vec2 s = vec2(uv.x, -0.57735027 * uv.x + 1.15470054 * uv.y);
	vec2 cell = floor(s);
	vec2 f = s - cell;
	ivec2 b = ivec2(cell);
	float z = 1.0 - f.x - f.y;
	vec3 w;
	ivec2 v1, v2, v3;
	if (z > 0.0) {
		w = vec3(z, f.y, f.x);
		v1 = b; v2 = b + ivec2(0, 1); v3 = b + ivec2(1, 0);
	} else {
		w = vec3(-z, 1.0 - f.y, 1.0 - f.x);
		v1 = b + ivec2(1, 1); v2 = b + ivec2(1, 0); v3 = b + ivec2(0, 1);
	}
	vec3 c1, c2, c3;
	ivec2 k1, k2, k3;
	hex_centre(v1, band_lon, k, pixel, c1, k1);
	hex_centre(v2, band_lon, k, pixel, c2, k2);
	hex_centre(v3, band_lon, k, pixel, c3, k3);
	int l1 = int(layer), l2 = l1, l3 = l1;
	if (use_ground_regions) {
		l1 = hex_layer(c1, k1, kind); l2 = hex_layer(c2, k2, kind); l3 = hex_layer(c3, k3, kind);
	}
	// Mostly one patch inside each hexagon, a narrow blend at its edge; where the hexagons use
	// different textures, a blend across the whole hexagon, so one kind of ground eases into the next.
	if (l1 == l2 && l2 == l3) w = w * w * w;
	w /= w.x + w.y + w.z;
	vec3 d = w.x * (ground_patch(c1, k1, geo, k, lod, float(l1)) - 1.0)
		+ w.y * (ground_patch(c2, k2, geo, k, lod, float(l2)) - 1.0)
		+ w.z * (ground_patch(c3, k3, geo, k, lod, float(l3)) - 1.0);
	d /= sqrt(dot(w, w));
	return max(1.0 + d * ground_texture_strength, vec3(0.0));
}

// How much lower the ground is than the ground round it, as a share of the sample spacing: positive
// in hollows and channels, negative on ridges, -1 to 1.
float terrain_hollow() {
	vec2 low = max(height_coord - 1.0, vec2(0.0));
	vec2 high = min(height_coord + 1.0, vec2(HEIGHT_INTERVALS));
	float around = (height_at(vec2(low.x, height_coord.y)) + height_at(vec2(high.x, height_coord.y))
		+ height_at(vec2(height_coord.x, low.y)) + height_at(vec2(height_coord.x, high.y))) * 0.25;
	float metres = tile_rect.w / (HEIGHT_INTERVALS * height_window.z) * radius_km * 1000.0;
	return clamp((around - height_at(height_coord)) / (metres * 0.03), -1.0, 1.0);
}

// The sea from its tone (see sea_shallow), or a lake.
vec3 water_colour(float tone) {
	if (tone > 0.998) {
		return lake_colour;
	}
	vec3 colour = mix(sea_shallow, sea_shelf, smoothstep(0.02, 0.2, tone));
	return mix(colour, sea_deep, smoothstep(0.2, 0.6, tone));
}

void fragment() {
	// On the flat map the world is cut at 180 degrees from the view. A tile that crosses the cut
	// is drawn twice, once at each end of the map, and each copy drops the part beyond the cut.
	if (abs(relative_geo.x) > PI + 1e-5) {
		discard;
	}

	float lat = relative_geo.y;
	float lon = relative_geo.x + view_lon;

	// Ground distance covered by one pixel.
	vec2 geo_per_pixel = fwidth(relative_geo);
	float km_per_pixel = radius_km * 0.7 * length(vec2(geo_per_pixel.x * cos(lat), geo_per_pixel.y));

	float shade = mix(1.0, clamp(relief_shade(lat), 0.35, 1.6), relief_strength);
	vec3 color;
	if (use_surface) {
		// The colour tiles hold no shadows of their own; the slope shading is the only light.
		vec4 surface = texture(colour_tiles, vec3(texel_uv(colour_coord), colour_window.w));
		float cover = texture(water_tiles, vec3(texel_uv(water_coord), water_window.w)).r;
		// The coast is where the water share crosses one half. Up close the share changes a lot
		// from pixel to pixel and the edge is sharp; from far away it changes little within a
		// pixel and the edge blends, so small islands fade instead of flickering.
		float edge = max(fwidth(cover) * coast_softness, 1e-4);
		float wet = smoothstep(0.5 - edge, 0.5 + edge, cover);

		// Up close, where land cover samples are at least about a pixel wide, draw the kinds of
		// ground one by one; further out, the blended colour, which is their average. The two
		// agree on average, so the change is a sharpening, not a shift.
		// (Derivatives are taken here, outside any branch that differs between pixels.)
		vec3 land = show_distant_colour ? surface.rgb : vec3(0.21);
		// Samples per pixel along the axis where they are smallest. The smooth drawing holds up
		// to about two samples a pixel before it would shimmer.
		vec2 cover_step = fwidth(cover_coord);
		float samples_per_pixel = max(max(cover_step.x, cover_step.y), 1e-4);
		// Both: few enough samples a pixel, and near enough in the long fade (cover_fade_km).
		float sharp = smoothstep(0.3, 0.6, 1.0 / samples_per_pixel)
			* (1.0 - smoothstep(cover_fade_km.y, cover_fade_km.x, km_per_pixel));
		// For the close-up ground textures: where we are, and how that changes from pixel to pixel
		// (longitude kept continuous across 180 degrees).
		vec2 ground_geo = vec2(lon, lat);
		vec2 ground_dx = dFdx(ground_geo);
		vec2 ground_dy = dFdy(ground_geo);
		ground_dx.x -= 6.2831853 * round(ground_dx.x / 6.2831853);
		ground_dy.x -= 6.2831853 * round(ground_dy.x / 6.2831853);
		vec2 style = vec2(0.8, 0.3);
		if (show_cover && cover_window.w >= 0.0 && sharp > 0.0) {
			float dominant;
			vec4 kinds = cover_land(lon, lat, dominant);
			land = mix(land, kinds.rgb, sharp * kinds.a);
			if (dominant >= 0.0) style = mix(style, kind_style(dominant), sharp * kinds.a);
			// Up close, the fine texture of real ground of this kind (fades in as the view nears).
			if (use_ground_textures && dominant >= 0.0 && km_per_pixel < ground_texture_far_km) {
				int layer = ground_layer[int(dominant)];
				if (layer >= 0) {
					float near = smoothstep(ground_texture_far_km, ground_texture_far_km * 0.5, km_per_pixel);
					vec3 texture_here = ground_texture(ground_geo, ground_dx, ground_dy, int(dominant));
					land *= mix(vec3(1.0), texture_here, near * sharp * kinds.a);
				}
			}
		}
		if (procedural_strength > 0.0) {
			land *= procedural_detail(lat, lon, km_per_pixel, style, terrain_hollow());
		}
		vec3 detail = show_detail
			? texture(detail_tiles, vec3(texel_uv(detail_coord), detail_window.w)).rgb * (255.0 / 128.0)
			: vec3(1.0);
		color = mix(land * detail * shade, water_colour(surface.a), wet);
	} else {
		// No colour or water packs: the game shows an error; the ground is left plain grey.
		color = vec3(0.3) * shade;
	}

	if (show_tile_levels) {
		vec3 tint = 0.5 + 0.5 * cos(tile_level * 2.4 + vec3(0.0, 2.1, 4.2));
		color = mix(color, tint, 0.3);
		vec2 to_edge = min(tile_uv, 1.0 - tile_uv);
		vec2 uv_per_pixel = fwidth(tile_uv);
		float border = max(
			line_mask(to_edge.x, uv_per_pixel.x, 0.5),
			line_mask(to_edge.y, uv_per_pixel.y, 0.5));
		color = mix(color, tint, border);
	}


	if (show_graticule) {
		vec2 cell = degrees(vec2(lon, lat)) / graticule_step_deg;
		vec2 cell_per_pixel = degrees(geo_per_pixel) / graticule_step_deg;
		vec2 to_line = fract(cell - 0.5) - 0.5;
		float grid = max(
			line_mask(to_line.x, cell_per_pixel.x, 0.4),
			line_mask(to_line.y, cell_per_pixel.y, 0.4));
		color = mix(color, vec3(1.0), grid * 0.45);
	}

	if (show_tissot) {
		// Equal-sized circles on the ground. Any that look squashed or stretched
		// show the distortion at that spot.
		float lat_step = radians(20.0);
		float center_lat = clamp(round(lat / lat_step) * lat_step, -radians(80.0), radians(80.0));
		float lon_step = abs(center_lat) > radians(70.0) ? radians(90.0) : radians(30.0);
		float center_lon = round(lon / lon_step) * lon_step;
		float d_km = angular_distance(lat, center_lat, lon - center_lon) * radius_km;
		float inside = 1.0 - smoothstep(-1.0, 1.0, (d_km - tissot_radius_km) / max(km_per_pixel, 1e-6));
		color = mix(color, vec3(1.0, 0.55, 0.2), inside * 0.3);
		color = mix(color, vec3(1.0, 0.45, 0.1), line_mask(d_km - tissot_radius_km, km_per_pixel, 0.8));
	}

	if (show_target_ring) {
		// A circle on the ground around the view target, in pure magenta so the
		// capture tool can measure it.
		float d_km = angular_distance(lat, view_lat, relative_geo.x) * radius_km;
		color = mix(color, vec3(1.0, 0.0, 1.0), line_mask(d_km - target_ring_radius_km, km_per_pixel, 1.2));
	}

	if (marker.z > 0.0) {
		float d_km = angular_distance(lat, marker.x, lon - marker.y) * radius_km;
		float inside = 1.0 - smoothstep(-1.0, 1.0, (d_km - marker.z) / max(km_per_pixel, 1e-6));
		color = mix(color, vec3(1.0, 0.0, 1.0), inside);
	}

	ALBEDO = color;
}
