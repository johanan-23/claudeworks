import { useCallback, useEffect, useState } from "react";
import { send } from "./lib/bridge";
import { useAppState } from "./lib/store";
import { Composer } from "./components/Composer";
import { Messages } from "./components/Messages";
import { Header, History, SettingsPanel } from "./components/Panels";
import { EmptyState, SignIn } from "./components/Screens";
import { X } from "./components/Icons";

export default function App() {
  const [s, dispatch] = useAppState();
  const [panel, setPanel] = useState<"none" | "history" | "settings">("none");

  // Theme: follow SOLIDWORKS (or the OS) unless the user picked one.
  useEffect(() => {
    const mode = s.settings.theme === "system"
      ? (s.swTheme.mode ?? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light"))
      : s.settings.theme;
    document.documentElement.classList.toggle("dark", mode === "dark");
  }, [s.settings.theme, s.swTheme.mode]);

  const toast = useCallback((message: string) => dispatch({ type: "toast", message }), [dispatch]);

  useEffect(() => {
    if (!s.toasts.length) return;
    const t = setTimeout(() => dispatch({ type: "toast.dismiss", id: s.toasts[0].id }), 4500);
    return () => clearTimeout(t);
  }, [s.toasts, dispatch]);

  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.altKey) return;
      const key = e.key.toLowerCase();
      if (key === "n" && !s.busy) { e.preventDefault(); send({ type: "conv.new" }); setPanel("none"); }
      else if (key === "k") { e.preventDefault(); setPanel((p) => (p === "history" ? "none" : "history")); }
      else if (key === ",") { e.preventDefault(); setPanel("settings"); }
    };
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [s.busy]);

  if (!s.ready) return <div className="flex h-full items-center justify-center text-sm text-faint">Starting…</div>;
  if (!s.auth.signedIn) return <SignIn s={s} />;

  const empty = s.conversation.messages.filter(Boolean).length === 0 && !s.busy;
  return (
    <div className="relative flex h-full min-w-[300px] flex-col">
      <Header s={s} onHistory={() => setPanel("history")} onSettings={() => setPanel("settings")} />
      {empty ? <EmptyState s={s} /> : <Messages s={s} />}
      <Composer s={s} toast={toast} />
      {panel === "history" && <History s={s} onClose={() => setPanel("none")} />}
      {panel === "settings" && <SettingsPanel s={s} onClose={() => setPanel("none")} />}
      <div className="pointer-events-none absolute inset-x-0 bottom-24 z-40 flex flex-col items-center gap-1.5 px-3">
        {s.toasts.map((t) => (
          <div key={t.id} role="status"
            className={`pointer-events-auto flex max-w-full items-start gap-2 rounded-md px-3 py-2 text-sm shadow-lg ${t.kind === "error" ? "bg-bad text-white" : "bg-ink text-paper"}`}>
            <span className="min-w-0">{t.message}</span>
            <button className="opacity-70 hover:opacity-100" aria-label="Dismiss" onClick={() => dispatch({ type: "toast.dismiss", id: t.id })}><X size={13} /></button>
          </div>
        ))}
      </div>
    </div>
  );
}
