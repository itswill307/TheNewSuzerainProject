namespace Tns.Map;

public readonly record struct TestPlace(string Name, double LatitudeDeg, double LongitudeDeg);

/// <summary>Places used to check the fold, from no distortion (equator) to the most (near the pole).</summary>
public static class TestPlaces
{
    public static readonly TestPlace[] All =
    {
        new("Gulf of Guinea", 5.0, 5.0),
        new("Paris", 49.0, 2.0),
        new("Svalbard", 78.0, 16.0),
        new("Near north pole", 86.0, 0.0),
        new("Tierra del Fuego", -54.0, -68.0),
    };
}
