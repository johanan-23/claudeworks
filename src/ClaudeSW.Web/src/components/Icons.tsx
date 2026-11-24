import type { SVGProps } from "react";

type P = SVGProps<SVGSVGElement> & { size?: number };
const base = (size = 16) => ({
  width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor",
  strokeWidth: 1.8, strokeLinecap: "round" as const, strokeLinejoin: "round" as const, "aria-hidden": true,
});

export const Spark = ({ size = 16, ...p }: P) => (
  <svg {...base(size)} {...p} stroke="none" fill="currentColor">
    <path d="M12 1.8l1.5 7.1 4.3-5.4-2.6 6.4 6.9-1.7-6 3.8 6 3.8-6.9-1.7 2.6 6.4-4.3-5.4L12 22.2l-1.5-7.1-4.3 5.4 2.6-6.4-6.9 1.7 6-3.8-6-3.8 6.9 1.7-2.6-6.4 4.3 5.4z" />
  </svg>
);
export const Menu = (p: P) => <svg {...base(p.size)} {...p}><path d="M4 6h16M4 12h16M4 18h10" /></svg>;
export const Plus = (p: P) => <svg {...base(p.size)} {...p}><path d="M12 5v14M5 12h14" /></svg>;
export const Gear = (p: P) => (
  <svg {...base(p.size)} {...p}>
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
  </svg>
);
export const ArrowUp = (p: P) => <svg {...base(p.size)} {...p} strokeWidth={2.2}><path d="M12 19V5M5 12l7-7 7 7" /></svg>;
export const Stop = (p: P) => <svg {...base(p.size)} {...p} fill="currentColor" stroke="none"><rect x="6" y="6" width="12" height="12" rx="2" /></svg>;
export const Clip = (p: P) => <svg {...base(p.size)} {...p}><path d="M21 11.5l-8.6 8.6a5.5 5.5 0 0 1-7.8-7.8l8.6-8.6a3.7 3.7 0 0 1 5.2 5.2l-8.6 8.6a1.8 1.8 0 0 1-2.6-2.6l7.9-7.9" /></svg>;
export const X = (p: P) => <svg {...base(p.size)} {...p}><path d="M18 6L6 18M6 6l12 12" /></svg>;
export const Copy = (p: P) => <svg {...base(p.size)} {...p}><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h8" /></svg>;
export const Check = (p: P) => <svg {...base(p.size)} {...p}><path d="M20 6L9 17l-5-5" /></svg>;
export const Retry = (p: P) => <svg {...base(p.size)} {...p}><path d="M3 12a9 9 0 0 1 15.5-6.2L21 8M21 3v5h-5M21 12a9 9 0 0 1-15.5 6.2L3 16M3 21v-5h5" /></svg>;
export const Pencil = (p: P) => <svg {...base(p.size)} {...p}><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z" /></svg>;
export const Undo = (p: P) => <svg {...base(p.size)} {...p}><path d="M9 14L4 9l5-5" /><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11" /></svg>;
export const Chevron = (p: P) => <svg {...base(p.size)} {...p}><path d="M9 6l6 6-6 6" /></svg>;
export const Search = (p: P) => <svg {...base(p.size)} {...p}><circle cx="11" cy="11" r="7" /><path d="M20 20l-3.5-3.5" /></svg>;
export const Trash = (p: P) => <svg {...base(p.size)} {...p}><path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6" /></svg>;
export const Alert = (p: P) => <svg {...base(p.size)} {...p}><path d="M12 9v4M12 17h.01" /><path d="M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" /></svg>;
export const Cursor = (p: P) => <svg {...base(p.size)} {...p}><path d="M4 3l7 17 2.5-7.5L21 10z" /></svg>;
export const Doc = (p: P) => <svg {...base(p.size)} {...p}><path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z" /><path d="M14 3v6h6" /></svg>;
export const Key = (p: P) => <svg {...base(p.size)} {...p}><circle cx="7.5" cy="15.5" r="4.5" /><path d="M10.7 12.3L21 2M16 7l3 3M18 5l2 2" /></svg>;
