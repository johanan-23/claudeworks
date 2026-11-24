import { useEffect, useRef, useState } from "react";
import { send } from "../lib/bridge";
import { fmtTokens, type State } from "../lib/store";
import type { Attachment } from "../lib/types";
import { ArrowUp, Clip, Cursor, Doc, Stop, X } from "./Icons";

const MAX_IMAGE = 5 * 1024 * 1024;
const MAX_PDF = 24 * 1024 * 1024;
const MAX_TEXT = 512 * 1024;
const TEXT_EXT = /\.(txt|md|csv|json|xml|log|ini|cfg|yaml|yml|vba|swp|bas|cs|py|js|ts)$/i;

function readFile(f: File): Promise<Attachment> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve({ name: f.name || "pasted.png", mediaType: f.type || "text/plain", base64: String(r.result).split(",")[1] ?? "", size: f.size });
    r.onerror = () => reject(r.error);
    r.readAsDataURL(f);
  });
}

export function Composer({ s, toast }: { s: State; toast: (m: string) => void }) {
  const [text, setText] = useState("");
  const [files, setFiles] = useState<Attachment[]>([]);
  const [useSel, setUseSel] = useState(false);
  const [drag, setDrag] = useState(false);
  const ta = useRef<HTMLTextAreaElement>(null);
  const picker = useRef<HTMLInputElement>(null);
  const ctx = s.context;

  useEffect(() => { if (ctx.selection === 0) setUseSel(false); }, [ctx.selection]);
  useEffect(() => {
    const el = ta.current; if (!el) return;
    el.style.height = "auto";
    el.style.height = Math.min(el.scrollHeight, 200) + "px";
  }, [text]);
  useEffect(() => {
    const f = (e: Event) => { setText((e as CustomEvent).detail ?? ""); ta.current?.focus(); };
    window.addEventListener("composer-fill", f);
    return () => window.removeEventListener("composer-fill", f);
  }, []);
  useEffect(() => { if (!s.busy) ta.current?.focus(); }, [s.busy, s.conversation.id]);

  async function add(list: FileList | File[]) {
    for (const f of Array.from(list)) {
      const isImg = /^image\/(png|jpe?g|gif|webp)$/.test(f.type);
      const isPdf = f.type === "application/pdf";
      const isText = f.type.startsWith("text/") || TEXT_EXT.test(f.name);
      if (!isImg && !isPdf && !isText) { toast(`${f.name}: only images, PDFs and text files can be attached.`); continue; }
      const limit = isImg ? MAX_IMAGE : isPdf ? MAX_PDF : MAX_TEXT;
      if (f.size > limit) { toast(`${f.name} is too large (max ${Math.round(limit / 1048576)} MB).`); continue; }
      try {
        const a = await readFile(f);
        if (isText && !a.mediaType.startsWith("text/")) a.mediaType = "text/plain";
        setFiles((prev) => [...prev, a]);
      } catch { toast(`Couldn't read ${f.name}.`); }
    }
  }

  function submit() {
    if (s.busy) return;
    if (!text.trim() && files.length === 0) return;
    send({ type: "chat.send", text, attachments: files, useSelection: useSel });
    setText(""); setFiles([]); setUseSel(false);
  }

  const lastHuman = () => {
    const msgs = s.conversation.messages;
    for (let i = msgs.length - 1; i >= 0; i--) if (msgs[i]?.role === "user" && !msgs[i].content.every((b) => b.type === "tool_result")) return i;
    return -1;
  };

  const ctxSize = (s.usage.last?.input_tokens ?? 0) + (s.usage.last?.cache_read_input_tokens ?? 0) + (s.usage.last?.cache_creation_input_tokens ?? 0);

  return (
    <div
      className="shrink-0 px-3 pb-2"
      onDragOver={(e) => { e.preventDefault(); setDrag(true); }}
      onDragLeave={() => setDrag(false)}
      onDrop={(e) => { e.preventDefault(); setDrag(false); if (e.dataTransfer.files.length) add(e.dataTransfer.files); }}
    >
      <div className={`mx-auto max-w-[760px] rounded-xl bg-surface shadow-[0_0_0_1px_rgb(var(--line)),0_1px_3px_rgb(0_0_0/0.04)] transition-shadow focus-within:shadow-[0_0_0_1px_rgb(var(--faint)),0_1px_3px_rgb(0_0_0/0.06)] ${drag ? "ring-2 ring-clay/60" : ""}`}>
        {files.length > 0 && (
          <div className="flex flex-wrap gap-1.5 px-2.5 pt-2">
            {files.map((f, i) => (
              <span key={i} className="inline-flex max-w-full items-center gap-1 rounded-md bg-sunk py-0.5 pl-1 pr-0.5 text-xs text-muted">
                {f.mediaType.startsWith("image/")
                  ? <img src={`data:${f.mediaType};base64,${f.base64}`} className="h-5 w-5 rounded object-cover" alt="" />
                  : <Doc size={13} />}
                <span className="truncate">{f.name}</span>
                <button className="rounded p-0.5 hover:text-ink" aria-label={`Remove ${f.name}`} onClick={() => setFiles(files.filter((_, j) => j !== i))}><X size={12} /></button>
              </span>
            ))}
          </div>
        )}
        <textarea
          ref={ta}
          rows={1}
          value={text}
          placeholder={ctx.doc ? `Ask about ${ctx.doc}, or describe what to build` : "Describe a part to build, or ask anything"}
          aria-label="Message Claude"
          className="block max-h-[200px] w-full resize-none bg-transparent px-3 pb-1 pt-2.5 text-base text-ink outline-none placeholder:text-faint"
          onChange={(e) => setText(e.target.value)}
          onPaste={(e) => { const imgs = Array.from(e.clipboardData.files); if (imgs.length) { e.preventDefault(); add(imgs); } }}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); submit(); }
            else if (e.key === "Escape" && s.busy) { e.preventDefault(); send({ type: "chat.stop" }); }
            else if (e.key === "ArrowUp" && !text && !s.busy) { const i = lastHuman(); if (i >= 0) { e.preventDefault(); window.dispatchEvent(new CustomEvent("edit-message", { detail: i })); } }
          }}
        />
        <div className="flex items-center gap-1 px-1.5 pb-1.5">
          <button className="icon-btn" title="Attach images, PDFs or text files" aria-label="Attach files" onClick={() => picker.current?.click()}><Clip /></button>
          <input ref={picker} type="file" multiple hidden accept="image/png,image/jpeg,image/gif,image/webp,application/pdf,text/*,.md,.csv,.json" onChange={(e) => { if (e.target.files) add(e.target.files); e.target.value = ""; }} />
          {ctx.selection > 0 && (
            <button
              className={`inline-flex min-w-0 items-center gap-1 rounded-md px-1.5 py-1 text-xs transition-colors ${useSel ? "bg-clay/15 text-clay" : "text-muted hover:bg-sunk hover:text-ink"}`}
              aria-pressed={useSel}
              title="Tell Claude your message is about the current selection"
              onClick={() => setUseSel(!useSel)}
            >
              <Cursor size={13} />
              <span className="truncate">{useSel ? "Using" : "Use"} selection: {ctx.selectionLabel ?? ctx.selection + " items"}</span>
            </button>
          )}
          <div className="flex-1" />
          {s.busy ? (
            <button className="inline-flex h-7 w-7 items-center justify-center rounded-lg bg-ink text-paper" title="Stop (Esc)" aria-label="Stop" onClick={() => send({ type: "chat.stop" })}><Stop size={14} /></button>
          ) : (
            <button
              className="inline-flex h-7 w-7 items-center justify-center rounded-lg bg-clay text-white transition-opacity disabled:opacity-35"
              disabled={!text.trim() && files.length === 0}
              title="Send (Enter)" aria-label="Send" onClick={submit}
            ><ArrowUp size={15} /></button>
          )}
        </div>
      </div>
      <div className="mx-auto mt-1 flex max-w-[760px] items-center justify-between px-1 text-xs text-faint">
        {s.external && Date.now() - s.external.at < 60000 ? (
          <span className={`truncate ${s.external.isError ? "text-bad" : "text-muted"}`} title={`${s.external.client} used ${s.external.tool}`}>
            {s.external.client}: {s.external.summary}
          </span>
        ) : (
          <span className="truncate">{ctx.doc ? `${ctx.doc}` : "No document open"}</span>
        )}
        {ctxSize > 0 && (
          <span title={`Last request: ${s.usage.last?.input_tokens ?? 0} new + ${s.usage.last?.cache_read_input_tokens ?? 0} cached input tokens, ${s.usage.last?.output_tokens ?? 0} output.\nThis chat: ${s.usage.total?.output_tokens ?? 0} output tokens.`}>
            {fmtTokens(ctxSize)} tokens in context
          </span>
        )}
      </div>
    </div>
  );
}
