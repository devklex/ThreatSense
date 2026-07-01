using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using GameOffsets2;
using GameOffsets2.Native;
using ImGuiNET;
using Newtonsoft.Json;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace ThreatSense;

public sealed class ThreatSense : BaseSettingsPlugin<ThreatSenseSettings>
{
    private const string PluginVersion = "v0.1";
    private const string UnknownEffectsDumpFileName = "UnknownEffectsDump.txt";
    private const string AbyssMapHistoryFileName = "abyss_map_history.json";
    private const string AbyssMarkerDumpFileName = "AbyssMarkerDump.txt";
    private const string AbyssPitActiveIcon = "AbyssPitActive";
    private const string AbyssPitInactiveIcon = "AbyssPitInactive";
    private static readonly EntityType[] EffectCandidateEntityTypes =
    {
        EntityType.Effect,
        EntityType.MonsterMods,
        EntityType.Terrain,
        EntityType.ServerObject,
        EntityType.None
    };

    private readonly List<WarningTarget> _targets = new List<WarningTarget>();
    private readonly HashSet<string> _enabledAffixIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterAffixRule> _affixRulesById = new Dictionary<string, MonsterAffixRule>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string, bool>> _effectMatchers = new Dictionary<string, Func<string, bool>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedMonsterMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedEffectMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<AmanamuCloud> _amanamuClouds = new List<AmanamuCloud>();
    private readonly List<EffectPathRule> _activeEffectRules = new List<EffectPathRule>();
    private readonly List<EffectPathRule> _activeAnyEntityRules = new List<EffectPathRule>();
    private readonly List<WarningTarget> _cachedAnyEntityPathTargets = new List<WarningTarget>();
    private readonly Dictionary<long, TrackedAmanamuTarget> _trackedAmanamuTargets = new Dictionary<long, TrackedAmanamuTarget>();
    private readonly HashSet<long> _amanamuTargetsSeenThisScan = new HashSet<long>();
    private readonly Dictionary<string, TrackedAbyssPit> _trackedAbyssPits = new Dictionary<string, TrackedAbyssPit>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedAbyssPitMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbyssMapHistoryEntry> _abyssMapHistory = new Dictionary<string, AbyssMapHistoryEntry>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MonsterAffixDefinition> _affixDefinitions = Array.Empty<MonsterAffixDefinition>();
    private IReadOnlyList<EffectRuleDefinition> _effectDefinitions = Array.Empty<EffectRuleDefinition>();
    private string _affixSearch = string.Empty;
    private string _effectSearch = string.Empty;
    private long _lastScanMs;
    private long _lastFullEntityScanMs;
    private string _currentAbyssPitAreaKey = string.Empty;
    private string _currentAbyssMapKey = string.Empty;
    private string _currentAbyssMapName = string.Empty;
    private string _currentAbyssAreaId = string.Empty;
    private string _currentAbyssRunKey = string.Empty;
    private bool _currentAbyssRunRecorded;
    private bool _insideMapSideArea;
    private int _abyssPitTerrainCount = -1;
    private bool _abyssPitTerrainScanAttempted;
    private int _lastMonsterCount;
    private int _lastEffectCount;
    private int _lastDebugEffectCount;
    private bool _abyssMapHistoryDirty;
    private bool _unknownEffectsDirty;

    public override bool Initialise()
    {
        _affixDefinitions = ThreatSenseData.LoadMonsterAffixes(DirectoryFullName, out var affixMessage);
        _effectDefinitions = ThreatSenseData.LoadEffectRules(DirectoryFullName, out var effectMessage);
        Settings.EnsureDefaults(_affixDefinitions, _effectDefinitions);
        RebuildAffixLookup();
        LoadUnknownEffectsDump();
        LoadAbyssMapHistory();

        Settings.ResetToBundledDefaults.OnPressed += () =>
        {
            Settings.ReplaceWithBundledDefaults(_affixDefinitions, _effectDefinitions);
            RebuildAffixLookup();
            ResetAbyssPitTracking();
        };

        DebugWindow.LogMsg("[ThreatSense] " + affixMessage, 5);
        DebugWindow.LogMsg("[ThreatSense] " + effectMessage, 5);
        return base.Initialise();
    }

    public override void Tick()
    {
        if (!Settings.Enable.Value || GameController?.Player == null || !GameController.InGame)
        {
            _targets.Clear();
            _trackedAmanamuTargets.Clear();
            _cachedAnyEntityPathTargets.Clear();
            if (!Settings.Enable.Value)
                ResetAbyssPitTracking();
            return;
        }

        if (IsCurrentAreaPeaceful())
        {
            _targets.Clear();
            _amanamuClouds.Clear();
            _trackedAmanamuTargets.Clear();
            _cachedAnyEntityPathTargets.Clear();
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastScanMs < Math.Max(33, Settings.ScanIntervalMs.Value))
            return;

        _lastScanMs = now;
        try
        {
            ScanTargets();
        }
        catch (Exception ex)
        {
            DebugWindow.LogError("[ThreatSense] Scan failed: " + ex.Message);
            _targets.Clear();
        }
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        if (ShouldHideOverlayUnderPanels())
            return;

        DrawAbyssPitCounterOverlay();

        if (_targets.Count == 0)
            return;

        IDisposable? textScale = null;
        try
        {
            if (Settings.ShowLabels.Value)
                textScale = Graphics.SetTextScale(Settings.LabelScale.Value);

            foreach (var target in _targets.ToArray())
            {
                if (target.Entity == null)
                    continue;

                if (!target.AllowInvalidEntity && !target.Entity.IsValid)
                    continue;

                if (GetTargetDistance(target) > GetTargetMaxDrawDistance(target))
                    continue;

                DrawTarget(target);
            }
        }
        finally
        {
            textScale?.Dispose();
        }
    }

    public override void DrawSettings()
    {
        ImGui.TextDisabled($"ThreatSense {PluginVersion}");
        ImGui.TextDisabled($"Visible warnings: {_targets.Count}, monsters: {_lastMonsterCount}, effects: {_lastEffectCount}, debug effects: {_lastDebugEffectCount}");
        ImGui.TextDisabled($"Abyss pits: {GetAbyssPitClosedCount()}/{GetAbyssPitFoundCount()} closed");

        if (ImGui.Button("Reset bundled rules"))
            Settings.ResetToBundledDefaults.OnPressed();
        HelpMarker("Reloads the bundled monster affix and ground/effect rule lists. This does not reset personal display, Abyss, Amanamu, Ritual Wisp, or debug settings.");

        DrawGeneralSettings();
        DrawAbyssPitCounterSettings();
        DrawAmanamuSettings();
        DrawRitualWispSettings();
        DrawMonsterAffixSettings();
        DrawEffectRuleSettings();
        DrawDebugSettings();
        DrawUnknownEffectSettings();
    }

    public override void AreaChange(AreaInstance area)
    {
        FlushPersistentData(false);
        _targets.Clear();
        _amanamuClouds.Clear();
        _trackedAmanamuTargets.Clear();
        _cachedAnyEntityPathTargets.Clear();
        _lastFullEntityScanMs = 0;

        if (!IsPeacefulArea(area))
        {
            var areaKey = GetAbyssPitAreaKey(area);
            var isMapSideArea = IsMapSideArea(area);
            if ((isMapSideArea || _insideMapSideArea) && !string.IsNullOrWhiteSpace(_currentAbyssPitAreaKey))
            {
                if (Settings.Debug.LogMatchedAbyssPits.Value)
                    DebugWindow.LogMsg($"[ThreatSense] Preserving Abyss pit counter through map side-area transition: {DescribeArea(area)}", 5);

                _insideMapSideArea = isMapSideArea;
            }
            else if (!string.Equals(areaKey, _currentAbyssPitAreaKey, StringComparison.Ordinal))
            {
                ResetAbyssPitTracking();
                _currentAbyssPitAreaKey = areaKey;
                SetCurrentAbyssMap(area, areaKey);
                _insideMapSideArea = false;
            }
            else if (string.IsNullOrWhiteSpace(_currentAbyssMapKey))
            {
                SetCurrentAbyssMap(area, areaKey);
                _insideMapSideArea = false;
            }
        }
        else
        {
            _insideMapSideArea = false;
        }

        base.AreaChange(area);
    }

    public override void OnPluginDestroyForHotReload()
    {
        FlushPersistentData(false);
        base.OnPluginDestroyForHotReload();
    }

    public override void Dispose()
    {
        FlushPersistentData(false);
        base.Dispose();
    }

    private void ScanTargets()
    {
        _targets.Clear();
        _amanamuClouds.Clear();
        _amanamuTargetsSeenThisScan.Clear();
        _lastMonsterCount = 0;
        _lastEffectCount = 0;
        _lastDebugEffectCount = 0;

        ScanAbyssPits();

        var now = Environment.TickCount64;
        var amanamuOverlayEnabled = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value;
        var ritualWispOverlayEnabled = Settings.RitualWisp.EnableSpecialOverlay.Value;
        var effectScanningEnabled =
            Settings.DrawGroundEffectWarnings.Value ||
            amanamuOverlayEnabled ||
            Settings.Debug.CollectUnknownEffects.Value;
        var anyEntityPathScanningEnabled = Settings.DrawGroundEffectWarnings.Value || ritualWispOverlayEnabled;
        var fullEntityScanEnabled =
            anyEntityPathScanningEnabled ||
            Settings.Debug.DrawAllEffectCandidates.Value ||
            amanamuOverlayEnabled;
        var refreshFullEntityScan = fullEntityScanEnabled && ShouldRefreshFullEntityScan(now);

        if (Settings.Debug.DrawAllEffectCandidates.Value && refreshFullEntityScan)
            ScanDebugEntityCandidates();

        if (effectScanningEnabled)
            ScanGroundEffects(Settings.DrawGroundEffectWarnings.Value);

        if (anyEntityPathScanningEnabled)
        {
            if (refreshFullEntityScan)
                ScanAnyEntityPathRules();
            AddCachedAnyEntityPathTargets();
        }
        else
        {
            _cachedAnyEntityPathTargets.Clear();
        }

        if (Settings.DrawMonsterAffixWarnings.Value || amanamuOverlayEnabled)
            ScanMonsterAffixes(Settings.DrawMonsterAffixWarnings.Value, amanamuOverlayEnabled && refreshFullEntityScan);

        AddTrackedAmanamuTargets();

        if (_unknownEffectsDirty)
            DumpUnknownEffects(false);
    }

    private bool ShouldRefreshFullEntityScan(long now)
    {
        var interval = Math.Max(Settings.ScanIntervalMs.Value, Settings.FullEntityScanIntervalMs.Value);
        if (_lastFullEntityScanMs != 0 && now - _lastFullEntityScanMs < interval)
            return false;

        _lastFullEntityScanMs = now;
        return true;
    }

    private bool IsCurrentAreaPeaceful()
    {
        try
        {
            var area = GameController?.Area?.CurrentArea;
            return area != null && IsPeacefulArea(area);
        }
        catch
        {
            return false;
        }
    }

    private void AddCachedAnyEntityPathTargets()
    {
        for (var i = _cachedAnyEntityPathTargets.Count - 1; i >= 0; i--)
        {
            var target = _cachedAnyEntityPathTargets[i];
            if (target.Entity == null || (!target.AllowInvalidEntity && !target.Entity.IsValid))
            {
                _cachedAnyEntityPathTargets.RemoveAt(i);
                continue;
            }

            if (GetTargetDistance(target) > GetTargetMaxDrawDistance(target))
                continue;

            _targets.Add(target);
            _lastEffectCount++;
        }
    }

    private void AddAnyEntityPathTarget(WarningTarget target)
    {
        _cachedAnyEntityPathTargets.Add(target);
    }

    private void ScanAbyssPits()
    {
        if (!Settings.AbyssPitCounter.Enable.Value)
            return;

        EnsureCurrentAbyssMapContext();
        MaybeScanAbyssPitTerrainTotal();

        foreach (var entity in GetCachedEntities())
        {
            try
            {
                if (entity == null)
                    continue;

                if (!TryGetAbyssPitMatch(entity, out var path, out var minimapIconName))
                    continue;

                TrackAbyssPit(entity, path, minimapIconName);
            }
            catch (Exception ex)
            {
                if (Settings.Debug.LogMatchedAbyssPits.Value)
                    DebugWindow.LogMsg($"[ThreatSense] Skipped Abyss pit candidate: {ex.Message}", 5);
            }
        }

        UpdateAbyssMapHistory();
    }

    private void MaybeScanAbyssPitTerrainTotal()
    {
        if (!Settings.AbyssPitCounter.UseTerrainFeatureTotal.Value || _abyssPitTerrainScanAttempted)
            return;

        _abyssPitTerrainScanAttempted = true;
        try
        {
            _abyssPitTerrainCount = CountAbyssPitTerrainFeatures();
            if (Settings.Debug.LogMatchedAbyssPits.Value && _abyssPitTerrainCount > 0)
                DebugWindow.LogMsg($"[ThreatSense] Abyss pit terrain features in area: {_abyssPitTerrainCount}", 5);
        }
        catch (Exception ex)
        {
            _abyssPitTerrainCount = 0;
            if (Settings.Debug.LogMatchedAbyssPits.Value)
                DebugWindow.LogMsg($"[ThreatSense] Abyss pit terrain scan failed: {ex.Message}", 5);
        }
    }

    private int CountAbyssPitTerrainFeatures()
    {
        var data = GameController?.IngameState?.Data;
        var memory = GameController?.Memory;
        if (data == null || memory == null)
            return 0;

        var terrain = data.Terrain;
        var tileData = memory.ReadStdVector<TileStructure>(terrain.TgtArray);
        if (tileData == null || tileData.Length == 0)
            return 0;

        var matches = new List<(int X, int Y)>();
        for (var tileNumber = 0; tileNumber < tileData.Length; tileNumber++)
        {
            var tgtTileStruct = memory.Read<TgtTileStruct>(tileData[tileNumber].TgtFilePtr);
            var detail = memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr);
            var detailName = detail.name.ToString(memory);
            var tgtPath = tgtTileStruct.TgtPath.ToString(memory);
            if (!IsAbyssPitPath(detailName) && !IsAbyssPitPath(tgtPath))
                continue;

            matches.Add((tileNumber % terrain.NumCols, tileNumber / terrain.NumCols));
        }

        return CountAdjacentTileClusters(matches);
    }

    private static int CountAdjacentTileClusters(IReadOnlyList<(int X, int Y)> tiles)
    {
        if (tiles.Count == 0)
            return 0;

        var remaining = new HashSet<(int X, int Y)>(tiles);
        var stack = new Stack<(int X, int Y)>();
        var clusters = 0;

        while (remaining.Count > 0)
        {
            var start = remaining.First();
            remaining.Remove(start);
            stack.Push(start);
            clusters++;

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var neighbor in GetAdjacentTiles(current))
                {
                    if (!remaining.Remove(neighbor))
                        continue;

                    stack.Push(neighbor);
                }
            }
        }

        return clusters;
    }

    private static IEnumerable<(int X, int Y)> GetAdjacentTiles((int X, int Y) tile)
    {
        for (var x = tile.X - 1; x <= tile.X + 1; x++)
        {
            for (var y = tile.Y - 1; y <= tile.Y + 1; y++)
            {
                if (x == tile.X && y == tile.Y)
                    continue;

                yield return (x, y);
            }
        }
    }

    private void TrackAbyssPit(Entity entity, string path, string minimapIconName)
    {
        var key = GetAbyssPitKey(entity, path, minimapIconName);
        if (!_trackedAbyssPits.TryGetValue(key, out var tracked))
        {
            tracked = new TrackedAbyssPit
            {
                Key = key,
                Path = path,
                MinimapIconName = minimapIconName,
                Position = entity.Pos,
                FirstSeenMs = Environment.TickCount64
            };
            _trackedAbyssPits[key] = tracked;

            if (Settings.Debug.LogMatchedAbyssPits.Value && _loggedAbyssPitMatches.Add(key + "|found"))
                DebugWindow.LogMsg($"[ThreatSense] Abyss pit found: {DescribeAbyssPit(entity, path, minimapIconName)}", 5);
        }

        tracked.LastSeenMs = Environment.TickCount64;
        tracked.Path = path;
        tracked.MinimapIconName = minimapIconName;
        tracked.Position = entity.Pos;

        var state = ReadAbyssPitState(entity);
        tracked.WasTargetable |= state.IsTargetable == true;
        tracked.WasMapVisible |= state.MapVisible == true;
        tracked.WasTransitionActive |= state.TransitionFlag1 == 1;

        var wasClosed = tracked.Closed;
        tracked.Closed |= IsAbyssPitClosed(tracked, state, path);
        tracked.LastState = state;

        if (!wasClosed && tracked.Closed && Settings.Debug.LogMatchedAbyssPits.Value && _loggedAbyssPitMatches.Add(key + "|closed"))
            DebugWindow.LogMsg($"[ThreatSense] Abyss pit closed: {DescribeAbyssPit(entity, path, minimapIconName)}", 5);
    }

    private AbyssPitState ReadAbyssPitState(Entity entity)
    {
        bool? chestOpened = null;
        bool? mapVisible = null;
        bool? mapHidden = null;
        bool? isTargetable = null;
        byte? transitionFlag1 = null;
        string minimapIconName = string.Empty;

        try
        {
            chestOpened = entity.IsOpened;
        }
        catch
        {
            // Some terrain entities do not expose the opened shortcut.
        }

        if (entity.TryGetComponent<Chest>(out var chest))
            chestOpened = chestOpened == true || chest.IsOpened;

        if (entity.TryGetComponent<MinimapIcon>(out var minimapIcon))
        {
            mapVisible = minimapIcon.IsVisible;
            mapHidden = minimapIcon.IsHide;
            minimapIconName = minimapIcon.Name ?? string.Empty;
        }

        if (entity.TryGetComponent<Targetable>(out _))
        {
            try
            {
                isTargetable = entity.IsTargetable;
            }
            catch
            {
                isTargetable = false;
            }
        }

        if (entity.TryGetComponent<Transitionable>(out var transitionable))
            transitionFlag1 = transitionable.Flag1;

        return new AbyssPitState(chestOpened, mapVisible, mapHidden, isTargetable, transitionFlag1, minimapIconName);
    }

    private static bool IsAbyssPitClosed(TrackedAbyssPit tracked, AbyssPitState state, string path)
    {
        if (state.ChestOpened == true)
            return true;

        if (IsAbyssPitInactiveIcon(state.MinimapIconName))
            return true;

        if (ContainsClosedText(path))
            return true;

        if (tracked.WasTargetable && state.IsTargetable == false)
            return true;

        if (tracked.WasMapVisible && state.MapHidden == true && state.IsTargetable == false)
            return true;

        if (tracked.WasTransitionActive && state.TransitionFlag1 == 0)
            return true;

        return false;
    }

    private bool TryGetAbyssPitMatch(Entity entity, out string path, out string minimapIconName)
    {
        path = string.Empty;
        minimapIconName = GetMinimapIconName(entity);

        if (IsAbyssPitMinimapIcon(minimapIconName))
        {
            path = GetEffectPath(entity);
            return true;
        }

        if (!Settings.AbyssPitCounter.UsePathFallback.Value)
            return false;

        path = GetEffectPath(entity);
        return IsAbyssPitPath(path);
    }

    private static string GetMinimapIconName(Entity entity)
    {
        try
        {
            return entity.TryGetComponent<MinimapIcon>(out var minimapIcon) ? minimapIcon.Name ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsAbyssPitMinimapIcon(string minimapIconName)
    {
        return minimapIconName.Equals(AbyssPitActiveIcon, StringComparison.OrdinalIgnoreCase) ||
               IsAbyssPitInactiveIcon(minimapIconName);
    }

    private static bool IsAbyssPitInactiveIcon(string minimapIconName)
    {
        return minimapIconName.Equals(AbyssPitInactiveIcon, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAbyssPitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var patterns = Settings.AbyssPitCounter.PathContains;
        if (patterns == null || patterns.Count == 0)
            return AbyssPitCounterSettings.DefaultPathContains.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase));

        return patterns.Any(part => !string.IsNullOrWhiteSpace(part) && path.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsClosedText(string path)
    {
        return path.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAbyssPitKey(Entity entity, string path, string minimapIconName)
    {
        if (IsAbyssPitMinimapIcon(minimapIconName))
        {
            try
            {
                var grid = entity.GridPos;
                return $"pit:{(int)Math.Round(grid.X)}:{(int)Math.Round(grid.Y)}";
            }
            catch
            {
                // Fall back below if the grid position is unavailable.
            }
        }

        try
        {
            if (entity.Address != 0)
                return "addr:" + entity.Address.ToString("X");
        }
        catch
        {
            // Fall back to path plus grid position when the entity address is unavailable.
        }

        try
        {
            var grid = entity.GridPos;
            return $"grid:{path}|{grid.X}:{grid.Y}";
        }
        catch
        {
            return "path:" + path;
        }
    }

    private static string DescribeAbyssPit(Entity entity, string path, string minimapIconName)
    {
        try
        {
            var icon = string.IsNullOrWhiteSpace(minimapIconName) ? "no-icon" : minimapIconName;
            return $"{entity.RenderName} [{entity.Type}] icon={icon} {path}";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(minimapIconName) ? path : $"icon={minimapIconName} {path}";
        }
    }

    private void ScanMonsterAffixes(bool drawGenericAffixes, bool includeCachedMonsters)
    {
        var amanamuOverlayEnabled = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value;
        var scanDistance = Math.Max(Settings.MaxDrawDistance.Value, Settings.AmanamuVoid.EnableSpecialStateOverlay.Value ? Settings.AmanamuVoid.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value);
        var monsters = GetMonsterCandidates(includeCachedMonsters);
        foreach (var monster in monsters)
        {
            try
            {
                if (monster == null || !IsEntityAlive(monster))
                    continue;

                var distance = GetEntityDistance(monster);
                if (distance > scanDistance)
                    continue;

                var mods = monster.GetComponent<ObjectMagicProperties>()?.Mods;
                if (mods == null || mods.Count == 0)
                    continue;

                if (amanamuOverlayEnabled && TryGetAmanamuRule(mods, out var amanamuRule, out var amanamuMod))
                {
                    var target = CreateAmanamuTarget(monster, amanamuRule);
                    TrackAmanamuTarget(target);
                    _targets.Add(target);
                    _lastMonsterCount++;

                    if (Settings.Debug.LogMatchedMonsters.Value && _loggedMonsterMatches.Add(amanamuMod + "|special"))
                        DebugWindow.LogMsg($"[ThreatSense] Amanamu state match: {amanamuMod} on {monster.RenderName}", 5);

                    continue;
                }

                if (!drawGenericAffixes || !monster.IsValid || !monster.IsHostile)
                    continue;

                if (distance > Settings.MaxDrawDistance.Value)
                    continue;

                foreach (var mod in mods)
                {
                    if (!_enabledAffixIds.Contains(mod) || !_affixRulesById.TryGetValue(mod, out var rule))
                        continue;

                    _targets.Add(CreateMonsterTarget(monster, rule));
                    _lastMonsterCount++;

                    if (Settings.Debug.LogMatchedMonsters.Value && _loggedMonsterMatches.Add(mod))
                        DebugWindow.LogMsg($"[ThreatSense] Monster affix match: {mod} on {monster.RenderName}", 5);

                    break;
                }
            }
            catch (Exception ex)
            {
                if (Settings.Debug.LogMatchedMonsters.Value)
                    DebugWindow.LogMsg($"[ThreatSense] Skipped monster scan candidate: {ex.Message}", 5);
            }
        }
    }

    private void AddTrackedAmanamuTargets()
    {
        if (!Settings.AmanamuVoid.EnableSpecialStateOverlay.Value)
        {
            _trackedAmanamuTargets.Clear();
            return;
        }

        var keepMs = Settings.AmanamuVoid.KeepTrackedMarkerSeconds.Value * 1000L;
        if (keepMs <= 0)
        {
            _trackedAmanamuTargets.Clear();
            return;
        }

        var now = Environment.TickCount64;
        foreach (var pair in _trackedAmanamuTargets.ToArray())
        {
            if (_amanamuTargetsSeenThisScan.Contains(pair.Key))
                continue;

            var tracked = pair.Value;
            if (now - tracked.LastSeenMs > keepMs || !IsEntityAlive(tracked.Target.Entity))
            {
                _trackedAmanamuTargets.Remove(pair.Key);
                continue;
            }

            var target = RefreshTrackedAmanamuTarget(tracked.Target);
            _targets.Add(target with { AllowInvalidEntity = true });
            _lastMonsterCount++;
        }
    }

    private WarningTarget RefreshTrackedAmanamuTarget(WarningTarget trackedTarget)
    {
        try
        {
            var entity = trackedTarget.Entity;
            var mods = entity.GetComponent<ObjectMagicProperties>()?.Mods;
            if (mods != null && TryGetAmanamuRule(mods, out var rule, out _))
                return CreateAmanamuTarget(entity, rule) with { AllowInvalidEntity = true };

            return trackedTarget with { Position = entity.Pos, AllowInvalidEntity = true };
        }
        catch
        {
            return trackedTarget with { AllowInvalidEntity = true };
        }
    }

    private void TrackAmanamuTarget(WarningTarget target)
    {
        try
        {
            var key = target.Entity.Address;
            _amanamuTargetsSeenThisScan.Add(key);
            _trackedAmanamuTargets[key] = new TrackedAmanamuTarget(target with { AllowInvalidEntity = true }, Environment.TickCount64);
        }
        catch
        {
            // If ExileCore cannot expose an address for this entity, skip caching and keep the live target only.
        }
    }

    private void ScanGroundEffects(bool drawWarnings)
    {
        BuildActiveEffectRules(drawWarnings);
        var scanDistance = Math.Max(Settings.MaxDrawDistance.Value, Settings.AmanamuVoid.EnableSpecialStateOverlay.Value ? Settings.AmanamuVoid.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value);
        foreach (var entity in GetEffectCandidateEntities())
        {
            if (entity == null || !entity.IsValid)
                continue;

            var distance = entity.DistancePlayer;
            if (distance > scanDistance)
                continue;

            if (IsOwnedByPlayer(entity))
                continue;

            var path = GetEffectPath(entity);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var rule = FindFirstMatchingRule(path, _activeEffectRules);
            if (rule == null)
            {
                if (Settings.Debug.CollectUnknownEffects.Value && LooksDangerousEffectPath(path))
                    RecordUnknownEffect(path);
                continue;
            }

            if (rule.RequireGroundEffectComponent.Value && !entity.TryGetComponent<GroundEffect>(out _))
                continue;

            var target = CreateEffectTarget(entity, rule, path);
            if (IsAmanamuEffectRule(rule))
                _amanamuClouds.Add(new AmanamuCloud(target.Position, target.Radius));

            if (drawWarnings && rule.Enabled.Value && (distance <= Settings.MaxDrawDistance.Value || IsAmanamuEffectRule(rule)))
            {
                _targets.Add(target);
                _lastEffectCount++;
            }

            if (Settings.Debug.LogMatchedEffects.Value && _loggedEffectMatches.Add(rule.Id + "|" + path))
                DebugWindow.LogMsg($"[ThreatSense] Effect match: {rule.Name} -> {path}", 5);
        }
    }

    private IEnumerable<Entity> GetEffectCandidateEntities()
    {
        if (GameController.EntityListWrapper == null)
            yield break;

        foreach (var type in EffectCandidateEntityTypes)
        {
            foreach (var entity in GetEntities(type))
                yield return entity;
        }
    }

    private void ScanAnyEntityPathRules()
    {
        _cachedAnyEntityPathTargets.Clear();
        BuildActiveAnyEntityRules();
        if (_activeAnyEntityRules.Count == 0)
            return;

        var scanDistance = Math.Max(
            Settings.MaxDrawDistance.Value,
            Settings.RitualWisp.EnableSpecialOverlay.Value ? Settings.RitualWisp.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value);
        var seen = new HashSet<long>();
        foreach (var entity in GetCachedEntities())
        {
            if (!TryAddCandidate(seen, entity))
                continue;

            if (!TryGetEntityDistance(entity, out var distance) || distance > scanDistance)
                continue;

            var path = GetEffectPath(entity);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var rule = FindFirstMatchingRule(path, _activeAnyEntityRules);
            if (rule == null)
                continue;

            var maxDistance = IsRitualWispRule(rule)
                ? Settings.RitualWisp.MaxDrawDistance.Value
                : Settings.MaxDrawDistance.Value;
            if (distance > maxDistance)
                continue;

            if (rule.RequireGroundEffectComponent.Value && !entity.TryGetComponent<GroundEffect>(out _))
                continue;

            AddAnyEntityPathTarget(CreateEffectTarget(entity, rule, path));

            if (Settings.Debug.LogMatchedEffects.Value && _loggedEffectMatches.Add(rule.Id + "|" + path))
                DebugWindow.LogMsg($"[ThreatSense] Entity path match: {rule.Name} -> {path}", 5);
        }
    }

    private IEnumerable<Entity> GetEntities(EntityType type)
    {
        try
        {
            return GameController.EntityListWrapper?.ValidEntitiesByType[type] ?? Enumerable.Empty<Entity>();
        }
        catch
        {
            return Enumerable.Empty<Entity>();
        }
    }

    private IEnumerable<Entity> GetMonsterCandidates(bool includeCached)
    {
        var seen = new HashSet<long>();
        foreach (var entity in GetEntities(EntityType.Monster))
        {
            if (TryAddCandidate(seen, entity))
                yield return entity;
        }

        if (!includeCached)
            yield break;

        foreach (var entity in GetCachedEntities())
        {
            if (entity?.Type != EntityType.Monster)
                continue;

            if (TryAddCandidate(seen, entity))
                yield return entity;
        }
    }

    private IEnumerable<Entity> GetCachedEntities()
    {
        try
        {
            return GameController.EntityListWrapper?.Entities ?? GameController.Entities ?? Enumerable.Empty<Entity>();
        }
        catch
        {
            return Enumerable.Empty<Entity>();
        }
    }

    private static bool TryAddCandidate(HashSet<long> seen, Entity entity)
    {
        try
        {
            return entity != null && seen.Add(entity.Address);
        }
        catch
        {
            return false;
        }
    }

    private WarningTarget CreateMonsterTarget(Entity monster, MonsterAffixRule rule)
    {
        var radius = 45f;
        if (monster.TryGetComponent<Render>(out var render))
            radius = Math.Max(35f, render.Bounds.X * Settings.MonsterCircleScale.Value);

        return new WarningTarget(monster, monster.Pos, radius, rule.Color.Value, LabelFor(rule), false);
    }

    private WarningTarget CreateAmanamuTarget(Entity monster, MonsterAffixRule rule)
    {
        var radius = 45f;
        if (monster.TryGetComponent<Render>(out var render))
            radius = Math.Max(35f, render.Bounds.X * Settings.AmanamuVoid.MonsterCircleScale.Value);

        var state = GetAmanamuVoidState(monster, out var cloud);
        var color = state switch
        {
            AmanamuVoidState.Inside => Settings.AmanamuVoid.InsideVoidColor.Value,
            AmanamuVoidState.Outside => Settings.AmanamuVoid.OutsideVoidColor.Value,
            _ => Settings.AmanamuVoid.UnknownVoidColor.Value
        };
        var label = state switch
        {
            AmanamuVoidState.Inside => Settings.AmanamuVoid.InsideVoidLabel.Value,
            AmanamuVoidState.Outside => Settings.AmanamuVoid.OutsideVoidLabel.Value,
            _ => Settings.AmanamuVoid.UnknownVoidLabel.Value
        };

        if (string.IsNullOrWhiteSpace(label))
            label = LabelFor(rule);

        var linkPosition = Settings.AmanamuVoid.DrawMonsterToCloudLine.Value && cloud != null ? cloud.Position : (Vector3?)null;
        return new WarningTarget(monster, monster.Pos, radius, color, label, false, linkPosition, true, true);
    }

    private WarningTarget CreateEffectTarget(Entity entity, EffectPathRule rule, string path)
    {
        if (IsRitualWispRule(rule))
            return CreateRitualWispTarget(entity, rule);

        var radius = EstimateEffectRadius(entity, rule);
        return new WarningTarget(entity, entity.Pos, radius, rule.Color.Value, LabelFor(rule, path), true, UseAmanamuDistance: IsAmanamuEffectRule(rule));
    }

    private WarningTarget CreateRitualWispTarget(Entity entity, EffectPathRule rule)
    {
        var label = string.IsNullOrWhiteSpace(Settings.RitualWisp.Label.Value)
            ? LabelFor(rule, entity.Path)
            : Settings.RitualWisp.Label.Value;
        return new WarningTarget(
            entity,
            entity.Pos,
            EstimateEffectRadius(entity, Settings.RitualWisp.CircleSizeMultiplier.Value),
            Settings.RitualWisp.Color.Value,
            label,
            true,
            UseRitualWispDistance: true,
            DrawPlayerGuideLine: Settings.RitualWisp.DrawPlayerGuideLine.Value,
            RingThicknessOverride: Settings.RitualWisp.CircleThickness.Value,
            PlayerGuideLineThicknessOverride: Settings.RitualWisp.GuideLineThickness.Value);
    }

    private float EstimateEffectRadius(Entity entity, EffectPathRule rule)
    {
        return EstimateEffectRadius(entity, rule.SizeMultiplier.Value);
    }

    private float EstimateEffectRadius(Entity entity, float sizeMultiplier)
    {
        float? baseRadius = null;
        if (entity.TryGetComponent<GroundEffect>(out var groundEffect))
            baseRadius = groundEffect.EffectDescription?.BaseSize;

        if (baseRadius == null && entity.TryGetComponent<Animated>(out var animated))
            baseRadius = animated.MiscAnimated?.BaseSize;

        if (baseRadius == null && entity.TryGetComponent<Render>(out var render))
            baseRadius = Math.Max(render.Bounds.X, render.Bounds.Y);

        var scale = entity.GetComponent<Positioned>()?.Scale ?? 1f;
        return Math.Max(20f, (baseRadius ?? 45f) * scale * sizeMultiplier * Settings.EffectCircleScale.Value);
    }

    private bool ShouldHideOverlayUnderPanels()
    {
        var ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi == null)
            return false;

        if (Settings.HideUnderFullscreenPanels.Value && ingameUi.FullscreenPanels.Any(x => x.IsVisible))
            return true;

        return Settings.HideUnderLargePanels.Value && ingameUi.LargePanels.Any(x => x.IsVisible);
    }

    private void DrawAbyssPitCounterOverlay()
    {
        if (!Settings.AbyssPitCounter.Enable.Value)
            return;

        var found = GetAbyssPitFoundCount();
        var closed = GetAbyssPitClosedCount();
        if (Settings.AbyssPitCounter.HideWhenNoPitsFound.Value && found <= 0 && closed <= 0)
            return;

        var label = string.IsNullOrWhiteSpace(Settings.AbyssPitCounter.Label.Value)
            ? "Abyss Pits"
            : Settings.AbyssPitCounter.Label.Value.Trim();
        var lines = new List<(string Text, Color Color)>
        {
            ($"{label}: {closed}/{Math.Max(found, closed)} closed", closed > 0 && closed >= found && found > 0
                ? Settings.AbyssPitCounter.ClosedColor.Value
                : Settings.AbyssPitCounter.TextColor.Value)
        };

        if (Settings.AbyssPitCounter.ShowMapBestInCounter.Value &&
            TryGetCurrentAbyssMapHistory(out var historyEntry) &&
            historyEntry.BestSeen > 0)
        {
            var mapName = string.IsNullOrWhiteSpace(historyEntry.MapName) ? "this map" : historyEntry.MapName;
            lines.Add(($"Best {mapName}: {historyEntry.BestSeen} seen", Settings.AbyssPitCounter.TextColor.Value));
        }

        var position = new Vector2(Settings.AbyssPitCounter.PositionX.Value, Settings.AbyssPitCounter.PositionY.Value);

        using var textScale = Graphics.SetTextScale(Settings.AbyssPitCounter.TextScale.Value);
        var lineSizes = lines.Select(x => Graphics.MeasureText(x.Text, 15)).ToList();
        var textWidth = lineSizes.Count == 0 ? 0f : lineSizes.Max(x => x.X);
        var textHeight = lineSizes.Sum(x => x.Y) + Math.Max(0, lines.Count - 1) * 2f;
        const float paddingX = 9f;
        const float paddingY = 6f;
        var box = new RectangleF(
            position.X,
            position.Y,
            Math.Max(120f, textWidth + paddingX * 2f),
            Math.Max(28f, textHeight + paddingY * 2f));

        Graphics.DrawBox(box, Settings.AbyssPitCounter.BackgroundColor.Value);
        Graphics.DrawFrame(box, Settings.AbyssPitCounter.BorderColor.Value, 1);

        var y = box.Y + paddingY;
        for (var i = 0; i < lines.Count; i++)
        {
            Graphics.DrawText(lines[i].Text, new Vector2(box.X + paddingX, y), lines[i].Color);
            y += lineSizes[i].Y + 2f;
        }
    }

    private int GetAbyssPitFoundCount()
    {
        var terrainCount = Settings.AbyssPitCounter.UseTerrainFeatureTotal.Value ? Math.Max(0, _abyssPitTerrainCount) : 0;
        return Math.Max(terrainCount, _trackedAbyssPits.Count);
    }

    private int GetAbyssPitClosedCount()
    {
        return _trackedAbyssPits.Values.Count(x => x.Closed);
    }

    private void ResetAbyssPitTracking()
    {
        _trackedAbyssPits.Clear();
        _loggedAbyssPitMatches.Clear();
        _abyssPitTerrainCount = -1;
        _abyssPitTerrainScanAttempted = false;
    }

    private void EnsureCurrentAbyssMapContext()
    {
        if (!string.IsNullOrWhiteSpace(_currentAbyssMapKey))
            return;

        try
        {
            var area = GameController?.Area?.CurrentArea;
            if (area == null || IsPeacefulArea(area))
                return;

            var areaKey = GetAbyssPitAreaKey(area);
            _currentAbyssPitAreaKey = areaKey;
            SetCurrentAbyssMap(area, areaKey);
        }
        catch
        {
            // Best effort only; the counter still works without a history context.
        }
    }

    private void SetCurrentAbyssMap(AreaInstance area, string areaKey)
    {
        _currentAbyssAreaId = SafeAreaValue(() => area.Area.Id);
        _currentAbyssMapName = GetAbyssMapDisplayName(area);
        _currentAbyssMapKey = !string.IsNullOrWhiteSpace(_currentAbyssAreaId)
            ? _currentAbyssAreaId
            : _currentAbyssMapName;
        _currentAbyssRunKey = areaKey;
        _currentAbyssRunRecorded = false;
    }

    private static string GetAbyssMapDisplayName(AreaInstance area)
    {
        var areaName = SafeAreaValue(() => area.Area.Name);
        if (!string.IsNullOrWhiteSpace(areaName))
            return areaName;

        var instanceName = SafeAreaValue(() => area.Name);
        return string.IsNullOrWhiteSpace(instanceName) ? "Unknown Area" : instanceName;
    }

    private void UpdateAbyssMapHistory()
    {
        if (!Settings.AbyssPitCounter.RecordMapHistory.Value)
            return;

        var found = GetAbyssPitFoundCount();
        if (found <= 0)
            return;

        EnsureCurrentAbyssMapContext();
        if (string.IsNullOrWhiteSpace(_currentAbyssMapKey))
            return;

        var closed = GetAbyssPitClosedCount();
        var now = DateTimeOffset.UtcNow;
        if (!_abyssMapHistory.TryGetValue(_currentAbyssMapKey, out var entry))
        {
            entry = new AbyssMapHistoryEntry
            {
                MapKey = _currentAbyssMapKey,
                MapName = _currentAbyssMapName,
                AreaId = _currentAbyssAreaId
            };
            _abyssMapHistory[_currentAbyssMapKey] = entry;
        }

        var changed = false;
        entry.MapName = string.IsNullOrWhiteSpace(_currentAbyssMapName) ? entry.MapName : _currentAbyssMapName;
        entry.AreaId = string.IsNullOrWhiteSpace(_currentAbyssAreaId) ? entry.AreaId : _currentAbyssAreaId;

        if (!_currentAbyssRunRecorded || !string.Equals(entry.LastRunKey, _currentAbyssRunKey, StringComparison.Ordinal))
        {
            entry.Runs++;
            entry.LastRunKey = _currentAbyssRunKey;
            _currentAbyssRunRecorded = true;
            changed = true;
        }

        if (entry.LastRunSeen != found)
        {
            entry.LastRunSeen = found;
            changed = true;
        }

        if (entry.LastRunClosed != closed)
        {
            entry.LastRunClosed = closed;
            changed = true;
        }

        if (found > entry.BestSeen)
        {
            entry.BestSeen = found;
            entry.BestSeenUtc = now.ToString("O");
            changed = true;
        }

        if (closed > entry.BestClosed)
        {
            entry.BestClosed = closed;
            changed = true;
        }

        var lastSeenUtc = now.ToString("O");
        if (!string.Equals(entry.LastSeenUtc, lastSeenUtc, StringComparison.Ordinal))
        {
            entry.LastSeenUtc = lastSeenUtc;
            _abyssMapHistoryDirty = true;
        }

        if (changed)
            SaveAbyssMapHistory(false);
    }

    private bool TryGetCurrentAbyssMapHistory(out AbyssMapHistoryEntry entry)
    {
        entry = null!;
        return !string.IsNullOrWhiteSpace(_currentAbyssMapKey) &&
               _abyssMapHistory.TryGetValue(_currentAbyssMapKey, out entry!);
    }

    private bool IsPeacefulArea(AreaInstance area)
    {
        try
        {
            return area.IsPeaceful || area.IsTown || area.IsHideout;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMapSideArea(AreaInstance area)
    {
        return ContainsMapSideAreaName(SafeAreaValue(() => area.Area.Id)) ||
               ContainsMapSideAreaName(SafeAreaValue(() => area.Area.Name)) ||
               ContainsMapSideAreaName(SafeAreaValue(() => area.Name));
    }

    private static bool ContainsMapSideAreaName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("Abyss_Depths", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyssal Depths", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyssal_Depths", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyss_Boss1", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyss_Boss2", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyss_Boss_01", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Abyss_Boss_02", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Lightless Void", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Lightless_Void", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Dark Domain", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Dark_Domain", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Delirium_HungerBoss", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Loathsome Mire", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Loathsome_Mire", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeArea(AreaInstance area)
    {
        var id = SafeAreaValue(() => area.Area.Id);
        var name = SafeAreaValue(() => area.Area.Name);
        var instanceName = SafeAreaValue(() => area.Name);
        return string.Join(" / ", new[] { id, name, instanceName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private string GetAbyssPitAreaKey(AreaInstance area)
    {
        var rawName = SafeAreaValue(() => area.Area.Id);
        var areaName = SafeAreaValue(() => area.Area.Name);
        var instanceName = SafeAreaValue(() => area.Name);
        var terrainAddress = GetObjectAddress(GameController?.IngameState?.Data?.Terrain);
        var dimensions = SafeAreaValue(() => GameController?.IngameState?.Data?.AreaDimensions.ToString() ?? string.Empty);

        return string.Join("|", rawName, areaName, instanceName, terrainAddress.ToString("X"), dimensions);
    }

    private static string SafeAreaValue(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long GetObjectAddress(object? value)
    {
        if (value == null)
            return 0;

        try
        {
            var property = value.GetType().GetProperty("Address");
            if (property?.GetValue(value) is { } address)
                return Convert.ToInt64(address);
        }
        catch
        {
            // Address is best-effort only; area names/dimensions remain as fallback.
        }

        return 0;
    }

    private void DrawTarget(WarningTarget target)
    {
        if (target.DrawAmanamuMapMarker)
        {
            DrawAmanamuRadarGuideLine(target);
            DrawAmanamuMapMarker(target);
        }

        if (target.DrawPlayerGuideLine)
            DrawPlayerGuideLine(target);

        if (Settings.DrawFilledCircles.Value)
            Graphics.DrawFilledCircleInWorld(target.Position, target.Radius, Color.FromArgb(Math.Min(80, (int)target.Color.A), target.Color), 48);

        if (target.LinkPosition is { } linkPosition)
        {
            var start = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(target.Position);
            var end = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(linkPosition);
            Graphics.DrawLine(start, end, Settings.AmanamuVoid.LinkThickness.Value, target.Color);
        }

        Graphics.DrawCircleInWorld(target.Position, target.Radius, target.Color, target.RingThicknessOverride ?? Settings.CircleThickness.Value);

        if (!Settings.ShowLabels.Value || string.IsNullOrWhiteSpace(target.Label))
            return;

        DrawTargetLabel(target);
    }

    private float GetTargetMaxDrawDistance(WarningTarget target)
    {
        if (target.UseDebugEffectDistance)
            return Settings.Debug.AllEffectCandidateMaxDistance.Value;

        if (target.UseRitualWispDistance)
            return Settings.RitualWisp.MaxDrawDistance.Value;

        return target.UseAmanamuDistance ? Settings.AmanamuVoid.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value;
    }

    private float GetTargetDistance(WarningTarget target)
    {
        try
        {
            return target.Entity.DistancePlayer;
        }
        catch
        {
            var player = GameController?.Player;
            return player == null ? float.MaxValue : Distance2D(player.Pos, target.Position);
        }
    }

    private void DrawAmanamuMapMarker(WarningTarget target)
    {
        if (!Settings.AmanamuVoid.DrawMapMarker.Value || !TryWorldToMap(target.Position, out var mapPosition, out var mapCenter))
            return;

        var size = Settings.AmanamuVoid.MapMarkerSize.Value;
        var thickness = Settings.AmanamuVoid.MapMarkerThickness.Value;
        var accentColor = Settings.AmanamuVoid.MapMarkerAccentColor.Value;
        var haloRadius = size;

        if (Settings.AmanamuVoid.DrawMapGuideLine.Value)
            Graphics.DrawLine(mapCenter, mapPosition, Settings.AmanamuVoid.RadarGuideLineThickness.Value, Color.FromArgb(220, target.Color));

        if (Settings.AmanamuVoid.DrawRareIconHalo.Value)
        {
            var haloThickness = Settings.AmanamuVoid.RareIconHaloThickness.Value;
            haloRadius = size + Settings.AmanamuVoid.RareIconHaloPadding.Value;
            Graphics.DrawCircle(mapPosition, haloRadius + 3, Color.Black, haloThickness + 2, 48);
            Graphics.DrawCircle(mapPosition, haloRadius, target.Color, haloThickness, 48);
            var accentRadius = haloRadius - Math.Max(2, haloThickness + 2);
            if (accentRadius > size + 1)
                Graphics.DrawCircle(mapPosition, accentRadius, accentColor, Math.Max(1, haloThickness - 1), 48);
        }

        var outerRadius = size + Math.Max(2, thickness + 3);
        var accentOuterRadius = size + Math.Max(1, thickness + 1);
        Graphics.DrawCircle(mapPosition, outerRadius, Color.Black, Math.Max(2, thickness + 2), 40);
        Graphics.DrawCircle(mapPosition, accentOuterRadius, accentColor, Math.Max(2, thickness + 1), 40);
        Graphics.DrawCircle(mapPosition, size, Color.Black, Math.Max(2, thickness + 2), 40);
        Graphics.DrawCircle(mapPosition, size, target.Color, thickness, 40);
        Graphics.DrawCircle(mapPosition, Math.Max(1f, size * 0.55f), accentColor, Math.Max(1, thickness - 1), 28);
        var crosshairRadius = size + Math.Max(1, thickness + 1);
        Graphics.DrawLine(mapPosition - new Vector2(crosshairRadius, 0), mapPosition + new Vector2(crosshairRadius, 0), Math.Max(1, thickness - 1), accentColor);
        Graphics.DrawLine(mapPosition - new Vector2(0, crosshairRadius), mapPosition + new Vector2(0, crosshairRadius), Math.Max(1, thickness - 1), accentColor);

        if (!Settings.AmanamuVoid.DrawMapMarkerLabel.Value || string.IsNullOrWhiteSpace(Settings.AmanamuVoid.MapMarkerLabel.Value))
            return;

        var label = Settings.AmanamuVoid.MapMarkerLabel.Value;
        var textSize = Graphics.MeasureText(label, 15);
        var labelPosition = new Vector2(mapPosition.X - textSize.X / 2f, mapPosition.Y + haloRadius + 6f);
        DrawOutlinedText(label, labelPosition, target.Color);
    }

    private void DrawAmanamuRadarGuideLine(WarningTarget target)
    {
        if (!Settings.AmanamuVoid.DrawRadarGuideLine.Value)
            return;

        var player = GameController?.Player;
        var camera = RemoteMemoryObject.TheGame?.IngameState?.Camera;
        if (player == null || camera == null)
            return;

        var start = camera.WorldToScreen(player.Pos);
        var end = camera.WorldToScreen(target.Position);
        if (!IsFinite(start) || !IsFinite(end))
            return;

        Graphics.DrawLine(start, end, Settings.AmanamuVoid.RadarGuideLineThickness.Value + 2, Color.Black);
        Graphics.DrawLine(start, end, Settings.AmanamuVoid.RadarGuideLineThickness.Value, Color.FromArgb(230, target.Color));
    }

    private void DrawPlayerGuideLine(WarningTarget target)
    {
        var player = GameController?.Player;
        var camera = RemoteMemoryObject.TheGame?.IngameState?.Camera;
        if (player == null || camera == null)
            return;

        var start = camera.WorldToScreen(player.Pos);
        var end = camera.WorldToScreen(target.Position);
        if (!IsFinite(start) || !IsFinite(end))
            return;

        var thickness = target.PlayerGuideLineThicknessOverride ?? Settings.CircleThickness.Value;
        Graphics.DrawLine(start, end, thickness + 2, Color.Black);
        Graphics.DrawLine(start, end, thickness, Color.FromArgb(230, target.Color));
    }

    private bool TryWorldToMap(Vector3 worldPosition, out Vector2 mapPosition, out Vector2 mapCenter)
    {
        mapPosition = default;
        mapCenter = default;

        var ingameUi = GameController?.Game?.IngameState?.IngameUi;
        var playerRender = GameController?.Player?.GetComponent<Render>();
        if (ingameUi?.Map == null || playerRender == null)
            return false;

        var mapScale = 0f;
        var clipToSmallMap = false;
        var smallMapRect = ingameUi.Map.SmallMiniMap.GetClientRectCache;

        var smallMiniMap = ingameUi.Map.SmallMiniMap;
        if (smallMiniMap.IsValid && smallMiniMap.IsVisibleLocal)
        {
            mapCenter = smallMapRect.Center;
            mapScale = smallMiniMap.MapScale;
            clipToSmallMap = true;
        }
        else if (ingameUi.Map.LargeMap.IsVisibleLocal)
        {
            var largeMap = ingameUi.Map.LargeMap;
            mapCenter = largeMap.MapCenter;
            mapScale = largeMap.MapScale;
        }
        else
        {
            return false;
        }

        var terrainData = GameController?.IngameState?.Data;
        if (terrainData == null)
            return false;

        var playerGrid = playerRender.Pos.WorldToGrid();
        var targetGrid = worldPosition.WorldToGrid();
        var playerHeight = -playerRender.UnclampedHeight;
        var deltaZ = (playerHeight + terrainData.GetTerrainHeightAt(targetGrid)) * PoeMapExtension.WorldToGridConversion;
        mapPosition = mapCenter + DeltaInWorldToMinimapDelta(targetGrid - playerGrid, deltaZ, mapScale);
        return !clipToSmallMap || smallMapRect.Contains(mapPosition);
    }

    private const float CameraAngle = 38.7f * MathF.PI / 180;
    private static readonly float CameraAngleCos = MathF.Cos(CameraAngle);
    private static readonly float CameraAngleSin = MathF.Sin(CameraAngle);

    private static Vector2 DeltaInWorldToMinimapDelta(Vector2 delta, float deltaZ, float mapScale)
    {
        return mapScale * Vector2.Multiply(new Vector2(delta.X - delta.Y, deltaZ - (delta.X + delta.Y)), new Vector2(CameraAngleCos, CameraAngleSin));
    }

    private void DrawOutlinedText(string text, Vector2 position, Color color)
    {
        const float offset = 1f;
        Graphics.DrawText(text, position + new Vector2(-offset, 0), Color.Black);
        Graphics.DrawText(text, position + new Vector2(offset, 0), Color.Black);
        Graphics.DrawText(text, position + new Vector2(0, -offset), Color.Black);
        Graphics.DrawText(text, position + new Vector2(0, offset), Color.Black);
        Graphics.DrawText(text, position, color);
    }

    private void DrawTargetLabel(WarningTarget target)
    {
        var screenPos = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(target.Position);
        if (!IsFinite(screenPos))
            return;

        var textSize = Graphics.MeasureText(target.Label, 15);
        var position = new Vector2(screenPos.X - textSize.X / 2f, screenPos.Y - target.Radius * 0.25f);

        if (Settings.DrawLabelBackgrounds.Value)
        {
            const float paddingX = 4f;
            const float paddingY = 2f;
            var box = new RectangleF(
                position.X - paddingX,
                position.Y - paddingY,
                textSize.X + paddingX * 2f,
                textSize.Y + paddingY * 2f);
            Graphics.DrawBox(box, Settings.LabelBackgroundColor.Value);
        }

        Graphics.DrawText(target.Label, position, target.Color);
    }

    private void ScanDebugEntityCandidates()
    {
        var seen = new HashSet<long>();
        foreach (var entity in GetCachedEntities())
        {
            if (_lastDebugEffectCount >= Settings.Debug.AllEffectCandidateLimit.Value)
                return;

            if (!TryAddCandidate(seen, entity))
                continue;

            if (!TryGetEntityDistance(entity, out var distance) ||
                distance > Settings.Debug.AllEffectCandidateMaxDistance.Value)
                continue;

            AddDebugEntityCandidate(entity, GetEffectPath(entity));
        }
    }

    private void AddDebugEntityCandidate(Entity entity, string path)
    {
        if (_lastDebugEffectCount >= Settings.Debug.AllEffectCandidateLimit.Value)
            return;

        if (!TryGetEntityDistance(entity, out var distance) ||
            distance > Settings.Debug.AllEffectCandidateMaxDistance.Value)
            return;

        var target = new WarningTarget(
            entity,
            entity.Pos,
            EstimateEffectRadius(entity, Settings.Debug.AllEffectCandidateSizeMultiplier.Value),
            Settings.Debug.AllEffectCandidateColor.Value,
            MakeDebugEntityLabel(entity, path),
            true,
            UseDebugEffectDistance: true);

        _targets.Add(target);
        _lastDebugEffectCount++;

        if (Settings.Debug.SaveAllDrawnEffectCandidates.Value && !string.IsNullOrWhiteSpace(path))
            RecordUnknownEffect(path);
    }

    private bool TryGetEntityDistance(Entity entity, out float distance)
    {
        distance = float.MaxValue;
        try
        {
            if (entity == null || !entity.IsValid)
                return false;

            distance = entity.DistancePlayer;
            return float.IsFinite(distance);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private void RebuildAffixLookup()
    {
        _enabledAffixIds.Clear();
        _affixRulesById.Clear();
        foreach (var rule in Settings.MonsterAffixes)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id))
                continue;

            rule.EnsureDefaults();
            _affixRulesById[rule.Id] = rule;
            if (rule.Enabled.Value)
                _enabledAffixIds.Add(rule.Id);
        }
    }

    private Func<string, bool> GetEffectMatcher(EffectPathRule rule)
    {
        IReadOnlyList<string> patterns = rule.PathContains != null ? rule.PathContains : Array.Empty<string>();
        var key = string.Join("|", patterns);
        if (_effectMatchers.TryGetValue(key, out var matcher))
            return matcher;

        matcher = path => patterns.Any(part => !string.IsNullOrWhiteSpace(part) && path.Contains(part, StringComparison.OrdinalIgnoreCase));
        _effectMatchers[key] = matcher;
        return matcher;
    }

    private void BuildActiveEffectRules(bool drawWarnings)
    {
        _activeEffectRules.Clear();
        var amanamuOverlayEnabled = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value;
        foreach (var rule in Settings.EffectRules)
        {
            if (rule == null)
                continue;

            rule.EnsureDefaults();
            var isAmanamuEffectRule = IsAmanamuEffectRule(rule);
            if (rule.MatchAnyEntityType.Value && !isAmanamuEffectRule)
                continue;

            if ((drawWarnings && rule.Enabled.Value) || (amanamuOverlayEnabled && isAmanamuEffectRule))
                _activeEffectRules.Add(rule);
        }
    }

    private void BuildActiveAnyEntityRules()
    {
        _activeAnyEntityRules.Clear();
        var drawGroundEffects = Settings.DrawGroundEffectWarnings.Value;
        var ritualWispOverlayEnabled = Settings.RitualWisp.EnableSpecialOverlay.Value;
        if (!drawGroundEffects && !ritualWispOverlayEnabled)
            return;

        foreach (var rule in Settings.EffectRules)
        {
            if (rule == null)
                continue;

            rule.EnsureDefaults();
            if (!rule.MatchAnyEntityType.Value)
                continue;

            var isRitualWispRule = IsRitualWispRule(rule);
            if ((drawGroundEffects && rule.Enabled.Value && !isRitualWispRule) ||
                (ritualWispOverlayEnabled && isRitualWispRule))
            {
                _activeAnyEntityRules.Add(rule);
            }
        }
    }

    private EffectPathRule? FindFirstMatchingRule(string path, IReadOnlyList<EffectPathRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (GetEffectMatcher(rule)(path))
                return rule;
        }

        return null;
    }

    private AmanamuVoidState GetAmanamuVoidState(Entity monster, out AmanamuCloud? nearestCloud)
    {
        if (Settings.AmanamuVoid.PreferImmuneBuffState.Value && HasBuff(monster, "abyss_lightless_well_immune"))
        {
            TryFindNearestAmanamuCloud(monster.Pos, out nearestCloud, out _);
            return AmanamuVoidState.Inside;
        }

        if (!TryFindNearestAmanamuCloud(monster.Pos, out nearestCloud, out var distance))
            return AmanamuVoidState.Unknown;

        var cloud = nearestCloud;
        if (cloud == null)
            return AmanamuVoidState.Unknown;

        var insideDistance = cloud.Radius + Settings.AmanamuVoid.CloudRadiusPadding.Value;
        return distance <= insideDistance ? AmanamuVoidState.Inside : AmanamuVoidState.Outside;
    }

    private bool TryFindNearestAmanamuCloud(Vector3 position, out AmanamuCloud? nearestCloud, out float distance)
    {
        nearestCloud = null;
        distance = float.MaxValue;

        foreach (var cloud in _amanamuClouds)
        {
            var currentDistance = Distance2D(position, cloud.Position);
            if (currentDistance >= distance)
                continue;

            distance = currentDistance;
            nearestCloud = cloud;
        }

        return nearestCloud != null;
    }

    private static float Distance2D(Vector3 left, Vector3 right)
    {
        return Vector2.Distance(new Vector2(left.X, left.Y), new Vector2(right.X, right.Y));
    }

    private static bool IsAmanamuAffix(string mod)
    {
        return mod.Equals("MonsterAbyssLightlessFaction1", StringComparison.OrdinalIgnoreCase) ||
               mod.Equals("PlayerMonsterAbyssLightlessFaction1", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetAmanamuRule(IEnumerable<string> mods, out MonsterAffixRule rule, out string mod)
    {
        foreach (var currentMod in mods)
        {
            if (string.IsNullOrWhiteSpace(currentMod) ||
                !IsAmanamuAffix(currentMod) ||
                !_affixRulesById.TryGetValue(currentMod, out var foundRule))
                continue;

            rule = foundRule;
            mod = currentMod;
            return true;
        }

        rule = null!;
        mod = string.Empty;
        return false;
    }

    private static bool IsAmanamuEffectRule(EffectPathRule rule)
    {
        return rule.Id.Equals("amanamus_void", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRitualWispRule(EffectPathRule rule)
    {
        return rule.Id.Equals("ritual_wisp_anchor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEntityAlive(Entity entity)
    {
        try
        {
            return entity.IsAlive;
        }
        catch
        {
            return false;
        }
    }

    private static float GetEntityDistance(Entity entity)
    {
        try
        {
            return entity.DistancePlayer;
        }
        catch
        {
            return float.MaxValue;
        }
    }

    private static bool HasBuff(Entity entity, string buffName)
    {
        return entity.TryGetComponent<Buffs>(out var buffs) && buffs.HasBuff(buffName);
    }

    private static bool IsOwnedByPlayer(Entity entity)
    {
        var player = RemoteMemoryObject.TheGame?.IngameState?.Data?.LocalPlayer;
        if (player == null)
            return false;

        if (entity.GetComponent<Buffs>()?.Owner?.Address == player.Address)
            return true;
        if (entity.GetComponent<Stats>()?.Owner?.Address == player.Address)
            return true;

        if (entity.TryGetComponent<Animated>(out var animated) && animated.BaseAnimatedObjectEntity != null)
        {
            var baseEntity = animated.BaseAnimatedObjectEntity;
            if (baseEntity.GetComponent<Buffs>()?.Owner?.Address == player.Address)
                return true;
            if (baseEntity.GetComponent<Stats>()?.Owner?.Address == player.Address)
                return true;
        }

        return false;
    }

    private static string GetEffectPath(Entity entity)
    {
        if (entity.TryGetComponent<Animated>(out var animated) && animated.BaseAnimatedObjectEntity?.Path is { Length: > 0 } animatedPath)
            return animatedPath;

        return entity.Path ?? string.Empty;
    }

    private static bool LooksDangerousEffectPath(string path)
    {
        return path.Contains("ground", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("beacon", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("volatile", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("explode", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("vortex", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("orb", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("crystal", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("storm", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("mirage", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("siphon", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("caustic", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("desecrate", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("magma", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("flamewall", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("shroud", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("shade", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("lightless", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ritual", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("altar", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("rune", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("void", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeDebugEntityLabel(Entity entity, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return SafeEntityType(entity);

        var normalized = path.Replace('\\', '/');
        var prefixes = new[]
        {
            "Metadata/Effects/Spells/monsters_effects/",
            "Metadata/Effects/Spells/",
            "Metadata/Effects/",
            "Metadata/Monsters/"
        };

        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length);
                break;
            }
        }

        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var shortPath = parts.Length <= 3
            ? normalized
            : string.Join("/", parts.Skip(parts.Length - 3));

        return $"{SafeEntityType(entity)} {shortPath}";
    }

    private static string SafeEntityType(Entity entity)
    {
        try
        {
            return entity?.Type.ToString() ?? "entity";
        }
        catch
        {
            return "entity";
        }
    }

    private static string LabelFor(MonsterAffixRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Label.Value))
            return rule.Label.Value;

        return rule.Name;
    }

    private static string LabelFor(EffectPathRule rule, string path)
    {
        if (!string.IsNullOrWhiteSpace(rule.Label.Value))
            return rule.Label.Value;

        return !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name : path.Split('/').LastOrDefault() ?? "DANGER";
    }

    private void DrawGeneralSettings()
    {
        if (!ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawToggle("Enable", Settings.Enable);
        DrawToggle("Monster affix warnings", Settings.DrawMonsterAffixWarnings, "Draw world circles and labels around monsters whose ObjectMagicProperties.Mods match enabled dangerous affix rules.");
        DrawToggle("Ground / effect warnings", Settings.DrawGroundEffectWarnings, "Draw world circles and labels around spawned effect entities, such as beacons, volatiles, storms, and ground effects.");
        DrawToggle("Show labels", Settings.ShowLabels);
        DrawToggle("Draw label backgrounds", Settings.DrawLabelBackgrounds, "Draw a configurable background behind world labels so text stays readable over bright effects.");
        DrawColorEditor("Label background color", Settings.LabelBackgroundColor, "Color behind warning labels. Use the alpha channel to control opacity. Default is translucent black.");
        DrawToggle("Draw filled circles", Settings.DrawFilledCircles);
        DrawToggle("Hide under large panels", Settings.HideUnderLargePanels, "Suppress all overlays while large in-game panels are open so warning graphics do not cover UI.");
        DrawToggle("Hide under fullscreen panels", Settings.HideUnderFullscreenPanels, "Suppress all overlays while fullscreen in-game panels are open.");
        DrawIntSlider("Scan interval ms", Settings.ScanIntervalMs, "How often the plugin rescans nearby entities. Lower values update faster but cost more CPU.");
        DrawIntSlider("Full entity scan interval ms", Settings.FullEntityScanIntervalMs, "How often ThreatSense walks the full cached entity list for broad path rules such as boss telegraphs, Ritual Wisp, debug candidates, and cached Amanamu fallback. Increase this if ThreatSense [P] is high.");
        DrawIntSlider("Generic max draw distance", Settings.MaxDrawDistance, "Maximum distance for normal monster affix and effect warnings. Amanamu has a separate distance setting below.");
        DrawIntSlider("Circle thickness", Settings.CircleThickness, "Line thickness for world-space warning circles.");
        DrawFloatSlider("Monster circle scale", Settings.MonsterCircleScale, 1f, "Multiplier for normal monster warning circle size.");
        DrawFloatSlider("Effect circle scale", Settings.EffectCircleScale, 1f, "Global multiplier for effect warning circle size. Individual effect rules also have their own size multiplier.");
        DrawFloatSlider("Label scale", Settings.LabelScale, 1f, "Scale for warning text labels.");
    }

    private void DrawAbyssPitCounterSettings()
    {
        if (!ImGui.CollapsingHeader("Abyss Pit Counter", ImGuiTreeNodeFlags.None))
            return;

        DrawToggle("Enable Abyss pit counter", Settings.AbyssPitCounter.Enable, "Tracks AbyssPitActive/AbyssPitInactive minimap-icon entities exposed by ExileCore. This does not require the MinimapIcons plugin to be enabled.");
        DrawToggle("Only show after pit detected", Settings.AbyssPitCounter.HideWhenNoPitsFound, "When enabled, the counter is hidden until at least one Abyss pit has been seen in the current map. Disable this if you always want the counter visible.");
        DrawToggle("Record map history", Settings.AbyssPitCounter.RecordMapHistory, "Save the best observed Abyss pit count per map to a local personal data file. This only records pits ExileCore actually exposes while you are in the map.");
        DrawToggle("Show map best in counter", Settings.AbyssPitCounter.ShowMapBestInCounter, "Adds a second counter line showing the best observed pit count for the current map.");
        DrawTextEditor("Counter label", Settings.AbyssPitCounter.Label);
        DrawIntSlider("Counter X", Settings.AbyssPitCounter.PositionX);
        DrawIntSlider("Counter Y", Settings.AbyssPitCounter.PositionY);
        DrawFloatSlider("Counter text scale", Settings.AbyssPitCounter.TextScale, 1f);
        DrawIntSlider("History rows shown", Settings.AbyssPitCounter.HistoryRowsShown, "Maximum rows shown in the Abyss Map History table.");
        DrawColorEditor("Counter text color", Settings.AbyssPitCounter.TextColor);
        DrawColorEditor("Counter complete color", Settings.AbyssPitCounter.ClosedColor);
        DrawColorEditor("Counter border color", Settings.AbyssPitCounter.BorderColor);
        DrawColorEditor("Counter background color", Settings.AbyssPitCounter.BackgroundColor);

        ImGui.TextDisabled($"Current area: {GetAbyssPitClosedCount()}/{GetAbyssPitFoundCount()} closed, {_trackedAbyssPits.Count} runtime pit entities tracked");

        DrawAbyssMapHistorySettings();
        DrawAbyssPitAdvancedSettings();
    }

    private void DrawAbyssPitAdvancedSettings()
    {
        if (!ImGui.CollapsingHeader("Abyss Advanced / Experimental", ImGuiTreeNodeFlags.None))
            return;

        ImGui.TextDisabled("Normal pit tracking uses ExileCore's AbyssPitActive/AbyssPitInactive minimap-icon names.");
        HelpMarker("These settings are fallback tools for troubleshooting. Terrain feature total scans the map TGT terrain files for matching Abyss art/features and can overcount. Path fallback counts runtime entities whose paths contain the configured substrings. Leave both off unless the normal counter misses pits.");
        DrawToggle("Use terrain feature total", Settings.AbyssPitCounter.UseTerrainFeatureTotal, "Experimental. Counts matching TGT terrain features for the current map total. This can overcount Abyss art/layout pieces, so leave it off for normal pit tracking.");
        DrawToggle("Use path fallback", Settings.AbyssPitCounter.UsePathFallback, "Experimental. Also counts entities whose path contains the configured substrings. Leave off for normal tracking; the reliable source is MinimapIcon.Name AbyssPitActive/AbyssPitInactive.");

        ImGui.SeparatorText("Current map actions");
        if (ImGui.Button("Reset live counter"))
            ResetAbyssPitTracking();
        HelpMarker("Clears only the live counter for the current area. It does not erase Abyss map history.");

        if (ImGui.Button("Dump Abyss markers"))
            DumpAbyssMarkers();
        HelpMarker("Writes currently exposed Abyss marker candidates to a text file with entity type, grid position, minimap icon state, targetable/chest/transition state, and path.");

        ImGui.SameLine();
        if (ImGui.Button("Copy Abyss dump path"))
            ImGui.SetClipboardText(GetAbyssMarkerDumpPath());

        var pathMatchingOpen = ImGui.TreeNode("Path matching");
        HelpMarker("Only used by the experimental path fallback and terrain feature total options. Each value is a case-insensitive substring matched against exposed entity or terrain paths.");
        if (pathMatchingOpen)
        {
            DrawAbyssPitPathEditor();
            ImGui.TreePop();
        }
    }

    private void DrawAbyssMapHistorySettings()
    {
        if (!ImGui.CollapsingHeader($"Abyss Map History ({_abyssMapHistory.Count})", ImGuiTreeNodeFlags.None))
            return;

        ImGui.TextDisabled("Best observed pit counts by map. This is observed runtime data, not guaranteed total possible spawns.");
        ImGui.TextDisabled($"History file: {GetAbyssMapHistoryPath()}");

        if (TryGetCurrentAbyssMapHistory(out var currentEntry))
        {
            ImGui.TextDisabled($"Current map best: {currentEntry.MapName} - {currentEntry.BestSeen} seen, {currentEntry.BestClosed} closed");
        }
        else if (!string.IsNullOrWhiteSpace(_currentAbyssMapName))
        {
            ImGui.TextDisabled($"Current map: {_currentAbyssMapName} - no Abyss pits recorded yet");
        }

        if (ImGui.Button("Save history now"))
            SaveAbyssMapHistory();

        ImGui.SameLine();
        if (ImGui.Button("Copy history path"))
            ImGui.SetClipboardText(GetAbyssMapHistoryPath());

        ImGui.SameLine();
        if (ImGui.Button("Open history folder"))
            OpenAbyssMapHistoryFolder();

        ImGui.SameLine();
        if (ImGui.Button("Reset history"))
        {
            _abyssMapHistory.Clear();
            _currentAbyssRunRecorded = false;
            SaveAbyssMapHistory();
            UpdateAbyssMapHistory();
        }

        if (_abyssMapHistory.Count == 0)
        {
            ImGui.TextDisabled("No Abyss map history recorded yet.");
            return;
        }

        var rows = _abyssMapHistory.Values
            .OrderByDescending(x => x.BestSeen)
            .ThenByDescending(x => x.LastRunSeen)
            .ThenBy(x => x.MapName, StringComparer.OrdinalIgnoreCase)
            .Take(Settings.AbyssPitCounter.HistoryRowsShown.Value)
            .ToList();

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        var tableHeight = Math.Min(360f, 28f + rows.Count * 24f);
        if (!ImGui.BeginTable("##abyss_map_history", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Best", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Closed", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 125f);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.MapName) ? row.MapKey : row.MapName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.BestSeen.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.BestClosed.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.LastRunSeen.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Runs.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatHistoryTime(row.LastSeenUtc));
        }

        ImGui.EndTable();
    }

    private void DrawAbyssPitPathEditor()
    {
        Settings.AbyssPitCounter.PathContains ??= AbyssPitCounterSettings.DefaultPathContains.ToList();

        for (var i = 0; i < Settings.AbyssPitCounter.PathContains.Count; i++)
        {
            ImGui.PushID($"abyss_pit_path_{i}");
            var value = Settings.AbyssPitCounter.PathContains[i] ?? string.Empty;
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputText("##path", ref value, 256))
            {
                Settings.AbyssPitCounter.PathContains[i] = value;
                ResetAbyssPitTracking();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                Settings.AbyssPitCounter.PathContains.RemoveAt(i);
                ResetAbyssPitTracking();
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        if (ImGui.Button("Add path substring"))
        {
            Settings.AbyssPitCounter.PathContains.Add(string.Empty);
            ResetAbyssPitTracking();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset path substrings"))
        {
            Settings.AbyssPitCounter.ResetPathContains();
            ResetAbyssPitTracking();
        }
    }

    private void DrawAmanamuSettings()
    {
        if (!ImGui.CollapsingHeader("Amanamu's Void / Omen of Light", ImGuiTreeNodeFlags.None))
            return;

        ImGui.TextDisabled("Special handling for Amanamu's Void rares. Red means pull it out; green means kill it.");
        DrawToggle("Enable special state overlay", Settings.AmanamuVoid.EnableSpecialStateOverlay, "Detect Amanamu's Void rare monsters even when the normal monster affix warnings are disabled.");
        DrawIntSlider("Amanamu max draw distance", Settings.AmanamuVoid.MaxDrawDistance, "Separate scan/draw distance for Amanamu rares and their void cloud. Increase this if you want the warning earlier than normal hazards.");
        DrawIntSlider("Keep detected marker seconds", Settings.AmanamuVoid.KeepTrackedMarkerSeconds, "Keeps the last known Amanamu marker briefly if ExileCore drops the rare from the strict valid entity list while MinimapIcons-style cached data still exists. Set to 0 to disable this cache.");
        DrawToggle("Prefer immune buff state", Settings.AmanamuVoid.PreferImmuneBuffState, "When ExileCore exposes the abyss_lightless_well_immune buff, treat it as the strongest signal that the monster is still inside the void.");
        DrawIntSlider("Cloud radius padding", Settings.AmanamuVoid.CloudRadiusPadding, "Extra radius added around the detected void cloud when deciding whether the rare is inside or outside.");
        DrawFloatSlider("Amanamu monster circle scale", Settings.AmanamuVoid.MonsterCircleScale, 2f, "Multiplier for the world circle drawn around the Amanamu rare.");

        ImGui.SeparatorText("Cloud link");
        DrawToggle("Draw monster-to-cloud line", Settings.AmanamuVoid.DrawMonsterToCloudLine, "Draw a direct screen line between the Amanamu rare and the detected void cloud.");
        DrawIntSlider("Link thickness", Settings.AmanamuVoid.LinkThickness, "Line thickness for the monster-to-cloud link.");

        ImGui.SeparatorText("Map marker");
        DrawToggle("Draw minimap / overlay marker", Settings.AmanamuVoid.DrawMapMarker, "Draw a high-contrast marker on the small minimap or large map overlay at the Amanamu rare's position.");
        DrawToggle("Draw marker label", Settings.AmanamuVoid.DrawMapMarkerLabel, "Show the marker text label next to the minimap/overlay marker.");
        DrawTextEditor("Marker label", Settings.AmanamuVoid.MapMarkerLabel);
        DrawIntSlider("Marker size", Settings.AmanamuVoid.MapMarkerSize, "Size of the Amanamu minimap/overlay marker.");
        DrawIntSlider("Marker thickness", Settings.AmanamuVoid.MapMarkerThickness, "Ring thickness for the Amanamu minimap/overlay marker.");
        DrawToggle("Draw rare-icon halo", Settings.AmanamuVoid.DrawRareIconHalo, "Draw a larger outer halo so the Amanamu marker remains visible when another plugin draws a rare monster icon on top of the center.");
        DrawIntSlider("Rare-icon halo padding", Settings.AmanamuVoid.RareIconHaloPadding, "Extra space between the normal Amanamu marker and the outer anti-overlap halo.");
        DrawIntSlider("Rare-icon halo thickness", Settings.AmanamuVoid.RareIconHaloThickness, "Thickness for the outer anti-overlap halo.");
        DrawColorEditor("Marker accent color", Settings.AmanamuVoid.MapMarkerAccentColor, "The secondary ring/crosshair color. The main ring still changes by Amanamu state.");

        ImGui.SeparatorText("Radar guide");
        DrawToggle("Draw screen radar line", Settings.AmanamuVoid.DrawRadarGuideLine, "Draw a screen-space guide line from your character toward the Amanamu rare as soon as it is detected.");
        DrawToggle("Draw minimap / overlay guide line", Settings.AmanamuVoid.DrawMapGuideLine, "Draw a guide line on the minimap or large map overlay from the map center/player marker to the Amanamu marker.");
        DrawIntSlider("Guide line thickness", Settings.AmanamuVoid.RadarGuideLineThickness, "Line thickness for the screen and map guide lines.");

        ImGui.SeparatorText("Labels and colors");
        DrawTextEditor("Inside void label", Settings.AmanamuVoid.InsideVoidLabel);
        DrawColorEditor("Inside void color", Settings.AmanamuVoid.InsideVoidColor, "Color used when the rare appears to be inside the void cloud. Default red means pull it out before killing.");
        DrawTextEditor("Outside void label", Settings.AmanamuVoid.OutsideVoidLabel);
        DrawColorEditor("Outside void color", Settings.AmanamuVoid.OutsideVoidColor, "Color used when the rare appears to be outside the void cloud. Default green means it should be safe to kill.");
        DrawTextEditor("Unknown state label", Settings.AmanamuVoid.UnknownVoidLabel);
        DrawColorEditor("Unknown state color", Settings.AmanamuVoid.UnknownVoidColor, "Color used when the Amanamu rare is detected but the cloud/inside state cannot be confirmed.");
    }

    private void DrawRitualWispSettings()
    {
        if (!ImGui.CollapsingHeader("Ritual Wisp", ImGuiTreeNodeFlags.None))
            return;

        ImGui.TextDisabled("Highlights the moving Ritual wisp bonus area so it is easier to keep during combat.");
        HelpMarker("Standing in the small wisp area during Ritual applies a bonus Tribute buff while you kill enemies. The wisp can move or teleport around the Ritual circle, and it is easy to lose under large area effects or ground damage.");
        DrawToggle("Enable Ritual Wisp overlay", Settings.RitualWisp.EnableSpecialOverlay, "Detects the RitualWisp daemon and draws a world ring around the small moving bonus Tribute area.");
        DrawIntSlider("Wisp max draw distance", Settings.RitualWisp.MaxDrawDistance, "Separate scan/draw distance for the Ritual Wisp marker. Lower values reduce clutter and scan work when you only care about nearby Ritual positioning.");
        DrawFloatSlider("Wisp ring size", Settings.RitualWisp.CircleSizeMultiplier, 5f, "Multiplier for the Ritual Wisp ring radius. The ring is intended to make the small bonus Tribute area visible through combat effects.");
        DrawIntSlider("Wisp ring thickness", Settings.RitualWisp.CircleThickness, "Line thickness for only the Ritual Wisp ring. A thicker line helps keep the moving wisp visible under area effects.");

        ImGui.SeparatorText("Guide line");
        DrawToggle("Draw character-to-wisp line", Settings.RitualWisp.DrawPlayerGuideLine, "Draws a screen-space line from your character to the Ritual Wisp so you can reacquire it after it moves or teleports.");
        DrawIntSlider("Wisp guide line thickness", Settings.RitualWisp.GuideLineThickness, "Line thickness for the character-to-wisp guide line.");

        ImGui.SeparatorText("Label and color");
        DrawTextEditor("Wisp label", Settings.RitualWisp.Label);
        HelpMarker("Default is TRIBUTE because the marker is showing where to stand for the bonus Tribute buff. Short labels such as WISP also work well if you prefer naming the object directly.");
        DrawColorEditor("Wisp color", Settings.RitualWisp.Color, "Default is white for high contrast against Ritual and ground effects.");
        ImGui.TextDisabled("Matched path: Daemon/RitualWisp");
    }

    private void DrawMonsterAffixSettings()
    {
        if (!ImGui.CollapsingHeader($"Monster Affixes ({Settings.MonsterAffixes.Count})"))
            return;

        ImGui.TextDisabled("These match ObjectMagicProperties.Mods on live hostile monsters.");
        HelpMarker("Enable individual monster affix rules here. Amanamu's Void also has a separate special overlay section above.");
        DrawSearchBox("Search monster affixes", ref _affixSearch);

        foreach (var group in Settings.MonsterAffixes
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Other" : x.Category)
                     .OrderBy(x => x.Key))
        {
            var rules = group.OrderBy(x => x.Name).ToList();
            var displayedRules = rules
                .Where(x => MatchesSearch(x.Id, x.Name, x.Category, x.Type, _affixSearch))
                .ToList();
            if (displayedRules.Count == 0)
                continue;

            var filteredSuffix = displayedRules.Count == rules.Count ? string.Empty : $", showing {displayedRules.Count}";
            if (!ImGui.TreeNode($"{group.Key} ({rules.Count(x => x.Enabled.Value)}/{rules.Count} enabled{filteredSuffix})###monster_group_{group.Key}"))
                continue;

            DrawMonsterAffixBulkButtons(group.Key, rules);

            foreach (var rule in displayedRules)
                DrawMonsterAffixRule(rule);

            ImGui.TreePop();
        }
    }

    private void DrawEffectRuleSettings()
    {
        var visibleRules = Settings.EffectRules.Where(x => !IsRitualWispRule(x)).ToList();
        if (!ImGui.CollapsingHeader($"Ground / Effect Rules ({visibleRules.Count})"))
            return;

        ImGui.TextDisabled("These draw circles around spawned ground/effect entities by path.");
        HelpMarker("Effect rules match live entity paths, not monster mods. Some effects expose a real GroundEffect radius; others use render bounds and size multipliers.");
        DrawSearchBox("Search effect rules", ref _effectSearch);

        foreach (var group in visibleRules
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Other" : x.Category)
                     .OrderBy(x => x.Key))
        {
            var rules = group.OrderBy(x => x.Name).ToList();
            var displayedRules = rules
                .Where(x => MatchesSearch(x.Id, x.Name, x.Category, string.Join(" ", x.PathContains), _effectSearch))
                .ToList();
            if (displayedRules.Count == 0)
                continue;

            var filteredSuffix = displayedRules.Count == rules.Count ? string.Empty : $", showing {displayedRules.Count}";
            if (!ImGui.TreeNode($"{group.Key} ({rules.Count(x => x.Enabled.Value)}/{rules.Count} enabled{filteredSuffix})###effect_group_{group.Key}"))
                continue;

            DrawEffectRuleBulkButtons(group.Key, rules);

            foreach (var rule in displayedRules)
                DrawEffectRule(rule);

            ImGui.TreePop();
        }
    }

    private void DrawMonsterAffixBulkButtons(string groupName, IReadOnlyList<MonsterAffixRule> rules)
    {
        ImGui.PushID($"monster_bulk_{groupName}");
        if (ImGui.SmallButton("Select All"))
        {
            foreach (var rule in rules)
                rule.Enabled.Value = true;
            RebuildAffixLookup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Deselect All"))
        {
            foreach (var rule in rules)
                rule.Enabled.Value = false;
            RebuildAffixLookup();
        }

        ImGui.PopID();
    }

    private static void DrawEffectRuleBulkButtons(string groupName, IReadOnlyList<EffectPathRule> rules)
    {
        ImGui.PushID($"effect_bulk_{groupName}");
        if (ImGui.SmallButton("Select All"))
        {
            foreach (var rule in rules)
                rule.Enabled.Value = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Deselect All"))
        {
            foreach (var rule in rules)
                rule.Enabled.Value = false;
        }

        ImGui.PopID();
    }

    private void DrawMonsterAffixRule(MonsterAffixRule rule)
    {
        var enabled = rule.Enabled.Value;
        if (ImGui.Checkbox($"{rule.Name}##affix_{rule.Id}", ref enabled))
        {
            rule.Enabled.Value = enabled;
            RebuildAffixLookup();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(rule.Id);

        if (ImGui.TreeNode($"Details##affix_details_{rule.Id}"))
        {
            DrawColorEditor("Color", rule.Color);
            DrawTextEditor("Label", rule.Label);
            if (!string.IsNullOrWhiteSpace(rule.Text))
                ImGui.TextWrapped(rule.Text);
            ImGui.TreePop();
        }
    }

    private void DrawEffectRule(EffectPathRule rule)
    {
        var enabled = rule.Enabled.Value;
        if (ImGui.Checkbox($"{rule.Name}##effect_{rule.Id}", ref enabled))
            rule.Enabled.Value = enabled;

        if (ImGui.TreeNode($"Settings##effect_settings_{rule.Id}"))
        {
            DrawColorEditor("Color", rule.Color);
            DrawTextEditor("Label", rule.Label);
            DrawFloatSlider("Size multiplier", rule.SizeMultiplier, 1f, "Per-rule multiplier for the detected effect radius. Use Set 1.00 to return to the raw detected size.");
            DrawToggle("Require GroundEffect component", rule.RequireGroundEffectComponent, "Only draw this rule when the entity exposes a real GroundEffect component. Useful for avoiding false positives from visual-only paths.");
            if (rule.MatchAnyEntityType.Value)
                ImGui.TextDisabled("Matches any entity type.");
            ImGui.TextDisabled("Path contains:");
            foreach (var path in rule.PathContains)
                ImGui.BulletText(path);
            ImGui.TreePop();
        }
    }

    private void DrawUnknownEffectSettings()
    {
        if (!Settings.Debug.CollectUnknownEffects.Value || _unknownEffects.Count == 0)
            return;

        if (!ImGui.CollapsingHeader($"Collected unknown dangerous-looking effects ({_unknownEffects.Count})"))
            return;

        DrawUnknownEffectsActions();

        foreach (var path in _unknownEffects.OrderBy(x => x).Take(100))
            ImGui.TextWrapped(path);
    }

    private void DrawUnknownEffectsActions()
    {
        ImGui.TextDisabled($"Dump file: {GetUnknownEffectsDumpPath()}");

        if (ImGui.Button("Save unknown effects now"))
            DumpUnknownEffects();

        ImGui.SameLine();
        if (ImGui.Button("Copy dump path"))
            ImGui.SetClipboardText(GetUnknownEffectsDumpPath());

        ImGui.SameLine();
        if (ImGui.Button("Open dump folder"))
            OpenUnknownEffectsFolder();
    }

    private void DrawDebugSettings()
    {
        if (!ImGui.CollapsingHeader("Debug", ImGuiTreeNodeFlags.None))
            return;

        DrawToggle("Log matched monsters", Settings.Debug.LogMatchedMonsters);
        DrawToggle("Log matched effects", Settings.Debug.LogMatchedEffects);
        DrawToggle("Log matched Abyss pits", Settings.Debug.LogMatchedAbyssPits);
        DrawToggle("Collect unknown dangerous-looking effects", Settings.Debug.CollectUnknownEffects);
        DrawToggle("Draw all nearby entity candidates", Settings.Debug.DrawAllEffectCandidates, "Diagnostic overlay for finding hidden marker paths. It scans the full cached entity list, not just effect buckets, and draws rings/short labels for nearby valid entities.");
        DrawToggle("Save all drawn entity candidates", Settings.Debug.SaveAllDrawnEffectCandidates, "When the all-entity diagnostic overlay is enabled, also save every drawn entity path to the unknown-effects dump. This can collect noise, so leave it off unless gathering data.");
        DrawIntSlider("All-entity max distance", Settings.Debug.AllEffectCandidateMaxDistance, "Maximum distance for the all-entity diagnostic overlay.");
        DrawIntSlider("All-entity label limit", Settings.Debug.AllEffectCandidateLimit, "Maximum diagnostic entity labels per scan. Lower this if the screen gets too busy.");
        DrawFloatSlider("All-entity size multiplier", Settings.Debug.AllEffectCandidateSizeMultiplier, 1f, "Circle size multiplier for diagnostic entity markers.");
        DrawColorEditor("All-entity marker color", Settings.Debug.AllEffectCandidateColor);

        DrawUnknownEffectsActions();
    }

    private void DumpAbyssMarkers()
    {
        var dumpPath = GetAbyssMarkerDumpPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            var lines = new List<string>
            {
                "# ThreatSense Abyss marker dump",
                "# Runtime MinimapIcon.Name == AbyssPitActive/AbyssPitInactive is the primary signal. Path matches below are diagnostic only.",
                "TimestampUtc\tEntityType\tGridPosition\tMinimapIcon.Name\tMinimapIcon.IsVisible\tMinimapIcon.IsHide\tIsTargetable\tChest.IsOpened\tTransitionable.Flag1\tPath"
            };

            foreach (var entity in GetCachedEntities())
            {
                try
                {
                    if (entity == null)
                        continue;

                    var path = GetEffectPath(entity);
                    var minimapIconName = GetMinimapIconName(entity);
                    if (!IsAbyssPitMinimapIcon(minimapIconName) && !IsAbyssPitPath(path))
                        continue;

                    var state = ReadAbyssPitState(entity);
                    lines.Add(string.Join("\t", new[]
                    {
                        DateTimeOffset.UtcNow.ToString("O"),
                        DumpValue(ReadEntityType(entity)),
                        DumpValue(ReadGridPosition(entity)),
                        DumpValue(state.MinimapIconName),
                        DumpValue(state.MapVisible),
                        DumpValue(state.MapHidden),
                        DumpValue(state.IsTargetable),
                        DumpValue(state.ChestOpened),
                        DumpValue(state.TransitionFlag1),
                        DumpValue(path)
                    }));
                }
                catch (Exception ex)
                {
                    lines.Add(DateTimeOffset.UtcNow.ToString("O") + "\t<entity read failed>\t" + DumpValue(ex.Message));
                }
            }

            File.WriteAllLines(dumpPath, lines);
            DebugWindow.LogMsg($"[ThreatSense] Wrote {Math.Max(0, lines.Count - 3)} Abyss marker rows to {dumpPath}", 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to write Abyss marker dump: {ex.Message}");
        }
    }

    private string GetAbyssMarkerDumpPath()
    {
        var directory = string.IsNullOrWhiteSpace(ConfigDirectory) ? DirectoryFullName : ConfigDirectory;
        return Path.Combine(directory, AbyssMarkerDumpFileName);
    }

    private static string ReadEntityType(Entity entity)
    {
        try
        {
            return entity.Type.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadGridPosition(Entity entity)
    {
        try
        {
            var grid = entity.GridPos;
            return $"{grid.X:0.##},{grid.Y:0.##}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DumpValue(object? value)
    {
        return (value?.ToString() ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private void LoadAbyssMapHistory()
    {
        var path = GetAbyssMapHistoryPath();
        if (!File.Exists(path))
            return;

        try
        {
            var file = JsonConvert.DeserializeObject<AbyssMapHistoryFile>(File.ReadAllText(path));
            _abyssMapHistory.Clear();

            foreach (var entry in file?.Entries ?? new List<AbyssMapHistoryEntry>())
            {
                if (string.IsNullOrWhiteSpace(entry.MapKey))
                    continue;

                if (string.IsNullOrWhiteSpace(entry.MapName))
                    entry.MapName = entry.MapKey;

                _abyssMapHistory[entry.MapKey] = entry;
            }

            if (_abyssMapHistory.Count > 0)
                DebugWindow.LogMsg($"[ThreatSense] Loaded {_abyssMapHistory.Count} Abyss map history rows from {path}", 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to load Abyss map history: {ex.Message}");
        }
    }

    private void SaveAbyssMapHistory(bool showLog = true)
    {
        var path = GetAbyssMapHistoryPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new AbyssMapHistoryFile
            {
                Entries = _abyssMapHistory.Values
                    .OrderBy(x => x.MapName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            _abyssMapHistoryDirty = false;

            if (showLog)
                DebugWindow.LogMsg($"[ThreatSense] Wrote {_abyssMapHistory.Count} Abyss map history rows to {path}", 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to write Abyss map history: {ex.Message}");
        }
    }

    private string GetAbyssMapHistoryPath()
    {
        var directory = string.IsNullOrWhiteSpace(ConfigDirectory) ? DirectoryFullName : ConfigDirectory;
        return Path.Combine(directory, AbyssMapHistoryFileName);
    }

    private void FlushPersistentData(bool showLog)
    {
        if (_trackedAbyssPits.Count > 0)
            UpdateAbyssMapHistory();

        if (_abyssMapHistoryDirty && _abyssMapHistory.Count > 0)
            SaveAbyssMapHistory(showLog);

        if (_unknownEffectsDirty)
            DumpUnknownEffects(showLog);
    }

    private void OpenAbyssMapHistoryFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(GetAbyssMapHistoryPath());
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            Process.Start("explorer.exe", directory);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to open Abyss map history folder: {ex.Message}");
        }
    }

    private static string FormatHistoryTime(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var timestamp))
            return string.Empty;

        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void RecordUnknownEffect(string path)
    {
        if (!_unknownEffects.Add(path))
            return;

        _unknownEffectsDirty = true;
        DebugWindow.LogMsg($"[ThreatSense] Unknown effect candidate: {path}", 5);
    }

    private void LoadUnknownEffectsDump()
    {
        var dumpPath = GetUnknownEffectsDumpPath();
        if (!File.Exists(dumpPath))
            return;

        try
        {
            foreach (var line in File.ReadAllLines(dumpPath))
            {
                var path = line.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    _unknownEffects.Add(path);
            }

            if (_unknownEffects.Count > 0)
                DebugWindow.LogMsg($"[ThreatSense] Loaded {_unknownEffects.Count} unknown effect paths from {dumpPath}", 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to load unknown effects dump: {ex.Message}");
        }
    }

    private void DumpUnknownEffects(bool showLog = true)
    {
        var dumpPath = GetUnknownEffectsDumpPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            var lines = _unknownEffects
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            File.WriteAllLines(dumpPath, lines);
            _unknownEffectsDirty = false;

            if (showLog)
                DebugWindow.LogMsg($"[ThreatSense] Wrote {lines.Length} unknown effect paths to {dumpPath}", 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to write unknown effects dump: {ex.Message}");
        }
    }

    private string GetUnknownEffectsDumpPath()
    {
        var directory = string.IsNullOrWhiteSpace(ConfigDirectory) ? DirectoryFullName : ConfigDirectory;
        return Path.Combine(directory, UnknownEffectsDumpFileName);
    }

    private void OpenUnknownEffectsFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(GetUnknownEffectsDumpPath());
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            Process.Start("explorer.exe", directory);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[ThreatSense] Failed to open unknown effects folder: {ex.Message}");
        }
    }

    private static void DrawSearchBox(string label, ref string value)
    {
        ImGui.SetNextItemWidth(360);
        ImGui.InputText(label, ref value, 256);
    }

    private static void DrawTextEditor(string label, ExileCore2.Shared.Nodes.TextNode node)
    {
        var value = node.Value ?? string.Empty;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText(label, ref value, 128))
            node.Value = value;
    }

    private static void DrawIntSlider(string label, ExileCore2.Shared.Nodes.RangeNode<int> node, string? helpText = null)
    {
        var value = node.Value;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;

        HelpMarker(helpText);
    }

    private static void DrawFloatSlider(string label, ExileCore2.Shared.Nodes.RangeNode<float> node, float? resetValue = null, string? helpText = null)
    {
        var value = node.Value;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat(label, ref value, node.Min, node.Max, "%.2f"))
            node.Value = (float)Math.Round(value, 2);

        if (resetValue != null)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Set {resetValue.Value:0.00}##{label}_{node.GetHashCode()}"))
                node.Value = resetValue.Value;
        }

        HelpMarker(helpText);
    }

    private static void DrawToggle(string label, ExileCore2.Shared.Nodes.ToggleNode node, string? helpText = null)
    {
        var value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;

        HelpMarker(helpText);
    }

    private static void DrawColorEditor(string label, ExileCore2.Shared.Nodes.ColorNode node, string? helpText = null)
    {
        var color = node.Value;
        var vector = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (ImGui.ColorEdit4(label, ref vector))
        {
            node.Value = Color.FromArgb(
                ClampByte(vector.W * 255f),
                ClampByte(vector.X * 255f),
                ClampByte(vector.Y * 255f),
                ClampByte(vector.Z * 255f));
        }

        HelpMarker(helpText);
    }

    private static void HelpMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static int ClampByte(float value)
    {
        return Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static bool MatchesSearch(string id, string name, string category, string type, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               type.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AmanamuCloud(Vector3 Position, float Radius);

    private sealed record TrackedAmanamuTarget(WarningTarget Target, long LastSeenMs);

    private sealed class AbyssMapHistoryFile
    {
        public int Version { get; set; } = 1;
        public List<AbyssMapHistoryEntry> Entries { get; set; } = new List<AbyssMapHistoryEntry>();
    }

    private sealed class AbyssMapHistoryEntry
    {
        public string MapKey { get; set; } = string.Empty;
        public string MapName { get; set; } = string.Empty;
        public string AreaId { get; set; } = string.Empty;
        public string LastRunKey { get; set; } = string.Empty;
        public int BestSeen { get; set; }
        public int BestClosed { get; set; }
        public int LastRunSeen { get; set; }
        public int LastRunClosed { get; set; }
        public int Runs { get; set; }
        public string BestSeenUtc { get; set; } = string.Empty;
        public string LastSeenUtc { get; set; } = string.Empty;
    }

    private sealed class TrackedAbyssPit
    {
        public string Key { get; init; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string MinimapIconName { get; set; } = string.Empty;
        public Vector3 Position { get; set; }
        public bool Closed { get; set; }
        public bool WasTargetable { get; set; }
        public bool WasMapVisible { get; set; }
        public bool WasTransitionActive { get; set; }
        public long FirstSeenMs { get; init; }
        public long LastSeenMs { get; set; }
        public AbyssPitState LastState { get; set; } = new AbyssPitState(null, null, null, null, null, string.Empty);
    }

    private sealed record AbyssPitState(bool? ChestOpened, bool? MapVisible, bool? MapHidden, bool? IsTargetable, byte? TransitionFlag1, string MinimapIconName);

    private enum AmanamuVoidState
    {
        Unknown,
        Inside,
        Outside
    }

    private sealed record WarningTarget(
        Entity Entity,
        Vector3 Position,
        float Radius,
        Color Color,
        string Label,
        bool IsEffect,
        Vector3? LinkPosition = null,
        bool UseAmanamuDistance = false,
        bool DrawAmanamuMapMarker = false,
        bool AllowInvalidEntity = false,
        bool UseDebugEffectDistance = false,
        bool UseRitualWispDistance = false,
        bool DrawPlayerGuideLine = false,
        int? RingThicknessOverride = null,
        int? PlayerGuideLineThicknessOverride = null);
}
