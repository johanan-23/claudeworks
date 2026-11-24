using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;

namespace ClaudeSW.Core.Agent
{
    public sealed class ModelRequest
    {
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public IReadOnlyList<ToolSpec> Tools { get; set; } = new List<ToolSpec>();
        public IReadOnlyList<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        /// <summary>Show the model's reasoning summary and think harder (effort high).</summary>
        public bool ExtendedThinking { get; set; }
        public int MaxTokens { get; set; } = 32000;
    }

    public sealed class ModelResponse
    {
        public List<Block> Content { get; set; } = new List<Block>();
        public string StopReason { get; set; }
        public Usage Usage { get; set; } = new Usage();
        public string Model { get; set; }
        public string RefusalExplanation { get; set; }
    }

    /// <summary>Receives streaming deltas. Indices are the API's content block indices.</summary>
    public interface IStreamSink
    {
        void OnMessageStart(string model);
        void OnBlockStart(int index, Block block);
        void OnTextDelta(int index, string text);
        void OnThinkingDelta(int index, string text);
        void OnToolInputDelta(int index, string partialJson);
        void OnBlockStop(int index);
    }

    public sealed class ModelInfoLite
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("display_name")] public string DisplayName { get; set; }
    }

    public interface IModelClient
    {
        Task<ModelResponse> StreamAsync(ModelRequest request, AuthCredentials credentials, IStreamSink sink, CancellationToken ct);
        Task<IReadOnlyList<ModelInfoLite>> ListModelsAsync(AuthCredentials credentials, CancellationToken ct);
    }

    public enum ModelErrorKind { Auth, RateLimit, Overloaded, BadRequest, Network, Server, TooLarge, Billing, Unknown }

    /// <summary>Normalised API failure with a message that is safe to show the user.</summary>
    public sealed class ModelApiException : Exception
    {
        public ModelErrorKind Kind { get; }
        public TimeSpan? RetryAfter { get; }
        public ModelApiException(ModelErrorKind kind, string message, Exception inner = null, TimeSpan? retryAfter = null)
            : base(message, inner)
        {
            Kind = kind;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>Known Claude models, used when the Models API cannot be reached.</summary>
    public static class KnownModels
    {
        public const string Default = "claude-opus-5-5";

        public static readonly IReadOnlyList<ModelInfoLite> Fallback = new List<ModelInfoLite>
        {
            new ModelInfoLite { Id = "claude-opus-5-5", DisplayName = "Claude Opus 5.5" },
            new ModelInfoLite { Id = "claude-sonnet-5-5", DisplayName = "Claude Sonnet 5.5" },
            new ModelInfoLite { Id = "claude-haiku-5-5", DisplayName = "Claude Haiku 5.5" },
            new ModelInfoLite { Id = "claude-fable-5-2", DisplayName = "Claude Fable 5.2" },
        };

        /// <summary>Models that accept the server-side refusal fallback ("fallbacks": "default").</summary>
        public static bool SupportsServerFallback(string model) =>
            model != null && (model.StartsWith("claude-opus-5", StringComparison.Ordinal) ||
                              model.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal) ||
                              model.StartsWith("claude-fable-5", StringComparison.Ordinal));

        /// <summary>Models with adaptive thinking + effort (everything current).</summary>
        public static bool SupportsEffort(string model)
        {
            if (model == null || model.StartsWith("claude-3", StringComparison.Ordinal) || model.StartsWith("claude-haiku-4", StringComparison.Ordinal)) return false;
            // Adaptive thinking + effort arrived with the 4.6 generation; 4.5 and older reject them.
            foreach (var old in new[] { "-4-5", "-4-1", "-4-0", "claude-opus-4-2", "claude-sonnet-4-2" })
                if (model.Contains(old)) return false;
            return true;
        }
    }
}
