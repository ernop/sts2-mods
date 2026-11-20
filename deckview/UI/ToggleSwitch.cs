using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;

namespace DeckView;

/// <summary>Shared game-native visual metrics for DeckView controls.</summary>
internal static class GameStyle
{
    internal static readonly Color TextColor = Colors.White;
    internal static readonly Color TextOutlineColor = new(0f, 0f, 0f, 0.72f);
    internal static Font? Font { get; private set; }
    internal static Texture2D? Ticked { get; private set; }
    internal static Texture2D? Unticked { get; private set; }
    internal static int ToggleFontSize { get; private set; } = 21;
    internal static float ToggleBoxSize { get; private set; } = 38.5f;
    internal static float ToggleBoxGap { get; private set; } = 5f;

    private static readonly List<ToggleSwitch> Toggles = new();
    private static bool _loaded;

    // One static subscription hides every live toggle on disable; per-instance subscriptions
    // would outlive freed Godot objects and throw on disposed wrappers during cleanup.
    static GameStyle() => ModRuntime.Disabled += HideAllToggles;

    private static void HideAllToggles()
    {
        Toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch toggle in Toggles)
            toggle.OnModDisabled();
    }

    internal static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Font = GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres")
            ?? GD.Load<Font>("res://themes/kreon_bold_shared.tres")
            ?? GD.Load<Font>("res://themes/kreon_regular_shared.tres");
        Ticked = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres");
        Unticked = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres");
        Log.Info($"[DeckView] game style: font={Font != null}, ticked={Ticked != null}, unticked={Unticked != null}");
    }

    internal static void Register(ToggleSwitch toggle)
    {
        Toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        Toggles.Add(toggle);
    }

    internal static void Unregister(ToggleSwitch toggle) => Toggles.Remove(toggle);

    /// <summary>
    /// Measure the live vanilla "View upgrades" control in host-local coordinates so DeckView's
    /// checkbox and label match vanilla STS2 tickboxes pixel-for-pixel.
    /// </summary>
    internal static void ConfigureToggleMetrics(Control? host, Control? checkboxVisuals, Control? label)
    {
        EnsureLoaded();

        Vector2 hostScale = Vector2.One;
        if (host != null && GodotObject.IsInstanceValid(host) && host.IsInsideTree())
        {
            Vector2 s = host.GetGlobalTransformWithCanvas().Scale;
            hostScale = new Vector2(Mathf.Max(0.01f, Mathf.Abs(s.X)), Mathf.Max(0.01f, Mathf.Abs(s.Y)));
        }

        // 1. Measure the actual vanilla checkbox glyph first (never crush it to a fallback font cap).
        float measuredBox = 0f;
        float glyphRightLocal = float.NaN;
        Transform2D invHost = (host != null && GodotObject.IsInstanceValid(host) && host.IsInsideTree())
            ? host.GetGlobalTransformWithCanvas().AffineInverse()
            : Transform2D.Identity;
        Control? glyph = checkboxVisuals != null && GodotObject.IsInstanceValid(checkboxVisuals)
            ? (checkboxVisuals.GetNodeOrNull<Control>("Ticked")
               ?? checkboxVisuals.GetNodeOrNull<Control>("NotTicked")
               ?? checkboxVisuals)
            : null;
        if (glyph != null && GodotObject.IsInstanceValid(glyph) && glyph.IsInsideTree())
        {
            Vector2 gScale = glyph.GetGlobalTransformWithCanvas().Scale;
            float relX = Mathf.Abs(gScale.X) / hostScale.X;
            float relY = Mathf.Abs(gScale.Y) / hostScale.Y;
            measuredBox = Mathf.Max(glyph.Size.X * relX, glyph.Size.Y * relY);
            if (measuredBox > 18f)
                glyphRightLocal = (invHost * glyph.GlobalPosition).X + glyph.Size.X * relX;
        }

        // 2. Measure the live vanilla label font, size, and box-to-label gap.
        int measuredFont = 0;
        if (label != null && GodotObject.IsInstanceValid(label) && label.IsInsideTree())
        {
            Font? liveFont = label is RichTextLabel
                ? label.GetThemeFont("normal_font")
                : label.GetThemeFont("font");
            if (liveFont != null)
                Font = liveFont;

            int themed = label is RichTextLabel
                ? label.GetThemeFontSize("normal_font_size")
                : label.GetThemeFontSize("font_size");
            if (themed <= 0)
                themed = label.GetThemeFontSize("font_size");
            float relScaleY = Mathf.Abs(label.GetGlobalTransformWithCanvas().Scale.Y) / hostScale.Y;
            measuredFont = Mathf.RoundToInt(themed * Mathf.Clamp(relScaleY, 0.25f, 2f));

            if (!float.IsNaN(glyphRightLocal))
            {
                float labelLeftLocal = (invHost * label.GlobalPosition).X;
                float gap = labelLeftLocal - glyphRightLocal;
                if (gap >= 1f && gap <= 24f)
                    ToggleBoxGap = Mathf.Clamp(gap, 2f, 14f);
            }
        }

        if (measuredBox > 18f)
            ToggleBoxSize = Mathf.Clamp(measuredBox, 24f, 52f);

        if (measuredFont > 16)
            ToggleFontSize = Mathf.Clamp(measuredFont, 18, 36);
        else if (measuredBox > 18f)
            ToggleFontSize = Mathf.Clamp(Mathf.RoundToInt(ToggleBoxSize * 0.72f), 18, 36);

        if (measuredBox <= 18f)
            ToggleBoxSize = Mathf.Clamp(ToggleFontSize * 1.375f, 24f, 52f);

        Toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch toggle in Toggles)
            toggle.RefreshMetrics();
    }
}

/// <summary>
/// A code-built checkbox using STS2's own art and font. It is focusable and responds to
/// MegaInput.confirm / MegaInput.select, so mouse, keyboard, and controller share one path.
/// </summary>
internal sealed partial class ToggleSwitch : Control
{
    private bool _on;
    private readonly string _label;
    private readonly Action<bool> _onToggled;
    private int _fontSize;
    private float _box;
    private float _gap;
    internal const float Pad = 3f;
    private const float TrackW = 46f;

    internal ToggleSwitch(string label, bool on, Action<bool> onToggled)
    {
        _label = label;
        _on = on;
        _onToggled = onToggled;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        GameStyle.EnsureLoaded();
        GameStyle.Register(this);
        RefreshMetrics();

        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
        Connect(Control.SignalName.FocusEntered, Callable.From(QueueRedraw));
        Connect(Control.SignalName.FocusExited, Callable.From(QueueRedraw));
        Connect(Node.SignalName.TreeExiting, Callable.From(() => GameStyle.Unregister(this)));
    }

    internal void RefreshMetrics()
    {
        _fontSize = GameStyle.ToggleFontSize;
        _box = GameStyle.ToggleBoxSize;
        _gap = GameStyle.ToggleBoxGap;
        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        float textWidth = font.GetStringSize(
            _label, HorizontalAlignment.Left, -1, _fontSize).X;
        float glyphWidth = (GameStyle.Ticked != null || GameStyle.Unticked != null) ? _box : TrackW;
        float height = Mathf.Max(_box, _fontSize + 8f) + Pad * 2f;
        Size = new Vector2(Mathf.Ceil(Pad * 2f + glyphWidth + _gap + textWidth + 6f), Mathf.Ceil(height));
        CustomMinimumSize = Size;
        QueueRedraw();
    }

    internal void SetOn(bool on)
    {
        if (_on == on) return;
        _on = on;
        QueueRedraw();
    }

    private void Toggle()
    {
        if (!ModRuntime.Enabled)
            return;
        _on = !_on;
        QueueRedraw();
        try
        {
            _onToggled(_on);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable($"{nameof(ToggleSwitch)} '{_label}'", ex);
        }
    }

    private void OnGuiInput(InputEvent e)
    {
        if (!ModRuntime.Enabled)
            return;
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            GrabFocus();
            Toggle();
            AcceptEvent();
            return;
        }

        if (e.IsActionPressed(MegaInput.confirm) || e.IsActionPressed(MegaInput.select))
        {
            Toggle();
            AcceptEvent();
        }
    }

    internal void OnModDisabled()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        Visible = false;
    }

    private void OnDraw()
    {
        GameStyle.EnsureLoaded();
        float cy = Size.Y * 0.5f;
        float labelX;

        Texture2D? tex = _on ? GameStyle.Ticked : GameStyle.Unticked;
        if (tex != null)
        {
            DrawTextureRect(tex, new Rect2(Pad, cy - _box * 0.5f, _box, _box), false);
            labelX = Pad + _box + _gap;
        }
        else
        {
            const float tr = 9f;
            float left = Pad + tr + 1f, right = Pad + TrackW - tr - 1f;
            Color track = _on ? new Color(0.95f, 0.80f, 0.35f) : new Color(0.24f, 0.26f, 0.30f);
            DrawCircle(new Vector2(left, cy), tr, track);
            DrawCircle(new Vector2(right, cy), tr, track);
            DrawRect(new Rect2(left, cy - tr, right - left, tr * 2f), track);
            DrawCircle(new Vector2(_on ? right : left, cy), tr - 2f, new Color(0.97f, 0.98f, 1f));
            labelX = Pad + TrackW + 8f;
        }

        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        var textPos = new Vector2(labelX, cy + _fontSize * 0.35f);
        DrawStringOutline(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, 6, GameStyle.TextOutlineColor);
        DrawString(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, GameStyle.TextColor);

        if (HasFocus())
            DrawRect(new Rect2(1f, 1f, Size.X - 2f, Size.Y - 2f),
                new Color(1f, 0.88f, 0.45f, 0.95f), false, 2f);
    }
}
