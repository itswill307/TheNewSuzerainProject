// Map projection maths shared by the map shaders.
// Mirrored on the CPU in src/Core/Geo/MapProjection.cs: keep the two in step.
//
// All angles are radians. `dlon` is longitude relative to the view longitude,
// in [-PI, PI]. Results are in units of Earth radii.

// Standard parallel of the Winkel tripel projection: acos(2 / PI).
const float WINKEL_COS_PHI1 = 0.6366197723675814;

vec2 aitoff(float lat, float dlon) {
	float cos_lat = cos(lat);
	float alpha = acos(clamp(cos_lat * cos(0.5 * dlon), -1.0, 1.0));
	float inv_sinc = alpha < 1e-4 ? 1.0 : alpha / sin(alpha);
	return vec2(2.0 * cos_lat * sin(0.5 * dlon), sin(lat)) * inv_sinc;
}

// Flat map position. blend 0 = equirectangular, 0.5 = Winkel tripel, 1 = Aitoff.
vec2 flat_project(float lat, float dlon, float blend) {
	vec2 equirectangular = vec2(dlon * WINKEL_COS_PHI1, lat);
	return mix(equirectangular, aitoff(lat, dlon), blend);
}

// Position on a unit globe that has been turned so the view target
// (view_lat, dlon = 0) faces +Z and sits at the origin.
vec3 globe_project(float lat, float dlon, float view_lat) {
	float cos_lat = cos(lat);
	vec3 p = vec3(cos_lat * sin(dlon), sin(lat), cos_lat * cos(dlon));
	float c = cos(view_lat);
	float s = sin(view_lat);
	return vec3(p.x, p.y * c - p.z * s, p.y * s + p.z * c - 1.0);
}

// The fold: 0 = flat map, 1 = globe. The view target stays at the origin
// throughout, so the camera never has to chase it.
vec3 fold_project(float lat, float dlon, float view_lat, float blend, float fold) {
	vec3 flat_pos = vec3(flat_project(lat, dlon, blend) - vec2(0.0, view_lat), 0.0);
	return mix(flat_pos, globe_project(lat, dlon, view_lat), fold);
}

// Great-circle distance between two points, as an angle.
float angular_distance(float lat_a, float lat_b, float dlon) {
	float sin_dlat = sin(0.5 * (lat_a - lat_b));
	float sin_dlon = sin(0.5 * dlon);
	float h = sin_dlat * sin_dlat + cos(lat_a) * cos(lat_b) * sin_dlon * sin_dlon;
	return 2.0 * asin(sqrt(clamp(h, 0.0, 1.0)));
}
