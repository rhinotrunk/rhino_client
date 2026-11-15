using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Collections.Specialized;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ContentHash = RhinoTrunk.Contracts.ContentHash;

namespace RhinoClient.Sync;

/// <summary>
/// Maps a Rhino document to settings, layers, and model-space objects. Other tables and block instances are skipped.
/// </summary>
internal static class DocumentMapper
{
    internal const string AttributesComponent = "attributes";
    internal const string GeometryComponent = "geometry";

    internal static bool IsSupported(EntityVersion entity)
    {
        if (entity.Kind is EntityKinds.Settings or EntityKinds.Layer) return true;
        if (entity.Kind != EntityKinds.Object) return false;
        return entity.Manifest?.Subtype is not "InstanceReferenceGeometry";
    }

    internal static int ModelObjectCount(RhinoDoc doc) =>
        doc.Objects.GetObjectList(ModelSettings()).Count(obj => obj.Attributes.Space == ActiveSpace.ModelSpace);

    internal static CapturedPush Capture(RhinoDoc doc, WorkingCopy copy)
    {
        var captured = new CapturedPush();
        var settings = SettingsAttributes(doc);
        Consider(captured, copy, EntityKinds.Settings, Names.SettingsId, null, null, null,
            HashAttributes(settings), settings, new List<EntityRef>());

        var layers = LiveLayers(doc);
        if (layers.Count == 0) throw new SyncException("The document has no layers to push.");
        foreach (var layer in layers.Values)
        {
            if (layer.ParentLayerId != Guid.Empty && !layers.ContainsKey(layer.ParentLayerId))
                throw new SyncException($"Layer '{layer.Name}' has a parent this client is not pushing.");
            var name = layer.Name ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Contains("::", StringComparison.Ordinal))
                throw new SyncException($"Layer {layer.Id:D} has a missing or invalid name.");
        }

        foreach (var layer in ParentsFirst(layers.Values))
        {
            var attributes = LayerAttributes(layer);
            Consider(captured, copy, EntityKinds.Layer, layer.Id.ToString("D"), null, null, null,
                HashAttributes(attributes), attributes, LayerRefs(layer));
        }

        foreach (var obj in doc.Objects.GetObjectList(ModelSettings()))
        {
            var id = obj.Id.ToString("D");
            if (ShouldSkip(obj))
            {
                captured.Skipped++;
                KeepKnown(captured, copy, id);
                continue;
            }

            var layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
            if (layer is null || layer.IsDeleted || layer.IsReference || !layers.ContainsKey(layer.Id))
                throw new SyncException($"Object {id} is on a layer this client is not pushing.");
            var geometry = obj.Geometry ?? throw new SyncException($"Object {id} has no geometry.");
            var existing = copy.Find(WorkingCopy.Key(EntityKinds.Object, id));
            byte[]? geometryBytes = null;
            string geometryHash;
            if (copy.SerialMatches(id, obj.RuntimeSerialNumber) && existing?.Geometry is { } cached)
            {
                geometryHash = cached;
            }
            else
            {
                var serialized = SerializeGeometry(geometry);
                geometryHash = serialized.Hash;
                geometryBytes = serialized.Bytes;
            }

            var attributes = ObjectAttributesJson(obj);
            var refs = new List<EntityRef> { Changes.Ref(EntityKinds.Layer, layer.Id.ToString("D"), RefRoles.Layer) };
            captured.Serials[id] = obj.RuntimeSerialNumber;
            Consider(captured, copy, EntityKinds.Object, id, geometry.GetType().Name, geometryHash, geometryBytes,
                HashAttributes(attributes), attributes, refs);
        }

        foreach (var entry in copy.Entries)
        {
            if (entry.Kind is not (EntityKinds.Layer or EntityKinds.Object)) continue;
            if (!captured.Entries.ContainsKey(WorkingCopy.Key(entry.Kind, entry.Id)))
                captured.Changes.Add(Changes.Delete(entry.Kind, entry.Id));
        }

        return captured;
    }

    internal static void RejectUnsupported(IReadOnlyList<EntityVersion> entities)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var settings = 0;
        var layers = 0;
        foreach (var entity in entities)
        {
            if (!IsSupported(entity))
            {
                var label = entity.Kind == EntityKinds.Object && entity.Manifest?.Subtype is { Length: > 0 } subtype
                    ? entity.Kind + ":" + subtype
                    : entity.Kind;
                counts[label] = counts.TryGetValue(label, out var count) ? count + 1 : 1;
                continue;
            }

            if (entity.Manifest is null)
                throw new SyncException($"{entity.Kind} {entity.Id} did not include a manifest.");
            if (entity.Kind == EntityKinds.Settings) settings++;
            if (entity.Kind == EntityKinds.Layer) layers++;
            if (entity.Kind == EntityKinds.Object && !entity.Manifest.Components.ContainsKey(GeometryComponent))
                throw new SyncException($"Object {entity.Id} has no geometry.");
        }

        if (counts.Count > 0)
            throw new SyncException("This commit has entities this client cannot apply yet: " +
                string.Join(", ", counts.Select(pair => pair.Value + " " + pair.Key)) + ".");
        if (settings != 1) throw new SyncException("The commit must contain exactly one settings entity.");
        if (layers < 1) throw new SyncException("The commit has no layers.");
    }

    internal static List<DecodedObject> DecodeObjects(IReadOnlyList<EntityVersion> entities, IReadOnlyDictionary<string, byte[]> blobs)
    {
        var decoded = new List<DecodedObject>();
        try
        {
            foreach (var entity in entities)
            {
                if (entity.Kind != EntityKinds.Object) continue;
                var attributes = RequireAttributes(entity, blobs);
                var space = ReadString(attributes, "space", "model");
                if (!string.Equals(space, "model", StringComparison.Ordinal))
                    throw new SyncException($"Object {entity.Id} is in {space} space. This client only pulls model geometry.");
                decoded.Add(new DecodedObject(entity, attributes, DecodeGeometry(entity, blobs)));
            }

            return decoded;
        }
        catch
        {
            foreach (var item in decoded) item.Geometry.Dispose();
            throw;
        }
    }

    /// <param name="replace">
    /// Make the model match the commit: overwrite objects that differ without a review and delete model
    /// objects the commit does not list. Objects this client does not sync, such as block instances, are kept.
    /// Unpushed edits are lost, so only pass it once they have been pushed or the user chose to discard them.
    /// </param>
    internal static Dictionary<string, uint> Apply(
        RhinoDoc doc,
        IReadOnlyList<EntityVersion> entities,
        IReadOnlyDictionary<string, byte[]> blobs,
        IReadOnlyList<DecodedObject> objects,
        IReadOnlyDictionary<string, ConflictChoice> resolutions,
        bool replace = false)
    {
        var settings = entities.Single(entity => entity.Kind == EntityKinds.Settings);
        var layers = ParentsFirst(entities.Where(entity => entity.Kind == EntityKinds.Layer).ToList());
        var settingsAttributes = RequireAttributes(settings, blobs);
        var layerAttributes = layers.ToDictionary(layer => layer.Id, layer => RequireAttributes(layer, blobs), StringComparer.Ordinal);
        return ApplyPrepared(doc, settingsAttributes, layers, layerAttributes, objects, resolutions, replace);
    }

    private static Dictionary<string, uint> ApplyPrepared(
        RhinoDoc doc,
        JsonObject settingsAttributes,
        IReadOnlyList<EntityVersion> layers,
        Dictionary<string, JsonObject> layerAttributes,
        IReadOnlyList<DecodedObject> objects,
        IReadOnlyDictionary<string, ConflictChoice> resolutions,
        bool replace)
    {
        var redraw = doc.Views.RedrawEnabled;
        doc.Views.RedrawEnabled = false;
        var undo = doc.BeginUndoRecord(replace ? "RhinoTrunk checkout" : "RhinoTrunk pull");
        try
        {
            ApplySettings(doc, settingsAttributes);
            var keep = new HashSet<Guid>();
            foreach (var layer in layers)
            {
                var id = ParseGuid(layer.Id, "layer");
                keep.Add(id);
                UpsertLayer(doc, id, ParentGuid(layer), layerAttributes[layer.Id]);
            }

            if (replace) DeleteUnlistedObjects(doc, objects);
            var deleted = DeletedModelObjects(doc);
            var serials = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var item in objects)
            {
                ConflictChoice? choice = resolutions.TryGetValue(item.Entity.Id, out var selected) ? selected : null;
                PlaceObject(doc, item, choice, replace, deleted, serials);
            }

            // Rhino will not delete a layer that still holds objects, so this runs after objects move to the commit's layers.
            EnsureCurrent(doc, keep);
            DeleteExtraLayers(doc, keep);
            if (undo != 0) doc.EndUndoRecord(undo);
            return serials;
        }
        catch (Exception failure)
        {
            if (undo != 0)
            {
                try
                {
                    doc.EndUndoRecord(undo);
                    doc.Undo();
                }
                catch (Exception undoFailure)
                {
                    throw new SyncException($"{failure.Message} Undo failed: {undoFailure.Message}", failure);
                }
            }

            throw;
        }
        finally
        {
            doc.Views.RedrawEnabled = redraw;
            doc.Views.Redraw();
        }
    }

    private static void Consider(
        CapturedPush captured,
        WorkingCopy copy,
        string kind,
        string id,
        string? subtype,
        string? geometry,
        byte[]? geometryBytes,
        string attributesHash,
        JsonObject attributes,
        List<EntityRef> refs)
    {
        var key = WorkingCopy.Key(kind, id);
        var existing = copy.Find(key);
        if (existing is not null && Matches(existing, subtype, geometry, attributesHash, refs))
        {
            captured.Entries[key] = existing.Copy();
            return;
        }

        if (geometryBytes is not null && geometry is not null)
            captured.Blobs[geometry] = geometryBytes;
        captured.Entries[key] = new BaseEntry
        {
            Kind = kind,
            Id = id,
            Geometry = geometry,
            Attributes = attributesHash,
            Subtype = subtype,
            Refs = refs.Select(reference => new EntityRef { Kind = reference.Kind, Id = reference.Id, Role = reference.Role }).ToList(),
        };
        captured.Changes.Add(Changes.Put(kind, id, new ManifestInput
        {
            Subtype = subtype,
            Attributes = attributes,
            Refs = refs.Count == 0 ? null : refs,
            Components = geometry is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { [GeometryComponent] = geometry },
        }));
    }

    private static void KeepKnown(CapturedPush captured, WorkingCopy copy, string id)
    {
        var key = WorkingCopy.Key(EntityKinds.Object, id);
        if (copy.Find(key) is { } existing) captured.Entries[key] = existing.Copy();
    }

    private static bool Matches(BaseEntry entry, string? subtype, string? geometry, string attributes, List<EntityRef> refs) =>
        entry.Subtype == subtype && entry.Geometry == geometry && entry.Attributes == attributes && SameRefs(entry.Refs, refs);

    private static bool SameRefs(List<EntityRef> left, List<EntityRef> right)
    {
        if (left.Count != right.Count) return false;
        if (left.Count == 0) return true;
        var a = left.Select(RefText).OrderBy(text => text, StringComparer.Ordinal).ToArray();
        var b = right.Select(RefText).OrderBy(text => text, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string RefText(EntityRef reference) => reference.Kind + "\0" + reference.Id + "\0" + reference.Role;

    private static JsonObject SettingsAttributes(RhinoDoc doc) => new()
    {
        ["unitSystem"] = doc.ModelUnitSystem.ToString(),
        ["absoluteTolerance"] = doc.ModelAbsoluteTolerance,
        ["angleToleranceRadians"] = doc.ModelAngleToleranceRadians,
        ["relativeTolerance"] = doc.ModelRelativeTolerance,
        ["userText"] = DocumentUserText(doc),
    };

    private static JsonObject LayerAttributes(Layer layer) => new()
    {
        ["name"] = layer.Name ?? "",
        ["color"] = ToHex(layer.Color),
        ["visible"] = layer.IsVisible,
        ["locked"] = layer.IsLocked,
        ["userText"] = UserText(layer.GetUserStrings()),
    };

    private static JsonObject ObjectAttributesJson(RhinoObject obj)
    {
        var attributes = obj.Attributes;
        return new JsonObject
        {
            ["name"] = attributes.Name ?? "",
            ["colorSource"] = ColorSourceName(attributes.ColorSource),
            ["color"] = ToHex(attributes.ObjectColor),
            ["materialSource"] = MaterialSourceName(attributes.MaterialSource),
            ["linetypeSource"] = LinetypeSourceName(attributes.LinetypeSource),
            ["plotColorSource"] = PlotColorSourceName(attributes.PlotColorSource),
            ["plotWeightSource"] = PlotWeightSourceName(attributes.PlotWeightSource),
            ["visible"] = attributes.Visible,
            ["mode"] = ModeName(attributes.Mode),
            ["displayOrder"] = attributes.DisplayOrder,
            ["space"] = "model",
            ["userText"] = UserText(attributes.GetUserStrings()),
        };
    }

    private static List<EntityRef> LayerRefs(Layer layer)
    {
        if (layer.ParentLayerId == Guid.Empty) return new List<EntityRef>();
        return new List<EntityRef> { Changes.Ref(EntityKinds.Layer, layer.ParentLayerId.ToString("D"), RefRoles.Parent) };
    }

    private static void ApplySettings(RhinoDoc doc, JsonObject attributes)
    {
        var unitName = ReadString(attributes, "unitSystem");
        if (!Enum.TryParse(unitName, ignoreCase: true, out UnitSystem units))
            throw new SyncException($"Unknown unit system '{unitName}'.");
        doc.ModelUnitSystem = units;
        doc.ModelAbsoluteTolerance = ReadRequiredDouble(attributes, "absoluteTolerance");
        doc.ModelAngleToleranceRadians = ReadRequiredDouble(attributes, "angleToleranceRadians");
        doc.ModelRelativeTolerance = ReadRequiredDouble(attributes, "relativeTolerance");
        ApplyDocumentUserText(doc, ReadObject(attributes, "userText"));
    }

    private static void UpsertLayer(RhinoDoc doc, Guid id, Guid parentId, JsonObject attributes)
    {
        var name = ReadString(attributes, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Contains("::", StringComparison.Ordinal))
            throw new SyncException($"Layer {id:D} has a missing or invalid name.");
        RenameClash(doc, parentId, name, id);

        var index = doc.Layers.Find(id, true, RhinoMath.UnsetIntIndex);
        var found = index != RhinoMath.UnsetIntIndex;
        var layer = found ? doc.Layers[index] : new Layer { Id = id };
        layer.Name = name;
        layer.Color = ParseColor(ReadString(attributes, "color", "#000000"));
        layer.IsVisible = ReadBool(attributes, "visible", true);
        layer.IsLocked = ReadBool(attributes, "locked", false);
        layer.ParentLayerId = parentId;
        WriteUserText((key, value) => layer.SetUserString(key, value), found ? layer.GetUserStrings() : null, ReadObject(attributes, "userText"));

        if (!found)
        {
            var added = doc.Layers.Add(layer);
            if (added < 0 || doc.Layers[added].Id != id)
                throw new SyncException($"Could not add layer '{name}'.");
            return;
        }

        if (!TryModify(doc, layer, index))
            throw new SyncException($"Could not update layer '{name}'.");
    }

    private static void RenameClash(RhinoDoc doc, Guid parentId, string name, Guid keepId)
    {
        var clash = doc.Layers.Find(parentId, name, RhinoMath.UnsetIntIndex);
        if (clash == RhinoMath.UnsetIntIndex) return;
        var other = doc.Layers[clash];
        if (other.Id == keepId) return;
        other.Name = doc.Layers.GetUnusedLayerName();
        if (!doc.Layers.Modify(other, clash, true))
            throw new SyncException($"Could not rename layer '{name}' out of the way.");
    }

    private static bool TryModify(RhinoDoc doc, Layer layer, int index)
    {
        if (doc.Layers.Modify(layer, index, true)) return true;
        if (doc.Layers.CurrentLayerIndex != index) return false;
        var other = AnotherLayer(doc, index);
        if (other < 0 || !doc.Layers.SetCurrentLayerIndex(other, true)) return false;
        return doc.Layers.Modify(layer, index, true);
    }

    private static void EnsureCurrent(RhinoDoc doc, HashSet<Guid> keep)
    {
        var currentIndex = doc.Layers.CurrentLayerIndex;
        if (currentIndex >= 0 && currentIndex < doc.Layers.Count)
        {
            var current = doc.Layers[currentIndex];
            if (!current.IsDeleted && keep.Contains(current.Id) && current.IsVisible && !current.IsLocked) return;
        }

        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer.IsDeleted || !keep.Contains(layer.Id) || !layer.IsVisible || layer.IsLocked) continue;
            doc.Layers.SetCurrentLayerIndex(i, true);
            return;
        }
    }

    private static void DeleteExtraLayers(RhinoDoc doc, HashSet<Guid> keep)
    {
        var pending = new List<Guid>();
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer.IsDeleted || layer.IsReference || keep.Contains(layer.Id)) continue;
            pending.Add(layer.Id);
        }

        while (pending.Count > 0)
        {
            var removed = -1;
            for (var i = 0; i < pending.Count; i++)
            {
                if (HasPendingChild(doc, pending[i], pending)) continue;
                if (!doc.Layers.Delete(pending[i], true))
                    throw new SyncException($"Could not delete layer {pending[i]:D}, which is not in the commit.");
                removed = i;
                break;
            }

            if (removed < 0)
                throw new SyncException("Could not delete layers that are not in the commit.");
            pending.RemoveAt(removed);
        }
    }

    private static bool HasPendingChild(RhinoDoc doc, Guid id, List<Guid> pending)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (!layer.IsDeleted && layer.ParentLayerId == id && pending.Contains(layer.Id)) return true;
        }

        return false;
    }

    private static void DeleteUnlistedObjects(RhinoDoc doc, IReadOnlyList<DecodedObject> objects)
    {
        var listed = new HashSet<Guid>(objects.Select(item => ParseObjectId(item.Entity.Id)));
        var extras = doc.Objects.GetObjectList(ModelSettings())
            .Where(obj => obj.Attributes.Space == ActiveSpace.ModelSpace && !ShouldSkip(obj) && !listed.Contains(obj.Id))
            .ToList();
        foreach (var extra in extras)
        {
            if (!doc.Objects.Delete(extra, true, true))
                throw new SyncException($"Could not delete object {extra.Id:D}, which is not in the commit.");
        }
    }

    internal static Guid ParseObjectId(string id) => ParseGuid(id, "object");

    internal static Dictionary<Guid, RhinoObject> DeletedModelObjects(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            DeletedObjects = true,
            NormalObjects = false,
            LockedObjects = false,
            HiddenObjects = false,
            ActiveObjects = false,
            IdefObjects = false,
            ReferenceObjects = false,
            IncludeLights = false,
            IncludeGrips = false,
            IncludePhantoms = false,
        };
        var map = new Dictionary<Guid, RhinoObject>();
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj.IsDeleted && obj.Attributes.Space == ActiveSpace.ModelSpace)
                map[obj.Id] = obj;
        }

        return map;
    }

    internal static bool ContentMatches(RhinoDoc doc, RhinoObject obj, DecodedObject item)
    {
        var geometry = obj.Geometry;
        if (geometry is null || !GeometryBase.GeometryEquals(geometry, item.Geometry)) return false;
        return AttributesMatch(doc, obj.Attributes, item);
    }

    private static void PlaceObject(
        RhinoDoc doc,
        DecodedObject item,
        ConflictChoice? choice,
        bool replace,
        Dictionary<Guid, RhinoObject> deleted,
        Dictionary<string, uint> serials)
    {
        var id = ParseObjectId(item.Entity.Id);
        if (choice == ConflictChoice.KeepExisting) return;
        if (choice == ConflictChoice.Merge)
        {
            AddObject(doc, item, Guid.NewGuid(), merged: true);
            return;
        }

        var live = doc.Objects.FindId(id);
        if (replace && choice is null && (live ?? deleted.GetValueOrDefault(id)) is { } existing && !ContentMatches(doc, existing, item))
            choice = ConflictChoice.KeepIncoming;
        if (live is null && deleted.TryGetValue(id, out var dead))
        {
            var restore = choice == ConflictChoice.KeepIncoming || (choice is null && ContentMatches(doc, dead, item));
            if (!restore)
                throw new SyncException($"Object {item.Entity.Id} differs and was not reviewed.");
            if (!doc.Objects.Undelete(dead))
                throw new SyncException($"Could not restore object {item.Entity.Id}.");
            live = doc.Objects.FindId(id) ?? throw new SyncException($"Rhino did not keep the id for object {item.Entity.Id}.");
            if (choice is null)
            {
                serials[item.Entity.Id] = live.RuntimeSerialNumber;
                return;
            }
        }

        if (live is not null)
        {
            if (choice is null)
            {
                if (!ContentMatches(doc, live, item))
                    throw new SyncException($"Object {item.Entity.Id} differs and was not reviewed.");
                serials[item.Entity.Id] = live.RuntimeSerialNumber;
                return;
            }

            if (!doc.Objects.Replace(id, item.Geometry, true))
                throw new SyncException($"Could not replace object {item.Entity.Id}.");
            if (!doc.Objects.ModifyAttributes(id, BuildAttributes(doc, item, id), true))
                throw new SyncException($"Could not update object {item.Entity.Id}.");
            var updated = doc.Objects.FindId(id) ?? throw new SyncException($"Rhino did not keep the id for object {item.Entity.Id}.");
            serials[item.Entity.Id] = updated.RuntimeSerialNumber;
            return;
        }

        serials[item.Entity.Id] = AddObject(doc, item, id, merged: false);
    }

    private static bool AttributesMatch(RhinoDoc doc, ObjectAttributes attributes, DecodedObject item)
    {
        var layerRef = item.Entity.Manifest?.Refs.FirstOrDefault(reference =>
            reference.Role == RefRoles.Layer && reference.Kind == EntityKinds.Layer);
        if (layerRef is null) return false;
        var layerId = ParseGuid(layerRef.Id, "layer");
        var layer = doc.Layers.FindIndex(attributes.LayerIndex);
        if (layer is null || layer.IsDeleted || layer.Id != layerId) return false;
        if (!string.Equals(attributes.Name ?? "", ReadString(item.Attributes, "name"), StringComparison.Ordinal)) return false;
        if (attributes.ColorSource != ParseColorSource(ReadString(item.Attributes, "colorSource", "layer"))) return false;
        if (attributes.ObjectColor.ToArgb() != ParseColor(ReadString(item.Attributes, "color", "#000000")).ToArgb()) return false;
        if (attributes.MaterialSource != ParseMaterialSource(ReadString(item.Attributes, "materialSource", "layer"))) return false;
        if (attributes.LinetypeSource != ParseLinetypeSource(ReadString(item.Attributes, "linetypeSource", "layer"))) return false;
        if (attributes.PlotColorSource != ParsePlotColorSource(ReadString(item.Attributes, "plotColorSource", "layer"))) return false;
        if (attributes.PlotWeightSource != ParsePlotWeightSource(ReadString(item.Attributes, "plotWeightSource", "layer"))) return false;
        if (attributes.Visible != ReadBool(item.Attributes, "visible", true)) return false;
        if (attributes.Mode != ParseMode(ReadString(item.Attributes, "mode", "normal"))) return false;
        if (attributes.DisplayOrder != ReadInt(item.Attributes, "displayOrder", 0)) return false;
        return UserTextMatches(attributes, ReadObject(item.Attributes, "userText"));
    }

    private static bool UserTextMatches(ObjectAttributes attributes, JsonObject incoming)
    {
        var current = UserText(attributes.GetUserStrings());
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in incoming)
        {
            if (pair.Key.StartsWith(WorkingCopy.Prefix, StringComparison.Ordinal)) continue;
            if (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            expected[pair.Key] = text;
        }

        foreach (var pair in expected)
        {
            if (current[pair.Key] is not JsonValue value || !value.TryGetValue<string>(out var text) || text != pair.Value)
                return false;
        }

        foreach (var pair in current)
        {
            if (pair.Key.StartsWith(WorkingCopy.Prefix, StringComparison.Ordinal)) continue;
            if (!expected.ContainsKey(pair.Key)) return false;
        }

        return true;
    }

    private static ObjectAttributes BuildAttributes(RhinoDoc doc, DecodedObject item, Guid objectId)
    {
        var layerRef = item.Entity.Manifest?.Refs.FirstOrDefault(reference =>
            reference.Role == RefRoles.Layer && reference.Kind == EntityKinds.Layer);
        if (layerRef is null) throw new SyncException($"Object {item.Entity.Id} has no layer.");
        var layerId = ParseGuid(layerRef.Id, "layer");
        var layerIndex = doc.Layers.Find(layerId, true, RhinoMath.UnsetIntIndex);
        if (layerIndex == RhinoMath.UnsetIntIndex)
            throw new SyncException($"Object {item.Entity.Id} is missing layer {layerRef.Id}.");

        var attributes = new ObjectAttributes
        {
            ObjectId = objectId,
            LayerIndex = layerIndex,
            Name = ReadString(item.Attributes, "name"),
            ColorSource = ParseColorSource(ReadString(item.Attributes, "colorSource", "layer")),
            ObjectColor = ParseColor(ReadString(item.Attributes, "color", "#000000")),
            MaterialSource = ParseMaterialSource(ReadString(item.Attributes, "materialSource", "layer")),
            LinetypeSource = ParseLinetypeSource(ReadString(item.Attributes, "linetypeSource", "layer")),
            PlotColorSource = ParsePlotColorSource(ReadString(item.Attributes, "plotColorSource", "layer")),
            PlotWeightSource = ParsePlotWeightSource(ReadString(item.Attributes, "plotWeightSource", "layer")),
            Visible = ReadBool(item.Attributes, "visible", true),
            Mode = ParseMode(ReadString(item.Attributes, "mode", "normal")),
            DisplayOrder = ReadInt(item.Attributes, "displayOrder", 0),
            Space = ActiveSpace.ModelSpace,
        };
        if (string.IsNullOrEmpty(attributes.Name)) attributes.Name = null;
        WriteUserText((key, value) => attributes.SetUserString(key, value), null, ReadObject(item.Attributes, "userText"));
        return attributes;
    }

    private static uint AddObject(RhinoDoc doc, DecodedObject item, Guid id, bool merged)
    {
        var added = doc.Objects.Add(item.Geometry, BuildAttributes(doc, item, id));
        if (added != id)
            throw new SyncException(merged
                ? $"Rhino did not keep the new id for a merged copy of object {item.Entity.Id}."
                : $"Rhino did not keep the id for object {item.Entity.Id}.");
        var created = doc.Objects.FindId(id) ?? throw new SyncException($"Object {item.Entity.Id} was not added.");
        return created.RuntimeSerialNumber;
    }

    private static GeometryBase DecodeGeometry(EntityVersion entity, IReadOnlyDictionary<string, byte[]> blobs)
    {
        var hash = RequireComponent(entity, GeometryComponent);
        var json = Encoding.UTF8.GetString(RequireBytes(blobs, hash, $"object {entity.Id} geometry"));
        CommonObject? created;
        try
        {
            created = CommonObject.FromJSON(json);
        }
        catch (Exception ex)
        {
            throw new SyncException($"Object {entity.Id} geometry could not be read ({ex.Message}).", ex);
        }

        if (created is GeometryBase geometry) return geometry;
        created?.Dispose();
        throw new SyncException($"Object {entity.Id} geometry is not a Rhino object.");
    }

    private static (string Hash, byte[] Bytes) SerializeGeometry(GeometryBase geometry)
    {
        var json = geometry.ToJSON(new SerializationOptions
        {
            RhinoVersion = 8,
            WriteRenderMeshes = false,
            WriteAnalysisMeshes = false,
            WriteUserData = false,
        });
        if (string.IsNullOrEmpty(json)) throw new SyncException("Rhino returned empty geometry JSON.");
        var bytes = Encoding.UTF8.GetBytes(json);
        return (ContentHash.Compute(bytes), bytes);
    }

    private static string HashAttributes(JsonObject attributes) => ContentHash.Compute(CanonicalJson.Serialize(attributes));

    private static JsonObject RequireAttributes(EntityVersion entity, IReadOnlyDictionary<string, byte[]> blobs)
    {
        var hash = RequireComponent(entity, AttributesComponent);
        var node = JsonNode.Parse(RequireBytes(blobs, hash, $"{entity.Kind} {entity.Id} attributes"));
        if (node is not JsonObject attributes)
            throw new SyncException($"{entity.Kind} {entity.Id} attributes are not a JSON object.");
        return attributes;
    }

    private static string RequireComponent(EntityVersion entity, string name)
    {
        if (entity.Manifest is null || !entity.Manifest.Components.TryGetValue(name, out var hash) || string.IsNullOrEmpty(hash))
            throw new SyncException($"{entity.Kind} {entity.Id} has no {name}.");
        return hash;
    }

    private static byte[] RequireBytes(IReadOnlyDictionary<string, byte[]> blobs, string hash, string label)
    {
        if (!blobs.TryGetValue(hash, out var bytes))
            throw new SyncException($"Missing downloaded blob for {label}.");
        return bytes;
    }

    private static Dictionary<Guid, Layer> LiveLayers(RhinoDoc doc)
    {
        var layers = new Dictionary<Guid, Layer>();
        foreach (var layer in doc.Layers)
        {
            if (layer.IsDeleted || layer.IsReference) continue;
            layers[layer.Id] = layer;
        }

        return layers;
    }

    private static List<Layer> ParentsFirst(IReadOnlyCollection<Layer> layers)
    {
        var byId = layers.ToDictionary(layer => layer.Id);
        return ParentsFirst(layers, layer => layer.Id, layer =>
            layer.ParentLayerId == Guid.Empty ? null : byId[layer.ParentLayerId]);
    }

    private static List<EntityVersion> ParentsFirst(IReadOnlyList<EntityVersion> layers)
    {
        var byId = layers.ToDictionary(layer => layer.Id, StringComparer.Ordinal);
        return ParentsFirst(layers, layer => layer.Id, layer =>
        {
            var parent = layer.Manifest?.Refs.FirstOrDefault(reference =>
                reference.Role == RefRoles.Parent && reference.Kind == EntityKinds.Layer);
            if (parent is null) return null;
            if (!byId.TryGetValue(parent.Id, out var parentLayer))
                throw new SyncException($"Layer {layer.Id} references missing parent {parent.Id}.");
            return parentLayer;
        });
    }

    private static List<T> ParentsFirst<T, TId>(IEnumerable<T> layers, Func<T, TId> id, Func<T, T?> parent) where TId : notnull
    {
        var ordered = new List<T>();
        var done = new HashSet<TId>();
        var visiting = new HashSet<TId>();
        void Visit(T layer)
        {
            var layerId = id(layer);
            if (done.Contains(layerId)) return;
            if (!visiting.Add(layerId)) throw new SyncException($"Layer {layerId} is part of a parent cycle.");
            var parentLayer = parent(layer);
            if (parentLayer is not null) Visit(parentLayer);
            visiting.Remove(layerId);
            done.Add(layerId);
            ordered.Add(layer);
        }

        foreach (var layer in layers) Visit(layer);
        return ordered;
    }

    private static Guid ParentGuid(EntityVersion layer)
    {
        var parent = layer.Manifest?.Refs.FirstOrDefault(reference =>
            reference.Role == RefRoles.Parent && reference.Kind == EntityKinds.Layer);
        return parent is null ? Guid.Empty : ParseGuid(parent.Id, "layer parent");
    }

    private static Guid ParseGuid(string id, string label)
    {
        if (!Guid.TryParseExact(id, "D", out var guid))
            throw new SyncException($"{label} id '{id}' is not a guid.");
        return guid;
    }

    private static ObjectEnumeratorSettings ModelSettings() => new()
    {
        NormalObjects = true,
        LockedObjects = true,
        HiddenObjects = true,
        IdefObjects = false,
        DeletedObjects = false,
        ActiveObjects = true,
        ReferenceObjects = false,
        IncludeLights = false,
        IncludeGrips = false,
        IncludePhantoms = false,
    };

    private static bool ShouldSkip(RhinoObject obj)
    {
        if (obj.Attributes.Space != ActiveSpace.ModelSpace) return true;
        if (obj.Attributes.IsInstanceDefinitionObject || obj.Attributes.Mode == ObjectMode.InstanceDefinitionObject) return true;
        if (obj.ObjectType == ObjectType.InstanceReference || obj.Geometry is InstanceReferenceGeometry) return true;
        return obj.Geometry is null;
    }

    private static int AnotherLayer(RhinoDoc doc, int index)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            if (i != index && !doc.Layers[i].IsDeleted) return i;
        }

        return -1;
    }

    private static JsonObject DocumentUserText(RhinoDoc doc)
    {
        var userText = new JsonObject();
        var strings = doc.Strings;
        for (var i = 0; i < strings.Count; i++)
        {
            var key = strings.GetKey(i);
            if (string.IsNullOrEmpty(key) || !IsDocumentUserText(key)) continue;
            userText[key] = strings.GetValue(i) ?? "";
        }

        return userText;
    }

    private static void ApplyDocumentUserText(RhinoDoc doc, JsonObject userText)
    {
        var remove = new List<string>();
        var strings = doc.Strings;
        for (var i = 0; i < strings.Count; i++)
        {
            var key = strings.GetKey(i);
            if (string.IsNullOrEmpty(key) || !IsDocumentUserText(key) || userText.ContainsKey(key)) continue;
            remove.Add(key);
        }

        foreach (var key in remove) strings.Delete(key);
        foreach (var pair in userText)
        {
            if (!IsDocumentUserText(pair.Key) || pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text))
                continue;
            strings.SetString(pair.Key, text);
        }
    }

    private static bool IsDocumentUserText(string key) =>
        !key.Contains('\\') && !key.StartsWith(WorkingCopy.Prefix, StringComparison.Ordinal);

    private static JsonObject UserText(NameValueCollection? values)
    {
        var userText = new JsonObject();
        if (values is null) return userText;
        foreach (string? key in values.AllKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            userText[key] = values[key] ?? "";
        }

        return userText;
    }

    private static void WriteUserText(Action<string, string?> write, NameValueCollection? existing, JsonObject userText)
    {
        if (existing is not null)
        {
            foreach (string? key in existing.AllKeys)
            {
                if (string.IsNullOrEmpty(key) || userText.ContainsKey(key)) continue;
                write(key, null);
            }
        }

        foreach (var pair in userText)
        {
            if (pair.Key.StartsWith(WorkingCopy.Prefix, StringComparison.Ordinal)) continue;
            if (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            write(pair.Key, text);
        }
    }

    private static JsonObject ReadObject(JsonObject attributes, string name) =>
        attributes[name] as JsonObject ?? new JsonObject();

    private static string ReadString(JsonObject attributes, string name, string fallback = "")
    {
        if (attributes[name] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return fallback;
    }

    private static bool ReadBool(JsonObject attributes, string name, bool fallback)
    {
        if (attributes[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        return fallback;
    }

    private static int ReadInt(JsonObject attributes, string name, int fallback)
    {
        if (!TryNumber(attributes[name], out var number)) return fallback;
        return (int)number;
    }

    private static double ReadRequiredDouble(JsonObject attributes, string name)
    {
        if (!TryNumber(attributes[name], out var number))
            throw new SyncException($"Settings are missing {name}.");
        return number;
    }

    private static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<int>(out var whole))
        {
            number = whole;
            return true;
        }

        return false;
    }

    private static string ToHex(Color color) => color.A == 255
        ? FormattableString.Invariant($"#{color.R:X2}{color.G:X2}{color.B:X2}")
        : FormattableString.Invariant($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");

    private static Color ParseColor(string hex)
    {
        if (hex.Length is 7 or 9 && hex[0] == '#' &&
            ParseChannel(hex, 1, out var first) && ParseChannel(hex, 3, out var second) && ParseChannel(hex, 5, out var third) &&
            (hex.Length == 7 || ParseChannel(hex, 7, out _)))
        {
            return hex.Length == 7
                ? Color.FromArgb(first, second, third)
                : Color.FromArgb(first, second, third, ParseChannelValue(hex, 7));
        }

        throw new SyncException($"Could not read color '{hex}'.");
    }

    private static bool ParseChannel(string hex, int start, out byte value)
    {
        var parsed = byte.TryParse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return parsed;
    }

    private static byte ParseChannelValue(string hex, int start)
    {
        ParseChannel(hex, start, out var value);
        return value;
    }

    private static string ColorSourceName(ObjectColorSource source) => source switch
    {
        ObjectColorSource.ColorFromObject => "object",
        ObjectColorSource.ColorFromMaterial => "material",
        ObjectColorSource.ColorFromParent => "parent",
        _ => "layer",
    };

    private static string MaterialSourceName(ObjectMaterialSource source) => source switch
    {
        ObjectMaterialSource.MaterialFromObject => "object",
        ObjectMaterialSource.MaterialFromParent => "parent",
        _ => "layer",
    };

    private static string LinetypeSourceName(ObjectLinetypeSource source) => source switch
    {
        ObjectLinetypeSource.LinetypeFromObject => "object",
        ObjectLinetypeSource.LinetypeFromParent => "parent",
        _ => "layer",
    };

    private static string PlotColorSourceName(ObjectPlotColorSource source) => source switch
    {
        ObjectPlotColorSource.PlotColorFromObject => "object",
        ObjectPlotColorSource.PlotColorFromDisplay => "display",
        ObjectPlotColorSource.PlotColorFromParent => "parent",
        _ => "layer",
    };

    private static string PlotWeightSourceName(ObjectPlotWeightSource source) => source switch
    {
        ObjectPlotWeightSource.PlotWeightFromObject => "object",
        ObjectPlotWeightSource.PlotWeightFromParent => "parent",
        _ => "layer",
    };

    private static string ModeName(ObjectMode mode) => mode switch
    {
        ObjectMode.Hidden => "hidden",
        ObjectMode.Locked => "locked",
        _ => "normal",
    };

    private static ObjectColorSource ParseColorSource(string value) => value switch
    {
        "object" => ObjectColorSource.ColorFromObject,
        "material" => ObjectColorSource.ColorFromMaterial,
        "parent" => ObjectColorSource.ColorFromParent,
        _ => ObjectColorSource.ColorFromLayer,
    };

    private static ObjectMaterialSource ParseMaterialSource(string value) => value switch
    {
        "object" => ObjectMaterialSource.MaterialFromObject,
        "parent" => ObjectMaterialSource.MaterialFromParent,
        _ => ObjectMaterialSource.MaterialFromLayer,
    };

    private static ObjectLinetypeSource ParseLinetypeSource(string value) => value switch
    {
        "object" => ObjectLinetypeSource.LinetypeFromObject,
        "parent" => ObjectLinetypeSource.LinetypeFromParent,
        _ => ObjectLinetypeSource.LinetypeFromLayer,
    };

    private static ObjectPlotColorSource ParsePlotColorSource(string value) => value switch
    {
        "object" => ObjectPlotColorSource.PlotColorFromObject,
        "display" => ObjectPlotColorSource.PlotColorFromDisplay,
        "parent" => ObjectPlotColorSource.PlotColorFromParent,
        _ => ObjectPlotColorSource.PlotColorFromLayer,
    };

    private static ObjectPlotWeightSource ParsePlotWeightSource(string value) => value switch
    {
        "object" => ObjectPlotWeightSource.PlotWeightFromObject,
        "parent" => ObjectPlotWeightSource.PlotWeightFromParent,
        _ => ObjectPlotWeightSource.PlotWeightFromLayer,
    };

    private static ObjectMode ParseMode(string value) => value switch
    {
        "hidden" => ObjectMode.Hidden,
        "locked" => ObjectMode.Locked,
        _ => ObjectMode.Normal,
    };
}

internal sealed class CapturedPush
{
    internal List<Change> Changes { get; } = new();
    internal Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, BaseEntry> Entries { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, uint> Serials { get; } = new(StringComparer.Ordinal);
    internal int Skipped { get; set; }
}

internal sealed class DecodedObject(EntityVersion entity, JsonObject attributes, GeometryBase geometry)
{
    internal EntityVersion Entity { get; } = entity;
    internal JsonObject Attributes { get; } = attributes;
    internal GeometryBase Geometry { get; } = geometry;
}
