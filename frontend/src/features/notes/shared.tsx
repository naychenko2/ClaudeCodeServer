import { useEffect, useState, type ReactNode } from 'react';
import { C, FONT, R } from '../../lib/design';
import { projectColor } from '../../lib/tasks';

// Перетаскиваемая ширина панели (пара к ui/Splitter): персист в localStorage, клампы.
// rightSide — панель справа: тянем влево → ширина растёт (как артефакты в Workspace).
export function usePanelWidth(storageKey: string, def: number, min: number, max: number, rightSide = false) {
  const [width, setWidth] = useState(() => {
    const v = localStorage.getItem(storageKey);
    return v ? Math.max(min, Math.min(max, Number(v))) : def;
  });
  useEffect(() => { localStorage.setItem(storageKey, String(width)); }, [width, storageKey]);
  const [dragging, setDragging] = useState(false);

  const startDrag = (e: React.MouseEvent) => {
    e.preventDefault();
    setDragging(true);
    const startX = e.clientX;
    const startW = width;
    const onMove = (ev: MouseEvent) => {
      const d = ev.clientX - startX;
      setWidth(Math.max(min, Math.min(max, rightSide ? startW - d : startW + d)));
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(false);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  };

  return [width, dragging, startDrag] as const;
}

// Цвет источника заметки: личный vault — accent, проект — детерминированный цвет проекта.
export function sourceColor(source: string): string {
  return source === 'personal' ? C.accent : projectColor(source).main;
}

// --- Иконки (inline SVG, стиль Feather: stroke=currentColor) ---

const svg = (children: ReactNode, size = 16) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
);

export const IconSearch = () => svg(<><circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" /></>);
export const IconPlus = () => svg(<><path d="M12 5v14M5 12h14" /></>);
export const IconEye = () => svg(<><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" /><circle cx="12" cy="12" r="3" /></>);
export const IconPencil = () => svg(<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></>);
export const IconChat = () => svg(<><path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" /></>);
export const IconGraph = () => svg(<><circle cx="5" cy="6" r="2.5" /><circle cx="18" cy="7" r="2.5" /><circle cx="12" cy="18" r="2.5" /><path d="M7 7.5 10.5 16M15.8 8.6 13.5 16" /></>);
// Единая иконка «Заметки» (связанные ноды базы знаний) — та же в дереве файлов,
// пустом состоянии раздела и карточке заметки в чате
export const IconNotes = ({ size = 16 }: { size?: number }) => svg(<>
  <circle cx="6" cy="7" r="2.5" /><circle cx="18" cy="8" r="2.5" /><circle cx="12" cy="18" r="2.5" />
  <path d="M7.7 9 10.7 16M16.6 10 13.4 16M8.5 7.4 15.5 7.8" />
</>, size);
export const IconBacklink = () => svg(<><path d="M9 14 4 9l5-5" /><path d="M4 9h11a5 5 0 0 1 5 5v6" /></>);
export const IconOutlink = () => svg(<><path d="M7 17 17 7M8 7h9v9" /></>);
export const IconTrash = () => svg(<><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" /></>);
export const IconBack = () => svg(<><path d="M19 12H5M12 19l-7-7 7-7" /></>);
export const IconCalendarDay = () => svg(<><rect x="3" y="4" width="18" height="18" rx="2" /><path d="M16 2v4M8 2v4M3 10h18" /><circle cx="12" cy="16" r="1.5" fill="currentColor" stroke="none" /></>);
export const IconFolder = ({ size = 14 }: { size?: number }) => svg(<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />, size);
export const IconFolderMove = () => svg(<><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" /><path d="M10 13h6M13.5 10.5 16 13l-2.5 2.5" /></>);
export const IconSparkle = () => svg(<><path d="M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M18.4 5.6l-2.8 2.8M8.4 15.6l-2.8 2.8" /></>);
export const IconLink = () => svg(<><path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7" /><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" /></>);

// Точка-индикатор источника
export function SourceDot({ source, size = 8 }: { source: string; size?: number }) {
  return <span style={{ width: size, height: size, borderRadius: '50%', background: sourceColor(source), flex: 'none', display: 'inline-block' }} />;
}

// Чип-бейдж источника (точка + подпись)
export function SourceBadge({ source, label }: { source: string; label: string }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11, fontWeight: 600,
      padding: '2px 8px', borderRadius: R.sm, background: C.bgSelected, color: C.textSecondary,
    }}>
      <SourceDot source={source} size={7} />{label}
    </span>
  );
}

// --- Сворачиваемая группа (аналог CollapseGroup из ArtifactsPanel, вынесена) ---

export function CollapseGroup({ title, tail, defaultOpen = true, children }: {
  title: ReactNode;
  tail?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div style={{ marginBottom: 6 }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 7, padding: '5px 4px',
          background: 'none', border: 'none', cursor: 'pointer', textAlign: 'left',
          fontFamily: FONT.sans, color: C.textSecondary,
        }}
      >
        <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"
          strokeLinecap="round" strokeLinejoin="round"
          style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform .15s', color: C.textMuted, flex: 'none' }}>
          <path d="m9 18 6-6-6-6" />
        </svg>
        <span style={{ flex: 1, minWidth: 0 }}>{title}</span>
        {tail}
      </button>
      {open && <div>{children}</div>}
    </div>
  );
}
