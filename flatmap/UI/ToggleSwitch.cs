using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;

namespace FlatMap;

/// <summary>Shared game-native visual metrics for FlatMap controls.</summary>
internal static class GameStyle
{
    internal static Font? Font { get; private set; }
    internal static Texture2D? Ticked { get; private set; }
    internal static Texture2D? Unticked { get; private set; }
    internal static int ToggleFontSize { get; private set; } = 20;
    internal static float ToggleBoxSize { get; private set; } = 26f;

    private static readonly List<ToggleSwitch> Toggles = new();
    private static bool _loaded;

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
        Log.Info($"[FlatMap] game style: font={Font != null}, ticked={Ticked != null}, unticked={Unticked != null}");
    }

    internal static void Register(ToggleSwitch toggle)
    {
        Toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        Toggles.Add(toggle);
    }

    internal static void Unregister(ToggleSwitch toggle) => Toggles.Remove(toggle);
}

/// <summary>
/// A code-built checkbox using STS2's own art and font. Focusable and responds to
/// MegaInput.confirm / MegaInput.select so mouse, keyboard, and controller share one path.
/// Automatically chooses pure black on light parchment backgrounds and pure white (with dark
/// outline) on dark backgrounds so neutral text always has maximum contrast.
/// </summary>
internal sealed partial class ToggleSwitch : Control
{
    private bool _on;
    private bool _onLightBg;
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

    internal void SetOnLightBackground(bool onLightBg)
    {
        if (_onLightBg == onLightBg) return;
        _onLightBg = onLightBg;
        QueueRedraw();
    }

    internal void RefreshMetrics()
    {
        _fontSize = GameStyle.ToggleFontSize;
        _box = GameStyle.ToggleBoxSize;
        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        float textWidth = font.GetStringSize(_label, HorizontalAlignment.Left, -1, _fontSize).X;
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
        if (_onLightBg)
        {
            DrawString(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, Colors.Black);
        }
        else
        {
            DrawStringOutline(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, 4, new Color(0f, 0f, 0f, 0.85f));
            DrawString(font, textPos, _label, HorizontalAlignment.Left, -1, _fontSize, Colors.White);
        }

        if (HasFocus())
        {
            Color ring = _onLightBg ? new Color(0.12f, 0.12f, 0.12f, 0.95f) : new Color(1f, 0.88f, 0.45f, 0.95f);
            DrawRect(new Rect2(1f, 1f, Size.X - 2f, Size.Y - 2f), ring, false, 2f);
        }
    }
}
