using System.Text.Json;

namespace RhinoClient.Agent;

/// <summary>An image pasted into a prompt, saved under the session folder so the CLI can read it.</summary>
internal sealed record PromptImage(string Path, string MediaType);

internal static class PromptImages
{
    public const int MaxCount = 4;
    public const int MaxBytes = 6_000_000;

    /// <summary>Saves the <c>images</c> array of a panel command (base64 with a media type). Bad entries fail the whole command.</summary>
    public static List<PromptImage> Save(JsonElement command, string sessionDirectory)
    {
        var saved = new List<PromptImage>();
        if (!command.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return saved;
        var folder = Path.Combine(sessionDirectory, "images");
        foreach (var image in images.EnumerateArray())
        {
            if (saved.Count == MaxCount) throw new InvalidOperationException($"Attach at most {MaxCount} images to a message.");
            var type = AgentJson.Text(image, "mediaType").ToLowerInvariant();
            var extension = type switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => throw new InvalidOperationException("Only PNG, JPEG, GIF and WebP images can be attached.") };
            byte[] bytes;
            try { bytes = Convert.FromBase64String(AgentJson.Text(image, "data")); }
            catch (FormatException) { throw new InvalidOperationException("An attached image could not be read."); }
            if (bytes.Length is 0 or > MaxBytes) throw new InvalidOperationException("An attached image is empty or larger than 6 MB.");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"img-{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(path, bytes);
            saved.Add(new PromptImage(path, type));
        }
        return saved;
    }

    public static string Note(IReadOnlyCollection<PromptImage> images) =>
        images.Count == 0 ? "" : $"\n[{images.Count} image{(images.Count == 1 ? "" : "s")} attached]";
}
