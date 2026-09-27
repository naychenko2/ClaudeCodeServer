import type { ChatItem } from '../types';

// Подписи карточки «контекст обрезан» (событие context_pruned прокси локальной модели).
// Чистые функции без React — тестируются отдельно от рендера.

export type PrunedItem = Extract<ChatItem, { kind: 'context_pruned' }>;

// Тот же вид числа, что у карточки сжатия (compact_boundary): 171k, 1.5k, 940
export function fmtPrunedTokens(n: number): string {
  return n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1) + 'k' : String(n);
}

// Заголовок строки: что именно случилось и с каким объёмом
export function prunedHeadline(item: PrunedItem): string {
  if (item.pruneKind === 'compact_cloud') {
    // Сжатие ушло в облако: объём истории тут не главное — важна цена паузы
    const s = roundSeconds(item.prefillSeconds);
    return s === null ? 'сжатие в облаке' : `сжатие в облаке · ${s} с`;
  }
  const { tokensBefore: before, tokensAfter: after } = item;
  if (before > 0 && after > 0) return `контекст обрезан · ${fmtPrunedTokens(before)} → ${fmtPrunedTokens(after)}`;
  if (before > 0) return `контекст обрезан · было ${fmtPrunedTokens(before)}`;
  return 'контекст обрезан';
}

// Подзаголовок: сколько блоков ушло (виды с нулём не перечисляем), сколько ждали
// пересчёт префикса и какая доля запроса пришла из кэша. У сжатия в облаке подробностей
// нет — всё сказано в заголовке.
export function prunedDetails(item: PrunedItem): string | null {
  if (item.pruneKind === 'compact_cloud') return null;

  const parts: string[] = [];
  if (item.blocks > 0) {
    const kinds: string[] = [];
    if (item.resultBlocks > 0) kinds.push(`выводов ${item.resultBlocks}`);
    if (item.inputBlocks > 0) kinds.push(`входов ${item.inputBlocks}`);
    if (item.thinkingBlocks > 0) kinds.push(`размышлений ${item.thinkingBlocks}`);
    parts.push(kinds.length > 0
      ? `${item.blocks} ${blocksPlural(item.blocks)} (${kinds.join(', ')})`
      : `${item.blocks} ${blocksPlural(item.blocks)}`);
  }

  const s = roundSeconds(item.prefillSeconds);
  if (s !== null) parts.push(`пересчёт ${s} с`);

  const pct = cachePct(item);
  if (pct !== null) parts.push(`из кэша ${pct}%`);

  return parts.length > 0 ? parts.join(' · ') : null;
}

// Доля запроса, взятая из кэша: 0..100 либо null (нет данных / пустой запрос).
// Ноль — значимое число (кэш сгорел от сдвига), поэтому 0% показываем, а не прячем.
export function cachePct(item: Pick<PrunedItem, 'cacheReadTokens' | 'promptTokens'>): number | null {
  const { cacheReadTokens: read, promptTokens: prompt } = item;
  if (typeof read !== 'number' || typeof prompt !== 'number' || prompt <= 0) return null;
  return Math.min(100, Math.max(0, Math.round((read / prompt) * 100)));
}

// Секунды под округление: меньше половины секунды не показываем вовсе —
// «пересчёт 0 с» полезного не сообщает
function roundSeconds(sec: number | undefined): number | null {
  if (typeof sec !== 'number' || !isFinite(sec)) return null;
  const rounded = Math.round(sec);
  return rounded > 0 ? rounded : null;
}

function blocksPlural(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'блок';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return 'блока';
  return 'блоков';
}

// Сводка по чату для поповера контекста: сколько было сдвигов и сколько суммарно
// срезано. Обрезка и сжатие в облаке считаются вместе — для человека это одно и то же
// «контекст двигали», а разбивка по видам живёт в карточках ленты.
export interface PrunedSummary {
  count: number;        // сколько раз двигали контекст за чат
  savedTokens: number;  // сколько суммарно срезано (сумма before − after по сдвигам)
}

export function summarizePruned(items: ChatItem[]): PrunedSummary | undefined {
  let count = 0, savedTokens = 0;
  for (const it of items) {
    if (it.kind !== 'context_pruned') continue;
    count++;
    const saved = it.tokensBefore - it.tokensAfter;
    if (saved > 0) savedTokens += saved;
  }
  return count > 0 ? { count, savedTokens } : undefined;
}

export function prunedSummaryText(s: PrunedSummary): string {
  const shifts = `${s.count} ${shiftsPlural(s.count)}`;
  return s.savedTokens > 0 ? `${shifts} · −${fmtPrunedTokens(s.savedTokens)}` : shifts;
}

function shiftsPlural(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'сдвиг';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return 'сдвига';
  return 'сдвигов';
}
