using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClaudeSW.Core.Agent
{
    /// <summary>
    /// Typed, forgiving accessor over a tool's JSON input. Lengths come in as millimetres and
    /// angles as degrees; <see cref="Mm"/> / <see cref="Deg"/> convert to SOLIDWORKS metres / radians.
    /// Numbers sent as strings ("20") are accepted because models occasionally quote them.
    /// </summary>
    public sealed class Args
    {
        private readonly JsonElement _root;

        public Args(JsonElement root) { _root = root.ValueKind == JsonValueKind.Object ? root : Empty; }

        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

        public static Args Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) json = "{}";
            using (var doc = JsonDocument.Parse(json))
                return new Args(doc.RootElement.Clone());
        }

        public JsonElement Raw => _root;

        public bool Has(string name) =>
            _root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

        private JsonElement? Get(string name) =>
            _root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : (JsonElement?)null;

        public string Str(string name, string def = null)
        {
            var v = Get(name);
            if (v == null) return def;
            switch (v.Value.ValueKind)
            {
                case JsonValueKind.String: return v.Value.GetString();
                case JsonValueKind.Number: return v.Value.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return v.Value.GetRawText();
            }
        }

        public string RequireStr(string name)
        {
            var s = Str(name);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolException($"Missing required argument '{name}'.");
            return s;
        }

        public double Num(string name, double def = 0)
        {
            var v = Get(name);
            if (v == null) return def;
            return ToDouble(v.Value, name);
        }

        public double RequireNum(string name)
        {
            if (!Has(name)) throw new ToolException($"Missing required argument '{name}'.");
            return Num(name);
        }

        public int Int(string name, int def = 0)
        {
            var d = Num(name, def);
            if (Math.Abs(d - Math.Round(d)) > 1e-9) throw new ToolException($"Argument '{name}' must be a whole number.");
            return (int)Math.Round(d);
        }

        public bool Bool(string name, bool def = false)
        {
            var v = Get(name);
            if (v == null) return def;
            switch (v.Value.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    var s = v.Value.GetString().Trim().ToLowerInvariant();
                    if (s == "true" || s == "yes" || s == "1") return true;
                    if (s == "false" || s == "no" || s == "0") return false;
                    break;
                case JsonValueKind.Number: return v.Value.GetDouble() != 0;
            }
            throw new ToolException($"Argument '{name}' must be true or false.");
        }

        /// <summary>Millimetres -> metres.</summary>
        public double Mm(string name, double defMm = 0) => Num(name, defMm) / 1000.0;
        /// <summary>Degrees -> radians.</summary>
        public double Deg(string name, double defDeg = 0) => Num(name, defDeg) * Math.PI / 180.0;

        public double[] Nums(string name, int? expectedLength = null)
        {
            var v = Get(name);
            if (v == null) return null;
            if (v.Value.ValueKind != JsonValueKind.Array) throw new ToolException($"Argument '{name}' must be an array of numbers.");
            var arr = v.Value.EnumerateArray().Select(e => ToDouble(e, name)).ToArray();
            if (expectedLength.HasValue && arr.Length != expectedLength.Value)
                throw new ToolException($"Argument '{name}' must have {expectedLength} numbers.");
            return arr;
        }

        public List<string> Strs(string name)
        {
            var v = Get(name);
            if (v == null) return new List<string>();
            if (v.Value.ValueKind == JsonValueKind.String) return new List<string> { v.Value.GetString() };
            if (v.Value.ValueKind != JsonValueKind.Array) throw new ToolException($"Argument '{name}' must be an array of strings.");
            return v.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()).ToList();
        }

        public IEnumerable<Args> Objects(string name)
        {
            var v = Get(name);
            if (v == null) yield break;
            if (v.Value.ValueKind != JsonValueKind.Array) throw new ToolException($"Argument '{name}' must be an array.");
            foreach (var e in v.Value.EnumerateArray()) yield return new Args(e);
        }

        public T Enum<T>(string name, IDictionary<string, T> map, string def)
        {
            var s = (Str(name, def) ?? def ?? "").Trim().ToLowerInvariant();
            if (map.TryGetValue(s, out var r)) return r;
            throw new ToolException($"Argument '{name}' must be one of: {string.Join(", ", map.Keys)}.");
        }

        private static double ToDouble(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            if (e.ValueKind == JsonValueKind.String &&
                double.TryParse(e.GetString().Trim().Replace("mm", "").Replace("deg", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            throw new ToolException($"Argument '{name}' must be a number.");
        }

        public override string ToString() => _root.GetRawText();
    }
}
