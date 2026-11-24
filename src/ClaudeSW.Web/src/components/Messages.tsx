import { useEffect, useMemo, useRef, useState } from "react";
import { send } from "../lib/bridge";
import type { State } from "../lib/store";
import type { Block, ConfirmRequest, Message, ToolStatus } from "../lib/types";
import { Alert, Check, Chevron, Copy, Doc, Pencil, Retry, Spark, Undo, X } from "./Icons";
import { Markdown, copyText } from "./Markdown";

type Group =
  | { kind: "human"; index: number; msg: Message }
  | { kind: "assistant"; items: { index: number; msg: Message }[] };

const isToolResults = (m: Message) => m.role === "user" && m.content.length > 0 && m.content.every((b) => b.type === "tool_result");

function groupMessages(messages: Message[]): Group[] {
  const groups: Group[] = [];
  messages.forEach((msg, index) => {
    if (!msg) return;
    if (msg.role === "user" && !isToolResults(msg)) { groups.push({ kind: "human", index, msg }); return; }
    const last = groups[groups.length - 1];
    if (last && last.kind === "assistant") last.items.push({ index, msg });
    else groups.push({ kind: "assistant", items: [{ index, msg }] });
  });
  return groups;
}

const TOOL_NAMES: Record<string, string> = {
  begin_sketch: "Start sketch", end_sketch: "Close sketch", capture_view: "Capture view", find_faces: "Find faces",
  find_edges: "Find edges", get_model_info: "Read mass properties", list_features: "Read feature tree",
};
const pretty = (name?: string) => TOOL_NAMES[name ?? ""] ?? (name ?? "tool").replace(/_/g, " ").replace(/^./, (c) => c.toUpperCase());

export function ImageViewer({ src, onClose }: { src: string; onClose: () => void }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [onClose]);
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-3" onClick={onClose} role="dialog" aria-label="Image">
      <img src={src} className="max-h-full max-w-full rounded-md shadow-2xl" alt="" />
      <button className="icon-btn absolute right-2 top-2 bg-black/40 text-white hover:bg-black/60" aria-label="Close"><X /></button>
    </div>
  );
}

function Thumb({ block, onOpen }: { block: Block; onOpen: (src: string) => void }) {
  const src = `data:${block.media_type ?? "image/png"};base64,${block.base64}`;
  return (
    <button className="mt-1.5 block overflow-hidden rounded-md shadow-[0_0_0_1px_rgb(var(--line))]" onClick={() => onOpen(src)} aria-label="Open image">
      <img src={src} alt={block.file_name ?? "screenshot"} className="block max-h-56 w-full object-contain bg-white" />
    </button>
  );
}

function Thinking({ text, live }: { text: string; live: boolean }) {
  const [open, setOpen] = useState(false);
  if (!text && !live) return null;
  return (
    <div className="text-sm">
      <button className="inline-flex items-center gap-1 text-muted hover:text-ink" onClick={() => setOpen(!open)} aria-expanded={open}>
        <Chevron size={13} className={`transition-transform ${open ? "rotate-90" : ""}`} />
        {live ? <span className="breathe">Thinking</span> : "Thought process"}
      </button>
      {open && text && <div className="mt-1 whitespace-pre-wrap border-l border-line pl-3 text-muted">{text}</div>}
    </div>
  );
}

function StatusIcon({ status }: { status?: ToolStatus["status"] | "pending" }) {
  if (status === "running" || status === "pending")
    return <span className="spin inline-block h-3 w-3 rounded-full border-[1.5px] border-line border-t-clay" aria-label="running" />;
  if (status === "error") return <X size={13} className="text-bad" aria-label="failed" />;
  return <Check size={13} className="text-ok" aria-label="done" />;
}

function RailRow({ use, result, status, onOpen }: { use: Block; result?: Block; status?: ToolStatus; onOpen: (s: string) => void }) {
  const [open, setOpen] = useState(false);
  const state: ToolStatus["status"] | "pending" = status?.status ?? (result ? (result.is_error ? "error" : "ok") : "pending");
  const label = (state === "ok" || state === "error" ? result?.ui_summary ?? status?.summary : null) ?? status?.description ?? pretty(use.name);
  const images = result?.content?.filter((b) => b.type === "image" && b.base64) ?? [];
  const resultText = result?.content?.filter((b) => b.type === "text").map((b) => b.text).join("\n");
  let input = use.input ?? "{}";
  try { input = JSON.stringify(JSON.parse(input), null, 1); } catch { /* streaming */ }
  return (
    <li className="rail-row py-[3px]">
      <button className="flex w-full items-start gap-1.5 text-left text-sm" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span className="mt-[3px] flex h-3.5 w-3.5 shrink-0 items-center justify-center"><StatusIcon status={state} /></span>
        <span className={`min-w-0 flex-1 ${state === "error" ? "text-bad" : state === "running" || state === "pending" ? "text-muted" : "text-ink"}`}>{label}</span>
        <Chevron size={12} className={`mt-[3px] shrink-0 text-faint transition-transform ${open ? "rotate-90" : ""}`} />
      </button>
      {open && (
        <div className="mb-1 ml-5 mt-1 space-y-1 rounded-md bg-sunk px-2 py-1.5 font-mono text-[11px] leading-4 text-muted">
          <div className="text-faint">{use.name}</div>
          <pre className="whitespace-pre-wrap break-all">{input}</pre>
          {resultText && <pre className={`whitespace-pre-wrap break-all border-t border-line pt-1 ${result?.is_error ? "text-bad" : ""}`}>{resultText.length > 1500 ? resultText.slice(0, 1500) + "…" : resultText}</pre>}
        </div>
      )}
      {images.map((img, i) => <div key={i} className="ml-5"><Thumb block={img} onOpen={onOpen} /></div>)}
    </li>
  );
}

function ConfirmCard({ c }: { c: ConfirmRequest }) {
  const ref = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (!c.answered) ref.current?.focus(); }, [c.answered]);
  if (c.answered) return null;
  return (
    <div className="mt-2 rounded-md bg-surface p-2.5 shadow-[0_0_0_1px_rgb(var(--warn)/0.45)]" role="alertdialog" aria-label="Confirm action">
      <div className="flex items-start gap-2">
        <Alert size={15} className="mt-0.5 shrink-0 text-warn" />
        <div className="min-w-0 flex-1 whitespace-pre-wrap text-sm">{c.question}</div>
      </div>
      <div className="mt-2 flex justify-end gap-1.5">
        <button className="btn btn-ghost" onClick={() => send({ type: "confirm.answer", id: c.id, approved: false })}>Don't allow</button>
        <button ref={ref} className="btn btn-primary" onClick={() => send({ type: "confirm.answer", id: c.id, approved: true })}>Allow</button>
      </div>
    </div>
  );
}

/** Turns an ordered block list into prose / thinking / rail segments. */
function Segments({ blocks, results, tools, live, onOpen }: {
  blocks: Block[]; results: Map<string, Block>; tools: Record<string, ToolStatus>; live: boolean; onOpen: (s: string) => void;
}) {
  const out: JSX.Element[] = [];
  let rail: Block[] = [];
  const flush = (key: string) => {
    if (!rail.length) return;
    const r = rail; rail = [];
    out.push(
      <ul key={key} className="rail my-1">
        {r.map((u) => <RailRow key={u.id} use={u} result={results.get(u.id!)} status={tools[u.id!]} onOpen={onOpen} />)}
      </ul>,
    );
  };
  blocks.forEach((b, i) => {
    if (b.type === "tool_use") { rail.push(b); return; }
    flush("r" + i);
    if (b.type === "text" && b.text) out.push(<div key={i} className={live && i === blocks.length - 1 ? "caret-host" : ""}><Markdown text={b.text} /></div>);
    else if (b.type === "thinking") out.push(<Thinking key={i} text={b.thinking ?? ""} live={live && i === blocks.length - 1} />);
    else if (b.type === "fallback") out.push(<div key={i} className="text-xs text-faint">Continued with {b.to_model ?? "another model"}.</div>);
  });
  flush("rend");
  return <div className="space-y-2">{out}</div>;
}

function HumanBubble({ msg, index, busy }: { msg: Message; index: number; busy: boolean }) {
  const visible = msg.content.filter((b) => !b.ui_hidden);
  const text = visible.filter((b) => b.type === "text" && !b.file_name).map((b) => b.text).join("\n\n");
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(text);
  const [open, setOpen] = useState<string | null>(null);
  useEffect(() => {
    const h = (e: Event) => { if ((e as CustomEvent).detail === index && !busy) { setDraft(text); setEditing(true); } };
    window.addEventListener("edit-message", h);
    return () => window.removeEventListener("edit-message", h);
  }, [index, text, busy]);
  if (editing)
    return (
      <div className="ml-6 rounded-lg bg-surface p-2 shadow-[0_0_0_1px_rgb(var(--line))]">
        <textarea
          className="w-full resize-none bg-transparent text-base outline-none" rows={Math.min(8, draft.split("\n").length + 1)} value={draft} autoFocus
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") setEditing(false);
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); if (draft.trim()) { send({ type: "chat.edit", messageIndex: index, text: draft }); setEditing(false); } }
          }}
        />
        <div className="flex justify-end gap-1.5">
          <button className="btn btn-ghost" onClick={() => setEditing(false)}>Cancel</button>
          <button className="btn btn-primary" disabled={!draft.trim()} onClick={() => { send({ type: "chat.edit", messageIndex: index, text: draft }); setEditing(false); }}>Send</button>
        </div>
      </div>
    );
  return (
    <div className="group flex flex-col items-end">
      <div className="max-w-[88%] rounded-lg rounded-br-sm bg-sunk px-3 py-1.5">
        {visible.filter((b) => b.type === "image").map((b, i) => (
          <button key={i} onClick={() => setOpen(`data:${b.media_type};base64,${b.base64}`)} className="mb-1 block">
            <img src={`data:${b.media_type};base64,${b.base64}`} alt={b.file_name ?? "image"} className="max-h-32 rounded" />
          </button>
        ))}
        {visible.filter((b) => b.type === "document" || (b.type === "text" && b.file_name)).map((b, i) => (
          <div key={i} className="mb-1 flex items-center gap-1 text-sm text-muted"><Doc size={13} />{b.file_name}</div>
        ))}
        {text && <div className="whitespace-pre-wrap break-words">{text}</div>}
      </div>
      <div className="mt-0.5 flex gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100">
        <button className="icon-btn h-6 w-6" title="Copy" aria-label="Copy message" onClick={() => copyText(text)}><Copy size={13} /></button>
        {!busy && <button className="icon-btn h-6 w-6" title="Edit and resend" aria-label="Edit message" onClick={() => { setDraft(text); setEditing(true); }}><Pencil size={13} /></button>}
      </div>
      {open && <ImageViewer src={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

function Outcome({ s }: { s: State }) {
  const o = s.outcome;
  if (!o || o.outcome === "completed") return null;
  if (o.outcome === "cancelled") return <div className="text-sm text-faint">Stopped.</div>;
  const hint =
    o.errorKind === "auth" ? "Open Settings to update your API key." :
    o.errorKind === "network" ? "Check your connection, then retry." :
    o.errorKind === "ratelimit" || o.errorKind === "overloaded" ? "Wait a few seconds, then retry." : null;
  return (
    <div className={`rounded-md px-2.5 py-2 text-sm ${o.outcome === "failed" ? "bg-bad/10 text-bad" : "bg-sunk text-muted"}`} role="status">
      <div className="flex items-start gap-1.5"><Alert size={14} className="mt-0.5 shrink-0" /><span>{o.message}{hint ? " " + hint : ""}</span></div>
      <div className="mt-1.5 flex gap-1.5">
        {(o.outcome === "failed" || o.outcome === "maxtokens") && (
          <button className="btn btn-ghost text-ink" onClick={() => send({ type: "chat.regenerate" })}><Retry size={13} />Retry</button>
        )}
        {o.errorKind === "billing" && (
          <button className="btn btn-ghost text-ink" onClick={() => send({ type: "link.open", url: "https://console.anthropic.com/settings/billing" })}>Open Plans & Billing</button>
        )}
      </div>
    </div>
  );
}

export function Messages({ s }: { s: State }) {
  const { messages } = s.conversation;
  const groups = useMemo(() => groupMessages(messages), [messages]);
  const results = useMemo(() => {
    const m = new Map<string, Block>();
    messages.forEach((msg) => msg?.content.forEach((b) => { if (b.type === "tool_result" && b.tool_use_id) m.set(b.tool_use_id, b); }));
    return m;
  }, [messages]);
  const [viewer, setViewer] = useState<string | null>(null);
  const [copied, setCopied] = useState<number | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const stick = useRef(true);

  useEffect(() => {
    if (stick.current) bottom.current?.scrollIntoView({ block: "end" });
  });

  const draftBlocks = s.draft ? Object.keys(s.draft.blocks).map(Number).sort((a, b) => a - b).map((k) => s.draft!.blocks[k]) : [];
  const lastGroup = groups[groups.length - 1];
  const pendingConfirms = Object.values(s.confirms).filter((c) => !c.answered);
  const liveTail = s.busy && (!lastGroup || lastGroup.kind === "human");

  const assistantText = (g: Extract<Group, { kind: "assistant" }>) =>
    g.items.flatMap((i) => i.msg.content).filter((b) => b.type === "text" && b.text).map((b) => b.text).join("\n\n");

  return (
    <div
      ref={scroller}
      className="flex-1 overflow-y-auto px-3 pb-4 pt-3"
      onScroll={(e) => { const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60; }}
      aria-live="polite"
    >
      <div className="mx-auto max-w-[760px] space-y-4">
        {groups.map((g, gi) => {
          if (g.kind === "human") return <HumanBubble key={g.index} msg={g.msg} index={g.index} busy={s.busy} />;
          const isLast = gi === groups.length - 1;
          const blocks = g.items.filter((i) => i.msg.role === "assistant").flatMap((i) => i.msg.content);
          const live = isLast && s.busy;
          return (
            <div key={"a" + g.items[0].index} className="group/turn">
              <div className="mb-1 flex items-center gap-1.5 text-clay"><Spark size={14} className={live ? "breathe" : ""} /></div>
              <Segments blocks={live ? [...blocks, ...draftBlocks] : blocks} results={results} tools={s.tools} live={live} onOpen={setViewer} />
              {live && pendingConfirms.map((c) => <ConfirmCard key={c.id} c={c} />)}
              {!live && (
                <div className="mt-1 flex gap-0.5 text-muted">
                  <button className="icon-btn h-6 w-6" title="Copy" aria-label="Copy reply" onClick={() => { copyText(assistantText(g)); setCopied(gi); setTimeout(() => setCopied(null), 1400); }}>
                    {copied === gi ? <Check size={13} /> : <Copy size={13} />}
                  </button>
                  {isLast && !s.busy && <button className="icon-btn h-6 w-6" title="Regenerate" aria-label="Regenerate reply" onClick={() => send({ type: "chat.regenerate" })}><Retry size={13} /></button>}
                  {isLast && !s.busy && s.outcome?.canUndo && (
                    <button className="btn ml-1 h-6 px-2 py-0 text-xs text-muted hover:bg-sunk hover:text-ink" onClick={() => send({ type: "undo.last" })} title="Undo everything Claude changed in this reply">
                      <Undo size={13} />Undo changes
                    </button>
                  )}
                </div>
              )}
            </div>
          );
        })}
        {liveTail && (
          <div>
            <div className="mb-1 text-clay"><Spark size={14} className="breathe" /></div>
            {draftBlocks.length > 0
              ? <Segments blocks={draftBlocks} results={results} tools={s.tools} live onOpen={setViewer} />
              : <div className="text-sm text-faint">Working on it</div>}
            {pendingConfirms.map((c) => <ConfirmCard key={c.id} c={c} />)}
          </div>
        )}
        {!s.busy && <Outcome s={s} />}
        <div ref={bottom} />
      </div>
      {viewer && <ImageViewer src={viewer} onClose={() => setViewer(null)} />}
    </div>
  );
}
