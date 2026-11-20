using System;
using System.Collections.Generic;
using Godot;

namespace FlatMap;

/// <summary>
/// Full-viewport flat parchment backdrop (Act.MapBgColor) placed behind TheMap on NMapScreen
/// when Flat mode is active. Fades smoothly in sync with NMapScreen's Open/Close tweens.
/// </summary>
internal sealed partial class FlatBackdrop : ColorRect
{
    private Tween? _fadeTween;

    internal FlatBackdrop()
    {
        Name = "FlatMapBackdrop";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);
        OffsetLeft = 0f;
        OffsetTop = 0f;
        OffsetRight = 0f;
        OffsetBottom = 0f;
    }

    internal void SetActive(bool active, Color bgColor, bool animate = false)
    {
        _fadeTween?.Kill();
        _fadeTween = null;
        Color = new Color(bgColor.R, bgColor.G, bgColor.B, 1f);
        if (active)
        {
            Visible = true;
            if (animate)
            {
                Modulate = new Color(1f, 1f, 1f, 0f);
                _fadeTween = CreateTween();
                _fadeTween.TweenProperty(this, "modulate:a", 1f, 0.25)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            }
            else
            {
                Modulate = Colors.White;
            }
        }
        else
        {
            Modulate = Colors.White;
            Visible = false;
        }
    }

    internal void FadeOut(double duration = 0.15)
    {
        if (!Visible) return;
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0f, duration);
    }
}

/// <summary>
/// Event-driven dashed-path layer inside TheMap (right before Points) for Flat mode.
/// Redraws strictly on layout/mode/travel/ghosting changes — zero per-frame redraws.
/// </summary>
internal sealed partial class FlatPathsLayer : Control
{
    private const int CurveSteps = 20;

    private List<EdgeVisual> _edges = new();
    private Color _traveledColor = new(0.14f, 0.11f, 0.08f, 1f);
    private Color _untraveledColor = new(0.28f, 0.24f, 0.18f, 0.84f);
    private Color _ghostedColor = new(0.28f, 0.24f, 0.18f, 0.16f);

    private readonly Vector2[] _samplePts = new Vector2[CurveSteps + 1];
    private readonly float[] _sampleCum = new float[CurveSteps + 1];
    private readonly List<Vector2> _dashScratch = new(8);

    internal FlatPathsLayer()
    {
        Name = "FlatMapPaths";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
    }

    internal void UpdatePaths(
        List<EdgeVisual> edges,
        Color traveledColor,
        Color untraveledColor,
        Vector2 pointsOffset)
    {
        Position = pointsOffset;
        _edges = edges;
        // Strengthen contrast slightly for thin one-screen dashed lines while staying in the act's palette.
        _traveledColor = new Color(traveledColor.R, traveledColor.G, traveledColor.B, 1f);
        _untraveledColor = new Color(untraveledColor.R, untraveledColor.G, untraveledColor.B, Math.Max(0.84f, untraveledColor.A));
        _ghostedColor = new Color(untraveledColor.R, untraveledColor.G, untraveledColor.B, 0.16f);
        QueueRedraw();
    }

    internal void Clear()
    {
        _edges.Clear();
        Visible = false;
        QueueRedraw();
    }

    private void OnDraw()
    {
        if (!Visible || _edges.Count == 0)
            return;

        // Draw ghosted first, then reachable, then traveled/traveling on top so the player's
        // path always reads cleanly through junctions.
        DrawPass(EdgeVisualState.Ghosted, _ghostedColor, width: 2.0f, dash: 7f, gap: 5.5f);
        DrawPass(EdgeVisualState.Reachable, _untraveledColor, width: 2.8f, dash: 7.5f, gap: 5.5f);
        DrawPass(EdgeVisualState.Traveled, _traveledColor, width: 3.6f, dash: 9f, gap: 5.5f);
        DrawPass(EdgeVisualState.Traveling, _traveledColor, width: 4.0f, dash: 9f, gap: 5.5f);
    }

    private void DrawPass(EdgeVisualState targetState, Color color, float width, float dash, float gap)
    {
        for (int i = 0; i < _edges.Count; i++)
        {
            EdgeVisual e = _edges[i];
            if (e.State != targetState) continue;
            if (e.Curved)
                DrawDashedCubic(e.From, e.Ctrl1, e.Ctrl2, e.To, color, width, dash, gap);
            else
                DrawDashedLine(e.From, e.To, color, width, dash, aligned: true, antialiased: true);
        }
    }

    /// <summary>
    /// Render an arc-length-parameterized dashed cubic Bezier curve with symmetric dash alignment
    /// at both endpoints so every S-curve matches DrawDashedLine's crisp rhythm.
    /// </summary>
    private void DrawDashedCubic(
        Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3,
        Color color, float width, float dashLen, float gapLen)
    {
        _samplePts[0] = p0;
        _sampleCum[0] = 0f;
        for (int i = 1; i <= CurveSteps; i++)
        {
            float t = i / (float)CurveSteps;
            float u = 1f - t;
            float uu = u * u;
            float tt = t * t;
            Vector2 pt = (uu * u) * p0
                       + (3f * uu * t) * c1
                       + (3f * u * tt) * c2
                       + (tt * t) * p3;
            _samplePts[i] = pt;
            _sampleCum[i] = _sampleCum[i - 1] + pt.DistanceTo(_samplePts[i - 1]);
        }

        float totalLen = _sampleCum[CurveSteps];
        if (totalLen < 2f)
            return;

        if (totalLen <= dashLen * 1.15f)
        {
            DrawPolyline(_samplePts, color, width, antialiased: true);
            return;
        }

        int count = Math.Max(2, (int)MathF.Round((totalLen + gapLen) / (dashLen + gapLen)));
        float scale = totalLen / (count * dashLen + (count - 1) * gapLen);
        float effDash = dashLen * scale;
        float effPeriod = (dashLen + gapLen) * scale;

        int seg = 0;
        for (int d = 0; d < count; d++)
        {
            float s0 = d * effPeriod;
            float s1 = Math.Min(totalLen, s0 + effDash);
            if (s1 - s0 < 0.5f) continue;

            while (seg < CurveSteps - 1 && _sampleCum[seg + 1] < s0)
                seg++;

            _dashScratch.Clear();
            _dashScratch.Add(SampleAt(s0, seg));

            int k = seg + 1;
            while (k < CurveSteps && _sampleCum[k] < s1)
            {
                _dashScratch.Add(_samplePts[k]);
                k++;
            }
            int endSeg = Math.Max(seg, k - 1);
            _dashScratch.Add(SampleAt(s1, endSeg));

            if (_dashScratch.Count == 2)
                DrawLine(_dashScratch[0], _dashScratch[1], color, width, antialiased: true);
            else if (_dashScratch.Count > 2)
                DrawPolyline(_dashScratch.ToArray(), color, width, antialiased: true);

            seg = endSeg;
        }
    }

    private Vector2 SampleAt(float s, int seg)
    {
        while (seg < CurveSteps - 1 && _sampleCum[seg + 1] < s)
            seg++;
        float len = _sampleCum[seg + 1] - _sampleCum[seg];
        float f = len > 1e-4f ? Mathf.Clamp((s - _sampleCum[seg]) / len, 0f, 1f) : 0f;
        return _samplePts[seg].Lerp(_samplePts[seg + 1], f);
    }
}

/// <summary>
/// Responsive full-rect chrome overlay on NMapScreen hosting:
///   * Bottom-left toggle stack: "Flat map" (always visible when open), "Compress" &
///     "Hide unreachable nodes" (visible in Flat mode).
///   * Bottom-right act title ("Act N — Name", visible in Flat mode).
/// </summary>
internal sealed partial class FlatChromeOverlay : Control
{
    private const float EdgeMargin = 24f;
    private const float StackGap = 8f;
    private const int TitleFontSize = 22;

    private readonly ToggleSwitch _flatToggle;
    private readonly ToggleSwitch _compressToggle;
    private readonly ToggleSwitch _hideUnreachableToggle;
    private string _actTitle = "";
    private bool _isFlat;
    private bool _onLightBg;
    private float _bottomReservedHeight = EdgeMargin;
    private Tween? _fadeTween;

    internal FlatChromeOverlay(
        Action<bool> onFlatToggled,
        Action<bool> onCompressToggled,
        Action<bool> onHideUnreachableToggled)
    {
        Name = "FlatMapChrome";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        OffsetLeft = 0f;
        OffsetTop = 0f;
        OffsetRight = 0f;
        OffsetBottom = 0f;

        _flatToggle = new ToggleSwitch("Flat map", FlatMapConfig.PreferFlatMap, onFlatToggled)
        {
            Name = "FlatMapToggle",
        };
        _compressToggle = new ToggleSwitch("Compress", FlatMapConfig.CompressMap, onCompressToggled)
        {
            Name = "CompressMapToggle",
        };
        _hideUnreachableToggle = new ToggleSwitch("Hide unreachable nodes", FlatMapConfig.HideUnreachable, onHideUnreachableToggled)
        {
            Name = "HideUnreachableToggle",
        };

        AddChild(_flatToggle);
        AddChild(_compressToggle);
        AddChild(_hideUnreachableToggle);

        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.Resized, Callable.From(LayoutControls));
        Connect(Node.SignalName.Ready, Callable.From(LayoutControls));
    }

    internal float LeftTogglesRightEdge =>
        EdgeMargin + Math.Max(
            _flatToggle.Size.X,
            _isFlat ? Math.Max(_compressToggle.Size.X, _hideUnreachableToggle.Size.X) : 0f);

    internal void SyncState(
        bool isFlat,
        bool compress,
        bool hideUnreachable,
        string actTitle,
        Color actBgColor,
        float bottomReservedHeight)
    {
        _isFlat = isFlat;
        _actTitle = actTitle;
        _onLightBg = isFlat && actBgColor.Luminance > 0.42f;
        _bottomReservedHeight = Math.Max(EdgeMargin, bottomReservedHeight);

        _flatToggle.SetOn(isFlat);
        _compressToggle.SetOn(compress);
        _hideUnreachableToggle.SetOn(hideUnreachable);

        _flatToggle.SetOnLightBackground(_onLightBg);
        _compressToggle.SetOnLightBackground(_onLightBg);
        _hideUnreachableToggle.SetOnLightBackground(_onLightBg);

        _compressToggle.Visible = isFlat;
        _hideUnreachableToggle.Visible = isFlat;

        // Wire vertical focus chain cleanly across whichever toggles are currently visible.
        if (isFlat)
        {
            _flatToggle.FocusNeighborBottom = _flatToggle.GetPathTo(_compressToggle);
            _compressToggle.FocusNeighborTop = _compressToggle.GetPathTo(_flatToggle);
            _compressToggle.FocusNeighborBottom = _compressToggle.GetPathTo(_hideUnreachableToggle);
            _hideUnreachableToggle.FocusNeighborTop = _hideUnreachableToggle.GetPathTo(_compressToggle);
            _hideUnreachableToggle.FocusNeighborBottom = new NodePath();
        }
        else
        {
            _flatToggle.FocusNeighborBottom = new NodePath();
        }

        LayoutControls();
        QueueRedraw();
    }

    internal void ShowImmediate()
    {
        _fadeTween?.Kill();
        _fadeTween = null;
        Modulate = Colors.White;
        Visible = true;
    }

    internal void FadeOut(double duration = 0.15)
    {
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0f, duration);
    }

    internal void LayoutControls()
    {
        Vector2 sz = Size;
        if (sz.Y < 10f)
        {
            if (!IsInsideTree()) return;
            sz = GetViewportRect().Size;
        }

        // Reserve the bottom two rows' heights even in Classic mode so the "Flat map" checkbox
        // stays at the exact same pixel when toggling Flat <-> Classic (never jumps under the cursor).
        // Bottom clearance automatically sits above NMapScreen's Back button when enabled.
        float hideY = sz.Y - _bottomReservedHeight - _hideUnreachableToggle.Size.Y;
        float compressY = hideY - _compressToggle.Size.Y - StackGap;
        float flatY = compressY - _flatToggle.Size.Y - StackGap;

        _hideUnreachableToggle.Position = new Vector2(EdgeMargin, Math.Max(EdgeMargin, hideY));
        _compressToggle.Position = new Vector2(EdgeMargin, Math.Max(EdgeMargin, compressY));
        _flatToggle.Position = new Vector2(EdgeMargin, Math.Max(EdgeMargin, flatY));
    }

    private void OnDraw()
    {
        if (!_isFlat || string.IsNullOrWhiteSpace(_actTitle))
            return;

        GameStyle.EnsureLoaded();
        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        Vector2 sz = Size.X > 10f ? Size : (IsInsideTree() ? GetViewportRect().Size : new Vector2(1920f, 1080f));
        Vector2 textSize = font.GetStringSize(_actTitle, HorizontalAlignment.Left, -1, TitleFontSize);
        var pos = new Vector2(sz.X - EdgeMargin - textSize.X, sz.Y - EdgeMargin - 4f);

        if (_onLightBg)
        {
            DrawString(font, pos, _actTitle, HorizontalAlignment.Left, -1, TitleFontSize, Colors.Black);
        }
        else
        {
            DrawStringOutline(font, pos, _actTitle, HorizontalAlignment.Left, -1, TitleFontSize, 4, new Color(0f, 0f, 0f, 0.85f));
            DrawString(font, pos, _actTitle, HorizontalAlignment.Left, -1, TitleFontSize, Colors.White);
        }
    }
}
