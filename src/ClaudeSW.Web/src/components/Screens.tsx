import { useState } from "react";
import { send } from "../lib/bridge";
import type { State } from "../lib/store";
import { Key, Spark } from "./Icons";
import { McpSetup } from "./McpSetup";

export function SignIn({ s }: { s: State }) {
  const [key, setKey] = useState("");
  const busy = !!s.auth.busy;
  return (
    <div className="flex h-full flex-col overflow-y-auto px-5 py-8">
      <div className="mx-auto w-full max-w-[340px] flex-1">
        <div className="flex h-9 w-9 items-center justify-center rounded-[10px] bg-clay text-white"><Spark size={20} /></div>
        <h1 className="mt-4 text-xl font-semibold tracking-[-0.01em]">ClaudeWorks</h1>
        <p className="mt-1.5 text-muted">
          Build and edit parts, assemblies and drawings by describing them. Claude sees your active model and selection.
        </p>
        <form
          className="mt-6 space-y-2"
          onSubmit={(e) => { e.preventDefault(); if (key.trim()) send({ type: "auth.signIn", key }); }}
        >
          <label htmlFor="key" className="block text-sm font-medium">Anthropic API key</label>
          <input
            id="key" type="password" autoComplete="off" spellCheck={false} placeholder="sk-ant-..." className="field font-mono text-sm"
            value={key} onChange={(e) => setKey(e.target.value)} disabled={busy} autoFocus
            aria-invalid={!!s.auth.error} aria-describedby="key-help"
          />
          {s.auth.error && <p className="text-sm text-bad" role="alert">{s.auth.error}</p>}
          <button className="btn btn-primary w-full py-2" disabled={busy || !key.trim()}>
            {busy ? "Checking key…" : "Sign in"}
          </button>
          <p id="key-help" className="pt-1 text-sm text-muted">
            Create a key in the{" "}
            <a className="text-clay underline underline-offset-2" href="https://console.anthropic.com/settings/keys"
               onClick={(e) => { e.preventDefault(); send({ type: "link.open", url: "https://console.anthropic.com/settings/keys" }); }}>
              Anthropic Console
            </a>. Usage is billed to that account.
          </p>
        </form>
        <div className="mt-6 border-t border-line pt-4">
          <h2 className="text-sm font-medium">No API key? Use Claude Code or Claude Desktop</h2>
          <p className="mt-0.5 text-sm text-muted">
            On your Claude plan, Claude Code or Claude Desktop can control SOLIDWORKS through this add-in.
            You chat in their window, not here. They can see your model and selection, and what they do shows up in this pane.
          </p>
          <div className="mt-2"><McpSetup s={s} /></div>
          {s.external && (
            <p className={`mt-2 text-sm ${s.external.isError ? "text-bad" : "text-muted"}`}>{s.external.client}: {s.external.summary}</p>
          )}
        </div>
        <div className="mt-6 flex gap-2 rounded-md bg-sunk p-2.5 text-sm text-muted">
          <Key size={15} className="mt-0.5 shrink-0" />
          <span>Your key is encrypted for your Windows account and only sent to api.anthropic.com. It never leaves this PC otherwise.</span>
        </div>
      </div>
      <p className="mx-auto mt-6 max-w-[340px] text-center text-xs text-faint">
        Signing in with a Claude Pro or Max plan isn't available to third-party apps, so this add-in uses API keys.
      </p>
    </div>
  );
}

const SUGGESTIONS: Record<string, { label: string; prompt: string }[]> = {
  none: [
    { label: "Mounting plate", prompt: "Make a 100 x 60 x 20 mm plate with a 10 mm centre hole and 3 mm corner fillets." },
    { label: "Flanged bushing", prompt: "Model a flanged bushing: 20 mm bore, 30 mm OD, 25 mm long, with a 50 mm x 5 mm flange." },
    { label: "L-bracket", prompt: "Create an L-bracket from 4 mm plate, 60 x 40 x 30 mm, with two 6.6 mm holes in each leg." },
    { label: "Explain a workflow", prompt: "What's the cleanest way to model a part with a repeating pattern of slots around a circle?" },
  ],
  part: [
    { label: "Describe this part", prompt: "Describe this part: its main features, overall size and mass." },
    { label: "Make a drawing", prompt: "Create a 3-view drawing of this part with model dimensions." },
    { label: "Edit the selection", prompt: "Add a 2 mm fillet to the selected edges." },
    { label: "Check for problems", prompt: "Check the feature tree for rebuild errors and suggest fixes." },
  ],
  assembly: [
    { label: "List components", prompt: "List the components in this assembly with their files." },
    { label: "Check interference", prompt: "Run an interference check and tell me which parts collide." },
    { label: "Mate selection", prompt: "Make the two selected faces coincident." },
    { label: "Assembly drawing", prompt: "Create a drawing of this assembly with a BOM table." },
  ],
  drawing: [
    { label: "Add dimensions", prompt: "Insert the model dimensions into all views." },
    { label: "Section view", prompt: "Add a horizontal section view through the front view." },
    { label: "Title note", prompt: "Add a note with the material and general tolerances in the bottom-left corner." },
    { label: "Export PDF", prompt: "Export this drawing as a PDF next to the drawing file." },
  ],
};

export function EmptyState({ s }: { s: State }) {
  const kind = s.context.docType && SUGGESTIONS[s.context.docType] ? s.context.docType : "none";
  const heading = s.context.doc ? `What should we do with ${s.context.doc}?` : "What are we designing?";
  return (
    <div className="flex flex-1 flex-col justify-end overflow-y-auto px-4 pb-4">
      <div className="mx-auto w-full max-w-[520px]">
        <div className="text-clay"><Spark size={22} /></div>
        <h2 className="mt-3 text-xl font-semibold tracking-[-0.01em]">{heading}</h2>
        <p className="mt-1 text-muted">
          {s.context.doc
            ? "Claude can read the feature tree and your selection, change the model and take screenshots to check its work."
            : "Describe a part and Claude will model it step by step, or open a model and ask about it."}
        </p>
        <div className="mt-4 divide-y divide-line border-y border-line">
          {SUGGESTIONS[kind].map((x) => (
            <button
              key={x.label}
              className="block w-full py-2 text-left hover:bg-sunk/60"
              onClick={() => window.dispatchEvent(new CustomEvent("composer-fill", { detail: x.prompt }))}
            >
              <span className="block text-sm font-medium">{x.label}</span>
              <span className="block truncate text-sm text-muted">{x.prompt}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
