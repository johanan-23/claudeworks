import { useEffect, useReducer } from "react";
import { onMessage, send } from "./bridge";
import type {
  AuthState, Block, ConfirmRequest, Conversation, ConversationSummary, ModelInfo, Settings, SwContext,
  Toast, ToolStatus, TurnOutcome, Usage,
} from "./types";

export interface Draft {
  blocks: Record<number, Block>;
}

export interface State {
  ready: boolean;
  version: string;
  demo: boolean;
  auth: AuthState;
  settings: Settings;
  models: ModelInfo[];
  conversations: ConversationSummary[];
  conversation: Conversation;
  busy: boolean;
  draft: Draft | null;
  tools: Record<string, ToolStatus>;
  confirms: Record<string, ConfirmRequest>;
  context: SwContext;
  swTheme: { mode: "dark" | "light" | null; background: string | null };
  outcome: TurnOutcome | null;
  usage: { last?: Usage; total?: Usage };
  toasts: Toast[];
  external: { client: string; tool: string; summary: string; isError: boolean; at: number } | null;
  mcp: { code: boolean; desktop: boolean; codeInstalled: boolean; desktopInstalled: boolean } | null;
}

const initial: State = {
  ready: false,
  version: "",
  demo: false,
  auth: { signedIn: false },
  settings: { model: "claude-opus-5-5", extendedThinking: false, theme: "system", autoContext: true, allowAdvancedApi: false, confirmDestructive: true, customInstructions: "" },
  models: [],
  conversations: [],
  conversation: { id: "", title: "New chat", messages: [] },
  busy: false,
  draft: null,
  tools: {},
  confirms: {},
  context: { doc: null, docType: null, selection: 0, selectionLabel: null },
  swTheme: { mode: null, background: null },
  outcome: null,
  usage: {},
  toasts: [],
  external: null,
  mcp: null,
};

let toastId = 0;

export function reducer(s: State, m: any): State {
  switch (m.type) {
    case "init":
      return {
        ...s, ready: true, version: m.version, demo: !!m.demo, auth: m.auth, settings: m.settings, models: m.models ?? [],
        conversations: m.conversations ?? [], conversation: m.conversation, swTheme: m.theme ?? s.swTheme,
        usage: { last: m.conversation?.last_usage, total: m.conversation?.usage },
      };
    case "auth": return { ...s, auth: { signedIn: m.signedIn, provider: m.provider, hint: m.hint, error: m.error, busy: m.busy } };
    case "settings": return { ...s, settings: m.settings };
    case "models": return { ...s, models: m.items ?? [] };
    case "conversations": return { ...s, conversations: m.items ?? [] };
    case "conversation":
      return { ...s, conversation: m.conversation, draft: null, outcome: null, tools: {}, confirms: {}, usage: { last: m.conversation?.last_usage, total: m.conversation?.usage } };
    case "context": return { ...s, context: { doc: m.doc, docType: m.docType, selection: m.selection ?? 0, selectionLabel: m.selectionLabel } };
    case "theme": return { ...s, swTheme: { mode: m.mode ?? null, background: m.background ?? null } };
    case "message": {
      const messages = s.conversation.messages.slice();
      const idx = typeof m.index === "number" && m.index >= 0 ? m.index : messages.length;
      messages[idx] = m.message;
      return { ...s, conversation: { ...s.conversation, messages }, draft: m.message.role === "assistant" ? null : s.draft };
    }
    case "turn.start": return { ...s, busy: true, outcome: null, draft: null };
    case "turn.end":
      return {
        ...s, busy: false, draft: null,
        outcome: { outcome: m.outcome, message: m.message, errorKind: m.errorKind, canUndo: m.canUndo },
        conversation: { ...s.conversation, title: m.title ?? s.conversation.title },
        usage: { last: m.usage ?? s.usage.last, total: m.totalUsage ?? s.usage.total },
      };
    case "undo.state": return s.outcome ? { ...s, outcome: { ...s.outcome, canUndo: m.canUndo } } : s;
    case "stream.request": return { ...s, draft: { blocks: {} } };
    case "stream.block": {
      const d = s.draft ?? { blocks: {} };
      return { ...s, draft: { blocks: { ...d.blocks, [m.index]: { ...m.block } } } };
    }
    case "stream.text":
    case "stream.thinking": {
      const d = s.draft ?? { blocks: {} };
      const b = d.blocks[m.index] ?? { type: m.type === "stream.text" ? "text" : "thinking" };
      const nb: Block = m.type === "stream.text" ? { ...b, text: (b.text ?? "") + m.text } : { ...b, thinking: (b.thinking ?? "") + m.text };
      return { ...s, draft: { blocks: { ...d.blocks, [m.index]: nb } } };
    }
    case "tool.start": return { ...s, tools: { ...s.tools, [m.id]: { status: "running", description: m.description } } };
    case "tool.end": return { ...s, tools: { ...s.tools, [m.id]: { ...s.tools[m.id], status: m.isError ? "error" : "ok", summary: m.summary } } };
    case "confirm": return { ...s, confirms: { ...s.confirms, [m.id]: { id: m.id, tool: m.tool, question: m.question } } };
    case "confirm.done": return { ...s, confirms: { ...s.confirms, [m.id]: { ...s.confirms[m.id], answered: true, approved: m.approved } } };
    case "usage": return { ...s, usage: { last: m.last, total: m.total } };
    case "toast": return { ...s, toasts: [...s.toasts, { id: ++toastId, message: m.message, kind: m.kind }] };
    case "error": return { ...s, toasts: [...s.toasts, { id: ++toastId, message: m.message, kind: "error" }] };
    case "mcp.status": return { ...s, mcp: { code: !!m.code, desktop: !!m.desktop, codeInstalled: !!m.codeInstalled, desktopInstalled: !!m.desktopInstalled } };
    case "external": return { ...s, external: { client: m.client, tool: m.tool, summary: m.summary, isError: !!m.isError, at: Date.now() } };
    case "toast.dismiss": return { ...s, toasts: s.toasts.filter((t) => t.id !== m.id) };
    default: return s;
  }
}

export function useAppState() {
  const [state, dispatch] = useReducer(reducer, initial);
  useEffect(() => {
    const off = onMessage(dispatch);
    send({ type: "ready" });
    return () => { off(); };
  }, []);
  return [state, dispatch] as const;
}

export function fmtTokens(n?: number) {
  if (!n) return "0";
  return n >= 1000 ? (n / 1000).toFixed(n >= 100000 ? 0 : 1) + "k" : String(n);
}
