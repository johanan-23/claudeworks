import { send } from "../lib/bridge";
import type { State } from "../lib/store";
import { Check } from "./Icons";

/**
 * Shows whether Claude Code / Claude Desktop are set up to control SOLIDWORKS through this add-in
 * (MCP), with a button to set them up. You chat in those apps, not in this pane.
 */
export function McpSetup({ s }: { s: State }) {
  const m = s.mcp;
  const rows: { key: "code" | "desktop"; name: string; connected: boolean; installed: boolean; after: string }[] = [
    { key: "code", name: "Claude Code", connected: !!m?.code, installed: m?.codeInstalled ?? true, after: "Start a new Claude Code session and ask it to work in SOLIDWORKS." },
    { key: "desktop", name: "Claude Desktop", connected: !!m?.desktop, installed: m?.desktopInstalled ?? true, after: "Restart Claude Desktop, then ask it to work in SOLIDWORKS." },
  ];
  return (
    <ul className="divide-y divide-line border-y border-line">
      {rows.map((r) => (
        <li key={r.key} className="py-2">
          <div className="flex items-center justify-between gap-2">
            <span className="text-sm font-medium">{r.name}</span>
            {r.connected ? (
              <span className="inline-flex items-center gap-1 text-sm text-ok"><Check size={14} />Connected</span>
            ) : !r.installed ? (
              <span className="text-xs text-faint">Not found on this PC</span>
            ) : null}
          </div>
          {r.connected ? (
            <p className="mt-0.5 text-xs text-muted">{r.after}</p>
          ) : (
            <button className="btn btn-ghost mt-1.5 w-full" onClick={() => send({ type: "mcp.register", target: r.key })}>
              Set up {r.name} to control SOLIDWORKS
            </button>
          )}
        </li>
      ))}
    </ul>
  );
}
