using System;

namespace Tns.Core.Geo;

/// <summary>
/// Map projection maths. Mirrors shaders/map/map_projection.gdshaderinc: keep the two in step.
/// Angles are radians, dlon is longitude relative to the view longitude, and results are in
/// units of Earth radii. No engine types, so this can be unit tested and reused by the simulation.
/// </summary>
public static class MapProjection
{
    public const double EarthRadiusKm = 6371.0;

    /// <summary>Cosine of the Winkel tripel standard parallel, acos(2 / pi).</summary>
    public const double WinkelCosPhi1 = 2.0 / Math.PI;

    public static (double X, double Y) Aitoff(double lat, double dlon)
    {
        double cosLat = Math.Cos(lat);
        double alpha = Math.Acos(Math.Clamp(cosLat * Math.Cos(0.5 * dlon), -1.0, 1.0));
        double invSinc = alpha < 1e-9 ? 1.0 : alpha / Math.Sin(alpha);
        return (2.0 * cosLat * Math.Sin(0.5 * dlon) * invSinc, Math.Sin(lat) * invSinc);
    }

    /// <summary>Flat map position. blend 0 = equirectangular, 0.5 = Winkel tripel, 1 = Aitoff.</summary>
    public static (double X, double Y) Flat(double lat, double dlon, double blend)
    {
        var (ax, ay) = Aitoff(lat, dlon);
        double ex = dlon * WinkelCosPhi1;
        return (ex + (ax - ex) * blend, lat + (ay - lat) * blend);
    }

    /// <summary>
    /// Position on a unit globe turned so the view target (viewLat, dlon = 0) faces +Z
    /// and sits at the origin.
    /// </summary>
    public static (double X, double Y, double Z) Globe(double lat, double dlon, double viewLat)
    {
        double cosLat = Math.Cos(lat);
        double x = cosLat * Math.Sin(dlon);
        double y = Math.Sin(lat);
        double z = cosLat * Math.Cos(dlon);
        double c = Math.Cos(viewLat);
        double s = Math.Sin(viewLat);
        return (x, y * c - z * s, y * s + z * c - 1.0);
    }

    /// <summary>The fold: 0 = flat map, 1 = globe. The view target stays at the origin.</summary>
    public static (double X, double Y, double Z) Folded(
        double lat, double dlon, double viewLat, double blend, double fold)
    {
        var (fx, fy) = Flat(lat, dlon, blend);
        fy -= viewLat;
        var (gx, gy, gz) = Globe(lat, dlon, viewLat);
        return (fx + (gx - fx) * fold, fy + (gy - fy) * fold, gz * fold);
    }

    /// <summary>
    /// How far the map moves on screen per radian of longitude at the view target.
    /// On a true globe this is cos(lat).
    /// </summary>
    public static double EastWestScale(double lat, double blend, double fold)
    {
        double aitoff = Math.Abs(lat) < 1e-9 ? 1.0 : Math.Cos(lat) * lat / Math.Sin(lat);
        double flat = WinkelCosPhi1 + (aitoff - WinkelCosPhi1) * blend;
        return flat + (Math.Cos(lat) - flat) * fold;
    }

    /// <summary>
    /// How far north of the view target (as an angle along the ground) the top edge of the screen
    /// reaches, for a camera looking straight down. On the flat map this is simply half the visible
    /// height; on the globe the surface curves away from the camera, so the screen reaches further.
    /// </summary>
    public static double ArcToScreenEdge(double visibleHeightKm, double halfFovRadians, double fold)
    {
        double tanHalfFov = Math.Tan(halfFovRadians);
        double distance = visibleHeightKm / (2.0 * tanHalfFov) / EarthRadiusKm;

        // Height on screen of the point an angle `arc` north of the target, as a fraction of the
        // way to the top edge. It grows with arc, so the edge can be found by bisection.
        double ScreenFraction(double arc)
        {
            double y = arc + (Math.Sin(arc) - arc) * fold;
            double z = (Math.Cos(arc) - 1.0) * fold;
            return y / (distance - z) / tanHalfFov;
        }

        double low = 0.0;
        double high = Math.PI / 2.0;
        if (ScreenFraction(high) <= 1.0) return high;
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (low + high);
            if (ScreenFraction(mid) < 1.0) low = mid; else high = mid;
        }

        return high;
    }

    /// <summary>Point reached by travelling an angular distance from a start point on a bearing.</summary>
    public static (double Lat, double DLon) Destination(double lat, double bearing, double distance)
    {
        double sinLat = Math.Sin(lat) * Math.Cos(distance)
            + Math.Cos(lat) * Math.Sin(distance) * Math.Cos(bearing);
        double lat2 = Math.Asin(Math.Clamp(sinLat, -1.0, 1.0));
        double dlon = Math.Atan2(
            Math.Sin(bearing) * Math.Sin(distance) * Math.Cos(lat),
            Math.Cos(distance) - Math.Sin(lat) * sinLat);
        return (lat2, dlon);
    }

    public static double WrapLongitude(double lon)
    {
        lon = (lon + Math.PI) % (2.0 * Math.PI);
        if (lon < 0.0) lon += 2.0 * Math.PI;
        return lon - Math.PI;
    }
}
