// Выбор «других пропорций» при правке картинки и желание формата для котировки (ADR-025). Отдельный файл
// ради цикла импорта: format.ts зовёт котировку из run.ts, а run.ts читает выбор отсюда.

import { notifyKindChanged, type CropAnchor, type MediaFormatWish } from 'aihome_shell/kit';
import { getPrefs } from '../thread/prefs';

// Желание проекта для котировки генерации: общий кэш котировок ключуется телом, поэтому формат уходит явно
export const wishForQuote = (scope: string): MediaFormatWish | null => getPrefs(scope).format ?? null;

// ── Правка: «другие пропорции» живут одну правку ──

export interface EditChoice { aspect: string | null; how: 'outpaint' | 'crop' | null; anchor: CropAnchor; cropping: boolean }

const _edit = new Map<string, EditChoice>();
const NO_CHOICE: EditChoice = { aspect: null, how: null, anchor: 'center', cropping: false };

export const editOf = (threadId: string): EditChoice => _edit.get(threadId) ?? NO_CHOICE;
export const editAspectOf = (threadId: string): string | null => editOf(threadId).aspect;

export function setEdit(threadId: string, patch: Partial<EditChoice>) {
  _edit.set(threadId, { ...editOf(threadId), ...patch });
  notifyKindChanged();
}

export function resetEdit(threadId: string) {
  if (_edit.delete(threadId)) notifyKindChanged();
}

