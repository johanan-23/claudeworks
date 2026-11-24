using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using SolidWorks.Interop.sldworks;

namespace ClaudeSW.AddIn.Tools
{
    /// <summary>Small helpers shared by all tool definitions.</summary>
    internal static class K
    {
        public static ToolSpec Tool(string name, string description, SchemaBuilder schema, Func<Args, ToolContext, ToolOutput> run,
            bool modifies = false, bool destructive = false, Func<Args, string> describe = null, Func<Args, string> confirm = null, bool advanced = false) =>
            new ToolSpec
            {
                Name = name,
                Description = description,
                InputSchemaJson = (schema ?? new SchemaBuilder()).Build(),
                Run = run,
                Modifies = modifies,
                Destructive = destructive,
                Describe = describe,
                ConfirmMessage = confirm,
                Advanced = advanced,
            };

        public static SchemaBuilder S() => new SchemaBuilder();

        public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static T Check<T>(T obj, string what) where T : class =>
            obj ?? throw new ToolException($"SOLIDWORKS could not create {what}.");

        /// <summary>Standard "refs" property used by selection-taking tools.</summary>
        public static SchemaBuilder Refs(this SchemaBuilder s, string description, bool required = false, string name = "refs") =>
            s.Arr(name, "string", description + " References like \"S1\" (from the context), \"F3\"/\"E7\" (from find_faces/find_edges), or names of features/planes/components.", required);

        public static SchemaBuilder Points(this SchemaBuilder s) =>
            s.Str("entity_type", "Fallback picking only: FACE, EDGE or VERTEX for `points`.")
             .Arr("points", "array", "Fallback picking only: model-space points [[x,y,z], ...] in mm lying on the entities.");

        public static ToolOutput FeatureResult(SwSession s, ModelDoc2 doc, Feature feat, string what, string summary)
        {
            if (feat == null)
            {
                var errs = s.RebuildErrors(doc);
                throw new ToolException($"SOLIDWORKS did not create the {what}." + (errs.Count > 0 ? " Rebuild errors: " + string.Join("; ", errs) : " Check that the inputs are valid (closed profile, correct selections, sensible sizes)."));
            }
            var errors = s.RebuildErrors(doc);
            return ToolOutput.Obj(new { created = feat.Name, type = feat.GetTypeName2(), rebuild_errors = errors.Count == 0 ? null : errors }, summary.Replace("{name}", feat.Name));
        }

        public static readonly Dictionary<string, int> PaperSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = 0, ["b"] = 2, ["c"] = 3, ["d"] = 4, ["e"] = 5, ["a4"] = 6, ["a3"] = 8, ["a2"] = 9, ["a1"] = 10, ["a0"] = 11,
        };
    }
}
