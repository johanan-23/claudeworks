export type BlockType =
  | "text" | "thinking" | "redacted_thinking" | "tool_use" | "tool_result" | "image" | "document" | "fallback";

export interface Block {
  type: BlockType;
  text?: string;
  thinking?: string;
  signature?: string;
  id?: string;
  name?: string;
  input?: string;
  tool_use_id?: string;
  is_error?: boolean;
  content?: Block[];
  media_type?: string;
  base64?: string;
  file_name?: string;
  from_model?: string;
  to_model?: string;
  ui_hidden?: boolean;
  ui_summary?: string;
}

export interface Usage {
  input_tokens: number;
  output_tokens: number;
  cache_read_input_tokens: number;
  cache_creation_input_tokens: number;
}

export interface Message {
  role: "user" | "assistant";
  content: Block[];
  created?: string;
  model?: string;
  usage?: Usage;
  stop_reason?: string;
}

export interface Conversation {
  id: string;
  title: string;
  created?: string;
  updated?: string;
  model?: string;
  messages: Message[];
  usage?: Usage;
  last_usage?: Usage;
}

export interface ConversationSummary {
  id: string;
  title: string;
  updated: string;
  preview?: string;
}

export interface Settings {
  model: string;
  extendedThinking: boolean;
  theme: "system" | "light" | "dark";
  autoContext: boolean;
  allowAdvancedApi: boolean;
  confirmDestructive: boolean;
  customInstructions: string;
  allowExternalClients?: boolean;
  mcpExe?: string;
}

export interface AuthState {
  signedIn: boolean;
  provider?: string;
  hint?: string | null;
  error?: string | null;
  busy?: boolean;
}

export interface ModelInfo {
  id: string;
  display_name?: string;
  displayName?: string;
}

export interface SwContext {
  doc: string | null;
  docType: string | null;
  selection: number;
  selectionLabel: string | null;
}

export interface ToolStatus {
  status: "running" | "ok" | "error";
  description?: string;
  summary?: string;
}

export interface ConfirmRequest {
  id: string;
  tool: string;
  question: string;
  answered?: boolean;
  approved?: boolean;
}

export interface TurnOutcome {
  outcome: "completed" | "cancelled" | "refused" | "failed" | "iterationlimit" | "maxtokens";
  message?: string | null;
  errorKind?: string | null;
  canUndo?: boolean;
}

export interface Attachment {
  name: string;
  mediaType: string;
  base64: string;
  size: number;
}

export interface Toast {
  id: number;
  message: string;
  kind?: "info" | "error";
}
