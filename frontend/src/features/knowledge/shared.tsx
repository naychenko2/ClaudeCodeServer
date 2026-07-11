import type { ReactNode } from 'react';

// Иконки раздела «Знания» (Feather-стиль, stroke=currentColor — как по всему приложению).
const Svg = ({ size = 18, children }: { size?: number; children: ReactNode }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    {children}
  </svg>
);

export const IconSearch = (p: { size?: number }) => <Svg size={p.size}><circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" /></Svg>;
export const IconPlus = (p: { size?: number }) => <Svg size={p.size}><path d="M12 5v14M5 12h14" /></Svg>;
export const IconBook = (p: { size?: number }) => <Svg size={p.size}><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" /><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" /></Svg>;
export const IconNote = (p: { size?: number }) => <Svg size={p.size}><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6" /></Svg>;
export const IconFolder = (p: { size?: number }) => <Svg size={p.size}><path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z" /></Svg>;
export const IconBrain = (p: { size?: number }) => <Svg size={p.size}><path d="M9.5 2A2.5 2.5 0 0 1 12 4.5v15a2.5 2.5 0 0 1-4.96.44 2.5 2.5 0 0 1-2.96-3.08 3 3 0 0 1-.34-5.58 2.5 2.5 0 0 1 1.32-4.24 2.5 2.5 0 0 1 1.98-3A2.5 2.5 0 0 1 9.5 2Z" /><path d="M14.5 2A2.5 2.5 0 0 0 12 4.5v15a2.5 2.5 0 0 0 4.96.44 2.5 2.5 0 0 0 2.96-3.08 3 3 0 0 0 .34-5.58 2.5 2.5 0 0 0-1.32-4.24 2.5 2.5 0 0 0-1.98-3A2.5 2.5 0 0 0 14.5 2Z" /></Svg>;
export const IconGlobe = (p: { size?: number }) => <Svg size={p.size}><circle cx="12" cy="12" r="10" /><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" /></Svg>;
export const IconFile = (p: { size?: number }) => <Svg size={p.size}><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6" /></Svg>;
export const IconTrash = (p: { size?: number }) => <Svg size={p.size}><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M10 11v6M14 11v6" /></Svg>;
export const IconLock = (p: { size?: number }) => <Svg size={p.size}><rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></Svg>;
export const IconDots = (p: { size?: number }) => <Svg size={p.size}><circle cx="5" cy="12" r="1.6" /><circle cx="12" cy="12" r="1.6" /><circle cx="19" cy="12" r="1.6" /></Svg>;
export const IconChevronLeft = (p: { size?: number }) => <Svg size={p.size}><path d="M15 18l-6-6 6-6" /></Svg>;
export const IconChevronsLeft = (p: { size?: number }) => <Svg size={p.size}><path d="M11 18l-6-6 6-6M18 18l-6-6 6-6" /></Svg>;
export const IconPanel = (p: { size?: number }) => <Svg size={p.size}><rect x="3" y="3" width="18" height="18" rx="2" /><path d="M9 3v18" /></Svg>;
export const IconPin = (p: { size?: number }) => <Svg size={p.size}><path d="M12 17v5" /><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z" /></Svg>;
export const IconBack = (p: { size?: number }) => <Svg size={p.size}><path d="M19 12H5M12 19l-7-7 7-7" /></Svg>;
export const IconSparkles = (p: { size?: number }) => <Svg size={p.size}><path d="M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M18.4 5.6l-2.8 2.8M8.4 15.6l-2.8 2.8" /></Svg>;
export const IconUpload = (p: { size?: number }) => <Svg size={p.size}><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" /></Svg>;
export const IconTextDoc = (p: { size?: number }) => <Svg size={p.size}><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6M16 13H8M16 17H8M10 9H8" /></Svg>;

// Иконка по типу базы (для карточки и шапки)
export function typeIcon(type: string, size = 18) {
  switch (type) {
    case 'Заметки': return <IconNote size={size} />;
    case 'Проект': return <IconFolder size={size} />;
    case 'Память персоны': return <IconBrain size={size} />;
    case 'Публичная': return <IconGlobe size={size} />;
    default: return <IconBook size={size} />;
  }
}
