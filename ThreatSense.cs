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
using ImGuiNET;

namespace ThreatSense;

public sealed class ThreatSense : BaseSettingsPlugin<ThreatSenseSettings>
{
    private const string PluginVersion = "v0.1";
    private const string UnknownEffectsDumpFileName = "UnknownEffectsDump.txt";

    private readonly List<WarningTarget> _targets = new List<WarningTarget>();
    private readonly HashSet<string> _enabledAffixIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterAffixRule> _affixRulesById = new Dictionary<string, MonsterAffixRule>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string, bool>> _effectMatchers = new Dictionary<string, Func<string, bool>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedMonsterMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedEffectMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<AmanamuCloud> _amanamuClouds = new List<AmanamuCloud>();
    private readonly Dictionary<long, TrackedAmanamuTarget> _trackedAmanamuTargets = new Dictionary<long, TrackedAmanamuTarget>();
    private readonly HashSet<long> _amanamuTargetsSeenThisScan = new HashSet<long>();
    private IReadOnlyList<MonsterAffixDefinition> _affixDefinitions = Array.Empty<MonsterAffixDefinition>();
    private IReadOnlyList<EffectRuleDefinition> _effectDefinitions = Array.Empty<EffectRuleDefinition>();
    private string _affixSearch = string.Empty;
    private string _effectSearch = string.Empty;
    private long _lastScanMs;
    private int _lastMonsterCount;
    private int _lastEffectCount;
    private bool _unknownEffectsDirty;

    public override bool Initialise()
    {
        _affixDefinitions = ThreatSenseData.LoadMonsterAffixes(DirectoryFullName, out var affixMessage);
        _effectDefinitions = ThreatSenseData.LoadEffectRules(DirectoryFullName, out var effectMessage);
        Settings.EnsureDefaults(_affixDefinitions, _effectDefinitions);
        RebuildAffixLookup();
        LoadUnknownEffectsDump();

        Settings.ResetToBundledDefaults.OnPressed += () =>
        {
            Settings.ReplaceWithBundledDefaults(_affixDefinitions, _effectDefinitions);
            RebuildAffixLookup();
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
        if (!Settings.Enable.Value || _targets.Count == 0)
            return;

        var ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi != null)
        {
            if (Settings.HideUnderFullscreenPanels.Value && ingameUi.FullscreenPanels.Any(x => x.IsVisible))
                return;
            if (Settings.HideUnderLargePanels.Value && ingameUi.LargePanels.Any(x => x.IsVisible))
                return;
        }

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
        ImGui.TextDisabled($"Visible warnings: {_targets.Count}, monsters: {_lastMonsterCount}, effects: {_lastEffectCount}");

        if (ImGui.Button("Reset to bundled defaults"))
            Settings.ResetToBundledDefaults.OnPressed();

        DrawGeneralSettings();
        DrawAmanamuSettings();
        DrawMonsterAffixSettings();
        DrawEffectRuleSettings();
        DrawDebugSettings();
        DrawUnknownEffectSettings();
    }

    public override void AreaChange(AreaInstance area)
    {
        _targets.Clear();
        _amanamuClouds.Clear();
        _trackedAmanamuTargets.Clear();
        base.AreaChange(area);
    }

    private void ScanTargets()
    {
        _targets.Clear();
        _amanamuClouds.Clear();
        _amanamuTargetsSeenThisScan.Clear();
        _lastMonsterCount = 0;
        _lastEffectCount = 0;

        var amanamuOverlayEnabled = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value;

        if (Settings.DrawGroundEffectWarnings.Value || amanamuOverlayEnabled)
            ScanGroundEffects(Settings.DrawGroundEffectWarnings.Value);

        if (Settings.DrawMonsterAffixWarnings.Value || amanamuOverlayEnabled)
            ScanMonsterAffixes(Settings.DrawMonsterAffixWarnings.Value);

        AddTrackedAmanamuTargets();

        if (_unknownEffectsDirty)
            DumpUnknownEffects(false);
    }

    private void ScanMonsterAffixes(bool drawGenericAffixes)
    {
        var amanamuOverlayEnabled = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value;
        var scanDistance = Math.Max(Settings.MaxDrawDistance.Value, Settings.AmanamuVoid.EnableSpecialStateOverlay.Value ? Settings.AmanamuVoid.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value);
        var monsters = GetMonsterCandidates(amanamuOverlayEnabled);
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
        var scanDistance = Math.Max(Settings.MaxDrawDistance.Value, Settings.AmanamuVoid.EnableSpecialStateOverlay.Value ? Settings.AmanamuVoid.MaxDrawDistance.Value : Settings.MaxDrawDistance.Value);
        foreach (var entity in GetEffectCandidateEntities())
        {
            if (entity == null || !entity.IsValid || entity.DistancePlayer > scanDistance)
                continue;
            if (IsOwnedByPlayer(entity))
                continue;

            var path = GetEffectPath(entity);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var rule = Settings.EffectRules.FirstOrDefault(x => GetEffectMatcher(x)(path));
            if ((rule == null || !rule.Enabled.Value) && Settings.AmanamuVoid.EnableSpecialStateOverlay.Value)
                rule = Settings.EffectRules.FirstOrDefault(x => IsAmanamuEffectRule(x) && GetEffectMatcher(x)(path));

            if (rule == null)
            {
                if (Settings.Debug.CollectUnknownEffects.Value && LooksDangerousEffectPath(path))
                    RecordUnknownEffect(path);
                continue;
            }

            var allowAmanamuSpecialEffect = Settings.AmanamuVoid.EnableSpecialStateOverlay.Value && IsAmanamuEffectRule(rule);
            if (!rule.Enabled.Value && !allowAmanamuSpecialEffect)
                continue;

            if (rule.RequireGroundEffectComponent.Value && !entity.TryGetComponent<GroundEffect>(out _))
                continue;

            var target = CreateEffectTarget(entity, rule, path);
            if (IsAmanamuEffectRule(rule))
                _amanamuClouds.Add(new AmanamuCloud(target.Position, target.Radius));

            if (drawWarnings && rule.Enabled.Value && (entity.DistancePlayer <= Settings.MaxDrawDistance.Value || IsAmanamuEffectRule(rule)))
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
        var wrapper = GameController.EntityListWrapper;
        if (wrapper == null)
            return Array.Empty<Entity>();

        return GetEntities(EntityType.Effect)
            .Concat(GetEntities(EntityType.MonsterMods))
            .Concat(GetEntities(EntityType.Terrain))
            .Concat(GetEntities(EntityType.ServerObject))
            .Concat(GetEntities(EntityType.None));
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
        var radius = EstimateEffectRadius(entity, rule);
        return new WarningTarget(entity, entity.Pos, radius, rule.Color.Value, LabelFor(rule, path), true, UseAmanamuDistance: IsAmanamuEffectRule(rule));
    }

    private float EstimateEffectRadius(Entity entity, EffectPathRule rule)
    {
        float? baseRadius = null;
        if (entity.TryGetComponent<GroundEffect>(out var groundEffect))
            baseRadius = groundEffect.EffectDescription?.BaseSize;

        if (baseRadius == null && entity.TryGetComponent<Animated>(out var animated))
            baseRadius = animated.MiscAnimated?.BaseSize;

        if (baseRadius == null && entity.TryGetComponent<Render>(out var render))
            baseRadius = Math.Max(render.Bounds.X, render.Bounds.Y);

        var scale = entity.GetComponent<Positioned>()?.Scale ?? 1f;
        return Math.Max(20f, (baseRadius ?? 45f) * scale * rule.SizeMultiplier.Value * Settings.EffectCircleScale.Value);
    }

    private void DrawTarget(WarningTarget target)
    {
        if (target.DrawAmanamuMapMarker)
        {
            DrawAmanamuRadarGuideLine(target);
            DrawAmanamuMapMarker(target);
        }

        if (Settings.DrawFilledCircles.Value)
            Graphics.DrawFilledCircleInWorld(target.Position, target.Radius, Color.FromArgb(Math.Min(80, (int)target.Color.A), target.Color), 48);

        if (target.LinkPosition is { } linkPosition)
        {
            var start = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(target.Position);
            var end = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(linkPosition);
            Graphics.DrawLine(start, end, Settings.AmanamuVoid.LinkThickness.Value, target.Color);
        }

        Graphics.DrawCircleInWorld(target.Position, target.Radius, target.Color, Settings.CircleThickness.Value);

        if (!Settings.ShowLabels.Value || string.IsNullOrWhiteSpace(target.Label))
            return;

        var screenPos = RemoteMemoryObject.TheGame.IngameState.Camera.WorldToScreen(target.Position);
        var textSize = Graphics.MeasureText(target.Label, 15);
        Graphics.DrawText(target.Label, new Vector2(screenPos.X - textSize.X / 2f, screenPos.Y - target.Radius * 0.25f), target.Color);
    }

    private float GetTargetMaxDrawDistance(WarningTarget target)
    {
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
        var patterns = rule.PathContains ?? new List<string>();
        var key = string.Join("|", patterns);
        if (_effectMatchers.TryGetValue(key, out var matcher))
            return matcher;

        matcher = path => patterns.Any(part => !string.IsNullOrWhiteSpace(part) && path.Contains(part, StringComparison.OrdinalIgnoreCase));
        _effectMatchers[key] = matcher;
        return matcher;
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
               path.Contains("void", StringComparison.OrdinalIgnoreCase);
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
        DrawToggle("Draw filled circles", Settings.DrawFilledCircles);
        DrawToggle("Hide under large panels", Settings.HideUnderLargePanels, "Suppress all overlays while large in-game panels are open so warning graphics do not cover UI.");
        DrawToggle("Hide under fullscreen panels", Settings.HideUnderFullscreenPanels, "Suppress all overlays while fullscreen in-game panels are open.");
        DrawIntSlider("Scan interval ms", Settings.ScanIntervalMs, "How often the plugin rescans nearby entities. Lower values update faster but cost more CPU.");
        DrawIntSlider("Generic max draw distance", Settings.MaxDrawDistance, "Maximum distance for normal monster affix and effect warnings. Amanamu has a separate distance setting below.");
        DrawIntSlider("Circle thickness", Settings.CircleThickness, "Line thickness for world-space warning circles.");
        DrawFloatSlider("Monster circle scale", Settings.MonsterCircleScale, 1f, "Multiplier for normal monster warning circle size.");
        DrawFloatSlider("Effect circle scale", Settings.EffectCircleScale, 1f, "Global multiplier for effect warning circle size. Individual effect rules also have their own size multiplier.");
        DrawFloatSlider("Label scale", Settings.LabelScale, 1f, "Scale for warning text labels.");
    }

    private void DrawAmanamuSettings()
    {
        if (!ImGui.CollapsingHeader("Amanamu's Void / Omen of Light", ImGuiTreeNodeFlags.DefaultOpen))
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

    private void DrawMonsterAffixSettings()
    {
        if (!ImGui.CollapsingHeader($"Monster Affixes ({Settings.MonsterAffixes.Count})"))
            return;

        ImGui.TextDisabled("These match ObjectMagicProperties.Mods on live hostile monsters.");
        HelpMarker("Enable individual monster affix rules here. Amanamu's Void also has a separate special overlay section above.");
        DrawSearchBox("Search monster affixes", ref _affixSearch);

        foreach (var group in Settings.MonsterAffixes
                     .Where(x => MatchesSearch(x.Id, x.Name, x.Category, x.Type, _affixSearch))
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Other" : x.Category)
                     .OrderBy(x => x.Key))
        {
            if (!ImGui.TreeNode($"{group.Key} ({group.Count(x => x.Enabled.Value)}/{group.Count()} enabled)"))
                continue;

            foreach (var rule in group.OrderBy(x => x.Name))
                DrawMonsterAffixRule(rule);

            ImGui.TreePop();
        }
    }

    private void DrawEffectRuleSettings()
    {
        if (!ImGui.CollapsingHeader($"Ground / Effect Rules ({Settings.EffectRules.Count})"))
            return;

        ImGui.TextDisabled("These draw circles around spawned ground/effect entities by path.");
        HelpMarker("Effect rules match live entity paths, not monster mods. Some effects expose a real GroundEffect radius; others use render bounds and size multipliers.");
        DrawSearchBox("Search effect rules", ref _effectSearch);

        foreach (var rule in Settings.EffectRules
                     .Where(x => MatchesSearch(x.Id, x.Name, x.Category, string.Join(" ", x.PathContains), _effectSearch))
                     .OrderBy(x => x.Category)
                     .ThenBy(x => x.Name))
        {
            DrawEffectRule(rule);
        }
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
        DrawToggle("Collect unknown dangerous-looking effects", Settings.Debug.CollectUnknownEffects);

        DrawUnknownEffectsActions();
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

    private enum AmanamuVoidState
    {
        Unknown,
        Inside,
        Outside
    }

    private sealed record WarningTarget(Entity Entity, Vector3 Position, float Radius, Color Color, string Label, bool IsEffect, Vector3? LinkPosition = null, bool UseAmanamuDistance = false, bool DrawAmanamuMapMarker = false, bool AllowInvalidEntity = false);
}
