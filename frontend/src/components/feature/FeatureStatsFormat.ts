// Форматтеры чисел статистики фичи (ST-F8): вынесены из FeatureStatsBlocks.tsx
// (сам блок удалён будет в ST-F14) — поведение не меняется, это те же функции.
// Чистые помощники, без DOM.

// Токены: «1,2М» / «640 тыс.» / «12 345». null → '' (нет данных).
export function formatTokens(n: number | null): string {
  if (n == null) return '';
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace('.', ',')}М`;
  if (n >= 1_000) return `${Math.round(n / 1000)} тыс.`;
  return String(n);
}

// Деньги (USD): «$15,60» / «$0,42». null → '' (нет данных).
export function formatUsd(n: number | null): string {
  if (n == null) return '';
  return `$${n.toFixed(2).replace('.', ',')}`;
}

// Доля 0..1 → «42 %». Округление каждой доли независимо: сумма текстов может
// дать 101 % — полосу кольца вид строит от суммы частей, а не из этих текстов.
export function percentText(share: number): string {
  return `${Math.round(share * 100)} %`;
}
