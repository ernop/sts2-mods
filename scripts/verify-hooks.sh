#!/usr/bin/env bash
# Verify every game member DeckView and FlatMap hook by *string* (Harmony patch targets +
# AccessTools reflection) actually exists in the game's sts2.dll. The C# compiler already checks
# members accessed through typed APIs; it CANNOT check these string-named ones, so this is where
# a game update could move them. Each mod runs the same inventory as an in-game preflight and
# safely disables itself when a member is absent. Run this after each game update for a readable
# maintainer report before launching.
#
#   scripts/verify-hooks.sh
#
# Requires ilspycmd (see DEVELOPMENT.md). Exits non-zero if any member is missing.
set -u

if [ -n "${ILSPY:-}" ]; then
    ILSPY="$ILSPY"
elif command -v ilspycmd >/dev/null 2>&1; then
    ILSPY="$(command -v ilspycmd)"
else
    ILSPY="/mnt/c/Users/ernes/.dotnet/tools/ilspycmd.exe"
fi
DLL="${STS2_DLL:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64\\sts2.dll}"

if [ ! -x "$ILSPY" ]; then
    printf 'ilspycmd not found at %s; set ILSPY to its executable path\n' "$ILSPY" >&2
    exit 2
fi
# rg is not installed in every WSL distro; fall back to grep (same -m1/-w semantics here).
if command -v rg >/dev/null 2>&1; then
    SEARCH() { rg -m1 -w "$1" "$2" 2>/dev/null; }
    SEARCH_Q() { rg -q "$1" "$2" 2>/dev/null; }
else
    SEARCH() { grep -m1 -w -E "$1" "$2" 2>/dev/null; }
    SEARCH_Q() { grep -q -E "$1" "$2" 2>/dev/null; }
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail=0

# type | member  (member checked as a whole-word token in the decompiled type source)
CHECKS="
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|ConnectSignals
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|InitGrid
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_ExitTree
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_Process
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|CardPadding
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|CanScroll
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_cardSize
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_needsReinit
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_cardRows
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_cardsAnimatingOutForSetCards
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_scrollContainer
MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl|_isHovered
MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl|IsFocused
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_isHovered
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_isFocused
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_hoverTween
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|RefreshFocusState
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|DoCardHoverEffects
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|OnFocus
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|OnUnfocus
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|CreateHoverTips
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|ClearHoverTips
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|HoverScale
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|SmallScale
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|Hitbox
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder|Create
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder|OnFocus
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|ConnectSignals
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|_showUpgrades
MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen|_Ready
MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary|_Ready
MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary|_viewUpgrades
MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary|_viewMultiplayerCards
MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardGridSelectionScreen|ConnectSignalsAndInitGrid
MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardGridSelectionScreen|_peekButton
MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager|IsUsingDirectionalNavigation
MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext|GetCurrentScreen
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_Ready
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|SetMap
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|Open
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|Close
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|PlayStartOfActAnimation
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|InitMapPrompt
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|CanScroll
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|UpdateScrollPosition
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_Process
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_Input
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|RecalculateTravelability
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|RefreshAllPointVisuals
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|TravelToMapCoord
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|GetNetPositionFromScreenPosition
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|GetScreenPositionFromNetPosition
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|IsOpen
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|IsTravelEnabled
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|Drawings
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapContainer
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapBgContainer
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_pathsContainer
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_points
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_marker
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapLegend
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_drawingTools
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_backButton
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_backstop
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_shareButton
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapPointDictionary
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_startingPointNode
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_bossPointNode
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_secondBossPointNode
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_map
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_runState
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_hasPlayedAnimation
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_targetDragPos
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_actAnimTween
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapBg|OnViewportChange
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|Point
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|State
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|IsTravelable
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|HoverScale
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|DownScale
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|IsInputAllowed
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|_runState
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|RefreshColorInstantly
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|AnimHover
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|AnimUnhover
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|AnimPressDown
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|OnSelected
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_iconContainer
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_icon
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_outline
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_questIcon
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_tween
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_Process
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|RefreshColorInstantly
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|AnimHover
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|AnimUnhover
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|AnimPressDown
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|OnSelected
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_icon
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_outline
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_tween
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_elapsedTime
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|RefreshColorInstantly
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_usesSpine
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_material
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_blackLayerColor
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_mapColor
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_placeholderImage
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_placeholderOutline
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapMarker|SetMapPoint
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapMarker|_tween
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapShareButton|OnRelease
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapShareButton|IsTakingScreenshot
MegaCrit.Sts2.Core.Map.MapPoint|coord
MegaCrit.Sts2.Core.Map.MapPoint|PointType
MegaCrit.Sts2.Core.Map.MapPoint|Children
MegaCrit.Sts2.Core.Map.MapPoint|Quests
MegaCrit.Sts2.Core.Runs.RunState|CurrentMapCoord
MegaCrit.Sts2.Core.Runs.RunState|CurrentActIndex
MegaCrit.Sts2.Core.Runs.RunState|ActFloor
MegaCrit.Sts2.Core.Runs.RunState|Act
MegaCrit.Sts2.Core.Runs.RunState|MapPointHistory
MegaCrit.Sts2.Core.Runs.RunState|VisitedMapCoords
MegaCrit.Sts2.Core.Models.ActModel|Title
MegaCrit.Sts2.Core.Models.ActModel|MapBgColor
MegaCrit.Sts2.Core.Models.ActModel|MapTraveledColor
MegaCrit.Sts2.Core.Models.ActModel|MapUntraveledColor
MegaCrit.Sts2.Core.Localization.LocString|GetFormattedText
"

decompile() { # $1 = type ; caches to $TMP/<type>.cs
    local t="$1" f="$TMP/$1.cs"
    [ -s "$f" ] && return 0
    "$ILSPY" -t "$t" "$DLL" >"$f" 2>/dev/null
}

for line in $CHECKS; do
    [ -z "$line" ] && continue
    type="${line%%|*}"; member="${line##*|}"
    decompile "$type"
    f="$TMP/$type.cs"
    if [ ! -s "$f" ]; then
        printf 'FAIL  %-55s (type did not decompile)\n' "$type"
        fail=1; continue
    fi
    hit="$(SEARCH "$member" "$f" | sed 's/^[[:space:]]*//')"
    if [ -n "$hit" ]; then
        printf 'PASS  %-40s %-26s | %s\n' "${type##*.}" "$member" "$hit"
    else
        printf 'FAIL  %-40s %-26s | NOT FOUND\n' "${type##*.}" "$member"
        fail=1
    fi
done

# NGridCardHolder must NOT override SmallScale or OnUnfocus (base NCardHolder patches apply).
gf="$TMP/MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder.cs"
decompile "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder"
if SEARCH_Q "override .*\\bSmallScale\\b" "$gf"; then
    printf 'FAIL  %-40s %-26s | NGridCardHolder OVERRIDES SmallScale (patch would miss it)\n' "NGridCardHolder" "!SmallScale-override"
    fail=1
else
    printf 'PASS  %-40s %-26s | not overridden (base patch applies)\n' "NGridCardHolder" "SmallScale-not-overridden"
fi
if SEARCH_Q "override .*\\bOnUnfocus\\b" "$gf"; then
    printf 'FAIL  %-40s %-26s | NGridCardHolder OVERRIDES OnUnfocus (base patch would miss it)\n' "NGridCardHolder" "!OnUnfocus-override"
    fail=1
else
    printf 'PASS  %-40s %-26s | not overridden (base patch applies)\n' "NGridCardHolder" "OnUnfocus-not-overridden"
fi

echo
if [ "$fail" -eq 0 ]; then echo "ALL HOOKS PRESENT"; else echo "SOME HOOKS MISSING — fix before shipping"; fi
exit "$fail"
