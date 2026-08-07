// Время написания поста для панели действий в чат-ленте.
//
// Отдельно от fmtReset (lib/rateLimit) — тот форматирует БУДУЩЕЕ время сброса лимита
// («через 2ч», «в 14:32»), а здесь прошедшее время события, и подпись должна быть
// максимально короткой: она живёт в узкой hover-панели рядом с кнопками.

// Форматтеры — модульные константы, а не toLocale*-вызовы на каждую подпись: каждый
// такой вызов строит Intl.DateTimeFormat заново, и на ленте в тысячу с лишним элементов
// это стоило сотен миллисекунд при открытии чата (видно в CPU-профиле переключения).
const FMT_TIME = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const FMT_DATE = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short' });
const FMT_DATE_YEAR = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
const FMT_FULL = new Intl.DateTimeFormat('ru-RU', {
  day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit',
});

const hhmm = (d: Date) => FMT_TIME.format(d);

// Сравнение по календарному дню без toDateString (тот тоже форматирует строку)
const sameDay = (a: Date, b: Date) =>
  a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

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

  const date = (d.getFullYear() === now.getFullYear() ? FMT_DATE : FMT_DATE_YEAR).format(d);
  return `${date}, ${hhmm(d)}`;
}

// Полная дата-время — в тултип панели, где компактная подпись обрезает подробности
export function formatPostTimeFull(ts?: number | null): string | null {
  if (ts === null || ts === undefined) return null;
  const d = new Date(ts);
  if (isNaN(d.getTime())) return null;
  return FMT_FULL.format(d);
}
