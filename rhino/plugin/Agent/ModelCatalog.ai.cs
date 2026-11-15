using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhinoClient.Agent;

internal sealed record ModelOption(string Id, string Label, string Provider, bool Available);

// Which chat models a session can use. OpenAI models run through the Codex CLI and Claude models through the
// Claude Code CLI, so both reuse the CLI's own sign-in. Nothing here holds a key or calls a provider API.
internal static partial class ModelCatalog
{
    public const string Codex = "codex";
    public const string Claude = "claude";

    private static readonly ModelOption[] ClaudeModels =
    [
        new("claude-fable-5-2", "Claude Fable 5.2", Claude, true),
        new("claude-opus-5-5", "Claude Opus 5.5", Claude, true),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", Claude, true),
        new("claude-haiku-5-5", "Claude Haiku 5.5", Claude, true),
    ];

    private static IReadOnlyList<ModelOption>? _codexModels;
    private static DateTime _codexRead;

    public static string ProviderOf(string model) =>
        model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? Claude : Codex;

    // Free text is allowed so a model the lists below miss can still be tried; it only has to look like a model id.
    public static bool IsValid(string? model) => !string.IsNullOrWhiteSpace(model) && ModelId().IsMatch(model);

    public static IReadOnlyList<ModelOption> All(ControlState settings)
    {
        var claude = ClaudeRunner.IsAvailable(settings.ClaudePath);
        var codex = CodexRunner.IsAvailable(settings.CodexPath);
        var list = new List<ModelOption>();
        list.AddRange(CodexModels().Select(model => model with { Available = codex }));
        list.AddRange(ClaudeModels.Select(model => model with { Available = claude }));
        return list;
    }

    // Codex keeps the models its account may use in ~/.codex/models_cache.json; use that rather than a fixed list.
    private static IReadOnlyList<ModelOption> CodexModels()
    {
        if (_codexModels is not null && DateTime.UtcNow - _codexRead < TimeSpan.FromMinutes(1)) return _codexModels;
        var found = new List<ModelOption>();
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var file = Path.Combine(home, "models_cache.json");
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    foreach (var model in models.EnumerateArray())
                    {
                        var slug = AgentJson.Text(model, "slug");
                        if (!IsValid(slug) || AgentJson.Text(model, "visibility", "list") != "list") continue;
                        found.Add(new ModelOption(slug, AgentJson.Text(model, "display_name", slug), Codex, true));
                    }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        if (found.Count == 0)
        {
            found.Add(new("gpt-6-luna", "GPT-6 Luna", Codex, true));
            found.Add(new("gpt-6.1-terra", "GPT-6.1 Terra", Codex, true));
        }
        _codexModels = found; _codexRead = DateTime.UtcNow;
        return found;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]{0,99}$")]
    private static partial Regex ModelId();
}
