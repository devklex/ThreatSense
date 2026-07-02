using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace ThreatSense;

public class ThreatSenseSettings : ISettings
{
    private const int CurrentDefaultsVersion = 13;

    private static readonly HashSet<string> Version10RecommendedAffixIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MonsterAbyssMeteor",
        "PlayerMonsterAbyssMeteor",
        "MonsterBombardier1",
        "PlayerMonsterBombardier1",
        "MonsterGlacialPrison1",
        "PlayerMonsterGlacialPrison1",
        "MonsterAbyssLastGasp1",
        "PlayerMonsterAbyssLastGasp1",
        "MonsterAbyssPitSplitting",
        "PlayerMonsterAbyssPitSplitting",
        "MonsterAbyssImmuneAura1",
        "PlayerMonsterAbyssImmuneAura1",
        "MonsterImmuneAura1",
        "MonsterImmuneAura2",
        "MonsterPreventRecoveryAura1",
        "MonsterTemporalAura1",
        "MonsterProximalTangibility1",
        "PlayerMonsterProximalTangibility1"
    };

    private static readonly HashSet<string> Version11RecommendedAffixIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PlayerMonsterImmuneAura1",
        "PlayerMonsterImmuneAura2",
        "PlayerMonsterTemporalAura1",
        "PlayerMonsterTemporalAuraMinion1"
    };

    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode DrawMonsterAffixWarnings { get; set; } = new ToggleNode(true);
    public ToggleNode DrawGroundEffectWarnings { get; set; } = new ToggleNode(true);
    public ToggleNode ShowLabels { get; set; } = new ToggleNode(true);
    public ToggleNode DrawLabelBackgrounds { get; set; } = new ToggleNode(true);
    public ToggleNode DrawFilledCircles { get; set; } = new ToggleNode(false);
    public ToggleNode HideUnderLargePanels { get; set; } = new ToggleNode(true);
    public ToggleNode HideUnderFullscreenPanels { get; set; } = new ToggleNode(true);
    public RangeNode<int> ScanIntervalMs { get; set; } = new RangeNode<int>(120, 33, 1000);
    public RangeNode<int> FullEntityScanIntervalMs { get; set; } = new RangeNode<int>(250, 120, 2000);
    public RangeNode<int> MaxDrawDistance { get; set; } = new RangeNode<int>(140, 20, 300);
    public RangeNode<int> CircleThickness { get; set; } = new RangeNode<int>(4, 1, 12);
    public RangeNode<float> MonsterCircleScale { get; set; } = new RangeNode<float>(1.4f, 0.2f, 8f);
    public RangeNode<float> EffectCircleScale { get; set; } = new RangeNode<float>(1.0f, 0.2f, 10f);
    public RangeNode<float> LabelScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 2.5f);

    public ColorNode DefaultMonsterWarningColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 90, 90));
    public ColorNode DefaultGroundWarningColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 180, 0));
    public ColorNode LabelBackgroundColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(180, 0, 0, 0));

    [Menu(null, CollapsedByDefault = true)]
    public AmanamuSettings AmanamuVoid { get; set; } = new AmanamuSettings();

    [Menu(null, CollapsedByDefault = true)]
    public RitualWispSettings RitualWisp { get; set; } = new RitualWispSettings();

    [Menu(null, CollapsedByDefault = true)]
    public AbyssPitCounterSettings AbyssPitCounter { get; set; } = new AbyssPitCounterSettings();

    [Menu(null, CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new DebugSettings();

    [JsonIgnore]
    public ButtonNode ResetToBundledDefaults { get; set; } = new ButtonNode();

    public List<MonsterAffixRule> MonsterAffixes { get; set; } = new List<MonsterAffixRule>();
    public List<EffectPathRule> EffectRules { get; set; } = new List<EffectPathRule>();
    public int DefaultsVersion { get; set; }

    public void EnsureDefaults(IReadOnlyList<MonsterAffixDefinition> affixDefinitions, IReadOnlyList<EffectRuleDefinition> effectDefinitions)
    {
        AmanamuVoid ??= new AmanamuSettings();
        AmanamuVoid.EnsureDefaults();
        RitualWisp ??= new RitualWispSettings();
        RitualWisp.EnsureDefaults();
        AbyssPitCounter ??= new AbyssPitCounterSettings();
        AbyssPitCounter.EnsureDefaults();
        Debug ??= new DebugSettings();
        Debug.EnsureDefaults();

        Enable ??= new ToggleNode(false);
        DrawMonsterAffixWarnings ??= new ToggleNode(true);
        DrawGroundEffectWarnings ??= new ToggleNode(true);
        ShowLabels ??= new ToggleNode(true);
        DrawLabelBackgrounds ??= new ToggleNode(true);
        DrawFilledCircles ??= new ToggleNode(false);
        HideUnderLargePanels ??= new ToggleNode(true);
        HideUnderFullscreenPanels ??= new ToggleNode(true);
        ScanIntervalMs ??= new RangeNode<int>(120, 33, 1000);
        FullEntityScanIntervalMs ??= new RangeNode<int>(250, 120, 2000);
        MaxDrawDistance ??= new RangeNode<int>(140, 20, 300);
        CircleThickness ??= new RangeNode<int>(4, 1, 12);
        MonsterCircleScale ??= new RangeNode<float>(1.4f, 0.2f, 8f);
        EffectCircleScale ??= new RangeNode<float>(1.0f, 0.2f, 10f);
        LabelScale ??= new RangeNode<float>(1.0f, 0.5f, 2.5f);
        DefaultMonsterWarningColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 90, 90));
        DefaultGroundWarningColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 180, 0));
        LabelBackgroundColor ??= new ColorNode(System.Drawing.Color.FromArgb(180, 0, 0, 0));

        if (DefaultsVersion < 7)
        {
            AbyssPitCounter.UseTerrainFeatureTotal.Value = false;
            AbyssPitCounter.UsePathFallback.Value = false;
        }

        if (DefaultsVersion < 12)
            RitualWisp.ApplyVersion12Defaults();

        var recommendedAffixIdsToApply = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (DefaultsVersion < 10)
            recommendedAffixIdsToApply.UnionWith(Version10RecommendedAffixIds);
        if (DefaultsVersion < 11)
            recommendedAffixIdsToApply.UnionWith(Version11RecommendedAffixIds);

        MonsterAffixes = MergeMonsterAffixes(MonsterAffixes, affixDefinitions, recommendedAffixIdsToApply);
        EffectRules = MergeEffectRules(EffectRules, effectDefinitions);
        if (DefaultsVersion < 13)
            ApplyVersion13EffectSizeDefaults();

        DefaultsVersion = CurrentDefaultsVersion;
    }

    public void ReplaceWithBundledDefaults(IReadOnlyList<MonsterAffixDefinition> affixDefinitions, IReadOnlyList<EffectRuleDefinition> effectDefinitions)
    {
        MonsterAffixes = affixDefinitions.Select(MonsterAffixRule.FromDefinition).ToList();
        EffectRules = effectDefinitions.Select(EffectPathRule.FromDefinition).ToList();
        DefaultsVersion = CurrentDefaultsVersion;
    }

    private static List<MonsterAffixRule> MergeMonsterAffixes(IEnumerable<MonsterAffixRule>? existing, IReadOnlyList<MonsterAffixDefinition> definitions, IReadOnlySet<string> recommendedAffixIdsToApply)
    {
        var existingById = (existing ?? Enumerable.Empty<MonsterAffixRule>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<MonsterAffixRule>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (existingById.TryGetValue(definition.Id, out var saved))
            {
                var oldAutoLabel = MonsterAffixRule.MakeShortLabel(new MonsterAffixDefinition { Id = saved.Id, Name = saved.Name });
                saved.Name = definition.Name;
                saved.Category = definition.Category;
                saved.Type = definition.Type;
                saved.GenerationType = definition.GenerationType;
                saved.Text = definition.Text;
                saved.EnsureDefaults();
                if (!string.IsNullOrWhiteSpace(definition.Label) &&
                    (string.IsNullOrWhiteSpace(saved.Label.Value) ||
                     saved.Label.Value.Equals(oldAutoLabel, StringComparison.OrdinalIgnoreCase) ||
                     recommendedAffixIdsToApply.Contains(definition.Id)))
                {
                    saved.Label.Value = definition.Label;
                }

                if (definition.DefaultEnabled && recommendedAffixIdsToApply.Contains(definition.Id))
                {
                    saved.Enabled.Value = true;
                    saved.Color.Value = MonsterAffixRule.ColorForCategory(definition.Category);
                }

                merged.Add(saved);
            }
            else
            {
                merged.Add(MonsterAffixRule.FromDefinition(definition));
            }
        }

        return merged;
    }

    private static List<EffectPathRule> MergeEffectRules(IEnumerable<EffectPathRule>? existing, IReadOnlyList<EffectRuleDefinition> definitions)
    {
        var existingById = (existing ?? Enumerable.Empty<EffectPathRule>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<EffectPathRule>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (existingById.TryGetValue(definition.Id, out var saved))
            {
                saved.Name = definition.Name;
                saved.Category = definition.Category;
                saved.PathContains = definition.PathContains.ToList();
                saved.EnsureDefaults();

                if (!string.IsNullOrWhiteSpace(definition.Label) && string.IsNullOrWhiteSpace(saved.Label.Value))
                    saved.Label.Value = definition.Label;

                saved.MatchAnyEntityType.Value = definition.MatchAnyEntityType;
                merged.Add(saved);
            }
            else
            {
                merged.Add(EffectPathRule.FromDefinition(definition));
            }
        }

        return merged;
    }

    private void ApplyVersion13EffectSizeDefaults()
    {
        foreach (var rule in EffectRules)
        {
            rule.EnsureDefaults();
            if (!IsRitualWispRule(rule))
                rule.SizeMultiplier.Value = 1.0f;
        }
    }

    private static bool IsRitualWispRule(EffectPathRule rule)
    {
        return rule.Id.Equals("ritual_wisp_anchor", StringComparison.OrdinalIgnoreCase) ||
               rule.PathContains.Any(x => x.Contains("Daemon/RitualWisp", StringComparison.OrdinalIgnoreCase));
    }
}

public class AmanamuSettings
{
    public ToggleNode EnableSpecialStateOverlay { get; set; } = new ToggleNode(true);
    public ToggleNode DrawMonsterToCloudLine { get; set; } = new ToggleNode(true);
    public ToggleNode DrawMapMarker { get; set; } = new ToggleNode(true);
    public ToggleNode DrawMapMarkerLabel { get; set; } = new ToggleNode(true);
    public ToggleNode DrawRareIconHalo { get; set; } = new ToggleNode(true);
    public ToggleNode DrawRadarGuideLine { get; set; } = new ToggleNode(true);
    public ToggleNode DrawMapGuideLine { get; set; } = new ToggleNode(true);
    public ToggleNode PreferImmuneBuffState { get; set; } = new ToggleNode(true);
    public RangeNode<int> MaxDrawDistance { get; set; } = new RangeNode<int>(240, 20, 600);
    public RangeNode<int> KeepTrackedMarkerSeconds { get; set; } = new RangeNode<int>(20, 0, 120);
    public RangeNode<int> CloudRadiusPadding { get; set; } = new RangeNode<int>(5, 0, 80);
    public RangeNode<int> LinkThickness { get; set; } = new RangeNode<int>(3, 1, 12);
    public RangeNode<float> MonsterCircleScale { get; set; } = new RangeNode<float>(2.0f, 0.5f, 8f);
    public RangeNode<int> MapMarkerSize { get; set; } = new RangeNode<int>(20, 1, 64);
    public RangeNode<int> MapMarkerThickness { get; set; } = new RangeNode<int>(3, 1, 10);
    public RangeNode<int> RareIconHaloPadding { get; set; } = new RangeNode<int>(26, 0, 64);
    public RangeNode<int> RareIconHaloThickness { get; set; } = new RangeNode<int>(4, 1, 14);
    public RangeNode<int> RadarGuideLineThickness { get; set; } = new RangeNode<int>(3, 1, 12);
    public TextNode InsideVoidLabel { get; set; } = new TextNode("IN VOID - PULL");
    public TextNode OutsideVoidLabel { get; set; } = new TextNode("OUTSIDE - KILL");
    public TextNode UnknownVoidLabel { get; set; } = new TextNode("AMANAMU VOID");
    public TextNode MapMarkerLabel { get; set; } = new TextNode("VOID");
    public ColorNode MapMarkerAccentColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 220, 0));
    public ColorNode InsideVoidColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 40, 40));
    public ColorNode OutsideVoidColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 40, 255, 90));
    public ColorNode UnknownVoidColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 180, 90, 255));

    public void EnsureDefaults()
    {
        EnableSpecialStateOverlay ??= new ToggleNode(true);
        DrawMonsterToCloudLine ??= new ToggleNode(true);
        DrawMapMarker ??= new ToggleNode(true);
        DrawMapMarkerLabel ??= new ToggleNode(true);
        DrawRareIconHalo ??= new ToggleNode(true);
        DrawRadarGuideLine ??= new ToggleNode(true);
        DrawMapGuideLine ??= new ToggleNode(true);
        PreferImmuneBuffState ??= new ToggleNode(true);
        MaxDrawDistance ??= new RangeNode<int>(240, 20, 600);
        KeepTrackedMarkerSeconds ??= new RangeNode<int>(20, 0, 120);
        CloudRadiusPadding ??= new RangeNode<int>(5, 0, 80);
        LinkThickness ??= new RangeNode<int>(3, 1, 12);
        MonsterCircleScale ??= new RangeNode<float>(2.0f, 0.5f, 8f);
        MapMarkerSize ??= new RangeNode<int>(20, 1, 64);
        MapMarkerThickness ??= new RangeNode<int>(3, 1, 10);
        RareIconHaloPadding ??= new RangeNode<int>(26, 0, 64);
        RareIconHaloThickness ??= new RangeNode<int>(4, 1, 14);
        RadarGuideLineThickness ??= new RangeNode<int>(3, 1, 12);
        InsideVoidLabel ??= new TextNode("IN VOID - PULL");
        OutsideVoidLabel ??= new TextNode("OUTSIDE - KILL");
        UnknownVoidLabel ??= new TextNode("AMANAMU VOID");
        MapMarkerLabel ??= new TextNode("VOID");
        MapMarkerAccentColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 220, 0));
        InsideVoidColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 40, 40));
        OutsideVoidColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 40, 255, 90));
        UnknownVoidColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 180, 90, 255));
    }
}

public class RitualWispSettings
{
    public ToggleNode EnableSpecialOverlay { get; set; } = new ToggleNode(true);
    public ToggleNode DrawPlayerGuideLine { get; set; } = new ToggleNode(true);
    public RangeNode<int> MaxDrawDistance { get; set; } = new RangeNode<int>(100, 20, 500);
    public RangeNode<float> CircleSizeMultiplier { get; set; } = new RangeNode<float>(5.0f, 0.5f, 12f);
    public RangeNode<int> CircleThickness { get; set; } = new RangeNode<int>(8, 1, 20);
    public RangeNode<int> GuideLineThickness { get; set; } = new RangeNode<int>(3, 1, 12);
    public TextNode Label { get; set; } = new TextNode("TRIBUTE");
    public ColorNode Color { get; set; } = new ColorNode(System.Drawing.Color.White);

    public void EnsureDefaults()
    {
        EnableSpecialOverlay ??= new ToggleNode(true);
        DrawPlayerGuideLine ??= new ToggleNode(true);
        MaxDrawDistance ??= new RangeNode<int>(100, 20, 500);
        CircleSizeMultiplier ??= new RangeNode<float>(5.0f, 0.5f, 12f);
        CircleThickness ??= new RangeNode<int>(8, 1, 20);
        GuideLineThickness ??= new RangeNode<int>(3, 1, 12);
        Label ??= new TextNode("TRIBUTE");
        Color ??= new ColorNode(System.Drawing.Color.White);
    }

    public void ApplyVersion12Defaults()
    {
        EnsureDefaults();

        if (MaxDrawDistance.Value == 180)
            MaxDrawDistance.Value = 100;

        if (CircleThickness.Value == 6)
            CircleThickness.Value = 8;

        if (string.IsNullOrWhiteSpace(Label.Value) ||
            Label.Value.Equals("RITUAL", StringComparison.OrdinalIgnoreCase))
        {
            Label.Value = "TRIBUTE";
        }

        if (Color.Value.ToArgb() == System.Drawing.Color.FromArgb(255, 64, 224, 255).ToArgb())
            Color.Value = System.Drawing.Color.White;
    }
}

public class AbyssPitCounterSettings
{
    public static readonly string[] DefaultPathContains =
    {
        "AbyssPitFeature",
        "AbyssPit",
        "AbyssHole",
        "AbyssCircle",
        "abyss_transition"
    };

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public ToggleNode UseTerrainFeatureTotal { get; set; } = new ToggleNode(false);
    public ToggleNode UsePathFallback { get; set; } = new ToggleNode(false);
    public ToggleNode HideWhenNoPitsFound { get; set; } = new ToggleNode(true);
    public ToggleNode RecordMapHistory { get; set; } = new ToggleNode(true);
    public ToggleNode ShowMapBestInCounter { get; set; } = new ToggleNode(true);
    public ToggleNode DrawTrackedPitMarkers { get; set; } = new ToggleNode(false);
    public TextNode Label { get; set; } = new TextNode("Abyss Pits");
    public RangeNode<int> PositionX { get; set; } = new RangeNode<int>(20, 0, 3840);
    public RangeNode<int> PositionY { get; set; } = new RangeNode<int>(220, 0, 2160);
    public RangeNode<float> TextScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 2.5f);
    public RangeNode<int> HistoryRowsShown { get; set; } = new RangeNode<int>(25, 5, 200);
    public ColorNode TextColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 235, 235, 235));
    public ColorNode ClosedColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 80, 255, 120));
    public ColorNode BorderColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(220, 180, 120, 255));
    public ColorNode BackgroundColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(190, 6, 6, 10));
    public List<string> PathContains { get; set; } = DefaultPathContains.ToList();

    public void EnsureDefaults()
    {
        Enable ??= new ToggleNode(true);
        UseTerrainFeatureTotal ??= new ToggleNode(false);
        UsePathFallback ??= new ToggleNode(false);
        HideWhenNoPitsFound ??= new ToggleNode(true);
        RecordMapHistory ??= new ToggleNode(true);
        ShowMapBestInCounter ??= new ToggleNode(true);
        DrawTrackedPitMarkers ??= new ToggleNode(false);
        Label ??= new TextNode("Abyss Pits");
        PositionX ??= new RangeNode<int>(20, 0, 3840);
        PositionY ??= new RangeNode<int>(220, 0, 2160);
        TextScale ??= new RangeNode<float>(1.0f, 0.5f, 2.5f);
        HistoryRowsShown ??= new RangeNode<int>(25, 5, 200);
        TextColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 235, 235, 235));
        ClosedColor ??= new ColorNode(System.Drawing.Color.FromArgb(255, 80, 255, 120));
        BorderColor ??= new ColorNode(System.Drawing.Color.FromArgb(220, 180, 120, 255));
        BackgroundColor ??= new ColorNode(System.Drawing.Color.FromArgb(190, 6, 6, 10));

        if (PathContains == null || PathContains.Count == 0)
        {
            PathContains = DefaultPathContains.ToList();
        }
        else
        {
            foreach (var defaultPath in DefaultPathContains)
            {
                if (!PathContains.Any(x => x.Equals(defaultPath, StringComparison.OrdinalIgnoreCase)))
                    PathContains.Add(defaultPath);
            }
        }
    }

    public void ResetPathContains()
    {
        PathContains = DefaultPathContains.ToList();
    }
}

public class DebugSettings
{
    public ToggleNode LogMatchedMonsters { get; set; } = new ToggleNode(false);
    public ToggleNode LogMatchedEffects { get; set; } = new ToggleNode(false);
    public ToggleNode LogMatchedAbyssPits { get; set; } = new ToggleNode(false);
    public ToggleNode CollectUnknownEffects { get; set; } = new ToggleNode(false);
    public ToggleNode DrawAllEffectCandidates { get; set; } = new ToggleNode(false);
    public ToggleNode SaveAllDrawnEffectCandidates { get; set; } = new ToggleNode(false);
    public RangeNode<int> AllEffectCandidateMaxDistance { get; set; } = new RangeNode<int>(90, 20, 300);
    public RangeNode<int> AllEffectCandidateLimit { get; set; } = new RangeNode<int>(120, 10, 500);
    public RangeNode<float> AllEffectCandidateSizeMultiplier { get; set; } = new RangeNode<float>(1.0f, 0.1f, 5.0f);
    public ColorNode AllEffectCandidateColor { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(230, 80, 220, 255));

    public void EnsureDefaults()
    {
        LogMatchedMonsters ??= new ToggleNode(false);
        LogMatchedEffects ??= new ToggleNode(false);
        LogMatchedAbyssPits ??= new ToggleNode(false);
        CollectUnknownEffects ??= new ToggleNode(false);
        DrawAllEffectCandidates ??= new ToggleNode(false);
        SaveAllDrawnEffectCandidates ??= new ToggleNode(false);
        AllEffectCandidateMaxDistance ??= new RangeNode<int>(90, 20, 300);
        AllEffectCandidateLimit ??= new RangeNode<int>(120, 10, 500);
        AllEffectCandidateSizeMultiplier ??= new RangeNode<float>(1.0f, 0.1f, 5.0f);
        AllEffectCandidateColor ??= new ColorNode(System.Drawing.Color.FromArgb(230, 80, 220, 255));
    }
}

public class MonsterAffixRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string GenerationType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public ToggleNode Enabled { get; set; } = new ToggleNode(false);
    public ColorNode Color { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 90, 90));
    public TextNode Label { get; set; } = new TextNode(string.Empty);

    public void EnsureDefaults()
    {
        Enabled ??= new ToggleNode(false);
        Color ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 90, 90));
        Label ??= new TextNode(string.Empty);
    }

    public static MonsterAffixRule FromDefinition(MonsterAffixDefinition definition)
    {
        return new MonsterAffixRule
        {
            Id = definition.Id,
            Name = definition.Name,
            Category = definition.Category,
            Type = definition.Type,
            GenerationType = definition.GenerationType,
            Text = definition.Text,
            Enabled = new ToggleNode(definition.DefaultEnabled),
            Label = new TextNode(MakeShortLabel(definition)),
            Color = new ColorNode(ColorForCategory(definition.Category))
        };
    }

    public static string MakeShortLabel(MonsterAffixDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Label))
            return definition.Label.Trim();

        var basis = !string.IsNullOrWhiteSpace(definition.Name) ? definition.Name : definition.Id;
        var words = basis.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "DANGER" : string.Join(" ", words.Take(2)).ToUpperInvariant();
    }

    public static Color ColorForCategory(string category)
    {
        return category switch
        {
            "Death / explosion" => System.Drawing.Color.FromArgb(255, 255, 70, 30),
            "Ground effect" => System.Drawing.Color.FromArgb(255, 255, 180, 0),
            "Element / ailment" => System.Drawing.Color.FromArgb(255, 120, 210, 255),
            "Utility danger" => System.Drawing.Color.FromArgb(255, 210, 110, 255),
            _ => System.Drawing.Color.FromArgb(255, 255, 90, 90)
        };
    }
}

public class EffectPathRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> PathContains { get; set; } = new List<string>();
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);
    public ColorNode Color { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 180, 0));
    public TextNode Label { get; set; } = new TextNode(string.Empty);
    public RangeNode<float> SizeMultiplier { get; set; } = new RangeNode<float>(1f, 0.1f, 12f);
    public ToggleNode RequireGroundEffectComponent { get; set; } = new ToggleNode(false);
    public ToggleNode MatchAnyEntityType { get; set; } = new ToggleNode(false);

    public void EnsureDefaults()
    {
        PathContains ??= new List<string>();
        Enabled ??= new ToggleNode(true);
        Color ??= new ColorNode(System.Drawing.Color.FromArgb(255, 255, 180, 0));
        Label ??= new TextNode(string.Empty);
        SizeMultiplier ??= new RangeNode<float>(1f, 0.1f, 12f);
        RequireGroundEffectComponent ??= new ToggleNode(false);
        MatchAnyEntityType ??= new ToggleNode(false);
    }

    public static EffectPathRule FromDefinition(EffectRuleDefinition definition)
    {
        return new EffectPathRule
        {
            Id = definition.Id,
            Name = definition.Name,
            Category = definition.Category,
            PathContains = definition.PathContains.ToList(),
            Enabled = new ToggleNode(definition.DefaultEnabled),
            Color = new ColorNode(definition.ParsedColor),
            Label = new TextNode(definition.Label),
            SizeMultiplier = new RangeNode<float>(definition.SizeMultiplier, 0.1f, 12f),
            RequireGroundEffectComponent = new ToggleNode(definition.RequireGroundEffectComponent),
            MatchAnyEntityType = new ToggleNode(definition.MatchAnyEntityType)
        };
    }
}
