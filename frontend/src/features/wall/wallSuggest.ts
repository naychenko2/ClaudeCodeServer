// Чаты, которые стоит поставить колонками на стену. Правила, а не модель — всё нужное
// видно по статусу и свежести, поэтому подсказка AI-хаба работает офлайн, без токенов
// и без Ollama.
//
// Два разряда поводов, и разница между ними принципиальна для UI:
//  • ЖИВЫЕ (live) — прямо сейчас идёт ход или чат ждёт ответа на запрос разрешения.
//    Такое состояние живёт секунды-минуты, и ради него кнопку будить уместно.
//  • СВЕЖИЕ — ход уже завершён (штатный статус живого чата между ходами!), но чат
//    трогали за последние сутки. Это возможность, а не срочность: попадают в подсказку,
//    но кнопку не будят.
//
// Отсюда живёт и pull-путь (действие палитры wall.addActive), и push-путь (правило
// проактивных подсказок): один отбор на оба, иначе балун обещал бы одно, а клик делал
// другое.

import type { Session } from '../../types';
import { api } from '../../lib/api';
import { getWallState, addChat, MAX_CHATS } from './wallStore';

// Ждёт ответа — сильнее: такой чат простаивает без человека, а идущий ход движется сам
const LIVE_RANK: Partial<Record<Session['status'], number>> = { waiting: 2, working: 1 };
// Оборванные и упавшие чаты не предлагаем: ставить колонкой нечего
const SKIP_STATUS = new Set<Session['status']>(['orphaned', 'error']);
// Окно свежести: сутки. Дальше чат — не «сегодняшняя работа», а архив
const FRESH_MS = 24 * 3600_000;

export interface WallCandidate {
  session: Session;
  // Повод «прямо сейчас» (идёт ход / ждёт ответа) — по нему AI-хаб решает, будить ли кнопку
  live: boolean;
}

interface PickOptions {
  // Чаты, уже стоящие колонками
  taken: Set<string>;
  // Сколько свободных мест на стене
  limit: number;
  // Точка отсчёта свежести (в тестах задаётся явно — «сейчас» не должно течь)
  now?: number;
}

// Отбор и порядок: сперва ждущие ответа, потом идущие ходы, потом свежие;
// внутри разряда — тронутые недавно выше.
export function chatsForWall(candidates: Session[], { taken, limit, now = Date.now() }: PickOptions): WallCandidate[] {
  if (limit <= 0) return [];

  const scored: { session: Session; live: boolean; rank: number; updated: number }[] = [];
  for (const s of candidates) {
    if (taken.has(s.id)) continue;
    // Временный чат одноразовый по смыслу — место на стене ему ни к чему
    if (s.expiresAfterMinutes != null) continue;
    // Пустой чат нечего ставить рядом: смотреть в нём не на что
    if (s.messageCount <= 0) continue;
    if (SKIP_STATUS.has(s.status)) continue;

    const rank = LIVE_RANK[s.status] ?? 0;
    // Битая дата не должна притворяться свежестью
    const updated = Date.parse(s.updatedAt) || 0;
    // Не живой и не свежий — это архив, а не повод
    if (rank === 0 && now - updated > FRESH_MS) continue;

    scored.push({ session: s, live: rank > 0, rank, updated });
  }

  // Стабильный порядок вместо порядка бэка: разряд, потом свежесть
  scored.sort((a, b) => (b.rank - a.rank) || (b.updated - a.updated));
  return scored.slice(0, limit).map(({ session, live }) => ({ session, live }));
}

// Сколько мест на стене свободно (набор берём из стора, а не из замыкания вызывающего)
function freeSlots(): number {
  return MAX_CHATS - getWallState().chats.length;
}

// Кандидаты для подсказки, не больше свободных мест. Пустой массив — предлагать нечего
// (мест нет либо поводов нет), и тогда AI-хаб молчит: пустое обещание хуже отсутствующего.
export async function loadChatsForWall(): Promise<WallCandidate[]> {
  const free = freeSlots();
  if (free <= 0) return [];
  try {
    const candidates = await api.wall.candidates();
    return chatsForWall(candidates, { taken: new Set(getWallState().chats.map(c => c.id)), limit: free });
  } catch {
    return [];
  }
}

// Поставить предложенные чаты колонками. Возвращает, сколько встало — вызывающий сам
// решает, что сказать человеку (модуль остаётся без UI).
export async function addChatsToWall(): Promise<number> {
  const picked = await loadChatsForWall();
  let added = 0;
  for (const { session } of picked) {
    // Состав мог измениться, пока грузились кандидаты: addChat сам отсеет дубль и
    // переполнение, а считаем только реально вставшие колонки
    if (addChat(session) === 'added') added++;
  }
  return added;
}
