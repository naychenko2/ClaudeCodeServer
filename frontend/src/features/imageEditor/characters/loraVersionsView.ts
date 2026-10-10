// Чистая логика списка «Версии персонажа»: название, метка, доступные действия, проверка названия.
// React здесь нет, тестируется в node.

import type { LoraVersionDto } from '../api';
import { TRAIN_BADGE, VERSIONS_TEXT, VERSIONS_TITLE_MAX, VERSION_DEFAULT_TITLE } from './loraTrainTexts';
import { formatTrainedDate } from './loraTrainView';

export type VersionAction = 'connect' | 'rename' | 'remove';
export type VersionMarkTone = 'success' | 'neutral' | 'accent' | 'danger';

const when = (v: LoraVersionDto) => v.trainedAt ?? v.createdAt;
const dayKey = (iso: string | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isNaN(t) ? '' : new Date(t).toLocaleDateString('ru-RU');
};
const timeOf = (iso: string | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isNaN(t) ? '' : new Date(t).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
};

// Своё название, иначе «Обучено {дата}, {n} фото»; если в тот же день есть другая версия — со временем
export function versionTitle(v: LoraVersionDto, all: LoraVersionDto[]): string {
  const own = v.title?.trim();
  if (own) return own;
  const day = dayKey(when(v));
  const sameDay = !!day && all.some(x => x.runId !== v.runId && dayKey(when(x)) === day);
  const date = formatTrainedDate(when(v));
  return VERSION_DEFAULT_TITLE(sameDay ? `${date} ${timeOf(when(v))}` : date, v.photosKept ?? v.photosCount);
}

export function versionMark(v: LoraVersionDto): { text: string; tone: VersionMarkTone } {
  if (v.status === 'training') return { text: TRAIN_BADGE.running, tone: 'accent' };
  if (v.status === 'failed') return { text: TRAIN_BADGE.failed, tone: 'danger' };
  if (v.status === 'cancelled') return { text: TRAIN_BADGE.cancelled, tone: 'neutral' };
  return v.connected
    ? { text: VERSIONS_TEXT.connected, tone: 'success' }
    : { text: VERSIONS_TEXT.ready, tone: 'neutral' };
}

// Какие кнопки у строки: идущей версии действия не положены, упавшую и остановленную можно только удалить
export function versionActions(v: LoraVersionDto): VersionAction[] {
  switch (v.status) {
    case 'ready': return v.connected ? ['rename', 'remove'] : ['connect', 'rename', 'remove'];
    case 'failed': case 'cancelled': return ['remove'];
    default: return [];
  }
}

// Возврат к более старой версии спрашивает подтверждение, новую подключаем сразу
export function needsRevertConfirm(target: LoraVersionDto, all: LoraVersionDto[]): boolean {
  const cur = all.find(x => x.connected);
  if (!cur) return false;
  const a = Date.parse(when(target) ?? ''), b = Date.parse(when(cur) ?? '');
  return !Number.isNaN(a) && !Number.isNaN(b) && a < b;
}

// Пустое название после trim — сброс к шаблону; длиннее 60 — отказ до запроса
export function validateVersionTitle(raw: string): { ok: true; title: string } | { ok: false; error: string } {
  const title = raw.trim();
  return title.length > VERSIONS_TITLE_MAX
    ? { ok: false, error: VERSIONS_TEXT.titleTooLong }
    : { ok: true, title };
}

// Предел версий: подсказка «удалите ненужную»
export const atVersionLimit = (used: number, limit: number) => limit > 0 && used >= limit;
