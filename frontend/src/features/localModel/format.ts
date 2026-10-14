// Чистые функции форматирования вкладки «Текст»: юнит-тесты без React и DOM.
// Все подписи — по спецификации макета (docs/mockups/local-model-stats-panel.html,
// раздел «Спецификация для Киры»): единицы, запятая как десятичный разделитель,
// «—» когда движок значение не отдал.

import type { LocalModelStats } from './api';

// === Числа ===

// Ток/с: «38 ток/с» — целое, без десятичных (движок целыми и отсчитывает)
export function formatTps(v?: number | null): string {
  if (v == null || !Number.isFinite(v)) return '—';
  return `${Math.round(v)} ток/с`;
}

// Долю 0..1 зажать в диапазон: бэк обещает 0..1, но выброс (сбой датчика)
// не должен растягивать полосу или процент
export function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}

// Процент из доли 0..1: «61 %». Бэк отдаёт доли (0.61), не проценты;
// значения вне 0..1 зажимаются (0.5 → «50 %»). Без значения — «—».
export function formatPerc(fraction?: number | null): string {
  if (fraction == null || !Number.isFinite(fraction)) return '—';
  return `${Math.round(clamp01(fraction) * 100)} %`;
}

// Десятичное число по-русски: «21,4»; целое — без хвоста: «24»
function ruNum(n: number): string {
  const r = Math.round(n * 10) / 10;
  return Number.isInteger(r) ? String(r) : r.toFixed(1).replace('.', ',');
}

// Память видеокарты: «21,4/24 ГБ»
export function formatGpuMem(usedMb?: number | null, totalMb?: number | null): string {
  if (usedMb == null || totalMb == null || !Number.isFinite(usedMb) || !Number.isFinite(totalMb)) return '—';
  return `${ruNum(usedMb / 1024)}/${ruNum(totalMb / 1024)} ГБ`;
}

// Мощность: «212/220 Вт»; без лимита карты — «212 Вт»
export function formatPower(w?: number | null, limitW?: number | null): string {
  if (w == null || !Number.isFinite(w)) return '—';
  if (limitW == null || !Number.isFinite(limitW)) return `${Math.round(w)} Вт`;
  return `${Math.round(w)}/${Math.round(limitW)} Вт`;
}

// «не отвечает с 14:32» — из unix-мс (как downSince у бэка); без момента — пусто
export function formatSince(ms?: number | null): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// === Подписи видеокарт ===

// «RTX 3090 · текст» / «RTX 3090 · медиа» — имя из nvidia-smi хоста плюс место работы
const GPU_KIND_LABEL: Record<'text' | 'media', string> = { text: 'текст', media: 'медиа' };
export function gpuLabel(name: string, kind: 'text' | 'media'): string {
  return `${name} · ${GPU_KIND_LABEL[kind]}`;
}

// «карта 0 из 2»
export function gpuOrdinal(index: number, total: number): string {
  return `карта ${index} из ${total}`;
}

// === Строки «Кто сейчас на модели» ===

// Строка списка: чат со скоростью. Чужой чат — «Чат другого пользователя»
// БЕЗ названия и перехода (правило: даже админ не видит чужие названия);
// «Прочие запросы: N» — хвост списка, тоже без перехода.
export type WhoRow =
  | {
      kind: 'chat';
      own: boolean;
      // Чужой чат — заглушка, не название
      title: string;
      project?: string;
      speed: string;
      // gen — «38 ток/с» (C.textSecondary), read — «читает» (C.textMuted),
      // wait — «ждёт» (C.warningText)
      tone: 'gen' | 'read' | 'wait';
      sessionId?: string;
      key: string;
    }
  | { kind: 'others'; count: number; key: string };

// Порядок строк — как в макете: идущие, затем ждущие (в порядке поступления,
// номер очереди стенд не отдаёт). Скорость: генерация — ток/с, чтение промпта —
// «читает», ожидание — «ждёт» без номера. Имя проекта резолвит вызывающий из
// стора проектов по projectId (бэк отдаёт только id, и только для своих чатов);
// для чужого чата проекта нет вовсе.
export function whoRows(
  chats?: LocalModelStats['chats'],
  othersCount?: number,
  projectNameFor?: (projectId?: string) => string | undefined,
): WhoRow[] {
  const rows: WhoRow[] = [];
  (chats ?? []).forEach((c, i) => {
    const key = `chat-${i}`;
    let speed: string;
    let tone: 'gen' | 'read' | 'wait';
    if (c.phase === 'waiting') {
      speed = 'ждёт';
      tone = 'wait';
    } else if (c.phase === 'generating' && c.genTps != null && Number.isFinite(c.genTps)) {
      speed = formatTps(c.genTps);
      tone = 'gen';
    } else {
      // prefill/чтение промпта и прочие фазы без скорости
      speed = 'читает';
      tone = 'read';
    }
    rows.push({
      kind: 'chat',
      own: c.own,
      title: c.own ? (c.title ?? '—') : 'Чат другого пользователя',
      project: c.own ? projectNameFor?.(c.projectId) : undefined,
      speed,
      tone,
      sessionId: c.sessionId,
      key,
    });
  });
  if ((othersCount ?? 0) > 0) rows.push({ kind: 'others', count: othersCount!, key: 'others' });
  return rows;
}
