using System.Linq;
using System.Text.Json;
using ClaudeSW.Core.Agent;
using Xunit;

namespace ClaudeSW.Core.Tests
{
    public class ArgsTests
    {
        [Fact]
        public void ConvertsMillimetresAndDegrees()
        {
            var a = Args.Parse("{\"depth\": 20, \"angle\": 90}");
            Assert.Equal(0.02, a.Mm("depth"), 9);
            Assert.Equal(System.Math.PI / 2, a.Deg("angle"), 9);
        }

        [Fact]
        public void AcceptsQuotedNumbersAndUnits()
        {
            var a = Args.Parse("{\"depth\": \"12.5\", \"r\": \"3 mm\", \"n\": \"4\"}");
            Assert.Equal(12.5, a.Num("depth"));
            Assert.Equal(3, a.Num("r"));
            Assert.Equal(4, a.Int("n"));
        }

        [Fact]
        public void DefaultsWhenMissingOrNull()
        {
            var a = Args.Parse("{\"x\": null}");
            Assert.Equal(7, a.Num("x", 7));
            Assert.Equal("d", a.Str("missing", "d"));
            Assert.False(a.Has("x"));
            Assert.True(a.Bool("flag", true));
        }

        [Fact]
        public void BooleansFromStrings()
        {
            var a = Args.Parse("{\"a\": \"yes\", \"b\": \"false\", \"c\": 1}");
            Assert.True(a.Bool("a"));
            Assert.False(a.Bool("b"));
            Assert.True(a.Bool("c"));
        }

        [Fact]
        public void RejectsBadValuesWithFriendlyMessage()
        {
            var a = Args.Parse("{\"n\": 2.5, \"s\": \"abc\"}");
            Assert.Contains("whole number", Assert.Throws<ToolException>(() => a.Int("n")).Message);
            Assert.Contains("must be a number", Assert.Throws<ToolException>(() => a.Num("s")).Message);
            Assert.Contains("Missing required", Assert.Throws<ToolException>(() => a.RequireStr("zzz")).Message);
        }

        [Fact]
        public void ArraysAndEnums()
        {
            var a = Args.Parse("{\"p\": [1, \"2\", 3], \"refs\": \"S1\", \"dir\": \"Mid_Plane\"}");
            Assert.Equal(new double[] { 1, 2, 3 }, a.Nums("p", 3));
            Assert.Equal(new[] { "S1" }, a.Strs("refs"));
            var map = new System.Collections.Generic.Dictionary<string, int> { ["one_side"] = 0, ["mid_plane"] = 6 };
            Assert.Equal(6, a.Enum("dir", map, "one_side"));
            Assert.Throws<ToolException>(() => a.Nums("p", 2));
        }

        [Fact]
        public void NonObjectInputBecomesEmpty()
        {
            var a = new Args(JsonDocument.Parse("[1,2]").RootElement);
            Assert.False(a.Has("anything"));
        }
    }

    public class SchemaTests
    {
        private static readonly string Schema = new SchemaBuilder()
            .Num("depth", "mm", required: true, min: 0)
            .Str("direction", "dir", enumValues: new[] { "one_side", "mid_plane" })
            .Int("count", "n", min: 2, max: 100)
            .Arr("point", "number", "xyz", minItems: 3, maxItems: 3)
            .Bool("cut", "cut?")
            .Build();

        private static System.Collections.Generic.List<string> V(string json) =>
            SchemaValidator.Validate(Schema, JsonDocument.Parse(json).RootElement);

        [Fact] public void ValidInputPasses() => Assert.Empty(V("{\"depth\": 5, \"direction\": \"mid_plane\", \"count\": 3, \"point\": [0,0,0], \"cut\": true}"));
        [Fact] public void MissingRequired() => Assert.Contains("input.depth is required", V("{}"));
        [Fact] public void BelowMinimum() => Assert.Contains(V("{\"depth\": -1}"), e => e.Contains(">= 0"));
        [Fact] public void EnumIsCaseInsensitive() => Assert.Empty(V("{\"depth\": 1, \"direction\": \"MID_PLANE\"}"));
        [Fact] public void BadEnum() => Assert.Contains(V("{\"depth\": 1, \"direction\": \"sideways\"}"), e => e.Contains("one of"));
        [Fact] public void IntegerType() => Assert.Contains(V("{\"depth\": 1, \"count\": 2.5}"), e => e.Contains("integer"));
        [Fact] public void ArrayLength() => Assert.Contains(V("{\"depth\": 1, \"point\": [1,2]}"), e => e.Contains("at least 3"));
        [Fact] public void QuotedNumberAccepted() => Assert.Empty(V("{\"depth\": \"5\"}"));
        [Fact] public void WrongType() => Assert.Contains(V("{\"depth\": 1, \"cut\": 5}"), e => e.Contains("boolean"));

        [Fact]
        public void BuilderProducesStandardSchema()
        {
            using var doc = JsonDocument.Parse(Schema);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("depth", doc.RootElement.GetProperty("required").EnumerateArray().Single().GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("properties").GetProperty("point").GetProperty("maxItems").GetInt32());
        }
    }
}
