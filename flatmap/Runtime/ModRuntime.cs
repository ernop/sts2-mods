using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace FlatMap;

/// <summary>
/// Owns FlatMap's all-or-nothing Harmony lifecycle. A game update that moves a hook leaves
/// the unmodified game running and records every missing member in the log. Independent copy
/// from DeckView — the two mods share no assembly.
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
        var missing = new List<string>();
        try
        {
            missing.AddRange(HookCatalog.FindMissing());
            missing.AddRange(CompiledReferences.FindUnresolved(assembly));
        }
        catch (Exception ex)
        {
#if !PUBLIC_BUILD
            Log.Error($"[FlatMap] STRICT (dev) build: compatibility preflight threw: {ex}");
            throw new InvalidOperationException("[FlatMap] STRICT (dev) build: compatibility preflight threw — fix HookCatalog or CompiledReferences.", ex);
#else
            LogDisabled($"compatibility preflight failed: {ex}");
            return false;
#endif
        }
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing);
#if !PUBLIC_BUILD
            Log.Error($"[FlatMap] STRICT (dev) build: preflight found missing hooks: {list}");
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
                _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception rollback)
            {
                Log.Info($"[FlatMap] ERROR: patch rollback failed; all patch callbacks remain " +
                         $"disabled by their runtime guards: {rollback}");
            }
#if !PUBLIC_BUILD
            Log.Error($"[FlatMap] STRICT (dev) build: Harmony setup failed: {ex}");
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
        Log.Error($"[FlatMap] STRICT (dev) build failure in {location}: {ex}");
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
/// Keep this list synchronized with scripts/verify-hooks.sh (the FlatMap section).
/// </summary>
internal static class HookCatalog
{
    internal static IReadOnlyList<string> FindMissing()
    {
        var missing = new List<string>();

        Method(typeof(NMapScreen), "_Ready", missing);
        Method(typeof(NMapScreen), "SetMap", missing);
        Method(typeof(NMapScreen), "Open", missing);
        Method(typeof(NMapScreen), "Close", missing);
        Method(typeof(NMapScreen), "PlayStartOfActAnimation", missing);
        Method(typeof(NMapScreen), "InitMapPrompt", missing);
        Method(typeof(NMapScreen), "CanScroll", missing);
        Method(typeof(NMapScreen), "UpdateScrollPosition", missing);
        Method(typeof(NMapScreen), "_Process", missing);
        Method(typeof(NMapScreen), "_Input", missing);
        Method(typeof(NMapScreen), "RecalculateTravelability", missing);
        Method(typeof(NMapScreen), "RefreshAllPointVisuals", missing);
        Method(typeof(NMapScreen), "TravelToMapCoord", missing);
        Method(typeof(NMapScreen), "GetNetPositionFromScreenPosition", missing);
        Method(typeof(NMapScreen), "GetScreenPositionFromNetPosition", missing);
        Property(typeof(NMapScreen), "IsOpen", missing);
        Property(typeof(NMapScreen), "IsTravelEnabled", missing);
        Property(typeof(NMapScreen), "Drawings", missing);
        Field(typeof(NMapScreen), "_mapContainer", missing);
        Field(typeof(NMapScreen), "_mapBgContainer", missing);
        Field(typeof(NMapScreen), "_pathsContainer", missing);
        Field(typeof(NMapScreen), "_points", missing);
        Field(typeof(NMapScreen), "_marker", missing);
        Field(typeof(NMapScreen), "_mapLegend", missing);
        Field(typeof(NMapScreen), "_drawingTools", missing);
        Field(typeof(NMapScreen), "_backButton", missing);
        Field(typeof(NMapScreen), "_backstop", missing);
        Field(typeof(NMapScreen), "_shareButton", missing);
        Field(typeof(NMapScreen), "_mapPointDictionary", missing);
        Field(typeof(NMapScreen), "_startingPointNode", missing);
        Field(typeof(NMapScreen), "_bossPointNode", missing);
        Field(typeof(NMapScreen), "_secondBossPointNode", missing);
        Field(typeof(NMapScreen), "_map", missing);
        Field(typeof(NMapScreen), "_runState", missing);
        Field(typeof(NMapScreen), "_hasPlayedAnimation", missing);
        Field(typeof(NMapScreen), "_targetDragPos", missing);
        Field(typeof(NMapScreen), "_actAnimTween", missing);

        Method(typeof(NMapBg), "OnViewportChange", missing);

        Property(typeof(NMapPoint), "Point", missing);
        Property(typeof(NMapPoint), "State", missing);
        Property(typeof(NMapPoint), "IsTravelable", missing);
        Property(typeof(NMapPoint), "HoverScale", missing);
        Property(typeof(NMapPoint), "DownScale", missing);
        Method(typeof(NMapPoint), "IsInputAllowed", missing);
        Field(typeof(NMapPoint), "_runState", missing);
        Property(typeof(NClickableControl), "IsFocused", missing);

        Method(typeof(NNormalMapPoint), "RefreshColorInstantly", missing);
        Method(typeof(NNormalMapPoint), "AnimHover", missing);
        Method(typeof(NNormalMapPoint), "AnimUnhover", missing);
        Method(typeof(NNormalMapPoint), "AnimPressDown", missing);
        Method(typeof(NNormalMapPoint), "OnSelected", missing);
        Field(typeof(NNormalMapPoint), "_iconContainer", missing);
        Field(typeof(NNormalMapPoint), "_icon", missing);
        Field(typeof(NNormalMapPoint), "_outline", missing);
        Field(typeof(NNormalMapPoint), "_questIcon", missing);
        Field(typeof(NNormalMapPoint), "_tween", missing);

        Method(typeof(NAncientMapPoint), "_Process", missing);
        Method(typeof(NAncientMapPoint), "RefreshColorInstantly", missing);
        Method(typeof(NAncientMapPoint), "AnimHover", missing);
        Method(typeof(NAncientMapPoint), "AnimUnhover", missing);
        Method(typeof(NAncientMapPoint), "AnimPressDown", missing);
        Method(typeof(NAncientMapPoint), "OnSelected", missing);
        Field(typeof(NAncientMapPoint), "_icon", missing);
        Field(typeof(NAncientMapPoint), "_outline", missing);
        Field(typeof(NAncientMapPoint), "_tween", missing);
        Field(typeof(NAncientMapPoint), "_elapsedTime", missing);

        Method(typeof(NMapMarker), "SetMapPoint", missing);
        Field(typeof(NMapMarker), "_tween", missing);

        Method(typeof(NMapShareButton), "OnRelease", missing);
        Method(typeof(NMapShareButton), "IsTakingScreenshot", missing);

        Field(typeof(MapPoint), "coord", missing);
        Property(typeof(MapPoint), "PointType", missing);
        Property(typeof(MapPoint), "Children", missing);
        Property(typeof(MapPoint), "Quests", missing);

        Property(typeof(RunState), "CurrentMapCoord", missing);
        Property(typeof(RunState), "CurrentActIndex", missing);
        Property(typeof(RunState), "ActFloor", missing);
        Property(typeof(RunState), "Act", missing);
        Property(typeof(RunState), "MapPointHistory", missing);
        Property(typeof(RunState), "VisitedMapCoords", missing);

        Property(typeof(ActModel), "Title", missing);
        Property(typeof(ActModel), "MapBgColor", missing);
        Property(typeof(ActModel), "MapTraveledColor", missing);
        Property(typeof(ActModel), "MapUntraveledColor", missing);
        Method(typeof(LocString), "GetFormattedText", missing, Type.EmptyTypes);

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

/// <summary>
/// Preflight for the game members FlatMap's compiled code names directly.
/// </summary>
internal static class CompiledReferences
{
    private static readonly OpCode[] OneByteOpCodes = new OpCode[256];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[256];

    static CompiledReferences()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opCode = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)opCode.Value);
            if (opCode.Size == 1)
                OneByteOpCodes[value] = opCode;
            else
                TwoByteOpCodes[value & 0xFF] = opCode;
        }
    }

    internal static IReadOnlyList<string> FindUnresolved(Assembly assembly)
    {
        var unresolved = new List<string>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (Exception? loaderError in ex.LoaderExceptions)
                if (loaderError != null) unresolved.Add(loaderError.Message);
            return unresolved;
        }

        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                      | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in types)
        {
            Type[]? typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
            foreach (MethodBase method in type.GetMethods(declared).Concat<MethodBase>(type.GetConstructors(declared)))
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                    continue;
                Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                for (int i = 0; i < il.Length;)
                {
                    OpCode op = il[i] == 0xFE ? TwoByteOpCodes[il[i + 1]] : OneByteOpCodes[il[i]];
                    if (op.Size == 0)
                        throw new InvalidOperationException($"unknown IL opcode 0x{il[i]:X2} at {i} in {type.Name}.{method.Name}");
                    i += op.Size;
                    switch (op.OperandType)
                    {
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                            try
                            {
                                method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                            }
                            catch (Exception ex) when (ex is MissingMemberException or TypeLoadException)
                            {
                                unresolved.Add($"{type.Name}.{method.Name}: {ex.Message}");
                            }
                            i += 4;
                            break;
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            i += 1;
                            break;
                        case OperandType.InlineVar:
                            i += 2;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            i += 8;
                            break;
                        case OperandType.InlineSwitch:
                            i += 4 + 4 * BitConverter.ToInt32(il, i);
                            break;
                        default:
                            i += 4;
                            break;
                    }
                }
            }
        }
        return unresolved;
    }
}

internal static class Reflect
{
    internal static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

    internal static MethodInfo Method(Type type, string name, Type[]? args = null) =>
        (args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args))
        ?? throw new MissingMethodException(type.FullName, name);

    internal static MethodInfo PropertyGetter(Type type, string name) =>
        AccessTools.PropertyGetter(type, name) ?? throw new MissingMemberException(type.FullName, name);
}
