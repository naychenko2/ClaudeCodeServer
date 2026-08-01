// Время написания поста для панели действий в чат-ленте.
//
// Отдельно от fmtReset (lib/rateLimit) — тот форматирует БУДУЩЕЕ время сброса лимита
// («через 2ч», «в 14:32»), а здесь прошедшее время события, и подпись должна быть
// максимально короткой: она живёт в узкой hover-панели рядом с кнопками.

const hhmm = (d: Date) => d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });

const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString();

// Компактная подпись: сегодня — только часы, вчера — с пометкой, дальше — с датой.
// Год добавляем лишь когда он не текущий (в переписке этого года он лишний шум).
// null/undefined/битое значение → null: панель просто не рисует время (старая история).
export function formatPostTime(ts?: number | null): string | null {
  if (ts === null || ts === undefined) return null;
  const d = new Date(ts);
  if (isNaN(d.getTime())) return null;

  const now = new Date();
  if (sameDay(d, now)) return hhmm(d);

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (sameDay(d, yesterday)) return `вчера ${hhmm(d)}`;

  const date = d.toLocaleDateString('ru-RU', d.getFullYear() === now.getFullYear()
    ? { day: 'numeric', month: 'short' }
    : { day: 'numeric', month: 'short', year: 'numeric' });
  return `${date}, ${hhmm(d)}`;
}

// Полная дата-время — в тултип панели, где компактная подпись обрезает подробности
export function formatPostTimeFull(ts?: number | null): string | null {
  if (ts === null || ts === undefined) return null;
  const d = new Date(ts);
  if (isNaN(d.getTime())) return null;
  return d.toLocaleString('ru-RU', {
    day: 'numeric', month: 'long', year: 'numeric',
    hour: '2-digit', minute: '2-digit',
  });
}
