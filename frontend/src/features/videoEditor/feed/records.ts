// Записи модуля «Видео» в ленте: module_record { module: "videoeditor", recordType, data, fallback }
// (Jobs/VideoJobThreads.cs). Ядро отдаёт их в слот chat-item-tool по ключу `${module}:${recordType}`.
// recordType не удаляются никогда (VideoThreadRecordTypes): старая лента рисует карточку по ним.

import type { ChatItem } from '../../../types';

export const MODULE = 'videoeditor';
export const recordKey = (recordType: string) => `${MODULE}:${recordType}`;

export interface ModuleRecordView { recordType: string; data: Record<string, unknown>; fallback: string | null }

export function recordOf(item: ChatItem): ModuleRecordView | null {
  const r = item as unknown as { kind?: unknown; recordType?: unknown; data?: unknown; fallback?: unknown };
  if (r.kind !== 'module_record' || typeof r.recordType !== 'string') return null;
  return {
    recordType: r.recordType,
    data: r.data && typeof r.data === 'object' ? r.data as Record<string, unknown> : {},
    fallback: typeof r.fallback === 'string' ? r.fallback : null,
  };
}

export const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);
