using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace FlatMap;

/// <summary>
/// Owns FlatMap's all-or-nothing Harmony lifecycle. A game update that moves a hook now leaves
/// the unmodified game running and records every missing member in the log.
/// </summary>
internal static class ModRuntime
{
    internal const string HarmonyId = "ernes.flatmap";

    private static Harmony? _harmony;

    internal static bool Enabled { get; private set; }
    // Raised only in PUBLIC builds (strict/dev builds re-throw before reaching it), so the compiler's
    // "never used" warning is a false positive there.
#pragma warning disable CS0067
    internal static event Action? Disabled;
#pragma warning restore CS0067

    internal static bool TryEnable(Assembly assembly)
    {
        IReadOnlyList<string> missing;
        try
        {
            missing = HookCatalog.FindMissing();
        }
        catch (Exception ex)
        {
#if !PUBLIC_BUILD
            throw new InvalidOperationException("[FlatMap] STRICT (dev) build: compatibility preflight threw — fix HookCatalog.", ex);
#else
            LogDisabled($"compatibility preflight failed: {ex}");
            return false;
#endif
        }
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing);
#if !PUBLIC_BUILD
            // Dev build: a missing hook is either a real game change or (as here) a wrong HookCatalog
            // entry — either way we want it LOUD, not a silent revert that reads as "mod does nothing".
            throw new InvalidOperationException($"[FlatMap] STRICT (dev) build: preflight found missing hooks: {list}");
#else
            LogDisabled("incompatible game build; missing hooks: " + list);
            return false;
#endif
        }

        _harmony = new Harmony(HarmonyId);
        try
        {
            _harmony.PatchAll(assembly);
            Enabled = true;
            return true;
        }
        catch (Exception ex)
        {
            Enabled = false;
            try
            {
                // PatchAll is not transactional. Remove any classes it applied before the failure.
                _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception rollback)
            {
                Log.Info($"[FlatMap] ERROR: patch rollback failed; all patch callbacks remain " +
                         $"disabled by their runtime guards: {rollback}");
            }
#if !PUBLIC_BUILD
            // Dev build: a PatchAll failure is our bug (bad patch attribute) — crash loudly.
            throw new InvalidOperationException("[FlatMap] STRICT (dev) build: Harmony setup failed — fix before release.", ex);
#else
            LogDisabled($"Harmony setup failed: {ex}");
            return false;
#endif
        }
    }

    /// <summary>Disable callbacks after an unexpected runtime integration failure.</summary>
    internal static void Disable(string location, Exception ex)
    {
#if !PUBLIC_BUILD
        // Dev/local builds are STRICT: never mask our own bugs. Re-throw (preserving the stack) so an
        // unexpected failure in our patch code crashes loudly. Public builds (PUBLIC_BUILD) fall
        // through to the revert-with-warning below instead. (Compatibility preflight is separate and
        // reverts+warns in both.)
        ExceptionDispatchInfo.Capture(ex).Throw();
#else
        if (!Enabled)
            return;
        Enabled = false;
        Action? handlers = Disabled;
        if (handlers != null)
        {
            foreach (Action cleanup in handlers.GetInvocationList())
            {
                try { cleanup(); }
                catch (Exception cleanupError)
                {
                    Log.Info($"[FlatMap] WARNING: disable cleanup failed: {cleanupError}");
                }
            }
        }
        LogDisabled($"{location} failed: {ex}");
#endif
    }

    private static void LogDisabled(string reason) =>
        Log.Info($"[FlatMap] DISABLED — {reason}. The game will continue with its vanilla UI.");
}

/// <summary>
/// Preflight catalog for every non-public or string-named game member FlatMap relies on.
/// Keep this list synchronized with scripts/verify-hooks.sh.
/// </summary>
internal static class HookCatalog
{
    internal static IReadOnlyList<string> FindMissing()
    {
        var missing = new List<string>();

        Method(typeof(NMapScreen), "_Process", missing);
        Method(typeof(NMapScreen), "Open", missing);
        Method(typeof(NMapScreen), "SetTravelEnabled", missing);
        Method(typeof(NMapScreen), "RecalculateTravelability", missing, Type.EmptyTypes);
        Property(typeof(NMapScreen), "IsTravelEnabled", missing);
        Field(typeof(NMapScreen), "_mapPointDictionary", missing);
        FieldInfo? runStateField = Field(typeof(NMapScreen), "_runState", missing);
        Field(typeof(NMapScreen), "_marker", missing);
        Field(typeof(NMapScreen), "_mapLegend", missing);
        Field(typeof(NMapScreen), "_hasPlayedAnimation", missing);
        Field(typeof(NMapScreen), "_drawingTools", missing);
        Field(typeof(NMapScreen), "_drawingInput", missing);
        Field(typeof(NMapScreen), "_mapBgContainer", missing);
        Method(typeof(NMapScreen), "UpdateDrawingButtonStates", missing);
        Method(typeof(NMapScreen), "OnMapDrawingButtonPressed", missing);
        Method(typeof(NMapScreen), "OnMapErasingButtonPressed", missing);
        Method(typeof(NMapScreen), "OnClearMapDrawingButtonPressed", missing);
        Method(typeof(NMapScreen), "ProcessMouseDrawingEvent", missing);
        Property(typeof(NMapScreen), "Drawings", missing);

        Field(typeof(NNormalMapPoint), "_icon", missing);
        Field(typeof(NNormalMapPoint), "_outline", missing);
        Field(typeof(NNormalMapPoint), "_questIcon", missing);
        Field(typeof(NAncientMapPoint), "_icon", missing);
        Field(typeof(NAncientMapPoint), "_outline", missing);
        Field(typeof(NBossMapPoint), "_placeholderImage", missing);
        Property(typeof(NMapPoint), "Point", missing);
        Property(typeof(NMapPoint), "State", missing);
        Field(typeof(MapPoint), "coord", missing);
        Property(typeof(MapPoint), "PointType", missing);
        Property(typeof(MapPoint), "Children", missing);
        Property(typeof(MapPoint), "Quests", missing);

        if (runStateField != null)
        {
            Type runState = runStateField.FieldType;
            Property(runState, "CurrentMapCoord", missing);
            Property(runState, "CurrentActIndex", missing);
            Property(runState, "ActFloor", missing);
            PropertyInfo? act = Property(runState, "Act", missing);
            Property(runState, "MapPointHistory", missing);
            PropertyInfo? extra = Property(runState, "ExtraFields", missing);
            if (extra != null)
                Property(extra.PropertyType, "StartedWithNeow", missing);
            PropertyInfo? runMap = Property(runState, "Map", missing);
            if (runMap != null)
                Property(runMap.PropertyType, "SecondBossMapPoint", missing);
            if (act != null)
            {
                PropertyInfo? title = Property(act.PropertyType, "Title", missing);
                if (title != null)
                    Method(title.PropertyType, "GetFormattedText", missing, Type.EmptyTypes);
                Property(act.PropertyType, "MapBgColor", missing);
                Property(act.PropertyType, "MapTraveledColor", missing);
                Property(act.PropertyType, "MapUntraveledColor", missing);
                PropertyInfo? bossEnc = Property(act.PropertyType, "BossEncounter", missing);
                Property(act.PropertyType, "SecondBossEncounter", missing);
                if (bossEnc != null)
                    Property(bossEnc.PropertyType, "BossNodePath", missing);
            }
        }

        // The method's first parameter is the base InputEvent (our patch reads __args[0] and casts to
        // InputEventKey). So require the first param to be a type that an InputEventKey fits into
        // (InputEvent or InputEventKey) — NOT that it's exactly InputEventKey (it isn't).
        MethodInfo? shortcut = Method(typeof(NInputManager), "ProcessHotkeyInput", missing);
        if (shortcut != null &&
            (shortcut.GetParameters().Length == 0 ||
             !shortcut.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(InputEventKey))))
            missing.Add("NInputManager.ProcessHotkeyInput(first arg must accept an InputEventKey)");
        Property(typeof(NControllerManager), "IsUsingDirectionalNavigation", missing);

        return missing;
    }

    private static FieldInfo? Field(Type type, string name, List<string> missing)
    {
        FieldInfo? value = AccessTools.Field(type, name);
        if (value == null) missing.Add($"{type.Name}.{name}");
        return value;
    }

    private static MethodInfo? Method(Type type, string name, List<string> missing, Type[]? args = null)
    {
        MethodInfo? value = args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args);
        if (value == null) missing.Add($"{type.Name}.{name}()");
        return value;
    }

    private static PropertyInfo? Property(Type type, string name, List<string> missing)
    {
        PropertyInfo? value = AccessTools.Property(type, name);
        if (value == null) missing.Add($"{type.Name}.{name}");
        return value;
    }

}

// Reflection calls remain fail-fast internally, but ModRuntime preflights all static lookups before
// applying any patch and every patch boundary catches unexpected runtime failures.
internal static class Reflect
{
    internal static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

    internal static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    internal static MethodInfo Method(Type type, string name, Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);

    internal static MethodInfo PropertyGetter(Type type, string name) =>
        AccessTools.PropertyGetter(type, name)
        ?? throw new MissingMethodException(type.FullName, $"get_{name}");
}

internal static class Dbg
{
    private static readonly HashSet<string> Seen = new();

    internal static void Once(string key, string message)
    {
        if (Seen.Add(key)) Log.Info($"[FlatMap] {message}");
    }

    internal static void Rearm() => Seen.Clear();
}
