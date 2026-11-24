using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Model;

namespace ClaudeSW.Core.Agent
{
    /// <summary>Definition of a client-side tool exposed to the model.</summary>
    public sealed class ToolSpec
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON Schema (object) for the input, as JSON text.</summary>
        public string InputSchemaJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
        /// <summary>Deletes, overwrites or discards user work: requires a confirmation card.</summary>
        public bool Destructive { get; set; }
        /// <summary>Changes the model (participates in the per-turn undo group).</summary>
        public bool Modifies { get; set; }
        /// <summary>Only exposed when the user enables the advanced setting.</summary>
        public bool Advanced { get; set; }
        /// <summary>Optional: decide per call whether to confirm, returning the question (null = no confirm).</summary>
        public Func<Args, string> ConfirmMessage { get; set; }
        /// <summary>Optional one-line summary of a call for the tool card ("Extrude 20 mm").</summary>
        public Func<Args, string> Describe { get; set; }
        public Func<Args, ToolContext, ToolOutput> Run { get; set; }
    }

    /// <summary>Per-call context handed to a tool implementation.</summary>
    public sealed class ToolContext
    {
        public string ToolUseId { get; set; }
        public CancellationToken Cancellation { get; set; }
        /// <summary>Lets tools tell the undo grouper which document they touched.</summary>
        public Action<object> TouchedDocument { get; set; }
    }

    public sealed class ToolOutput
    {
        public List<Block> Content { get; set; } = new List<Block>();
        public bool IsError { get; set; }
        /// <summary>Short past-tense summary for the UI card ("Extruded Sketch1 by 20 mm").</summary>
        public string Summary { get; set; }

        public static ToolOutput Text(string text, string summary = null) =>
            new ToolOutput { Content = { Block.OfText(text) }, Summary = summary };

        public static ToolOutput Obj(object value, string summary = null) =>
            new ToolOutput { Content = { Block.OfText(JsonSerializer.Serialize(value, Json.Options)) }, Summary = summary };

        public static ToolOutput Error(string message) =>
            new ToolOutput { Content = { Block.OfText(message) }, IsError = true, Summary = message };

        public ToolOutput WithImage(string mediaType, string base64)
        {
            Content.Add(Block.OfImage(mediaType, base64));
            return this;
        }
    }

    /// <summary>Raised by tools for expected, user-facing failures.</summary>
    public sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    /// <summary>Executes tool calls. The add-in implementation marshals onto the SOLIDWORKS UI thread.</summary>
    public interface IToolExecutor
    {
        IReadOnlyList<ToolSpec> GetTools();
        Task<ToolOutput> ExecuteAsync(ToolSpec spec, Args args, ToolContext context);
    }

    /// <summary>Simple executor that runs tools inline (tests, scripted mode).</summary>
    public sealed class InlineToolExecutor : IToolExecutor
    {
        private readonly List<ToolSpec> _tools;
        public InlineToolExecutor(IEnumerable<ToolSpec> tools) { _tools = tools.ToList(); }
        public IReadOnlyList<ToolSpec> GetTools() => _tools;
        public Task<ToolOutput> ExecuteAsync(ToolSpec spec, Args args, ToolContext context)
        {
            try { return Task.FromResult(spec.Run(args, context)); }
            catch (ToolException e) { return Task.FromResult(ToolOutput.Error(e.Message)); }
        }
    }
}
