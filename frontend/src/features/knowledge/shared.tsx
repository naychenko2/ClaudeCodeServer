// Иконки раздела «Знания» (lucide-react, stroke=currentColor, strokeWidth=2 — как по всему приложению).
// Экспортные имена/сигнатуры сохранены — потребители не меняются.
import {
  Search, Plus, Book, StickyNote, Folder, Brain, Globe, File, Trash2, Lock, Ellipsis,
  ChevronsLeft, Pin, ArrowLeft, X, ChevronRight, Upload, FileText,
} from 'lucide-react';

const ICON_STYLE = { flexShrink: 0 } as const;

export const IconSearch = ({ size = 18 }: { size?: number }) => <Search size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconPlus = ({ size = 18 }: { size?: number }) => <Plus size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconBook = ({ size = 18 }: { size?: number }) => <Book size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconNote = ({ size = 18 }: { size?: number }) => <StickyNote size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconFolder = ({ size = 18 }: { size?: number }) => <Folder size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconBrain = ({ size = 18 }: { size?: number }) => <Brain size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconGlobe = ({ size = 18 }: { size?: number }) => <Globe size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconFile = ({ size = 18 }: { size?: number }) => <File size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconTrash = ({ size = 18 }: { size?: number }) => <Trash2 size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconLock = ({ size = 18 }: { size?: number }) => <Lock size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconDots = ({ size = 18 }: { size?: number }) => <Ellipsis size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconChevronsLeft = ({ size = 18 }: { size?: number }) => <ChevronsLeft size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconPin = ({ size = 18, filled }: { size?: number; filled?: boolean }) => <Pin size={size} strokeWidth={2} fill={filled ? 'currentColor' : 'none'} style={ICON_STYLE} />;
export const IconBack = ({ size = 18 }: { size?: number }) => <ArrowLeft size={size} strokeWidth={2} style={ICON_STYLE} />;
// Закрытие модалки/просмотра
export const IconClose = ({ size = 18 }: { size?: number }) => <X size={size} strokeWidth={2} style={ICON_STYLE} />;
// Индикатор кликабельности строки документа (раскрыть/подробнее)
export const IconChevronRight = ({ size = 18 }: { size?: number }) => <ChevronRight size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconUpload = ({ size = 18 }: { size?: number }) => <Upload size={size} strokeWidth={2} style={ICON_STYLE} />;
export const IconTextDoc = ({ size = 18 }: { size?: number }) => <FileText size={size} strokeWidth={2} style={ICON_STYLE} />;

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
