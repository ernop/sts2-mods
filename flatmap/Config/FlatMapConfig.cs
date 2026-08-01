using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace FlatMap;

internal static class FlatMapConfig
{
    private const string Path = "user://flatmap.cfg";
    // Pre-flip, the combined DeckView mod stored the map settings here; read once as the seed for
    // a fresh flatmap.cfg. Only [map] flat migrates — compress deliberately restarts at the new
    // OFF default. Never written.
    private const string LegacyPath = "user://deckview.cfg";

    private static bool _loaded;
    private static bool _canSave = true;
    private static bool _preferFlatMap;
    // Compress defaults OFF (2026-07-28): with the vertical one-screen layout, the raw game
    // columns are the most vanilla-faithful view. The choice persists for the user's lifetime.
    private static bool _compressMap;
    private static bool _dumpMapGraph;

    internal static bool PreferFlatMap
    {
        get { EnsureLoaded(); return _preferFlatMap; }
        set
        {
            EnsureLoaded();
            if (_preferFlatMap == value) return;
            _preferFlatMap = value;
            Save();
        }
    }

    internal static bool CompressMap
    {
        get { EnsureLoaded(); return _compressMap; }
        set
        {
            EnsureLoaded();
            if (_compressMap == value) return;
            _compressMap = value;
            Save();
        }
    }

    // Developer-only opt-in. New and upgraded users default to no graph dumps.
    internal static bool DumpMapGraph
    {
        get { EnsureLoaded(); return _dumpMapGraph; }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var cfg = new ConfigFile();
        Error result = cfg.Load(Path);
        if (result == Error.FileNotFound)
        {
            // First run under the new identity: adopt the legacy flat-map preference if present.
            var legacy = new ConfigFile();
            if (legacy.Load(LegacyPath) == Error.Ok)
            {
                _preferFlatMap = legacy.GetValue("map", "flat", false).AsBool();
                _dumpMapGraph = legacy.GetValue("debug", "dump_map_graph", false).AsBool();
            }
            return;
        }
        if (result != Error.Ok)
        {
            _canSave = false;
            Log.Info($"[FlatMap] WARNING: could not read preferences ({result}); " +
                     "using defaults without overwriting the file");
            return;
        }

        _preferFlatMap = cfg.GetValue("map", "flat", false).AsBool();
        _compressMap = cfg.GetValue("map", "compress", false).AsBool();
        _dumpMapGraph = cfg.GetValue("debug", "dump_map_graph", false).AsBool();
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
            Log.Info($"[FlatMap] WARNING: could not preserve preferences ({loadResult}); save skipped");
            return;
        }
        cfg.SetValue("map", "flat", _preferFlatMap);
        cfg.SetValue("map", "compress", _compressMap);
        Error result = cfg.Save(Path);
        if (result != Error.Ok)
            Log.Info($"[FlatMap] WARNING: could not save preferences ({result})");
    }
}
