using System;
using Godot;

namespace Tns.Map;

/// <summary>
/// Mouse and keyboard control of the map.
///
///   Left or middle drag: pan        Wheel: zoom
///   WASD / arrows: pan              Wheel up zooms toward the cursor
///   Right drag: tilt up/down and side to side, eases back on release
///   G: grid   T: distortion circles   R: ring round the target   L: terrain tile outlines
///   F: manual fold on/off   Q / E: fold less / more   Space: play the fold back and forth
///   1-5: jump to a test place
///   (F3, handled by the debug HUD: show or hide the on-screen readout)
/// </summary>
public partial class MapInput : Node
{
    [Export] public double WheelZoomStep = 1.15;
    /// <summary>When set, zooming out also keeps the place under the cursor fixed.</summary>
    [Export] public bool ZoomOutFromCursor;
    [Export] public double KeyPanScreensPerSecond = 0.6;
    [Export] public double TiltDegreesPerPixel = 0.25;
    [Export] public double PlaySeconds = 5.0;
    /// <summary>How quickly the tilt eases back once the right button is released. Higher is faster.</summary>
    [Export] public double TiltReturnRate = 6.0;

    private MapView _view;
    private bool _panning;
    private bool _tilting;
    private bool _playing;
    private double _playTime;
    private double _playLatitudeDeg;

    public override void _Ready()
    {
        _view = GetParent<MapView>();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button:
                OnMouseButton(button);
                break;
            case InputEventMouseMotion motion when _panning:
                _view.PanByPixels(motion.Relative);
                break;
            case InputEventMouseMotion motion when _tilting:
                _view.Tilt(-motion.Relative.X * TiltDegreesPerPixel, -motion.Relative.Y * TiltDegreesPerPixel);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key.PhysicalKeycode);
                break;
        }
    }

    public override void _Process(double delta)
    {
        double step = _view.VisibleHeightKm * KeyPanScreensPerSecond * delta;
        double east = Axis(Key.D, Key.Right) - Axis(Key.A, Key.Left);
        double north = Axis(Key.W, Key.Up) - Axis(Key.S, Key.Down);
        if (east != 0.0 || north != 0.0) _view.PanByKm(east * step, north * step);

        if (_view.ManualFold)
        {
            double change = Axis(Key.E, Key.E) - Axis(Key.Q, Key.Q);
            _view.ManualFoldAmount = Math.Clamp(_view.ManualFoldAmount + change * delta * 0.5, 0.0, 1.0);
        }

        if (!_tilting) ReturnTilt(delta);
        if (_playing) Play(delta);
    }

    private void OnMouseButton(InputEventMouseButton button)
    {
        switch (button.ButtonIndex)
        {
            case MouseButton.WheelUp when button.Pressed:
                _playing = false;
                _view.ZoomAt(button.Position, 1.0 / WheelZoomStep);
                break;
            case MouseButton.WheelDown when button.Pressed:
                _playing = false;
                if (ZoomOutFromCursor) _view.ZoomAt(button.Position, WheelZoomStep);
                else _view.ZoomBy(WheelZoomStep);
                break;
            case MouseButton.Left or MouseButton.Middle:
                _panning = button.Pressed;
                break;
            case MouseButton.Right:
                _tilting = button.Pressed;
                break;
        }
    }

    private void OnKey(Key key)
    {
        switch (key)
        {
            case Key.G: _view.ShowGraticule = !_view.ShowGraticule; break;
            case Key.T: _view.ShowTissot = !_view.ShowTissot; break;
            case Key.R: _view.ShowTargetRing = !_view.ShowTargetRing; break;
            case Key.L: _view.ShowTileLevels = !_view.ShowTileLevels; break;
            case Key.P: _view.Terrain.ShowGroundTextures = !_view.Terrain.ShowGroundTextures; break;
            case Key.O: _view.Terrain.FastLoading = !_view.Terrain.FastLoading; break;
            case Key.F:
                _view.ManualFoldAmount = _view.Fold;
                _view.ManualFold = !_view.ManualFold;
                break;
            case Key.Space:
                _playing = !_playing;
                _playTime = 0.0;
                _playLatitudeDeg = _view.LatitudeDeg;
                _view.ManualFold = false;
                break;
            case >= Key.Key1 and <= Key.Key9:
                int index = (int)(key - Key.Key1);
                if (index < TestPlaces.All.Length)
                {
                    TestPlace place = TestPlaces.All[index];
                    _view.SetView(place.LatitudeDeg, place.LongitudeDeg, _view.VisibleHeightKm);
                }
                break;
        }
    }

    /// <summary>Zooms in and out across the whole fold range so the fold can be watched hands-free.</summary>
    private void Play(double delta)
    {
        _playTime += delta;
        double phase = 0.5 - 0.5 * Math.Cos(_playTime / PlaySeconds * Math.PI);
        double far = Math.Log(_view.FoldStartKm * 1.3);
        double near = Math.Log(_view.FoldEndKm * 0.7);
        // Ask for the starting latitude every frame: the pole limit pushes the view toward the
        // equator while zoomed out, and this brings it back as the zoom returns.
        _view.SetView(_playLatitudeDeg, _view.LongitudeDeg, Math.Exp(far + (near - far) * phase));
    }

    /// <summary>Eases both tilts back to a straight-down view.</summary>
    private void ReturnTilt(double delta)
    {
        if (_view.PitchDeg == 0.0 && _view.SideTiltDeg == 0.0) return;
        double keep = Math.Exp(-TiltReturnRate * delta);
        _view.PitchDeg = Math.Abs(_view.PitchDeg) < 0.02 ? 0.0 : _view.PitchDeg * keep;
        _view.SideTiltDeg = Math.Abs(_view.SideTiltDeg) < 0.02 ? 0.0 : _view.SideTiltDeg * keep;
    }

    private static double Axis(Key a, Key b) =>
        Input.IsPhysicalKeyPressed(a) || Input.IsPhysicalKeyPressed(b) ? 1.0 : 0.0;
}
