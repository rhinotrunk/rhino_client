using Rhino;
using Rhino.DocObjects;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RhinoClient.Sync;

/// <summary>
/// Materials, linetypes and groups, and the references objects and layers make to them.
/// Materials and linetypes keep their Rhino ids. Rhino assigns group ids itself, so a group's entity id is derived
/// from its name and stays the same in every document (renaming a group is a delete and an add).
/// Values that are at their default are left out of attributes, so documents pushed by earlier versions of this
/// client do not show every layer and object as changed.
/// </summary>
internal static partial class DocumentMapper
{
    internal static readonly string[] TableKinds = [EntityKinds.Material, EntityKinds.Linetype, EntityKinds.Group];

    internal static bool IsTableKind(string kind) => TableKinds.Contains(kind);

    /// <summary>A stable entity id for a table entry Rhino does not let us give an id (groups).</summary>
    internal static string NameId(string kind, string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + name));
        return new Guid(digest.AsSpan(0, 16)).ToString("D");
    }

    private static void CaptureTables(RhinoDoc doc, WorkingCopy copy, CapturedPush captured)
    {
        foreach (var material in doc.Materials)
        {
            if (material.IsDeleted || material.IsReference || material.Index < 0) continue;
            var attributes = MaterialAttributes(material);
            Consider(captured, copy, EntityKinds.Material, material.Id.ToString("D"), null, null, null, HashAttributes(attributes), attributes, new List<EntityRef>());
        }

        foreach (var linetype in doc.Linetypes)
        {
            if (linetype.IsDeleted || linetype.IsReference || linetype.Index < 0) continue;
            var attributes = LinetypeAttributes(linetype);
            Consider(captured, copy, EntityKinds.Linetype, linetype.Id.ToString("D"), null, null, null, HashAttributes(attributes), attributes, new List<EntityRef>());
        }

        foreach (var group in doc.Groups)
        {
            if (group.IsDeleted || string.IsNullOrEmpty(group.Name)) continue;
            var attributes = new JsonObject { ["name"] = group.Name };
            Consider(captured, copy, EntityKinds.Group, NameId(EntityKinds.Group, group.Name), null, null, null, HashAttributes(attributes), attributes, new List<EntityRef>());
        }
    }

    private static JsonObject MaterialAttributes(Material material)
    {
        var attributes = new JsonObject
        {
            ["name"] = material.Name ?? "",
            ["diffuse"] = ToHex(material.DiffuseColor),
            ["specular"] = ToHex(material.SpecularColor),
            ["ambient"] = ToHex(material.AmbientColor),
            ["emission"] = ToHex(material.EmissionColor),
            ["transparency"] = material.Transparency,
            ["shine"] = material.Shine,
            ["reflectivity"] = material.Reflectivity,
            ["indexOfRefraction"] = material.IndexOfRefraction,
        };
        return attributes;
    }

    private static JsonObject LinetypeAttributes(Linetype linetype)
    {
        var segments = new JsonArray();
        for (var i = 0; i < linetype.SegmentCount; i++)
        {
            linetype.GetSegment(i, out var length, out var solid);
            segments.Add(new JsonArray(length, solid));
        }

        return new JsonObject { ["name"] = linetype.Name ?? "", ["segments"] = segments };
    }

    /// <summary>Layer values beyond name, colour, visibility and lock; only non-defaults are written.</summary>
    private static void AddLayerExtras(Layer layer, JsonObject attributes)
    {
        if (layer.PlotColor.ToArgb() != System.Drawing.Color.Black.ToArgb()) attributes["printColor"] = ToHex(layer.PlotColor);
        if (Math.Abs(layer.PlotWeight) > 1e-12) attributes["printWidth"] = layer.PlotWeight;
    }

    private static void AddLayerTableRefs(RhinoDoc doc, Layer layer, List<EntityRef> refs)
    {
        if (layer.LinetypeIndex >= 0 && layer.LinetypeIndex < doc.Linetypes.Count && doc.Linetypes[layer.LinetypeIndex] is { IsDeleted: false } linetype)
            refs.Add(Changes.Ref(EntityKinds.Linetype, linetype.Id.ToString("D"), RefRoles.Linetype));
        if (layer.RenderMaterialIndex >= 0 && layer.RenderMaterialIndex < doc.Materials.Count && doc.Materials[layer.RenderMaterialIndex] is { IsDeleted: false } material)
            refs.Add(Changes.Ref(EntityKinds.Material, material.Id.ToString("D"), RefRoles.Material));
    }

    /// <summary>Object values beyond the original set; only non-defaults are written.</summary>
    private static void AddObjectExtras(ObjectAttributes attributes, JsonObject json)
    {
        if (attributes.PlotColorSource == ObjectPlotColorSource.PlotColorFromObject) json["printColor"] = ToHex(attributes.PlotColor);
        if (attributes.PlotWeightSource == ObjectPlotWeightSource.PlotWeightFromObject) json["printWidth"] = attributes.PlotWeight;
    }

    private static void AddObjectTableRefs(RhinoDoc doc, ObjectAttributes attributes, List<EntityRef> refs)
    {
        if (attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject && attributes.MaterialIndex >= 0
            && attributes.MaterialIndex < doc.Materials.Count && doc.Materials[attributes.MaterialIndex] is { IsDeleted: false } material)
            refs.Add(Changes.Ref(EntityKinds.Material, material.Id.ToString("D"), RefRoles.Material));
        if (attributes.LinetypeSource == ObjectLinetypeSource.LinetypeFromObject && attributes.LinetypeIndex >= 0
            && attributes.LinetypeIndex < doc.Linetypes.Count && doc.Linetypes[attributes.LinetypeIndex] is { IsDeleted: false } linetype)
            refs.Add(Changes.Ref(EntityKinds.Linetype, linetype.Id.ToString("D"), RefRoles.Linetype));
        foreach (var index in attributes.GetGroupList() ?? Array.Empty<int>())
        {
            if (doc.Groups.FindIndex(index) is { IsDeleted: false, Name: { Length: > 0 } name })
                refs.Add(Changes.Ref(EntityKinds.Group, NameId(EntityKinds.Group, name), RefRoles.Group));
        }
    }

    // =========================================================================================
    // Pull
    // =========================================================================================

    private static void UpsertTable(RhinoDoc doc, EntityVersion entity, JsonObject attributes)
    {
        var id = ParseGuid(entity.Id, entity.Kind);
        switch (entity.Kind)
        {
            case EntityKinds.Material:
            {
                var index = MaterialIndex(doc, id, includeDeleted: false);
                var material = index >= 0 ? doc.Materials[index] : new Material { Id = id };
                material.Name = ReadString(attributes, "name");
                material.DiffuseColor = ParseColor(ReadString(attributes, "diffuse", "#ffffff"));
                material.SpecularColor = ParseColor(ReadString(attributes, "specular", "#ffffff"));
                material.AmbientColor = ParseColor(ReadString(attributes, "ambient", "#000000"));
                material.EmissionColor = ParseColor(ReadString(attributes, "emission", "#000000"));
                material.Transparency = ReadDouble(attributes, "transparency", 0);
                material.Shine = ReadDouble(attributes, "shine", 0);
                material.Reflectivity = ReadDouble(attributes, "reflectivity", 0);
                material.IndexOfRefraction = ReadDouble(attributes, "indexOfRefraction", 1);
                if (index >= 0 ? !doc.Materials.Modify(material, index, true) : doc.Materials.Add(material) < 0)
                    throw new SyncException($"Could not update material '{material.Name}'.");
                break;
            }
            case EntityKinds.Linetype:
            {
                var index = LinetypeIndex(doc, id);
                var linetype = index >= 0 ? doc.Linetypes[index] : new Linetype { Id = id };
                linetype.Name = ReadString(attributes, "name");
                while (linetype.SegmentCount > 0) linetype.RemoveSegment(linetype.SegmentCount - 1);
                if (attributes["segments"] is JsonArray segments)
                {
                    foreach (var segment in segments.OfType<JsonArray>())
                        linetype.AppendSegment(segment[0]?.GetValue<double>() ?? 0, segment[1]?.GetValue<bool>() ?? true);
                }

                if (index >= 0 ? !doc.Linetypes.Modify(linetype, index, true) : doc.Linetypes.Add(linetype) < 0)
                    throw new SyncException($"Could not update linetype '{linetype.Name}'.");
                break;
            }
            case EntityKinds.Group:
            {
                var name = ReadString(attributes, "name");
                if (doc.Groups.Find(name) < 0 && doc.Groups.Add(name) < 0)
                    throw new SyncException($"Could not add group '{name}'.");
                break;
            }
        }
    }

    private static void DeleteTable(RhinoDoc doc, string kind, string id)
    {
        var guid = ParseGuid(id, kind);
        switch (kind)
        {
            case EntityKinds.Material:
                var material = MaterialIndex(doc, guid, includeDeleted: false);
                if (material >= 0) doc.Materials.DeleteAt(material);
                break;
            case EntityKinds.Linetype:
                var linetype = LinetypeIndex(doc, guid);
                if (linetype >= 0) doc.Linetypes.Delete(linetype, true);
                break;
            case EntityKinds.Group:
                var group = GroupIndex(doc, id);
                if (group >= 0) doc.Groups.Delete(group);
                break;
        }
    }

    private static int MaterialIndex(RhinoDoc doc, Guid id, bool includeDeleted)
    {
        for (var i = 0; i < doc.Materials.Count; i++)
            if (doc.Materials[i].Id == id && (includeDeleted || !doc.Materials[i].IsDeleted)) return i;
        return -1;
    }

    private static int LinetypeIndex(RhinoDoc doc, Guid id)
    {
        for (var i = 0; i < doc.Linetypes.Count; i++)
            if (doc.Linetypes[i].Id == id && !doc.Linetypes[i].IsDeleted) return i;
        return -1;
    }

    private static int GroupIndex(RhinoDoc doc, string entityId)
    {
        foreach (var group in doc.Groups)
            if (!group.IsDeleted && !string.IsNullOrEmpty(group.Name) && NameId(EntityKinds.Group, group.Name) == entityId) return group.Index;
        return -1;
    }

    /// <summary>Applies an object's or layer's table references and extra values to its Rhino attributes.</summary>
    private static void ApplyObjectExtras(RhinoDoc doc, ObjectAttributes attributes, JsonObject json, IReadOnlyList<EntityRef> refs)
    {
        if (json["printColor"] is not null) attributes.PlotColor = ParseColor(ReadString(json, "printColor", "#000000"));
        if (json["printWidth"] is not null) attributes.PlotWeight = ReadDouble(json, "printWidth", 0);
        foreach (var reference in refs)
        {
            if (reference.Kind == EntityKinds.Material && reference.Role == RefRoles.Material)
                attributes.MaterialIndex = MaterialIndex(doc, ParseGuid(reference.Id, "material"), includeDeleted: false);
            else if (reference.Kind == EntityKinds.Linetype && reference.Role == RefRoles.Linetype)
                attributes.LinetypeIndex = LinetypeIndex(doc, ParseGuid(reference.Id, "linetype"));
            else if (reference.Kind == EntityKinds.Group && reference.Role == RefRoles.Group && GroupIndex(doc, reference.Id) is var group and >= 0)
                attributes.AddToGroup(group);
        }
    }

    private static void ApplyLayerExtras(RhinoDoc doc, Layer layer, JsonObject json, IReadOnlyList<EntityRef> refs)
    {
        layer.PlotColor = ParseColor(ReadString(json, "printColor", "#000000"));
        layer.PlotWeight = ReadDouble(json, "printWidth", 0);
        var linetype = refs.FirstOrDefault(r => r.Kind == EntityKinds.Linetype && r.Role == RefRoles.Linetype);
        layer.LinetypeIndex = linetype is null ? -1 : LinetypeIndex(doc, ParseGuid(linetype.Id, "linetype"));
        var material = refs.FirstOrDefault(r => r.Kind == EntityKinds.Material && r.Role == RefRoles.Material);
        layer.RenderMaterialIndex = material is null ? -1 : MaterialIndex(doc, ParseGuid(material.Id, "material"), includeDeleted: false);
    }

    private static double ReadDouble(JsonObject attributes, string name, double fallback) =>
        TryNumber(attributes[name], out var number) ? number : fallback;
}
