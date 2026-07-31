import type { ChatItem } from '../../types';

// Накопительная статистика генераций glif по ленте чата.
// Денежной суммы у glif нет — считаем генерации; кредиты — только если billing доехал.
// byType — разбивка по outputType (image/video/audio/…): число генераций каждого типа.
export interface GlifGenStats {
  count: number;
  credits: number;          // сумма списанных кредитов; 0 — данных о кредитах не было
  hasCredits: boolean;      // приехал ли credits хотя бы у одной генерации
  byType: Map<string, number>;
}

// Свод по glif_cost-элементам ленты. Дедуп по jobId (compose_project и get_job_status
// одной генерации несут один id; возможен повтор из истории).
export function computeGlifGenStats(items: ChatItem[]): GlifGenStats {
  const byType = new Map<string, number>();
  let count = 0, credits = 0, hasCredits = false;
  const seen = new Set<string>();
  for (const it of items) {
    if (it.kind !== 'glif_cost' || seen.has(it.jobId)) continue;
    seen.add(it.jobId);
    count++;
    if (typeof it.credits === 'number') { credits += it.credits; hasCredits = true; }
    const key = it.outputType ?? 'media';
    byType.set(key, (byType.get(key) ?? 0) + 1);
  }
  return { count, credits, hasCredits, byType };
}

// Компактный формат кредитов: целые — без дробной части (12 кр.), дробные — 1 знак (12.5 кр.)
export const fmtCredits = (n: number) =>
  (Number.isInteger(n) ? String(n) : n.toFixed(1)) + ' кр.';
