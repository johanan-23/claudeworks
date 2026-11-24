// Browser-only stand-in for the C# host (used with `npm run dev`). Mirrors the real message
// protocol closely enough to develop every screen: sign-in, streaming, tool rail, confirm, errors.
import type { Conversation, Message } from "./types";

type Emit = (m: any) => void;

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

function plateImage(): string {
  const c = document.createElement("canvas");
  c.width = 640; c.height = 420;
  const g = c.getContext("2d")!;
  const grd = g.createLinearGradient(0, 0, 0, 420);
  grd.addColorStop(0, "#e6e6e6"); grd.addColorStop(1, "#fafafa");
  g.fillStyle = grd; g.fillRect(0, 0, 640, 420);
  g.fillStyle = "#9a9a9a"; g.strokeStyle = "#222"; g.lineWidth = 1.5;
  g.beginPath(); g.moveTo(200, 90); g.lineTo(520, 180); g.lineTo(520, 330); g.lineTo(200, 240); g.closePath(); g.fill(); g.stroke();
  g.fillStyle = "#c4c4c4";
  g.beginPath(); g.moveTo(200, 90); g.lineTo(260, 70); g.lineTo(580, 160); g.lineTo(520, 180); g.closePath(); g.fill(); g.stroke();
  g.fillStyle = "#b0b0b0";
  g.beginPath(); g.moveTo(520, 180); g.lineTo(580, 160); g.lineTo(580, 310); g.lineTo(520, 330); g.closePath(); g.fill(); g.stroke();
  g.fillStyle = "#eee"; g.beginPath(); g.ellipse(360, 210, 16, 20, -0.3, 0, Math.PI * 2); g.fill(); g.stroke();
  return c.toDataURL("image/png").split(",")[1];
}

class MockHost {
  private signedIn = new URLSearchParams(location.search).get("signedIn") !== "0";
  private conv: Conversation = { id: "mock1", title: "New chat", messages: [] };
  private settings = { model: "claude-opus-5-5", extendedThinking: false, theme: "system", autoContext: true, allowAdvancedApi: false, confirmDestructive: true, customInstructions: "" };
  private convs = [
    { id: "a", title: "Bracket with 4 holes", updated: new Date(Date.now() - 3600e3).toISOString(), preview: "Added four Ø6.6 mm clearance holes on a 40 mm pitch." },
    { id: "b", title: "Shaft drawing for workshop", updated: new Date(Date.now() - 86400e3 * 2).toISOString(), preview: "Created a 3-view A3 drawing with a section A-A." },
  ];
  private stopFlag = false;
  private confirmResolve: ((ok: boolean) => void) | null = null;

  receive(msg: any, emit: Emit) {
    setTimeout(() => this.handle(msg, emit), 30);
  }

  private auth(error?: string, busy = false) {
    return { signedIn: this.signedIn, provider: "api_key", hint: this.signedIn ? "sk-ant-…9f2c" : null, error: error ?? null, busy };
  }

  private async handle(msg: any, emit: Emit) {
    switch (msg.type) {
      case "ready":
        emit({
          type: "init", version: "1.0.0", auth: this.auth(), settings: this.settings,
          models: [{ id: "claude-opus-5-5", display_name: "Claude Opus 5.5" }, { id: "claude-sonnet-5-5", display_name: "Claude Sonnet 5.5" }, { id: "claude-haiku-5-5", display_name: "Claude Haiku 5.5" }],
          conversations: this.convs, conversation: this.conv, demo: false, theme: { mode: null, background: null },
        });
        emit({ type: "context", doc: "Part1", docType: "part", selection: 1, selectionLabel: "face" });
        emit({ type: "mcp.status", code: true, desktop: false, codeInstalled: true, desktopInstalled: true });
        break;
      case "auth.signIn":
        emit({ type: "auth", ...this.auth(undefined, true) });
        await wait(700);
        if ((msg.key ?? "").length < 20) emit({ type: "auth", ...this.auth("That key was rejected by Anthropic. Copy it again from console.anthropic.com.") });
        else { this.signedIn = true; emit({ type: "auth", ...this.auth() }); }
        break;
      case "auth.signOut": this.signedIn = false; emit({ type: "auth", ...this.auth() }); break;
      case "settings.set": Object.assign(this.settings, msg.settings); emit({ type: "settings", settings: this.settings }); break;
      case "conv.new": this.conv = { id: "m" + Date.now(), title: "New chat", messages: [] }; emit({ type: "conversation", conversation: this.conv }); break;
      case "conv.list": emit({ type: "conversations", items: this.convs.filter((c) => !msg.search || c.title.toLowerCase().includes(msg.search.toLowerCase())) }); break;
      case "conv.delete": this.convs = this.convs.filter((c) => c.id !== msg.id); emit({ type: "conversations", items: this.convs }); break;
      case "conv.rename": this.convs = this.convs.map((c) => (c.id === msg.id ? { ...c, title: msg.title } : c)); this.conv.title = msg.title; emit({ type: "conversations", items: this.convs }); break;
      case "conv.open": emit({ type: "toast", message: "Mock: opening saved chats needs the add-in." }); break;
      case "chat.stop": this.stopFlag = true; this.confirmResolve?.(false); break;
      case "confirm.answer": this.confirmResolve?.(!!msg.approved); break;
      case "undo.last": emit({ type: "toast", message: "Undid Claude's last changes in Part1." }); break;
      case "chat.send": await this.turn(msg.text, emit); break;
      case "chat.regenerate": await this.turn(null, emit); break;
    }
  }

  private push(m: Message, emit: Emit) {
    this.conv.messages.push(m);
    emit({ type: "message", index: this.conv.messages.length - 1, message: m });
  }

  private async streamText(text: string, emit: Emit, index: number) {
    emit({ type: "stream.block", index, block: { type: "text", text: "" } });
    for (let i = 0; i < text.length; i += 6) {
      if (this.stopFlag) return false;
      emit({ type: "stream.text", index, text: text.slice(i, i + 6) });
      await wait(14);
    }
    return true;
  }

  private async turn(text: string | null, emit: Emit) {
    this.stopFlag = false;
    if (text) this.push({ role: "user", content: [{ type: "text", text: "<solidworks_context>…</solidworks_context>", ui_hidden: true }, { type: "text", text }] }, emit);
    emit({ type: "turn.start" });
    if (text?.toLowerCase().includes("error")) {
      emit({ type: "stream.request", iteration: 0 });
      await wait(500);
      emit({ type: "turn.end", outcome: "failed", message: "Can't reach the Anthropic API. Check your internet connection.", errorKind: "network", canUndo: false });
      return;
    }
    emit({ type: "stream.request", iteration: 0 });
    const t1 = "I'll sketch a centred 100 × 60 mm rectangle on the Front plane and extrude it 20 mm.";
    await this.streamText(t1, emit, 0);
    const calls = [
      { id: "t1", name: "begin_sketch", input: '{"plane":"Front"}', d: "Sketch on Front", s: "Started sketch on Front Plane" },
      { id: "t2", name: "add_rectangle", input: '{"x1":0,"y1":0,"x2":50,"y2":30,"centered":true}', d: "Rectangle", s: "Added 100 × 60 mm rectangle" },
      { id: "t3", name: "end_sketch", input: "{}", d: "Close sketch", s: "Closed Sketch1" },
      { id: "t4", name: "extrude", input: '{"depth":20}', d: "Extrude 20 mm", s: "Extruded Sketch1 20 mm → Boss-Extrude1" },
    ];
    const assistant: Message = { role: "assistant", content: [{ type: "text", text: t1 }, ...calls.map((c) => ({ type: "tool_use" as const, id: c.id, name: c.name, input: c.input }))] };
    this.push(assistant, emit);
    const results: Message = { role: "user", content: [] };
    for (const c of calls) {
      emit({ type: "tool.start", id: c.id, name: c.name, description: c.d });
      await wait(250);
      emit({ type: "tool.end", id: c.id, isError: false, summary: c.s });
      results.content.push({ type: "tool_result", tool_use_id: c.id, content: [{ type: "text", text: "ok" }], ui_summary: c.s });
    }
    this.push(results, emit);

    emit({ type: "stream.request", iteration: 1 });
    const confirmNeeded = text?.toLowerCase().includes("delete");
    if (confirmNeeded) {
      const tu: Message = { role: "assistant", content: [{ type: "tool_use", id: "t5", name: "delete_feature", input: '{"name":"Fillet1"}' }] };
      this.push(tu, emit);
      emit({ type: "tool.start", id: "t5", name: "delete_feature", description: "Delete Fillet1" });
      emit({ type: "confirm", id: "t5", tool: "delete_feature", question: 'Delete feature "Fillet1"?' });
      const ok = await new Promise<boolean>((r) => (this.confirmResolve = r));
      this.confirmResolve = null;
      emit({ type: "confirm.done", id: "t5", approved: ok });
      emit({ type: "tool.end", id: "t5", isError: !ok, summary: ok ? "Deleted Fillet1" : "The user declined this action." });
      this.push({ role: "user", content: [{ type: "tool_result", tool_use_id: "t5", is_error: !ok, content: [{ type: "text", text: ok ? "deleted" : "declined" }], ui_summary: ok ? "Deleted Fillet1" : "The user declined this action." }] }, emit);
    }
    const capture: Message = { role: "assistant", content: [{ type: "tool_use", id: "t6", name: "capture_view", input: '{"view":"isometric"}' }] };
    this.push(capture, emit);
    emit({ type: "tool.start", id: "t6", name: "capture_view", description: "Capture isometric view" });
    await wait(400);
    emit({ type: "tool.end", id: "t6", isError: false, summary: "Captured isometric view" });
    this.push({ role: "user", content: [{ type: "tool_result", tool_use_id: "t6", content: [{ type: "text", text: "Captured." }, { type: "image", media_type: "image/png", base64: plateImage() }], ui_summary: "Captured isometric view" }] }, emit);

    emit({ type: "stream.request", iteration: 2 });
    const t2 = "The plate is built:\n\n| Property | Value |\n|---|---|\n| Size | 100 × 60 × 20 mm |\n| Volume | 120 000 mm³ |\n\nNext I can add the **Ø10 mm** centre hole and the corner fillets:\n\n```csharp\nfm.FeatureFillet3(3, 0.003, 0, 0, 0, 0, 0, null, null, null, null, null, null, null);\n```";
    const ok = await this.streamText(t2, emit, 0);
    this.push({ role: "assistant", content: [{ type: "text", text: ok ? t2 : t2.slice(0, 40) }], stop_reason: ok ? "end_turn" : "cancelled" }, emit);
    const usage = { input_tokens: 820, output_tokens: 240, cache_read_input_tokens: 9100, cache_creation_input_tokens: 0 };
    emit({ type: "turn.end", outcome: ok ? "completed" : "cancelled", message: ok ? null : "Stopped.", canUndo: true, usage, totalUsage: { ...usage, input_tokens: 2400, output_tokens: 610 }, title: this.conv.title === "New chat" ? "Plate 100×60×20" : this.conv.title });
    this.conv.title = "Plate 100×60×20";
  }
}

export const mockHost = new MockHost();
