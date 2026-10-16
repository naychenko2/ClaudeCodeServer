import type { ChatItem, Session } from '../types';

// «Продолжить в новом чате»: чистая логика без React — показ кнопки, порог подсказки.

// Окно от 1M токенов считается «большим»: для него порог абсолютный, а не процентный
export const BIG_WINDOW_TOKENS = 1_000_000;
// Порог подсказки для моделей с окном 1M (токены контекста последнего хода)
export const CONTINUE_HINT_BIG_WINDOW_TOKENS = 300_000;
// Для остальных моделей — доля окна
export const CONTINUE_HINT_WINDOW_FRACTION = 0.6;
// «Не сейчас» скрывает подсказку до следующего роста контекста на этот шаг
export const CONTINUE_HINT_SNOOZE_STEP = 100_000;

// Индекс последнего ответа агента верхнего уровня (text без parentToolUseId, не реплика
// субагента). Продолжение возможно только с конца: кнопка стоит под ним и больше нигде.
// -1 — ответов агента в ленте нет.
export function lastAssistantTextIndex(items: readonly ChatItem[]): number {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind === 'text' && !it.parentToolUseId) return i;
  }
  return -1;
}

// Порог подсказки в токенах: 300K для окна ≥ 1M, иначе 60 % окна
export function continueHintThreshold(windowTokens: number): number {
  return windowTokens >= BIG_WINDOW_TOKENS
    ? CONTINUE_HINT_BIG_WINDOW_TOKENS
    : Math.round(windowTokens * CONTINUE_HINT_WINDOW_FRACTION);
}

// Показывать ли подсказку. snoozedAt — размер контекста на момент «Не сейчас»
// (undefined — не откладывали). Нет оценки контекста — подсказки нет.
export function shouldShowContinueHint(opts: {
  tokens: number | undefined;
  windowTokens: number;
  snoozedAt?: number;
}): boolean {
  const { tokens, windowTokens, snoozedAt } = opts;
  if (tokens == null || tokens <= 0) return false;
  if (tokens < continueHintThreshold(windowTokens)) return false;
  if (snoozedAt != null && tokens < snoozedAt + CONTINUE_HINT_SNOOZE_STEP) return false;
  return true;
}

// 312K / 1,2M: от 1M — в миллионах с одной цифрой после запятой, чтобы длинное число
// не разъезжало тонкую полосу «Продолжить» (спецификация continue-hint-variants.md)
export function formatContextK(tokens: number): string {
  if (tokens >= 1_000_000) {
    const m = Math.round((tokens / 1_000_000) * 10) / 10;
    return `${m.toString().replace('.', ',')}M`;
  }
  return `${Math.round(tokens / 1000)}K`;
}

// Связи продолжения из DTO сессии: поля могут отсутствовать — читаем как null
export function continuationLinks(s: Pick<Session, 'continuedFromSessionId' | 'continuedInSessionId'>) {
  return { from: s.continuedFromSessionId ?? null, into: s.continuedInSessionId ?? null };
}

// Отправка из диалога: true — запрос прошёл (владелец сам закроет диалог и уйдёт в новый чат),
// false — отказ (409 «чат занят», 400 «нет ходов агента»). Текст ошибки тостом показывает
// владелец в onConfirm; здесь только итог, по которому диалог снимает блокировку кнопки.
export async function submitContinue(
  onConfirm: (note: string) => Promise<void>,
  note: string,
): Promise<boolean> {
  try {
    await onConfirm(note.trim());
    return true;
  } catch {
    return false;
  }
}

// Кнопка «Продолжить в новом чате» под последним ответом: показываем, только когда ход
// завершён (ответ не стримится), сеть есть и чат ещё не продолжен. Привязка к конкретному
// элементу ленты (последний текст агента) считает ChatPanel отдельно.
//   online — сеть; isWaiting — ход занят (редьюсер); turnRunning — ход ещё идёт
//   (последний элемент ленты не result/error); continuedInto — чат уже продолжен.
export function continueButtonGate(opts: {
  online: boolean;
  isWaiting: boolean;
  turnRunning: boolean;
  continuedInto: boolean;
}): boolean {
  return opts.online && !opts.isWaiting && !opts.turnRunning && !opts.continuedInto;
}

// Отказ 409 «уже продолжен»: бэкенд кладёт ссылку на новый чат (id) в текст ошибки либо
// в отдельное поле тела. Если ссылка нашлась — верни её, иначе null (тост без действия).
export function continuedLinkFromError(err: unknown): string | null {
  const e = err as (Error & { status?: number; body?: unknown }) | null;
  if (!e || e.status !== 409) return null;
  const body = e.body as Record<string, unknown> | null;
  if (body) {
    for (const key of ['newSessionId', 'continuedInSessionId', 'sessionId', 'chatId']) {
      const v = body[key];
      if (typeof v === 'string' && v) return v;
    }
  }
  const m = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.exec(e.message ?? '');
  return m ? m[0] : null;
}
