using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Text;
using System.Text.Json.Nodes;

namespace RhinoClient.Sync;

/// <summary>
/// Layers two branches created separately. Rhino gives every new layer its own id, so when two branches each add a
/// layer at the same path (say both add "Seating"), the repository sees two different layers and a merge would hold
/// both under one name, which the server refuses. Before such a merge, one branch takes over the other's ids.
/// </summary>
public sealed partial class GeometrySync
{
    /// <summary>The commit that took the layers over, and their full paths.</summary>
    private sealed record AdoptedLayers(string Commit, IReadOnlyList<string> Paths);

    /// <summary>
    /// Commits on <paramref name="branch"/> (at <paramref name="head"/>) a change that gives its layers the ids of the
    /// layers at the same full path on <paramref name="from"/>: each taken-over layer is the exact version on
    /// <paramref name="from"/>, and the branch's objects, sublayers and other entities that pointed at its own copy now
    /// point at it. A merge then sees the same layer added on both sides, which is clean. Layers already shared by both
    /// sides are left alone. Returns null when no layer needs taking over.
    /// </summary>
    private async Task<AdoptedLayers?> AdoptLayersAsync(
        RhinoTrunkClient client, string repo, string branch, string head, string from, string fromLabel, CancellationToken cancellationToken)
    {
        var ours = await client.ListEntitiesAsync(repo, head, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
        var theirs = await client.ListEntitiesAsync(repo, from, doc: Names.DefaultDoc, kind: EntityKinds.Layer, includeManifests: true, ct: cancellationToken)
            .ConfigureAwait(false);
        var ourLayers = ours.Entities.Where(e => e.Kind == EntityKinds.Layer).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var theirLayers = theirs.Entities.Where(e => e.Kind == EntityKinds.Layer).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var names = await LayerNamesAsync(client, repo, ourLayers.Values.Concat(theirLayers.Values), cancellationToken).ConfigureAwait(false);
        var ourPaths = LayerPaths(ourLayers, names);
        var theirPaths = LayerPaths(theirLayers, names);

        // Only layers each side has on its own; a path used by more than one such layer is ambiguous and left alone.
        var theirsByPath = theirPaths
            .Where(pair => !ourLayers.ContainsKey(pair.Key))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key, StringComparer.Ordinal);
        var ourByPath = ourPaths
            .Where(pair => !theirLayers.ContainsKey(pair.Key))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key, StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // our id -> their id
        foreach (var (path, ourId) in ourByPath)
            if (theirsByPath.TryGetValue(path, out var theirId)) map[ourId] = theirId;

        // A taken-over layer keeps its parent from the other side, so that parent must exist here once the commit lands:
        // a layer this branch keeps, or another layer taken over.
        for (var dropped = true; dropped;)
        {
            dropped = false;
            foreach (var (ourId, theirId) in map.ToList())
            {
                var parent = ParentLayer(theirLayers[theirId].Manifest);
                if (parent is null) continue;
                var exists = (ourLayers.ContainsKey(parent) && !map.ContainsKey(parent)) || map.ContainsValue(parent);
                if (exists) continue;
                map.Remove(ourId);
                dropped = true;
            }
        }

        if (map.Count == 0) return null;

        var changes = new List<Change>();
        foreach (var (ourId, theirId) in map)
        {
            changes.Add(new Change { Op = ChangeOps.Delete, Kind = EntityKinds.Layer, Id = ourId });
            changes.Add(new Change { Op = ChangeOps.Put, Kind = EntityKinds.Layer, Id = theirId, Version = theirLayers[theirId].Version });
        }

        foreach (var entity in ours.Entities)
        {
            if (entity.Manifest is not { } manifest) continue;
            if (entity.Kind == EntityKinds.Layer && map.ContainsKey(entity.Id)) continue;
            if (!manifest.Refs.Any(reference => reference.Kind == EntityKinds.Layer && map.ContainsKey(reference.Id))) continue;
            changes.Add(new Change
            {
                Op = ChangeOps.Put,
                Kind = entity.Kind,
                Id = entity.Id,
                Manifest = new ManifestInput
                {
                    Kind = manifest.Kind,
                    Subtype = manifest.Subtype,
                    Components = new Dictionary<string, string>(manifest.Components, StringComparer.Ordinal),
                    Refs = manifest.Refs.Select(reference => reference.Kind == EntityKinds.Layer && map.TryGetValue(reference.Id, out var to)
                        ? reference with { Id = to }
                        : reference).ToList(),
                },
            });
        }

        var paths = map.Keys.Select(id => ourPaths[id]).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var response = await client.CommitAsync(repo, new CommitRequest
        {
            Branch = branch,
            ExpectedHead = head,
            Message = $"Use the layers from {fromLabel}: {string.Join(", ", paths.Take(6))}{(paths.Count > 6 ? $" and {paths.Count - 6} more" : "")}",
            Author = Author(),
            Changes = changes,
        }, cancellationToken).ConfigureAwait(false);
        return new AdoptedLayers(response.Commit, paths);
    }

    private static string? ParentLayer(Manifest? manifest) =>
        manifest?.Refs.FirstOrDefault(reference => reference.Role == RefRoles.Parent && reference.Kind == EntityKinds.Layer)?.Id;

    /// <summary>Layer names by id, read from each layer's attributes blob.</summary>
    private static async Task<Dictionary<string, string>> LayerNamesAsync(
        RhinoTrunkClient client, string repo, IEnumerable<EntityVersion> layers, CancellationToken cancellationToken)
    {
        var hashes = layers
            .Select(layer => (layer.Id, Hash: layer.Manifest?.Components.GetValueOrDefault("attributes")))
            .Where(item => item.Hash is not null)
            .ToList();
        var blobs = await DownloadAsync(client, repo, hashes.Select(item => item.Hash!).Distinct(StringComparer.Ordinal).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, hash) in hashes)
        {
            if (!blobs.TryGetValue(hash!, out var bytes)) continue;
            try
            {
                if (JsonNode.Parse(Encoding.UTF8.GetString(bytes)) is JsonObject attributes && attributes["name"]?.GetValue<string>() is { } name)
                    names[id] = name;
            }
            catch (Exception)
            {
                // An unreadable name leaves the layer out of the matching.
            }
        }

        return names;
    }

    /// <summary>Full paths ("Parent::Child") of the named layers whose ancestors are all named and present.</summary>
    private static Dictionary<string, string> LayerPaths(IReadOnlyDictionary<string, EntityVersion> layers, IReadOnlyDictionary<string, string> names)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in layers.Keys)
        {
            List<string>? parts = new();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = id;
            while (current is not null)
            {
                if (!seen.Add(current) || !layers.TryGetValue(current, out var layer) || !names.TryGetValue(current, out var name))
                {
                    parts = null;
                    break;
                }

                parts.Add(name);
                current = ParentLayer(layer.Manifest);
            }

            if (parts is not null)
            {
                parts.Reverse();
                paths[id] = string.Join("::", parts);
            }
        }

        return paths;
    }
}
