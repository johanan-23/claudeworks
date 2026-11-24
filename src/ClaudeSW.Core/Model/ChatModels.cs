using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSW.Core.Model
{
    /// <summary>
    /// Provider-neutral content block. This is what we persist and what the UI renders; the
    /// Anthropic client converts it to/from SDK types. Field names mirror the Messages API.
    /// </summary>
    public sealed class Block
    {
        public const string TextType = "text";
        public const string ThinkingType = "thinking";
        public const string RedactedThinkingType = "redacted_thinking";
        public const string ToolUseType = "tool_use";
        public const string ToolResultType = "tool_result";
        public const string ImageType = "image";
        public const string DocumentType = "document";
        public const string FallbackType = "fallback";

        [JsonPropertyName("type")] public string Type { get; set; }

        // text
        [JsonPropertyName("text")] public string Text { get; set; }

        // thinking
        [JsonPropertyName("thinking")] public string Thinking { get; set; }
        [JsonPropertyName("signature")] public string Signature { get; set; }
        [JsonPropertyName("data")] public string Data { get; set; } // redacted_thinking

        // tool_use
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        /// <summary>Raw JSON object text of the tool input.</summary>
        [JsonPropertyName("input")] public string InputJson { get; set; }

        // tool_result
        [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; }
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }
        [JsonPropertyName("content")] public List<Block> Content { get; set; }

        // image / document (base64)
        [JsonPropertyName("media_type")] public string MediaType { get; set; }
        [JsonPropertyName("base64")] public string Base64 { get; set; }
        [JsonPropertyName("file_name")] public string FileName { get; set; }

        // fallback (server-side refusal fallback switch point)
        [JsonPropertyName("from_model")] public string FromModel { get; set; }
        [JsonPropertyName("to_model")] public string ToModel { get; set; }

        // UI-only metadata (never sent to the API)
        /// <summary>Hidden context text injected by the add-in (selection, doc state).</summary>
        [JsonPropertyName("ui_hidden")] public bool? UiHidden { get; set; }
        /// <summary>Short human summary for tool results ("Extruded Sketch1 by 20 mm").</summary>
        [JsonPropertyName("ui_summary")] public string UiSummary { get; set; }

        public static Block OfText(string text) => new Block { Type = TextType, Text = text };
        public static Block OfImage(string mediaType, string base64, string fileName = null) =>
            new Block { Type = ImageType, MediaType = mediaType, Base64 = base64, FileName = fileName };
        public static Block OfDocument(string base64, string fileName) =>
            new Block { Type = DocumentType, MediaType = "application/pdf", Base64 = base64, FileName = fileName };
        public static Block OfToolUse(string id, string name, string inputJson) =>
            new Block { Type = ToolUseType, Id = id, Name = name, InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson };

        public Block Clone() => JsonSerializer.Deserialize<Block>(JsonSerializer.Serialize(this, Json.Options), Json.Options);
    }

    public sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } // "user" | "assistant"
        [JsonPropertyName("content")] public List<Block> Content { get; set; } = new List<Block>();
        [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>Model that produced an assistant message.</summary>
        [JsonPropertyName("model")] public string Model { get; set; }
        [JsonPropertyName("usage")] public Usage Usage { get; set; }
        [JsonPropertyName("stop_reason")] public string StopReason { get; set; }

        [JsonIgnore] public bool IsToolResultMessage =>
            Role == "user" && Content.Count > 0 && Content.All(b => b.Type == Block.ToolResultType);

        /// <summary>A user message the human typed (not an auto tool-result message).</summary>
        [JsonIgnore] public bool IsHumanTurn => Role == "user" && !IsToolResultMessage;

        public static ChatMessage User(params Block[] blocks) => new ChatMessage { Role = "user", Content = blocks.ToList() };
        public static ChatMessage Assistant(params Block[] blocks) => new ChatMessage { Role = "assistant", Content = blocks.ToList() };
    }

    public sealed class Usage
    {
        [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public long CacheReadInputTokens { get; set; }
        [JsonPropertyName("cache_creation_input_tokens")] public long CacheCreationInputTokens { get; set; }

        public void Add(Usage other)
        {
            if (other == null) return;
            InputTokens += other.InputTokens;
            OutputTokens += other.OutputTokens;
            CacheReadInputTokens += other.CacheReadInputTokens;
            CacheCreationInputTokens += other.CacheCreationInputTokens;
        }

        /// <summary>Total prompt size of the last request (what fills the context window).</summary>
        [JsonIgnore] public long PromptTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
    }

    public sealed class Conversation
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonPropertyName("title")] public string Title { get; set; } = "New chat";
        [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
        [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
        [JsonPropertyName("model")] public string Model { get; set; }
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        /// <summary>Usage accumulated over the whole conversation.</summary>
        [JsonPropertyName("usage")] public Usage TotalUsage { get; set; } = new Usage();
        /// <summary>Usage of the most recent API request (context size indicator).</summary>
        [JsonPropertyName("last_usage")] public Usage LastUsage { get; set; }
    }

    public sealed class ConversationSummary
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; }
        [JsonPropertyName("preview")] public string Preview { get; set; }
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
