using RhinoClient.Agent;

internal static class ModelTests
{
    public static void Run()
    {
        Expect(ModelCatalog.ProviderOf("claude-opus-5-5") == ModelCatalog.Claude, "claude ids use the Claude CLI");
        Expect(ModelCatalog.ProviderOf("gpt-6.1-terra") == ModelCatalog.Codex, "other ids use the Codex CLI");
        Expect(ModelCatalog.IsValid("gpt-5.6-sol") && ModelCatalog.IsValid("claude-sonnet-5-5") && ModelCatalog.IsValid("o4-mini"), "real ids are valid");
        Expect(!ModelCatalog.IsValid("") && !ModelCatalog.IsValid("bad model") && !ModelCatalog.IsValid("--flag x") && !ModelCatalog.IsValid("a;b"), "ids that could inject arguments are refused");

        // Claude stream-json becomes the Codex-shaped events the panel already parses.
        var events = new[]
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\"}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Built the grid.\"},{\"type\":\"tool_use\",\"name\":\"x\"}]}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":5,\"output_tokens\":7}}",
        }.SelectMany(ClaudeRunner.Translate).ToList();
        Expect(events.Count == 3, "init, one text block and the result become three events");
        Expect(events[0].Contains("\"thread.started\"") && events[0].Contains("abc"), "session id becomes the thread id");
        Expect(events[1].Contains("agent_message") && events[1].Contains("Built the grid."), "text becomes an agent message");
        Expect(events[2].Contains("turn.completed") && events[2].Contains("\"input_tokens\":15") && events[2].Contains("\"output_tokens\":7"), "usage counts cached input");
        var failed = ClaudeRunner.Translate("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true,\"result\":\"too long\"}").Single();
        Expect(failed.Contains("turn.failed") && failed.Contains("too long"), "an error result fails the turn");
        var dir = Path.Combine(Path.GetTempPath(), "rhinoclient-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
            using var ok = System.Text.Json.JsonDocument.Parse($"{{\"images\":[{{\"mediaType\":\"image/png\",\"data\":\"{png}\"}}]}}");
            var saved = PromptImages.Save(ok.RootElement, dir);
            Expect(saved.Count == 1 && File.Exists(saved[0].Path) && saved[0].Path.EndsWith(".png"), "a pasted image is saved as a file");
            Expect(PromptImages.Save(System.Text.Json.JsonDocument.Parse("{}").RootElement, dir).Count == 0, "no images is fine");
            ExpectFails(() => PromptImages.Save(System.Text.Json.JsonDocument.Parse($"{{\"images\":[{{\"mediaType\":\"text/html\",\"data\":\"{png}\"}}]}}").RootElement, dir), "only image types are accepted");
            ExpectFails(() => PromptImages.Save(System.Text.Json.JsonDocument.Parse("{\"images\":[{\"mediaType\":\"image/png\",\"data\":\"!!\"}]}").RootElement, dir), "bad base64 is refused");
            var five = string.Join(",", Enumerable.Repeat($"{{\"mediaType\":\"image/png\",\"data\":\"{png}\"}}", 5));
            ExpectFails(() => PromptImages.Save(System.Text.Json.JsonDocument.Parse($"{{\"images\":[{five}]}}").RootElement, dir), "more than four images are refused");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        Console.WriteLine("PASS: model ids pick the right CLI, unsafe ids are refused, and Claude output maps to panel events.");
    }

    private static void ExpectFails(Action action, string what)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected a refusal: " + what);
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition) throw new Exception("Expected: " + what);
    }
}
