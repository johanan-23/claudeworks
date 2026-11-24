using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSW.Core.Agent
{
    /// <summary>Fluent builder for tool input schemas (keeps tool definitions compact and valid).</summary>
    public sealed class SchemaBuilder
    {
        private readonly JsonObject _props = new JsonObject();
        private readonly List<string> _required = new List<string>();

        private SchemaBuilder Add(string name, JsonObject prop, bool required)
        {
            _props[name] = prop;
            if (required) _required.Add(name);
            return this;
        }

        public SchemaBuilder Str(string name, string description, bool required = false, IEnumerable<string> enumValues = null)
        {
            var p = new JsonObject { ["type"] = "string", ["description"] = description };
            if (enumValues != null) p["enum"] = new JsonArray(enumValues.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
            return Add(name, p, required);
        }

        public SchemaBuilder Num(string name, string description, bool required = false, double? min = null, double? max = null)
        {
            var p = new JsonObject { ["type"] = "number", ["description"] = description };
            if (min.HasValue) p["minimum"] = min.Value;
            if (max.HasValue) p["maximum"] = max.Value;
            return Add(name, p, required);
        }

        public SchemaBuilder Int(string name, string description, bool required = false, int? min = null, int? max = null)
        {
            var p = new JsonObject { ["type"] = "integer", ["description"] = description };
            if (min.HasValue) p["minimum"] = min.Value;
            if (max.HasValue) p["maximum"] = max.Value;
            return Add(name, p, required);
        }

        public SchemaBuilder Bool(string name, string description, bool required = false) =>
            Add(name, new JsonObject { ["type"] = "boolean", ["description"] = description }, required);

        public SchemaBuilder Arr(string name, string itemType, string description, bool required = false, int? minItems = null, int? maxItems = null, IEnumerable<string> itemEnum = null)
        {
            var items = new JsonObject { ["type"] = itemType };
            if (itemEnum != null) items["enum"] = new JsonArray(itemEnum.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
            var p = new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = description };
            if (minItems.HasValue) p["minItems"] = minItems.Value;
            if (maxItems.HasValue) p["maxItems"] = maxItems.Value;
            return Add(name, p, required);
        }

        /// <summary>Array of objects described by a nested builder.</summary>
        public SchemaBuilder ArrOf(string name, SchemaBuilder item, string description, bool required = false)
        {
            var p = new JsonObject { ["type"] = "array", ["items"] = item.BuildNode(), ["description"] = description };
            return Add(name, p, required);
        }

        public JsonObject BuildNode()
        {
            var o = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(_props.ToJsonString()) };
            if (_required.Count > 0) o["required"] = new JsonArray(_required.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
            return o;
        }

        public string Build() => BuildNode().ToJsonString();

        public static string Empty => "{\"type\":\"object\",\"properties\":{}}";
    }

    /// <summary>
    /// Minimal JSON-Schema validator covering what our tool schemas use: object/properties/required,
    /// type (string/number/integer/boolean/array/object), enum, minimum/maximum, items, min/maxItems.
    /// Unknown properties are tolerated. Strings holding numbers are accepted for numeric fields
    /// (mirrors <see cref="Args"/>'s leniency).
    /// </summary>
    public static class SchemaValidator
    {
        public static List<string> Validate(string schemaJson, JsonElement value)
        {
            var errors = new List<string>();
            using (var doc = JsonDocument.Parse(schemaJson))
                Check(doc.RootElement, value, "input", errors);
            return errors;
        }

        private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
        {
            if (schema.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                if (!TypeMatches(type, value))
                {
                    errors.Add($"{path} must be of type {type}");
                    return;
                }
            }

            if (schema.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
            {
                var ok = en.EnumerateArray().Any(e => e.ValueKind == value.ValueKind && e.GetRawText() == value.GetRawText()
                                                        || (e.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String &&
                                                            string.Equals(e.GetString(), value.GetString(), StringComparison.OrdinalIgnoreCase)));
                if (!ok) errors.Add($"{path} must be one of [{string.Join(", ", en.EnumerateArray().Select(e => e.ToString()))}]");
            }

            if (TryNumber(value, out var num))
            {
                if (schema.TryGetProperty("minimum", out var min) && num < min.GetDouble()) errors.Add($"{path} must be >= {min.GetDouble()}");
                if (schema.TryGetProperty("maximum", out var max) && num > max.GetDouble()) errors.Add($"{path} must be <= {max.GetDouble()}");
            }

            if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props))
            {
                if (schema.TryGetProperty("required", out var req))
                    foreach (var r in req.EnumerateArray())
                        if (!value.TryGetProperty(r.GetString(), out var rv) || rv.ValueKind == JsonValueKind.Null)
                            errors.Add($"{path}.{r.GetString()} is required");
                foreach (var p in props.EnumerateObject())
                    if (value.TryGetProperty(p.Name, out var pv) && pv.ValueKind != JsonValueKind.Null)
                        Check(p.Value, pv, $"{path}.{p.Name}", errors);
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var n = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var mi) && n < mi.GetInt32()) errors.Add($"{path} needs at least {mi.GetInt32()} items");
                if (schema.TryGetProperty("maxItems", out var ma) && n > ma.GetInt32()) errors.Add($"{path} allows at most {ma.GetInt32()} items");
                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var e in value.EnumerateArray()) Check(items, e, $"{path}[{i++}]", errors);
                }
            }
        }

        private static bool TryNumber(JsonElement v, out double d)
        {
            d = 0;
            if (v.ValueKind == JsonValueKind.Number) { d = v.GetDouble(); return true; }
            return false;
        }

        private static bool TypeMatches(string type, JsonElement v)
        {
            switch (type)
            {
                case "object": return v.ValueKind == JsonValueKind.Object;
                case "array": return v.ValueKind == JsonValueKind.Array;
                case "string": return v.ValueKind == JsonValueKind.String;
                case "boolean":
                    return v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False ||
                           (v.ValueKind == JsonValueKind.String && (v.GetString() == "true" || v.GetString() == "false"));
                case "number":
                    return v.ValueKind == JsonValueKind.Number ||
                           (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
                case "integer":
                    if (v.ValueKind == JsonValueKind.Number) return Math.Abs(v.GetDouble() - Math.Round(v.GetDouble())) < 1e-9;
                    return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out _);
                default: return true;
            }
        }
    }
}
