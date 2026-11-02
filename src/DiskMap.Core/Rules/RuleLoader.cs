using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DiskMap.Core.Rules;

public static partial class RuleLoader
{
    private static readonly HashSet<string> Tokens =
        ["UserProfile", "LocalAppData", "AppData", "Temp", "Downloads", "Windows", "Sid"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    [GeneratedRegex("[{]([^}]*)[}]")]
    private static partial Regex TokenRx();

    /// <exception cref="InvalidDataException">Unknown token, missing field or unknown enum value; the message names the file.</exception>
    public static IReadOnlyList<Rule> LoadAll(string dataDir)
    {
        var rules = new List<Rule>();
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            Rule? rule;
            try { rule = JsonSerializer.Deserialize<Rule>(File.ReadAllText(file), Options); }
            catch (JsonException e) { throw new InvalidDataException($"{name}: {e.Message}", e); }
            if (rule is null) throw new InvalidDataException($"{name}: empty rule file");
            foreach (var p in rule.Paths)
                foreach (Match m in TokenRx().Matches(p))
                    if (!Tokens.Contains(m.Groups[1].Value))
                        throw new InvalidDataException($"{name}: unknown token {m.Value} in path '{p}'");
            rules.Add(rule);
        }
        return rules;
    }
}
