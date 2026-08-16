// Пульс aurora-сияния от звуковых сигналов режима разговора.
//
// Каждый тик beep.ts (thinking-тик ожидания, «нужно решение», micReady, бип
// отправки) поднимает здесь уровень — сияние вспыхивает в такт и плавно гаснет.
// Модуль без состояния React: уровень забирает rAF-луп useMicLevel (takeAuroraPulse),
// подписка onAuroraWake нужна только чтобы разбудить спящий луп.

let level = 0;
const wakeSubs = new Set<() => void>();

// Всплеск сияния в такт звуку. Повторный пульс до затухания — берём максимум,
// чтобы тройной пинг «нужно решение» не глушил сам себя
export function auroraPulse(strength = 1): void {
  level = Math.max(level, strength);
  for (const wake of wakeSubs) {
    try { wake(); } catch { /* подписчик мёртв — не повод ронять остальные */ }
  }
}

// Забрать накопленный уровень (одноразово: следующий кадр начнёт с нуля)
export function takeAuroraPulse(): number {
  const v = level;
  level = 0;
  return v;
}

// Подписка «был пульс»: будит rAF-луп, заснувший после затухания
export function onAuroraWake(fn: () => void): () => void {
  wakeSubs.add(fn);
  return () => { wakeSubs.delete(fn); };
}
