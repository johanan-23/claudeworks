import { useEffect, useRef, useState } from "react";
import { send } from "../lib/bridge";
import type { State } from "../lib/store";
import type { Settings } from "../lib/types";
import { Gear, Menu, Pencil, Plus, Search, Trash, X } from "./Icons";
import { McpSetup } from "./McpSetup";

export function modelName(s: State, id: string) {
  const m = s.models.find((x) => x.id === id);
  return m?.display_name ?? m?.displayName ?? id;
}

export function Header({ s, onHistory, onSettings }: { s: State; onHistory: () => void; onSettings: () => void }) {
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(s.conversation.title);
  useEffect(() => setTitle(s.conversation.title), [s.conversation.title]);
  const canRename = s.conversation.messages.length > 0;
  const commit = () => {
    setEditing(false);
    if (title.trim() && title !== s.conversation.title) send({ type: "conv.rename", id: s.conversation.id, title: title.trim() });
  };
  return (
    <header className="flex h-10 shrink-0 items-center gap-0.5 border-b border-line px-1.5">
      <button className="icon-btn" title="Chats (Ctrl+K)" aria-label="Open chat history" onClick={onHistory}><Menu /></button>
      <div className="min-w-0 flex-1 px-1">
        {editing ? (
          <input
            className="field h-7 py-0 text-sm" value={title} autoFocus aria-label="Chat title"
            onChange={(e) => setTitle(e.target.value)} onBlur={commit}
            onKeyDown={(e) => { if (e.key === "Enter") commit(); if (e.key === "Escape") { setTitle(s.conversation.title); setEditing(false); } }}
          />
        ) : (
          <button
            className="block max-w-full truncate rounded px-1 text-left text-sm font-medium hover:bg-sunk disabled:hover:bg-transparent"
            disabled={!canRename} title={canRename ? "Rename chat" : undefined} onClick={() => setEditing(true)}
          >
            {s.conversation.title || "New chat"}
          </button>
        )}
      </div>
      <select
        className="h-7 max-w-[130px] truncate rounded-md bg-paper px-1 text-xs text-ink hover:bg-sunk focus:outline-none"
        value={s.settings.model} aria-label="Model" title="Model"
        onChange={(e) => send({ type: "settings.set", settings: { model: e.target.value } })}
      >
        {(s.models.length ? s.models : [{ id: s.settings.model }]).map((m) => (
          <option key={m.id} value={m.id}>{modelName(s, m.id)}</option>
        ))}
      </select>
      <button className="icon-btn" title="Settings" aria-label="Settings" onClick={onSettings}><Gear /></button>
      <button className="icon-btn" title="New chat (Ctrl+N)" aria-label="New chat" disabled={s.busy} onClick={() => send({ type: "conv.new" })}><Plus /></button>
    </header>
  );
}

function Sheet({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", k);
    (ref.current?.querySelector<HTMLElement>("input") ?? ref.current?.querySelector<HTMLElement>("button"))?.focus();
    return () => window.removeEventListener("keydown", k);
  }, [onClose]);
  return (
    <div className="absolute inset-0 z-30 flex" role="dialog" aria-label={title}>
      <div ref={ref} className="flex h-full w-[min(340px,92%)] flex-col bg-paper shadow-[1px_0_0_rgb(var(--line)),8px_0_24px_rgb(0_0_0/0.12)]">
        <div className="flex h-10 shrink-0 items-center justify-between border-b border-line pl-3 pr-1.5">
          <h2 className="text-sm font-semibold">{title}</h2>
          <button className="icon-btn" aria-label="Close" onClick={onClose}><X /></button>
        </div>
        {children}
      </div>
      <div className="flex-1 bg-black/20" onClick={onClose} />
    </div>
  );
}

function when(iso: string) {
  const d = new Date(iso);
  const days = Math.floor((Date.now() - d.getTime()) / 86400e3);
  if (days < 1) return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  if (days < 7) return d.toLocaleDateString([], { weekday: "short" });
  return d.toLocaleDateString([], { day: "numeric", month: "short" });
}

export function History({ s, onClose }: { s: State; onClose: () => void }) {
  const [q, setQ] = useState("");
  const [renaming, setRenaming] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [confirmDel, setConfirmDel] = useState<string | null>(null);
  useEffect(() => {
    const t = setTimeout(() => send({ type: "conv.list", search: q }), 180);
    return () => clearTimeout(t);
  }, [q]);
  return (
    <Sheet title="Chats" onClose={onClose}>
      <div className="p-2">
        <div className="relative">
          <Search size={14} className="absolute left-2 top-2 text-faint" />
          <input className="field pl-7 text-sm" placeholder="Search chats" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search chats" />
        </div>
      </div>
      <ul className="flex-1 overflow-y-auto px-1 pb-2">
        {s.conversations.length === 0 && <li className="px-3 py-6 text-center text-sm text-muted">{q ? "No chats match." : "Your chats will appear here."}</li>}
        {s.conversations.map((c) => (
          <li key={c.id} className={`group rounded-md ${c.id === s.conversation.id ? "bg-sunk" : "hover:bg-sunk/70"}`}>
            {renaming === c.id ? (
              <div className="p-1.5">
                <input
                  className="field text-sm" value={name} autoFocus aria-label="New name"
                  onChange={(e) => setName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") { send({ type: "conv.rename", id: c.id, title: name }); setRenaming(null); }
                    if (e.key === "Escape") { e.stopPropagation(); setRenaming(null); }
                  }}
                  onBlur={() => setRenaming(null)}
                />
              </div>
            ) : confirmDel === c.id ? (
              <div className="flex items-center gap-1.5 px-2 py-1.5 text-sm">
                <span className="flex-1">Delete this chat?</span>
                <button className="btn btn-ghost py-0.5" onClick={() => setConfirmDel(null)}>Keep</button>
                <button className="btn bg-bad py-0.5 text-white" onClick={() => { send({ type: "conv.delete", id: c.id }); setConfirmDel(null); }}>Delete</button>
              </div>
            ) : (
              <div className="flex items-start">
                <button className="min-w-0 flex-1 px-2 py-1.5 text-left" disabled={s.busy} onClick={() => { send({ type: "conv.open", id: c.id }); onClose(); }}>
                  <span className="flex items-baseline gap-2">
                    <span className="min-w-0 flex-1 truncate text-sm font-medium">{c.title}</span>
                    <span className="shrink-0 text-xs text-faint">{when(c.updated)}</span>
                  </span>
                  {c.preview && <span className="block truncate text-xs text-muted">{c.preview}</span>}
                </button>
                <div className="flex shrink-0 pr-1 pt-1 opacity-0 group-focus-within:opacity-100 group-hover:opacity-100">
                  <button className="icon-btn h-6 w-6" aria-label={`Rename ${c.title}`} onClick={() => { setName(c.title); setRenaming(c.id); }}><Pencil size={13} /></button>
                  <button className="icon-btn h-6 w-6" aria-label={`Delete ${c.title}`} onClick={() => setConfirmDel(c.id)}><Trash size={13} /></button>
                </div>
              </div>
            )}
          </li>
        ))}
      </ul>
    </Sheet>
  );
}

function Toggle({ label, help, checked, onChange }: { label: string; help?: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex cursor-default items-start gap-3 py-2">
      <span className="min-w-0 flex-1">
        <span className="block text-sm font-medium">{label}</span>
        {help && <span className="block text-xs text-muted">{help}</span>}
      </span>
      <input type="checkbox" className="peer sr-only" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className={`relative mt-0.5 h-[18px] w-8 shrink-0 rounded-full transition-colors peer-focus-visible:outline peer-focus-visible:outline-2 peer-focus-visible:outline-clay ${checked ? "bg-clay" : "bg-line"}`}>
        <span className={`absolute top-[2px] h-[14px] w-[14px] rounded-full bg-white shadow transition-transform ${checked ? "translate-x-[16px]" : "translate-x-[2px]"}`} />
      </span>
    </label>
  );
}

export function SettingsPanel({ s, onClose }: { s: State; onClose: () => void }) {
  const set = (p: Partial<Settings>) => send({ type: "settings.set", settings: p });
  const [instr, setInstr] = useState(s.settings.customInstructions);
  return (
    <Sheet title="Settings" onClose={onClose}>
      <div className="flex-1 space-y-4 overflow-y-auto px-3 py-3">
        <section>
          <label className="block text-sm font-medium" htmlFor="model">Model</label>
          <select id="model" className="field mt-1 text-sm" value={s.settings.model} onChange={(e) => set({ model: e.target.value })}>
            {(s.models.length ? s.models : [{ id: s.settings.model }]).map((m) => <option key={m.id} value={m.id}>{modelName(s, m.id)}</option>)}
          </select>
          <div className="divide-y divide-line">
            <Toggle label="Extended thinking" help="Think longer before acting and show a summary of the reasoning. Slower, uses more tokens."
              checked={s.settings.extendedThinking} onChange={(v) => set({ extendedThinking: v })} />
            <Toggle label="Share model context automatically" help="Send the active document, feature tree and selection with each message."
              checked={s.settings.autoContext} onChange={(v) => set({ autoContext: v })} />
            <Toggle label="Ask before destructive actions" help="Confirm before deleting features or components, overwriting files or closing with unsaved changes."
              checked={s.settings.confirmDestructive} onChange={(v) => set({ confirmDestructive: v })} />
            <Toggle label="Allow direct API calls" help="Advanced: lets Claude call any SOLIDWORKS API member. Every call still asks for approval."
              checked={s.settings.allowAdvancedApi} onChange={(v) => set({ allowAdvancedApi: v })} />
          </div>
        </section>
        <section className="border-t border-line pt-3">
          <Toggle label="Let Claude Code and Claude Desktop control SOLIDWORKS" help="On your Claude plan, no API key. You chat in their window; their actions show up here."
            checked={s.settings.allowExternalClients ?? true} onChange={(v) => set({ allowExternalClients: v })} />
          {(s.settings.allowExternalClients ?? true) && (
            <div className="space-y-1.5">
              <McpSetup s={s} />
              <p className="text-xs text-muted">Or by hand:</p>
              <code className="block select-all break-all rounded bg-sunk px-2 py-1 font-mono text-[11px] text-ink">claude mcp add --scope user solidworks-addin -- "{s.settings.mcpExe ?? "ClaudeSW.Mcp.exe"}"</code>
            </div>
          )}
        </section>
        <section>
          <span className="block text-sm font-medium">Appearance</span>
          <div className="mt-1 grid grid-cols-3 gap-1 rounded-md bg-sunk p-0.5" role="radiogroup" aria-label="Appearance">
            {(["system", "light", "dark"] as const).map((t) => (
              <button key={t} role="radio" aria-checked={s.settings.theme === t}
                className={`rounded px-2 py-1 text-sm ${s.settings.theme === t ? "bg-surface shadow-sm" : "text-muted hover:text-ink"}`}
                onClick={() => set({ theme: t })}>
                {t === "system" ? "SOLIDWORKS" : t === "light" ? "Light" : "Dark"}
              </button>
            ))}
          </div>
        </section>
        <section>
          <label className="block text-sm font-medium" htmlFor="instr">Instructions for Claude</label>
          <p className="text-xs text-muted">Standing preferences, e.g. "Use ISO metric hole sizes" or "Name features descriptively".</p>
          <textarea id="instr" className="field mt-1 min-h-[72px] text-sm" value={instr} onChange={(e) => setInstr(e.target.value)}
            onBlur={() => instr !== s.settings.customInstructions && set({ customInstructions: instr })} />
        </section>
        <section className="border-t border-line pt-3">
          <span className="block text-sm font-medium">Account</span>
          <p className="mt-0.5 text-sm text-muted">{s.demo ? "Demo mode: replies come from a script, nothing is sent to Anthropic." : <>API key <span className="font-mono">{s.auth.hint}</span></>}</p>
          {!s.demo && <button className="btn btn-ghost mt-2" onClick={() => { send({ type: "auth.signOut" }); onClose(); }}>Sign out</button>}
        </section>
        <section className="border-t border-line pt-3 text-xs text-faint">
          <p>ClaudeWorks {s.version}. Chats are stored in %APPDATA%\ClaudeForSolidWorks. Keyboard: Enter send, Shift+Enter new line, Esc stop, ↑ edit last message, Ctrl+N new chat, Ctrl+K chats.</p>
          <p className="mt-1">Not available here: voice, memory, Projects and Artifacts (these are claude.ai features, not part of the API).</p>
        </section>
      </div>
    </Sheet>
  );
}
