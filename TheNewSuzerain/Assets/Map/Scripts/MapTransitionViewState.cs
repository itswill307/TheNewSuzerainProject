using System;

[Serializable]
public struct MapCesiumTransitionViewState
{
    public bool isValid;
    public double focusLongitudeDeg;
    public double focusLatitudeDeg;
    public double focusHeightMeters;
    public float orbitYawDeg;
    public float orbitPitchDeg;
    public float fieldOfViewDeg;
    public float surfaceDistanceMeters;
    public float normalizedFillDistance;
    public float visibleLongitudeSpanDeg;
    public float visibleLatitudeSpanDeg;
}
