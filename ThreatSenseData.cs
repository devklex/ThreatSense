using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Newtonsoft.Json;

namespace ThreatSense;

public sealed class MonsterAffixDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string GenerationType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool DefaultEnabled { get; set; }
}

public sealed class EffectRuleDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> PathContains { get; set; } = new List<string>();
    public string Label { get; set; } = string.Empty;
    public bool DefaultEnabled { get; set; }
    public string Color { get; set; } = "#FFFF00";
    public float SizeMultiplier { get; set; } = 1f;
    public bool RequireGroundEffectComponent { get; set; }
    public bool MatchAnyEntityType { get; set; }

    [JsonIgnore]
    public Color ParsedColor => ParseColor(Color);

    private static Color ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return System.Drawing.Color.Yellow;

        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return System.Drawing.Color.Yellow;
        }
    }
}

public static class ThreatSenseData
{
    public static IReadOnlyList<MonsterAffixDefinition> LoadMonsterAffixes(string pluginDirectory, out string message)
    {
        var path = Path.Combine(pluginDirectory, "data", "monster_affixes.json");
        try
        {
            var container = JsonConvert.DeserializeObject<MonsterAffixContainer>(File.ReadAllText(path));
            var entries = container?.Entries ?? new List<MonsterAffixDefinition>();
            message = $"Loaded {entries.Count} monster affixes.";
            return entries;
        }
        catch (Exception ex)
        {
            message = $"Failed to load monster affixes from {path}: {ex.Message}";
            return Array.Empty<MonsterAffixDefinition>();
        }
    }

    public static IReadOnlyList<EffectRuleDefinition> LoadEffectRules(string pluginDirectory, out string message)
    {
        var path = Path.Combine(pluginDirectory, "data", "effect_rules.json");
        try
        {
            var container = JsonConvert.DeserializeObject<EffectRuleContainer>(File.ReadAllText(path));
            var entries = container?.Entries ?? new List<EffectRuleDefinition>();
            message = $"Loaded {entries.Count} effect rules.";
            return entries;
        }
        catch (Exception ex)
        {
            message = $"Failed to load effect rules from {path}: {ex.Message}";
            return Array.Empty<EffectRuleDefinition>();
        }
    }

    private sealed class MonsterAffixContainer
    {
        public List<MonsterAffixDefinition> Entries { get; set; } = new List<MonsterAffixDefinition>();
    }

    private sealed class EffectRuleContainer
    {
        public List<EffectRuleDefinition> Entries { get; set; } = new List<EffectRuleDefinition>();
    }
}
