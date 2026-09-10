using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AltiumMcp.Server;

/// <summary>
/// Field projection for tool results ("fields" parameter): keeps only the requested properties so the agent pays
/// tokens for what it asked. Rules:
///  - names are case-insensitive and camelCase as on the wire;
///  - if the result has a list property whose element objects carry any requested field (components, nets, items,
///    objects, violations, …), every element is projected and the surrounding metadata (total, offset, …) is kept;
///  - otherwise the top-level object itself is projected;
///  - "a.b" selects a nested property: the parent "a" is kept, reduced to "b" (one level).
/// Pure, unit-tested.
/// </summary>
public static class JsonProjection
{
    public static JsonElement Apply(JsonElement root, IReadOnlyCollection<string>? fields)
    {
        if (fields == null || fields.Count == 0 || root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        var wanted = new HashSet<string>(fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return root;
        }

        JsonNode? node = JsonNode.Parse(root.GetRawText());
        if (node is not JsonObject obj)
        {
            return root;
        }

        bool projectedAny = false;
        foreach (KeyValuePair<string, JsonNode?> prop in obj.ToList())
        {
            if (prop.Value is JsonArray arr && arr.Count > 0 && arr.All(e => e is JsonObject) && arr.Cast<JsonObject>().Any(e => HasAny(e, wanted)))
            {
                obj[prop.Key] = new JsonArray(arr.Cast<JsonObject>().Select(e => (JsonNode)Project(e, wanted)).ToArray());
                projectedAny = true;
            }
        }

        JsonObject result = projectedAny ? obj : Project(obj, wanted);
        return JsonSerializer.SerializeToElement(result);
    }

    private static bool HasAny(JsonObject o, HashSet<string> wanted) =>
        o.Any(p => wanted.Contains(p.Key) || wanted.Any(w => w.StartsWith(p.Key + ".", StringComparison.OrdinalIgnoreCase)));

    private static JsonObject Project(JsonObject o, HashSet<string> wanted)
    {
        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> p in o)
        {
            if (wanted.Contains(p.Key))
            {
                result[p.Key] = p.Value?.DeepClone();
                continue;
            }

            // Nested selection "parent.child": keep parent reduced to the listed children.
            var children = wanted.Where(w => w.StartsWith(p.Key + ".", StringComparison.OrdinalIgnoreCase)).Select(w => w.Substring(p.Key.Length + 1)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (children.Count == 0 || p.Value == null)
            {
                continue;
            }

            result[p.Key] = p.Value switch
            {
                JsonObject child => Project(child, children),
                JsonArray arr when arr.All(e => e is JsonObject) => new JsonArray(arr.Cast<JsonObject>().Select(e => (JsonNode)Project(e, children)).ToArray()),
                _ => p.Value.DeepClone(),
            };
        }

        return result;
    }
}
