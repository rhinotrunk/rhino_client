using System.Text.Json;
using Rhino;
using RhinoTrunk.Contracts;

namespace RhinoClient.Sync;

/// <summary>
/// Per-document RhinoTrunk checkout: server, repo, branch, base commit, and the entity hashes
/// those are based on. Runtime serial numbers stay in memory so an unchanged object is not serialized again.
/// </summary>
internal sealed class WorkingCopy
{
    internal const string Prefix = "rhinotrunk.";
    internal const string ServerKey = "rhinotrunk.server";
    internal const string RepoKey = "rhinotrunk.repo";
    internal const string BranchKey = "rhinotrunk.branch";
    internal const string BaseKey = "rhinotrunk.base";
    internal const string DefaultServer = "http://localhost:5280/";
    internal const string DefaultBranch = Names.DefaultBranch;

    private readonly Dictionary<string, BaseEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _serials = new(StringComparer.Ordinal);
    private string? _loadedFor = "\0";

    internal WorkingCopy(uint documentSerialNumber) => DocumentSerialNumber = documentSerialNumber;

    internal uint DocumentSerialNumber { get; }

    internal string Server { get; private set; } = DefaultServer;
    internal string Repo { get; private set; } = "";
    internal string Branch { get; private set; } = DefaultBranch;
    internal string? BaseCommit { get; private set; }
    internal bool HasUnsupported { get; private set; }
    internal string? UnsupportedSummary { get; private set; }
    internal bool HasEntries => _entries.Count > 0;
    internal IReadOnlyCollection<BaseEntry> Entries => _entries.Values;

    internal static string Key(string kind, string id) => kind + "\n" + id;

    internal RhinoDoc Require()
    {
        var doc = DocumentSerialNumber == 0
            ? RhinoDoc.ActiveDoc
            : RhinoDoc.FromRuntimeSerialNumber(DocumentSerialNumber);
        if (doc is null) throw new SyncException("The document is not open.");
        return doc;
    }

    /// <summary>Reads document strings and, when the base commit changed, the sidecar. Serial numbers survive repeat reads of the same base.</summary>
    internal void Read()
    {
        var doc = Require();
        Server = ValueOr(doc.Strings.GetValue(ServerKey), DefaultServer);
        Repo = doc.Strings.GetValue(RepoKey) ?? "";
        Branch = ValueOr(doc.Strings.GetValue(BranchKey), DefaultBranch);
        var basis = doc.Strings.GetValue(BaseKey);
        var commit = string.IsNullOrEmpty(basis) ? null : basis;
        if (_loadedFor == commit)
        {
            BaseCommit = commit;
            return;
        }

        BaseCommit = commit;
        _entries.Clear();
        _serials.Clear();
        HasUnsupported = false;
        UnsupportedSummary = null;
        _loadedFor = commit;
        if (commit is not null) LoadSidecar(doc);
    }

    internal void UseTarget(string server, string repo, string branch)
    {
        Server = server;
        Repo = repo;
        Branch = branch;
        var doc = Require();
        doc.Strings.SetString(ServerKey, server);
        doc.Strings.SetString(RepoKey, repo);
        doc.Strings.SetString(BranchKey, branch);
    }

    /// <summary>Forgets the base commit so the next push sends the whole document. A base commit only exists in the repository it came from.</summary>
    internal void Detach()
    {
        BaseCommit = null;
        _loadedFor = null;
        _entries.Clear();
        _serials.Clear();
        HasUnsupported = false;
        UnsupportedSummary = null;
        Require().Strings.Delete(BaseKey);
    }

    internal BaseEntry? Find(string key) => _entries.TryGetValue(key, out var entry) ? entry : null;

    internal bool SerialMatches(string id, uint serial) =>
        _serials.TryGetValue(id, out var known) && known == serial;

    /// <summary>
    /// Takes a commit's listing as the base. Entities this client does not apply (notes, other tables, block
    /// instances) are kept in the map so a push leaves them alone on the server.
    /// </summary>
    internal void Adopt(EntitiesResponse response)
    {
        _entries.Clear();
        foreach (var entity in response.Entities)
            _entries[Key(entity.Kind, entity.Id)] = BaseEntry.From(entity);
        HasUnsupported = false;
        UnsupportedSummary = null;
    }

    internal string? Replace(string commit, Dictionary<string, BaseEntry> entries, Dictionary<string, uint> serials)
    {
        _entries.Clear();
        foreach (var pair in entries) _entries.Add(pair.Key, pair.Value);
        _serials.Clear();
        foreach (var pair in serials) _serials.Add(pair.Key, pair.Value);
        BaseCommit = commit;
        _loadedFor = commit;
        HasUnsupported = false;
        UnsupportedSummary = null;
        return Save();
    }

    internal string? RememberSerials(Dictionary<string, uint> serials)
    {
        _serials.Clear();
        foreach (var pair in serials) _serials.Add(pair.Key, pair.Value);
        return Save();
    }

    private string? Save()
    {
        var doc = Require();
        doc.Strings.SetString(ServerKey, Server);
        doc.Strings.SetString(RepoKey, Repo);
        doc.Strings.SetString(BranchKey, Branch);
        if (string.IsNullOrEmpty(BaseCommit)) doc.Strings.Delete(BaseKey);
        else doc.Strings.SetString(BaseKey, BaseCommit);
        if (string.IsNullOrEmpty(doc.Path)) return null;

        try
        {
            var directory = doc.Path + ".rhinotrunk";
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "base.json");
            var temp = target + ".tmp";
            var file = new BaseFile { Format = 1, Commit = BaseCommit ?? "", Entities = _entries.Values.ToList() };
            File.WriteAllText(temp, JsonSerializer.Serialize(file, RhinoTrunkJson.Options));
            File.Move(temp, target, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private void LoadSidecar(RhinoDoc doc)
    {
        if (string.IsNullOrEmpty(doc.Path)) return;
        var path = Path.Combine(doc.Path + ".rhinotrunk", "base.json");
        if (!File.Exists(path)) return;
        try
        {
            var file = JsonSerializer.Deserialize<BaseFile>(File.ReadAllText(path), RhinoTrunkJson.Options);
            if (file is null || file.Format != 1 || !string.Equals(file.Commit, BaseCommit, StringComparison.Ordinal))
                return;
            foreach (var entry in file.Entities ?? new List<BaseEntry>())
            {
                if (string.IsNullOrEmpty(entry.Kind) || string.IsNullOrEmpty(entry.Id)) continue;
                if (string.IsNullOrEmpty(entry.Version) && string.IsNullOrEmpty(entry.Attributes)) continue;
                entry.Refs ??= new List<EntityRef>();
                _entries[Key(entry.Kind, entry.Id)] = entry;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
    }

    private static string ValueOr(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}

internal sealed class BaseEntry
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Geometry { get; set; }
    public string? Attributes { get; set; }
    public string? Subtype { get; set; }
    public List<EntityRef> Refs { get; set; } = new();

    public BaseEntry Copy() => new()
    {
        Kind = Kind,
        Id = Id,
        Version = Version,
        Geometry = Geometry,
        Attributes = Attributes,
        Subtype = Subtype,
        Refs = Refs.Select(reference => new EntityRef { Kind = reference.Kind, Id = reference.Id, Role = reference.Role }).ToList(),
    };

    /// <summary>The entity this entry describes, with a manifest rebuilt from its components and refs.</summary>
    public EntityVersion ToEntityVersion()
    {
        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Attributes is not null) components[DocumentMapper.AttributesComponent] = Attributes;
        if (Geometry is not null) components[DocumentMapper.GeometryComponent] = Geometry;
        return new EntityVersion
        {
            Kind = Kind,
            Id = Id,
            Version = Version,
            Manifest = new Manifest
            {
                Kind = Kind,
                Subtype = Subtype,
                Components = components,
                Refs = Refs.Select(reference => new EntityRef { Kind = reference.Kind, Id = reference.Id, Role = reference.Role }).ToList(),
            },
        };
    }

    public static BaseEntry From(EntityVersion entity)
    {
        var manifest = entity.Manifest ?? throw new SyncException($"{entity.Kind} {entity.Id} has no manifest.");
        manifest.Components.TryGetValue(DocumentMapper.AttributesComponent, out var attributes);
        manifest.Components.TryGetValue(DocumentMapper.GeometryComponent, out var geometry);
        return new BaseEntry
        {
            Kind = entity.Kind,
            Id = entity.Id,
            Version = entity.Version,
            Attributes = attributes,
            Geometry = geometry,
            Subtype = manifest.Subtype,
            Refs = manifest.Refs is null
            ? new List<EntityRef>()
            : manifest.Refs.Select(reference => new EntityRef { Kind = reference.Kind, Id = reference.Id, Role = reference.Role }).ToList(),
        };
    }
}

internal sealed class BaseFile
{
    public int Format { get; set; }
    public string Commit { get; set; } = "";
    public List<BaseEntry> Entities { get; set; } = new();
}
