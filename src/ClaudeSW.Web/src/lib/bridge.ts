// JS <-> C# bridge over WebView2 postMessage. In a plain browser (vite dev) a mock host
// stands in so the UI can be developed and screenshotted without SOLIDWORKS.
import { mockHost } from "./mock";

type Listener = (msg: any) => void;
const listeners = new Set<Listener>();

interface WebView {
  postMessage(msg: unknown): void;
  addEventListener(type: "message", cb: (e: { data: any }) => void): void;
}
const webview: WebView | undefined = (window as any).chrome?.webview;

export const inHost = !!webview;

export function send(msg: { type: string; [k: string]: unknown }) {
  if (webview) webview.postMessage(msg);
  else mockHost.receive(msg, emit);
}

function emit(msg: any) {
  listeners.forEach((l) => l(msg));
}

export function onMessage(l: Listener) {
  listeners.add(l);
  return () => listeners.delete(l);
}

if (webview) webview.addEventListener("message", (e) => emit(e.data));
