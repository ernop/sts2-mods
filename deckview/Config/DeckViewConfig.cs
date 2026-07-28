using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace DeckView;

internal static class DeckViewConfig
{
    // The [deck] section of deckview.cfg is where the combined mod always kept this setting, so
    // reclaiming the DeckView identity also reclaims the historical config location. Save()
    // preserves foreign sections (legacy [map] keys, which the FlatMap mod migrated away from).
    private const string Path = "user://deckview.cfg";

    private static bool _loaded;
    private static bool _canSave = true;
    private static bool _miniDeck = true;

    internal static bool MiniDeck
    {
        get { EnsureLoaded(); return _miniDeck; }
        set
        {
            EnsureLoaded();
            if (_miniDeck == value) return;
            _miniDeck = value;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var cfg = new ConfigFile();
        Error result = cfg.Load(Path);
        if (result == Error.FileNotFound)
            return;
        if (result != Error.Ok)
        {
            _canSave = false;
            Log.Info($"[DeckView] WARNING: could not read preferences ({result}); " +
                     "using defaults without overwriting the file");
            return;
        }

        _miniDeck = cfg.GetValue("deck", "mini", true).AsBool();
    }

    private static void Save()
    {
        if (!_canSave)
            return;
        var cfg = new ConfigFile();
        Error loadResult = cfg.Load(Path);
        if (loadResult != Error.Ok && loadResult != Error.FileNotFound)
        {
            _canSave = false;
            Log.Info($"[DeckView] WARNING: could not preserve preferences ({loadResult}); save skipped");
            return;
        }
        cfg.SetValue("deck", "mini", _miniDeck);
        Error result = cfg.Save(Path);
        if (result != Error.Ok)
            Log.Info($"[DeckView] WARNING: could not save preferences ({result})");
    }
}
