import { memo, useState, type ReactNode } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeHighlight from "rehype-highlight";
import { send } from "../lib/bridge";
import { Check, Copy } from "./Icons";

export function copyText(text: string) {
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(text).catch(() => fallbackCopy(text));
  else fallbackCopy(text);
}
function fallbackCopy(text: string) {
  const t = document.createElement("textarea");
  t.value = text; document.body.appendChild(t); t.select();
  try { document.execCommand("copy"); } finally { t.remove(); }
}

function textOf(node: ReactNode): string {
  if (typeof node === "string" || typeof node === "number") return String(node);
  if (Array.isArray(node)) return node.map(textOf).join("");
  if (node && typeof node === "object" && "props" in node) return textOf((node as any).props.children);
  return "";
}

function CodeBlock({ children, className }: { children: ReactNode; className?: string }) {
  const [copied, setCopied] = useState(false);
  const lang = /language-(\w+)/.exec(className ?? "")?.[1];
  return (
    <div className="group relative rounded-md bg-sunk">
      <div className="flex items-center justify-between px-2.5 pt-1.5 text-xs text-faint">
        <span>{lang ?? "code"}</span>
        <button
          className="inline-flex items-center gap-1 rounded px-1 py-0.5 hover:text-ink"
          onClick={() => { copyText(textOf(children).replace(/\n$/, "")); setCopied(true); setTimeout(() => setCopied(false), 1400); }}
          aria-label="Copy code"
        >
          {copied ? <Check size={13} /> : <Copy size={13} />}
          {copied ? "Copied" : "Copy"}
        </button>
      </div>
      <pre className="overflow-x-auto px-2.5 pb-2 pt-1 font-mono text-[12px] leading-[18px]">
        <code className={className}>{children}</code>
      </pre>
    </div>
  );
}

export const Markdown = memo(function Markdown({ text }: { text: string }) {
  return (
    <div className="prose">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeHighlight, { detect: false, ignoreMissing: true }]]}
        components={{
          pre: ({ children }) => {
            const child: any = Array.isArray(children) ? children[0] : children;
            return <CodeBlock className={child?.props?.className}>{child?.props?.children}</CodeBlock>;
          },
          a: ({ href, children }) => (
            <a href={href} onClick={(e) => { e.preventDefault(); if (href) send({ type: "link.open", url: href }); }}>{children}</a>
          ),
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
});
