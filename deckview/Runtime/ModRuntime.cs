using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace DeckView;

/// <summary>
/// Owns DeckView' all-or-nothing Harmony lifecycle. A game update that moves a hook leaves
/// the unmodified game running and records every missing member in the log. Same policy as the
/// deckview (map) mod, but a fully independent copy — the two mods share no assembly.
/// </summary>
internal static class ModRuntime
{
    internal const string HarmonyId = "ernes.deckview";

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
            throw new InvalidOperationException("[DeckView] STRICT (dev) build: compatibility preflight threw — fix HookCatalog or CompiledReferences.", ex);
#else
            LogDisabled($"compatibility preflight failed: {ex}");
            return false;
#endif
        }
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing);
#if !PUBLIC_BUILD
            // Dev build: a missing hook is either a real game change or a wrong HookCatalog entry —
            // either way we want it LOUD, not a silent revert that reads as "mod does nothing".
            throw new InvalidOperationException($"[DeckView] STRICT (dev) build: preflight found missing hooks: {list}");
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
                Log.Info($"[DeckView] ERROR: patch rollback failed; all patch callbacks remain " +
                         $"disabled by their runtime guards: {rollback}");
            }
#if !PUBLIC_BUILD
            // Dev build: a PatchAll failure is our bug (bad patch attribute) — crash loudly.
            throw new InvalidOperationException("[DeckView] STRICT (dev) build: Harmony setup failed — fix before release.", ex);
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
                    Log.Info($"[DeckView] WARNING: disable cleanup failed: {cleanupError}");
                }
            }
        }
        LogDisabled($"{location} failed: {ex}");
#endif
    }

    private static void LogDisabled(string reason) =>
        Log.Info($"[DeckView] DISABLED — {reason}. The game will continue with its vanilla UI.");
}

/// <summary>
/// Preflight catalog for every non-public or string-named game member DeckView relies on.
/// Keep this list synchronized with scripts/verify-hooks.sh (the "mini-cards" section).
/// </summary>
internal static class HookCatalog
{
    internal static IReadOnlyList<string> FindMissing()
    {
        var missing = new List<string>();

        Method(typeof(NCardGrid), "ConnectSignals", missing);
        Method(typeof(NCardGrid), "_ExitTree", missing);
        Method(typeof(NCardGrid), "_Process", missing);
        Property(typeof(NCardGrid), "CardPadding", missing);
        Property(typeof(NCardGrid), "CurrentlyDisplayedCardHolders", missing);
        Field(typeof(NCardGrid), "_cardSize", missing);
        Field(typeof(NCardGrid), "_needsReinit", missing);

        Property(typeof(NCardHolder), "SmallScale", missing);
        Property(typeof(NCardHolder), "Hitbox", missing);
        Method(typeof(NCardHolder), "RefreshFocusState", missing);
        Field(typeof(NCardHolder), "_isHovered", missing);
        Field(typeof(NCardHolder), "_isFocused", missing);
        Field(typeof(NCardHolder), "_hoverTween", missing);
        Method(typeof(NGridCardHolder), "Create", missing);
        if (AccessTools.DeclaredPropertyGetter(typeof(NGridCardHolder), "SmallScale") != null)
            missing.Add("NGridCardHolder must inherit SmallScale without overriding it");
        Field(typeof(NClickableControl), "_isHovered", missing);

        Method(typeof(NCardsViewScreen), "ConnectSignals", missing);
        Field(typeof(NCardsViewScreen), "_showUpgrades", missing);

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

/// <summary>
/// Preflight for the game members DeckView's compiled code names directly (public calls, fields,
/// types). .NET binds those only when the calling method first runs (forcing JIT compilation does
/// not bind them), so a renamed or re-signatured member would otherwise fail mid-run; resolving
/// every member token in our IL against the loaded game reports it here instead.
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
                        default: // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
                            i += 4;
                            break;
                    }
                }
            }
        }
        return unresolved;
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
}

internal static class Dbg
{
    private static readonly HashSet<string> Seen = new();

    internal static void Once(string key, string message)
    {
        if (Seen.Add(key)) Log.Info($"[DeckView] {message}");
    }

    internal static void Rearm() => Seen.Clear();
}
