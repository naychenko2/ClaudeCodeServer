// Счётчик горящих инцидентов для пункта «Телеметрия» в меню аватара.
//
// Шапка живёт во ВСЕХ разделах и монтируется на каждой навигации, а каждый запрос
// к /api/telemetry/incidents идёт живым обращением в SigNoz (до 20 с при медленном).
// Поэтому здесь модуль-кэш со сроком годности: значение переиспользуется всеми
// монтированиями шапки, а обновляется не чаще раза в минуту — ровно шаг опроса
// алертов, чаще всё равно не меняется.
import { api } from '../../lib/api';

const TTL_MS = 60_000;

let value = 0;
let fetchedAt = 0;
let inFlight: Promise<number> | null = null;

/// Число горящих инцидентов. Свежее значение из кэша — мгновенно, устаревшее — после
/// запроса. Ошибка/недоступный SigNoz — 0: бейдж не повод показывать аварию.
export function loadIncidentBadge(): Promise<number> {
  const now = Date.now();
  if (now - fetchedAt < TTL_MS) return Promise.resolve(value);
  if (inFlight) return inFlight;

  inFlight = api.telemetry.incidents()
    .then(res => {
      value = res.items.filter(i => i.isFiring).length;
      fetchedAt = Date.now();
      return value;
    })
    .catch(() => {
      // Помечаем попытку как состоявшуюся: иначе при лежащем SigNoz каждая навигация
      // снова уходила бы в двадцатисекундный запрос
      fetchedAt = Date.now();
      value = 0;
      return 0;
    })
    .finally(() => { inFlight = null; });

  return inFlight;
}
