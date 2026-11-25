using System;
using Godot;
using Tns.Core.Geo;
using Tns.Map.Terrain;

namespace Tns.Map;

/// <summary>
/// The world map. Owns where the camera is looking and how zoomed in it is, and folds the map
/// from a flat projection into a globe as the camera zooms in.
///
/// The point being looked at is always at the world origin. Panning changes which latitude and
/// longitude sit there; the camera only moves closer, further, or around it.
/// </summary>
public partial class MapView : Node3D
{
    private const double MaxLatitudeDeg = 89.0;

    [ExportGroup("View")]
    [Export] public double LatitudeDeg = 20.0;
    [Export] public double LongitudeDeg = 10.0;
    /// <summary>How many km of map fit top to bottom on screen at the view target.</summary>
    [Export] public double VisibleHeightKm = 20000.0;
    [Export] public double MinVisibleHeightKm = 150.0;
    /// <summary>Just under the pole-to-pole height of the flat map (pi x Earth radius).</summary>
    [Export] public double MaxVisibleHeightKm = 20000.0;
    /// <summary>Stops the top and bottom of the screen from passing the poles.</summary>
    [Export] public bool LimitToPoles = true;
    /// <summary>Tilt about the east-west line through the view target. Positive looks north.</summary>
    [Export] public double PitchDeg;
    /// <summary>Tilt about the central longitude. Positive puts the camera east of the target.</summary>
    [Export] public double SideTiltDeg;
    [Export] public double MaxTiltDeg = 75.0;
    [Export] public double ZoomSmoothing = 10.0;

    [ExportGroup("Fold")]
    /// <summary>The map is fully flat while more than this much is visible.</summary>
    [Export] public double FoldStartKm = 9000.0;
    /// <summary>The map is a complete globe once less than this much is visible.</summary>
    [Export] public double FoldEndKm = 3500.0;
    /// <summary>Flat projection: 0 = equirectangular, 0.5 = Winkel tripel, 1 = Aitoff.</summary>
    [Export(PropertyHint.Range, "0,1")] public double ProjectionBlend = 0.5;

    [ExportGroup("Terrain")]
    /// <summary>The ground stays smooth while more than this much is visible.</summary>
    [Export] public double ElevationFadeStartKm = 3500.0;
    /// <summary>The ground is fully raised once less than this much is visible.</summary>
    [Export] public double ElevationFadeEndKm = 2200.0;
    /// <summary>Slope shading, shown at every zoom: 0 = none, 1 = full.</summary>
    [Export(PropertyHint.Range, "0,1")] public double ReliefStrength = 0.5;
    /// <summary>Slopes are steepened this much before shading so gentle terrain still reads.</summary>
    [Export] public double ReliefExaggeration = 3.0;

    [ExportGroup("Debug")]
    /// <summary>Tints each terrain tile by its level and outlines it.</summary>
    [Export] public bool ShowTileLevels;
    [Export] public bool ShowGraticule;
    [Export] public bool ShowTissot;
    [Export] public bool ShowTargetRing;
    [Export] public double TargetRingRadiusKm = 600.0;
    /// <summary>When set, the fold follows ManualFoldAmount instead of the zoom level.</summary>
    [Export] public bool ManualFold;
    [Export(PropertyHint.Range, "0,1")] public double ManualFoldAmount;
    /// <summary>Draws a dot at a place on the map. A radius of 0 hides it.</summary>
    [Export] public double MarkerRadiusKm;
    [Export] public double MarkerLatitudeDeg;
    [Export] public double MarkerLongitudeDeg;

    public double TargetVisibleHeightKm { get; private set; }
    public Camera3D Camera { get; private set; }
    public TerrainRenderer Terrain { get; private set; }

    private ShaderMaterial _material;

    // While a zoom is anchored, the place at (_anchorLatitudeDeg, _anchorLongitudeDeg) is held
    // under the screen position _anchorScreen.
    private bool _zoomAnchored;
    private Vector2 _anchorScreen;
    private double _anchorLatitudeDeg;
    private double _anchorLongitudeDeg;

    /// <summary>0 = flat map, 1 = globe.</summary>
    public double Fold => ManualFold ? Math.Clamp(ManualFoldAmount, 0.0, 1.0) : FoldForHeight(VisibleHeightKm);

    /// <summary>
    /// How much of the ground's height is applied right now: 0 while the map is flat or still
    /// folding, rising to 1 (true scale, never more) as the camera zooms in on the finished globe.
    /// </summary>
    public double ElevationScale
    {
        get
        {
            if (Fold < 1.0) return 0.0;
            double t = (Math.Log(ElevationFadeStartKm) - Math.Log(VisibleHeightKm))
                / (Math.Log(ElevationFadeStartKm) - Math.Log(ElevationFadeEndKm));
            t = Math.Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }

    /// <summary>Screen pixels covered by one km at a distance of one km from the camera.</summary>
    public double PixelsPerUnitAtDistanceOne =>
        GetViewport().GetVisibleRect().Size.Y * 0.5 / Math.Tan(Mathf.DegToRad(Camera.Fov) * 0.5);

    /// <summary>
    /// How much wider than reality things are drawn at the view target, relative to their height.
    /// 1 means no distortion, which is what a globe gives.
    /// </summary>
    public double EastWestStretch
    {
        get
        {
            double lat = Mathf.DegToRad(LatitudeDeg);
            return MapProjection.EastWestScale(lat, ProjectionBlend, Fold) / Math.Cos(lat);
        }
    }

    /// <summary>
    /// The furthest north or south the view target can be at the current zoom before the edge of
    /// the screen would pass a pole. Assumes the camera is looking straight down.
    /// </summary>
    public double MaxLatitudeDegNow
    {
        get
        {
            if (!LimitToPoles || Camera == null) return MaxLatitudeDeg;
            double halfFov = Mathf.DegToRad(Camera.Fov) * 0.5;
            double arc = MapProjection.ArcToScreenEdge(VisibleHeightKm, halfFov, Fold);
            // A sliver of margin so the pole itself stays just off screen.
            double margin = 0.005 * VisibleHeightKm / MapProjection.EarthRadiusKm;
            return Math.Clamp(Mathf.RadToDeg(Math.PI / 2.0 - arc - margin), 0.0, MaxLatitudeDeg);
        }
    }

    public override void _Ready()
    {
        Camera = GetNode<Camera3D>("Camera");
        Terrain = GetNode<TerrainRenderer>("Terrain");
        _material = Terrain.Material;
        Terrain.Setup(this);

        TargetVisibleHeightKm = VisibleHeightKm;
        Apply();
    }

    public override void _Process(double delta)
    {
        double blend = 1.0 - Math.Exp(-ZoomSmoothing * delta);
        double logHeight = Mathf.Lerp(Math.Log(VisibleHeightKm), Math.Log(TargetVisibleHeightKm), blend);
        VisibleHeightKm = Math.Exp(logHeight);

        if (_zoomAnchored)
        {
            HoldZoomAnchor();
            if (Math.Abs(Math.Log(VisibleHeightKm / TargetVisibleHeightKm)) < 1e-4) _zoomAnchored = false;
        }

        // The pole limit always wins: zooming out near a pole slides the view toward the equator.
        LatitudeDeg = ClampLatitude(LatitudeDeg);
        Apply();
    }

    public double FoldForHeight(double visibleHeightKm)
    {
        double t = (Math.Log(FoldStartKm) - Math.Log(visibleHeightKm))
            / (Math.Log(FoldStartKm) - Math.Log(FoldEndKm));
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Jumps straight to a view with no zoom animation.</summary>
    public void SetView(double latitudeDeg, double longitudeDeg, double visibleHeightKm)
    {
        _zoomAnchored = false;
        VisibleHeightKm = Math.Clamp(visibleHeightKm, MinVisibleHeightKm, MaxVisibleHeightKm);
        TargetVisibleHeightKm = VisibleHeightKm;
        LatitudeDeg = ClampLatitude(latitudeDeg);
        LongitudeDeg = WrapDegrees(longitudeDeg);
        Apply();
    }

    /// <summary>Zooms toward or away from the centre of the screen.</summary>
    public void ZoomTo(double visibleHeightKm)
    {
        _zoomAnchored = false;
        TargetVisibleHeightKm = Math.Clamp(visibleHeightKm, MinVisibleHeightKm, MaxVisibleHeightKm);
    }

    public void ZoomBy(double factor) => ZoomTo(TargetVisibleHeightKm * factor);

    /// <summary>
    /// Zooms while keeping the place under a screen position where it is, so zooming in heads for
    /// the cursor. Falls back to zooming on the centre if that position is off the map.
    /// </summary>
    public void ZoomAt(Vector2 screenPosition, double factor)
    {
        ZoomBy(factor);
        if (!TryScreenToGeo(screenPosition, out double latitudeDeg, out double longitudeDeg)) return;
        _zoomAnchored = true;
        _anchorScreen = screenPosition;
        _anchorLatitudeDeg = latitudeDeg;
        _anchorLongitudeDeg = longitudeDeg;
    }

    /// <summary>Moves the view target. Distances are along the ground at the target.</summary>
    public void PanByKm(double eastKm, double northKm)
    {
        _zoomAnchored = false;
        double lat = Mathf.DegToRad(LatitudeDeg);
        double eastWestScale = Math.Max(0.02, MapProjection.EastWestScale(lat, ProjectionBlend, Fold));
        LatitudeDeg = ClampLatitude(LatitudeDeg + Mathf.RadToDeg(northKm / MapProjection.EarthRadiusKm));
        LongitudeDeg = WrapDegrees(
            LongitudeDeg + Mathf.RadToDeg(eastKm / (MapProjection.EarthRadiusKm * eastWestScale)));
    }

    /// <summary>Drags the map by a mouse movement, keeping the grabbed point under the cursor.</summary>
    public void PanByPixels(Vector2 pixels)
    {
        double kmPerPixel = VisibleHeightKm / GetViewport().GetVisibleRect().Size.Y;
        double east = pixels.X * kmPerPixel / Math.Max(0.2, Math.Cos(Mathf.DegToRad(SideTiltDeg)));
        double north = -pixels.Y * kmPerPixel / Math.Max(0.2, Math.Cos(Mathf.DegToRad(PitchDeg)));
        PanByKm(-east, -north);
    }

    /// <summary>Tilts the camera round the view target, sideways and up or down.</summary>
    public void Tilt(double sideDeltaDeg, double pitchDeltaDeg)
    {
        SideTiltDeg = Math.Clamp(SideTiltDeg + sideDeltaDeg, -MaxTiltDeg, MaxTiltDeg);
        PitchDeg = Math.Clamp(PitchDeg + pitchDeltaDeg, -MaxTiltDeg, MaxTiltDeg);
    }

    /// <summary>Where a place currently sits in the world, given the view and fold.</summary>
    public Vector3 GeoToWorld(double latitudeDeg, double longitudeDeg)
    {
        var (x, y, z) = FoldedKm(latitudeDeg, longitudeDeg, LatitudeDeg, LongitudeDeg);
        return new Vector3((float)x, (float)y, (float)z);
    }

    /// <summary>Where a place currently appears on screen, in viewport coordinates.</summary>
    public Vector2 GeoToScreen(double latitudeDeg, double longitudeDeg)
    {
        var (x, y) = Project(latitudeDeg, longitudeDeg, LatitudeDeg, LongitudeDeg);
        return new Vector2((float)x, (float)y);
    }

    /// <summary>
    /// Where a point given by latitude and by longitude relative to the view appears: screen
    /// position, distance from the camera in km, and whether it is on the side of the globe
    /// facing the camera. Longitude is not wrapped, so points just past the edge of the flat
    /// map still give sensible answers.
    /// </summary>
    public (double X, double Y, double Distance, bool Facing) ProjectRelative(double latitudeDeg, double relativeLongitudeDeg)
    {
        double fold = Fold;
        var (ux, uy, uz) = MapProjection.Folded(
            Mathf.DegToRad(latitudeDeg), Mathf.DegToRad(relativeLongitudeDeg),
            Mathf.DegToRad(LatitudeDeg), ProjectionBlend, fold);
        double r = MapProjection.EarthRadiusKm;
        var (position, right, up, forward) = CameraFrame();
        var toPoint = new Vec3(ux * r - position.X, uy * r - position.Y, uz * r - position.Z);
        double depth = Math.Max(1e-6, toPoint.Dot(forward));
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        double pixelsPerUnit = PixelsPerUnitAtDistanceOne;

        // On the finished globe a point faces the camera when the camera is above its horizon.
        // The centre of the globe is one radius behind the origin.
        bool facing = true;
        if (fold >= 0.999)
        {
            var outward = new Vec3(ux, uy, uz + 1.0);
            facing = outward.Dot(toPoint) < 0.02 * toPoint.Length();
        }

        return (
            viewport.X * 0.5 + toPoint.Dot(right) / depth * pixelsPerUnit,
            viewport.Y * 0.5 - toPoint.Dot(up) / depth * pixelsPerUnit,
            toPoint.Length(),
            facing);
    }

    /// <summary>The place under a screen position. False if that position is off the map.</summary>
    public bool TryScreenToGeo(Vector2 screenPosition, out double latitudeDeg, out double longitudeDeg)
    {
        double lat = LatitudeDeg;
        double dlon = 0.0;
        bool found = SolveNewton(
            (a, b) => Project(a, LongitudeDeg + b, LatitudeDeg, LongitudeDeg),
            screenPosition.X, screenPosition.Y, ref lat, ref dlon);
        latitudeDeg = lat;
        longitudeDeg = WrapDegrees(LongitudeDeg + dlon);
        return found && Math.Abs(lat) <= 90.0 && Math.Abs(dlon) <= 180.0;
    }

    /// <summary>Pushes the current view to the camera and the shader.</summary>
    public void Apply()
    {
        if (Camera == null) return;

        var (position, _, _, _) = CameraFrame();
        Camera.LookAtFromPosition(position.ToVector3(), Vector3.Zero, Vector3.Up);
        double distance = position.Length();
        Camera.Near = (float)Math.Max(0.5, distance * 0.05);
        Camera.Far = (float)(distance + 80000.0);

        _material.SetShaderParameter("radius_km", (float)MapProjection.EarthRadiusKm);
        _material.SetShaderParameter("view_lat", (float)Mathf.DegToRad(LatitudeDeg));
        _material.SetShaderParameter("view_lon", (float)Mathf.DegToRad(LongitudeDeg));
        _material.SetShaderParameter("fold", (float)Fold);
        _material.SetShaderParameter("projection_blend", (float)ProjectionBlend);
        _material.SetShaderParameter("elevation_scale", (float)ElevationScale);
        _material.SetShaderParameter("relief_strength", (float)ReliefStrength);
        _material.SetShaderParameter("relief_exaggeration", (float)ReliefExaggeration);
        _material.SetShaderParameter("show_tile_levels", ShowTileLevels);
        _material.SetShaderParameter("show_graticule", ShowGraticule);
        _material.SetShaderParameter("show_tissot", ShowTissot);
        _material.SetShaderParameter("show_target_ring", ShowTargetRing);
        _material.SetShaderParameter("target_ring_radius_km", (float)TargetRingRadiusKm);
        _material.SetShaderParameter("marker", new Vector3(
            (float)Mathf.DegToRad(MarkerLatitudeDeg),
            (float)Mathf.DegToRad(MarkerLongitudeDeg),
            (float)MarkerRadiusKm));

        Terrain.Refresh();
    }

    private double ClampLatitude(double latitudeDeg)
    {
        double limit = MaxLatitudeDegNow;
        return Math.Clamp(latitudeDeg, -limit, limit);
    }

    /// <summary>Moves the view so the zoom anchor is back under its screen position.</summary>
    private void HoldZoomAnchor()
    {
        double viewLat = LatitudeDeg;
        double viewLon = LongitudeDeg;
        bool found = SolveNewton(
            (a, b) => Project(_anchorLatitudeDeg, _anchorLongitudeDeg, a, b),
            _anchorScreen.X, _anchorScreen.Y, ref viewLat, ref viewLon);
        if (!found) return;
        LatitudeDeg = viewLat;
        LongitudeDeg = WrapDegrees(viewLon);
    }

    private (double X, double Y, double Z) FoldedKm(
        double latitudeDeg, double longitudeDeg, double viewLatitudeDeg, double viewLongitudeDeg)
    {
        double dlon = MapProjection.WrapLongitude(Mathf.DegToRad(longitudeDeg - viewLongitudeDeg));
        var (x, y, z) = MapProjection.Folded(
            Mathf.DegToRad(latitudeDeg), dlon, Mathf.DegToRad(viewLatitudeDeg), ProjectionBlend, Fold);
        double r = MapProjection.EarthRadiusKm;
        return (x * r, y * r, z * r);
    }

    /// <summary>
    /// Camera position and its right, up and forward directions, from the zoom and tilt. Done in
    /// doubles and without touching the Camera node, so the solvers below stay precise.
    /// </summary>
    private (Vec3 Position, Vec3 Right, Vec3 Up, Vec3 Forward) CameraFrame()
    {
        double distance = VisibleHeightKm / (2.0 * Math.Tan(Mathf.DegToRad(Camera.Fov) * 0.5));
        double pitch = Mathf.DegToRad(PitchDeg);
        double side = Mathf.DegToRad(SideTiltDeg);
        var position = new Vec3(
            distance * Math.Cos(pitch) * Math.Sin(side),
            -distance * Math.Sin(pitch),
            distance * Math.Cos(pitch) * Math.Cos(side));
        Vec3 forward = position.Scaled(-1.0 / distance);
        Vec3 right = forward.Cross(new Vec3(0.0, 1.0, 0.0)).Normalized();
        Vec3 up = right.Cross(forward);
        return (position, right, up, forward);
    }

    /// <summary>Screen position of a place for a given view target, in viewport coordinates.</summary>
    private (double X, double Y) Project(
        double latitudeDeg, double longitudeDeg, double viewLatitudeDeg, double viewLongitudeDeg)
    {
        var (x, y, z) = FoldedKm(latitudeDeg, longitudeDeg, viewLatitudeDeg, viewLongitudeDeg);
        var (position, right, up, forward) = CameraFrame();
        var toPoint = new Vec3(x - position.X, y - position.Y, z - position.Z);
        double depth = Math.Max(1e-6, toPoint.Dot(forward));

        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        double pixelsPerUnit = viewport.Y * 0.5 / Math.Tan(Mathf.DegToRad(Camera.Fov) * 0.5);
        return (
            viewport.X * 0.5 + toPoint.Dot(right) / depth * pixelsPerUnit,
            viewport.Y * 0.5 - toPoint.Dot(up) / depth * pixelsPerUnit);
    }

    /// <summary>
    /// Finds (a, b), in degrees, where function(a, b) lands on the target screen position, by
    /// Newton's method starting from the values passed in. True if it got within 0.05 px.
    /// </summary>
    private static bool SolveNewton(
        Func<double, double, (double X, double Y)> function,
        double targetX, double targetY, ref double a, ref double b)
    {
        const double step = 1e-5;
        const double maxMoveDeg = 25.0;

        for (int i = 0; i < 20; i++)
        {
            var (x, y) = function(a, b);
            double errorX = targetX - x;
            double errorY = targetY - y;
            if (errorX * errorX + errorY * errorY < 0.05 * 0.05) return true;

            var (xa, ya) = function(a + step, b);
            var (xb, yb) = function(a, b + step);
            double j11 = (xa - x) / step, j12 = (xb - x) / step;
            double j21 = (ya - y) / step, j22 = (yb - y) / step;
            double determinant = j11 * j22 - j12 * j21;
            if (Math.Abs(determinant) < 1e-12) return false;

            a += Math.Clamp((errorX * j22 - errorY * j12) / determinant, -maxMoveDeg, maxMoveDeg);
            b += Math.Clamp((errorY * j11 - errorX * j21) / determinant, -maxMoveDeg, maxMoveDeg);
        }

        return false;
    }

    private static double WrapDegrees(double degrees)
    {
        degrees = (degrees + 180.0) % 360.0;
        if (degrees < 0.0) degrees += 360.0;
        return degrees - 180.0;
    }

    /// <summary>A 3D vector in doubles. Godot's Vector3 is single precision.</summary>
    private readonly record struct Vec3(double X, double Y, double Z)
    {
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public Vec3 Scaled(double s) => new(X * s, Y * s, Z * s);
        public double Length() => Math.Sqrt(Dot(this));
        public Vec3 Normalized() => Scaled(1.0 / Length());
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    }
}
