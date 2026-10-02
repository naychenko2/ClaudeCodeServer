// Записи модуля «Звук» в ленте чата: module_record { module: "audioeditor", recordType, data, fallback }
// (Jobs/AudioJobThreads.cs). Ядро отдаёт их в слот chat-item-tool по ключу `${module}:${recordType}`.

import type { ChatItem } from '../../../types';

export const MODULE = 'audioeditor';
export const recordKey = (recordType: string) => `${MODULE}:${recordType}`;

export const RECORD_THREAD = 'audio_thread';
export const RECORD_LAUNCH = 'audio_launch_versions';

export interface ModuleRecordView { recordType: string; data: Record<string, unknown>; fallback: string | null }

// Запись ленты как module_record; другие виды — null
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
