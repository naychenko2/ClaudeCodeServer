import { useEffect, useState, type ReactNode } from 'react';
import {
  Search, Plus, Eye, SquarePen, MessageCircle, Share2, StickyNote, Undo2, ExternalLink,
  Trash2, ArrowLeft, CalendarDays, Folder, FolderOutput, Sparkles, Link2,
} from 'lucide-react';
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

  const startDrag = (e: React.PointerEvent) => {
    e.preventDefault();
    setDragging(true);
    const startX = e.clientX;
    const startW = width;
    const onMove = (ev: PointerEvent) => {
      const d = ev.clientX - startX;
      setWidth(Math.max(min, Math.min(max, rightSide ? startW - d : startW + d)));
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(false);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  return [width, dragging, startDrag] as const;
}

// Цвет источника заметки: личный vault — accent, проект — детерминированный цвет проекта.
export function sourceColor(source: string): string {
  return source === 'personal' ? C.accent : projectColor(source).main;
}

// --- Иконки (lucide-react, Feather-стиль: stroke=currentColor, strokeWidth=2) ---
// Экспортные имена/сигнатуры сохранены — потребители не меняются.

export const IconSearch = ({ size = 16 }: { size?: number }) => <Search size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconPlus = ({ size = 16 }: { size?: number }) => <Plus size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconEye = ({ size = 16 }: { size?: number }) => <Eye size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconPencil = ({ size = 16 }: { size?: number }) => <SquarePen size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconChat = ({ size = 16 }: { size?: number }) => <MessageCircle size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconGraph = ({ size = 16 }: { size?: number }) => <Share2 size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
// Единая иконка «Заметки» — та же в дереве файлов, пустом состоянии раздела и карточке заметки в чате
export const IconNotes = ({ size = 16 }: { size?: number }) => <StickyNote size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconBacklink = ({ size = 16 }: { size?: number }) => <Undo2 size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconOutlink = ({ size = 16 }: { size?: number }) => <ExternalLink size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconTrash = ({ size = 16 }: { size?: number }) => <Trash2 size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconBack = ({ size = 16 }: { size?: number }) => <ArrowLeft size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconCalendarDay = ({ size = 16 }: { size?: number }) => <CalendarDays size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconFolder = ({ size = 14 }: { size?: number }) => <Folder size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconFolderMove = ({ size = 16 }: { size?: number }) => <FolderOutput size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconSparkle = ({ size = 16 }: { size?: number }) => <Sparkles size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
export const IconLink = ({ size = 16 }: { size?: number }) => <Link2 size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;

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
