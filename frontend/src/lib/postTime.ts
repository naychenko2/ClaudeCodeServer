// Время написания поста для панели действий в чат-ленте.
//
// Отдельно от fmtReset (lib/rateLimit) — тот форматирует БУДУЩЕЕ время сброса лимита
// («через 2ч», «в 14:32»), а здесь прошедшее время события, и подпись должна быть
// максимально короткой: она живёт в узкой hover-панели рядом с кнопками.

const hhmm = (d: Date) => d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });

const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString();

// Свежие посты подписываем по-человечески («только что», «5 мин назад»): в живой
// переписке относительное время читается быстрее абсолютного — не надо сверяться
// с часами. Дальше часа смысл меняется на «когда это было», и там уже время суток.
const RECENT_MINUTES = 60;

// Компактная подпись: только что / N мин назад — для свежего, дальше сегодня — часы,
// вчера — с пометкой, ещё дальше — с датой. Год добавляем лишь когда он не текущий
// (в переписке этого года он лишний шум).
// null/undefined/битое значение → null: панель просто не рисует время (старая история).
export function formatPostTime(ts?: number | null): string | null {
  if (ts === null || ts === undefined) return null;
  const d = new Date(ts);
  if (isNaN(d.getTime())) return null;

  const now = new Date();
  const minutesAgo = Math.floor((now.getTime() - d.getTime()) / 60000);
  // Будущее (расхождение часов клиента и сервера) относительным не подписываем —
  // «-3 мин назад» выглядело бы поломкой
  if (minutesAgo >= 0 && minutesAgo < 1) return 'только что';
  if (minutesAgo >= 1 && minutesAgo < RECENT_MINUTES) return `${minutesAgo} мин назад`;

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
