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
    internal static readonly Color TextOutlineColor = new(0f, 0f, 0f, 0.85f);
    internal static Font? Font { get; private set; }
    internal static Texture2D? Ticked { get; private set; }
    internal static Texture2D? Unticked { get; private set; }
    internal static int ToggleFontSize { get; private set; } = 20;
    internal static float ToggleBoxSize { get; private set; } = 26f;

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
        Font = GD.Load<Font>("res://themes/kreon_regular_shared.tres");
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
    /// Measure the live vanilla "View upgrades" control. All DeckView toggles then use the same
    /// effective font size and a proportional checkbox size (matching vanilla's ~24-28px box
    /// rather than %TickboxVisuals' outer container rect).
    /// </summary>
    internal static void ConfigureToggleMetrics(Control? checkboxVisuals, Control? label)
    {
        EnsureLoaded();

        if (label != null && GodotObject.IsInstanceValid(label) && label.IsInsideTree())
        {
            int themed = label.GetThemeFontSize("font_size");
            float scale = Mathf.Abs(label.GetGlobalTransformWithCanvas().Scale.Y);
            int effective = Mathf.RoundToInt(themed * Mathf.Clamp(scale, 0.2f, 2f));
            if (effective > 0)
                ToggleFontSize = Mathf.Clamp(effective, 16, 26);
        }

        float targetBox = ToggleFontSize * 1.3f;
        Control? glyph = checkboxVisuals != null && GodotObject.IsInstanceValid(checkboxVisuals)
            ? (checkboxVisuals.GetNodeOrNull<Control>("Ticked") ?? checkboxVisuals)
            : null;
        if (glyph != null && GodotObject.IsInstanceValid(glyph) && glyph.IsInsideTree())
        {
            Vector2 scale = glyph.GetGlobalTransformWithCanvas().Scale;
            float measured = Mathf.Max(
                glyph.Size.X * Mathf.Abs(scale.X),
                glyph.Size.Y * Mathf.Abs(scale.Y));
            if (measured > 0f)
                targetBox = measured;
        }
        // Keep the box proportional to the label font so outer tickbox padding never bloats it.
        ToggleBoxSize = Mathf.Clamp(targetBox, ToggleFontSize * 1.15f, ToggleFontSize * 1.45f);

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
    private const float Pad = 3f;
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
        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        float textWidth = font.GetStringSize(
            _label, HorizontalAlignment.Left, -1, _fontSize).X;
        float glyphWidth = (GameStyle.Ticked != null || GameStyle.Unticked != null) ? _box : TrackW;
        float height = Mathf.Max(_box, _fontSize + 8f) + Pad * 2f;
        Size = new Vector2(Mathf.Ceil(Pad * 2f + glyphWidth + 8f + textWidth + 4f), Mathf.Ceil(height));
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
            labelX = Pad + _box + 8f;
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
        DrawStringOutline(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, 4, GameStyle.TextOutlineColor);
        DrawString(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, GameStyle.TextColor);

        if (HasFocus())
            DrawRect(new Rect2(1f, 1f, Size.X - 2f, Size.Y - 2f),
                new Color(1f, 0.88f, 0.45f, 0.95f), false, 2f);
    }
}
