// Чистые функции форматирования вкладки «Медиа» (ComfyUI): юнит-тесты без React и DOM.
// Все подписи — по спецификации макета (docs/mockups/local-model-stats-panel.html,
// раздел «Медиа · спецификация для Киры»): состояния ComfyUI, «Занят», «В памяти»
// (loadedModels узла: не более 3 имён + «ещё N», длинные — с многоточием),
// старт очереди, факт против обещанного ETA (подсветка при превышении на 20 %+),
// итог «последнего часа». Чужие прогоны анонимны — без перехода и без названия.

import type { LocalModelMedia, MediaDone, MediaQueue, MediaRunning } from './api';

// === Статус ComfyUI ===

// Тон статуса: ok — работает, busy — занят, down — не отвечает, disabled — выключено.
export type ComfyTone = 'ok' | 'busy' | 'down' | 'disabled';

// comfy.status (бэкенд уже решил: working|busy|down|disabled) → подпись + тон.
// «Занят» — 4 из 4 мест или идёт тяжёлая задача (решает бэкенд, мы только подписываем).
export function comfyStatusInfo(status: LocalModelMedia['comfy']['status']): { label: string; tone: ComfyTone } {
  switch (status) {
    case 'working': return { label: 'Работает', tone: 'ok' };
    case 'busy': return { label: 'Занят', tone: 'busy' };
    case 'down': return { label: 'Не отвечает', tone: 'down' };
    case 'disabled': return { label: 'Выключено', tone: 'disabled' };
  }
}

// «Занят» по слотам и идущим: все места заняты ИЛИ идёт тяжёлая задача.
// Чистая проверка — бэкенд считает status, но тон «Занят» на фронте сверяем сам.
export function isComfyBusy(slots: { used: number; limit: number }, running: MediaRunning[]): boolean {
  if (slots.limit > 0 && slots.used >= slots.limit) return true;
  return running.some(r => r.heavy);
}

// === «Модель в памяти» ===

// Тип состояния строки «В памяти»: loaded — модель в памяти (имена с узла или
// «работает сейчас: …»), empty — ничего, unknown — узел не сообщает (gpus==null).
export type LoadedModelState = 'loaded' | 'empty' | 'unknown';
export interface LoadedModelLine {
  state: LoadedModelState;
  label: string;
}

// Имя модели из строки операции: «Видео 5 с · MiniMax H3» → «MiniMax H3».
// Берём последний фрагмент после «·»; без «·» — вся строка (операция и есть модель).
export function modelFromOp(op?: string | null): string {
  if (!op) return '';
  const parts = op.split('·').map(p => p.trim());
  const last = parts[parts.length - 1];
  return last || op.trim();
}

// Обрезка длинного имени модели с многоточием: «…» заменяет хвост. Сервер уже
// чистит имя (basename, ≤ 96 символов), это — ширина строки на экране, не формат.
export function truncateName(name: string, max = 24): string {
  if (name.length <= max) return name;
  return `${name.slice(0, max - 1)}…`;
}

// «В памяти: <имена>»: не более 3 имён + «ещё N»; длинные имена — по ширине.
// Имена — как есть с сервера (текст, без разметки). Чистая функция — юнит-тесты.
export function inMemoryLine(names: string[], maxNames = 3): string {
  const shown = names.slice(0, maxNames).map(n => truncateName(n));
  const rest = names.length - maxNames;
  return `В памяти: ${shown.join(', ')}${rest > 0 ? `, ещё ${rest}` : ''}`;
}

// «В памяти»: узел сообщает loadedModels (непустой) — честные имена из его памяти;
// gpus==null — «узел не сообщает» (не выдумываем «загружена»);
// loadedModels нет, но идёт прогон — запасной: модель из его операции, подписана
// «работает сейчас» (не «загружена»); иначе — «ничего, модель загрузится на первой задаче».
export function loadedModelLine(media: LocalModelMedia): LoadedModelLine {
  const names = media.loadedModels;
  if (names != null && names.length > 0) {
    return { state: 'loaded', label: inMemoryLine(names) };
  }
  if (media.gpus == null) {
    return { state: 'unknown', label: 'узел не сообщает' };
  }
  const running = media.running[0];
  if (running) {
    const model = modelFromOp(running.op);
    if (model) return { state: 'loaded', label: `работает сейчас: ${model}` };
  }
  return { state: 'empty', label: 'ничего, модель загрузится на первой задаче' };
}

// === Время ===

// unix-мс → «14:32» (локальные часы:минуты). Без момента — пусто.
export function formatClockTime(ms?: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Секунды → «M:SS» (или «H:MM:SS» для часа+). «5:40», «0:47», «1:12». Без значения — «—».
export function formatClock(sec?: number | null | undefined): string {
  if (sec == null || !Number.isFinite(sec) || sec < 0) return '—';
  const s = Math.round(sec);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const r = s % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}`;
  return `${m}:${String(r).padStart(2, '0')}`;
}

// === Очередь: ожидаемый старт ===

// Строка ожидаемого старта задачи в очереди:
//  • есть startInSeconds → «старт ≈ HH:MM · через N мин» (HH:MM — из startInSeconds+now);
//  • только etaSeconds → «старт ≈ через N мин»;
//  • ничего → «время не замерено».
// compact — короткая форма (мобила): «≈ N мин» вместо «старт ≈ HH:MM · через N мин».
export function queueStartLine(startInSeconds: number | null | undefined, etaSeconds: number | null | undefined, now: number, compact?: boolean): string {
  if (startInSeconds != null && Number.isFinite(startInSeconds) && startInSeconds > 0) {
    const at = formatClockTime(now + startInSeconds * 1000);
    const inText = formatIn(etaSeconds);
    if (compact) return inText ? `≈ ${inText}` : at;
    return inText ? `старт ≈ ${at} · через ${inText}` : `старт ≈ ${at}`;
  }
  if (etaSeconds != null && Number.isFinite(etaSeconds) && etaSeconds > 0) {
    const inText = formatIn(etaSeconds);
    return compact ? `≈ ${inText}` : `старт ≈ через ${inText}`;
  }
  return 'время не замерено';
}

// «через 3 мин» / «через 40 с»: человекочитаемое время из секунд; null/нет — пусто.
function formatIn(sec: number | null | undefined): string {
  if (sec == null || !Number.isFinite(sec) || sec <= 0) return '';
  if (sec < 60) return `${Math.round(sec)} с`;
  const m = Math.round(sec / 60);
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return rm > 0 ? `${h} ч ${rm} мин` : `${h} ч`;
}

// === «Последний час»: факт против обещанного ETA ===

// Факт дольше обещанного на порог (по умолчанию 20 %) и сильнее → подсветка.
// etaSeconds=null (без замера) — не превышение. fact/eta ≤ 0 — не превышение.
export function isEtaOverdue(factSeconds: number | null | undefined, etaSeconds: number | null | undefined, threshold = 0.2): boolean {
  if (factSeconds == null || etaSeconds == null) return false;
  if (etaSeconds <= 0 || factSeconds <= 0) return false;
  return (factSeconds - etaSeconds) / etaSeconds >= threshold;
}

// Строка результата «последнего часа»:
//  • completed → «готово в HH:MM»;
//  • failed → «упала: {reason}» (reason из op, если есть);
//  • cancelled → «отменено».
export function doneOutcomeLine(d: MediaDone): string {
  if (d.outcome === 'failed') {
    const reason = modelFromOp(d.op);
    return reason ? `упала: ${reason}` : 'упала';
  }
  if (d.outcome === 'cancelled') return 'отменено';
  const at = formatClockTime(d.finishedAt);
  return at ? `готово в ${at}` : 'готово';
}

// === Итог «последнего часа» ===

export interface LastHourSummary {
  done: number;
  failed: number;
  // Среднее превышение факта над обещанным (доля, 0.04 = «на 4 % дольше»); null — не считали
  avgOverPerc: number | null;
  // Сколько строк скрыто за «и ещё N» (показываем до 8)
  more: number;
}

// Показываем до 8 последних; дальше «и ещё N».
export const LAST_HOUR_VISIBLE = 8;

// Итог: «Готово 6 · упало 1 · в среднем на 4 % дольше обещанного · и ещё 2».
// avgOverPerc — средняя доля превышения (fact−eta)/eta по прогонам, где есть и fact, и eta;
// null — таких прогонов нет (не пишем «в среднем …»).
export function lastHourSummary(lastHour: MediaDone[]): LastHourSummary {
  const done = lastHour.filter(d => d.outcome === 'completed').length;
  const failed = lastHour.filter(d => d.outcome === 'failed').length;
  const overs: number[] = [];
  for (const d of lastHour) {
    if (d.runSeconds != null && d.etaSeconds != null && d.etaSeconds > 0 && d.runSeconds > 0) {
      overs.push((d.runSeconds - d.etaSeconds) / d.etaSeconds);
    }
  }
  const avgOverPerc = overs.length > 0 ? overs.reduce((a, b) => a + b, 0) / overs.length : null;
  const more = Math.max(0, lastHour.length - LAST_HOUR_VISIBLE);
  return { done, failed, avgOverPerc, more };
}

// === Прогресс идущего прогона (полоса) ===

// Прогресс по шагам: fraction = step/total (0..1, предел 0.99), mode exact.
// Без шагов, но есть percent+percentExact → exact по percent.
// Без шагов и percentExact=false, но есть percent/ETA → estimate (предел 0.95).
// Ничего → indeterminate.
export interface JobProgress {
  mode: 'exact' | 'estimate' | 'indeterminate';
  fraction?: number;
}

export function runningProgress(r: MediaRunning): JobProgress {
  const s = r.steps;
  if (s != null && s.total > 0) {
    return { mode: 'exact', fraction: Math.max(0, Math.min(0.99, s.step / s.total)) };
  }
  if (r.percent != null && r.percentExact === true) {
    return { mode: 'exact', fraction: Math.max(0, Math.min(0.99, r.percent / 100)) };
  }
  if (r.percent != null || (r.remainingSeconds != null && r.remainingSeconds > 0)) {
    const frac = r.percent != null ? Math.min(0.95, r.percent / 100) : 0.5;
    return { mode: 'estimate', fraction: Math.max(0, frac) };
  }
  return { mode: 'indeterminate' };
}

// «осталось ≈ N мин» / «время не замерено» (под полосой, справа).
export function remainingLine(r: MediaRunning): string {
  if (r.remainingSeconds != null && r.remainingSeconds > 0) {
    return `осталось ≈ ${formatIn(r.remainingSeconds)}`;
  }
  return 'время не замерено';
}

// === Чей прогон: свой (с переходом) или чужой (без перехода) ===

// Свой прогон — клик открывает чат (нужен sessionId); чужой — div без перехода.
// Чужой прогон без sessionId (бэкенд его не отдаёт) — тоже без перехода.
export function isOwnJob(job: { own: boolean; sessionId?: string | null }): boolean {
  return job.own && job.sessionId != null;
}

// Строка «чей чат»: свой — название (из чата, резолвит вызывающий); чужой — заглушка.
export function jobOwnerLabel(job: { own: boolean; title?: string | null }): string {
  if (job.own) return job.title || '—';
  return 'Чат другого пользователя';
}

// === Подпись карты (общая с вкладкой «LLM») ===

// Очередь: «N из M мест занято» (счётчик мест в заголовке).
export function slotsLine(slots: { used: number; limit: number }): string {
  return `${slots.used} из ${slots.limit} мест занято`;
}

// Очередь: строка одной задачи — номер + операция (только просмотр, без кнопок).
export function queueJobLabel(q: MediaQueue): string {
  return `${q.position}. ${q.op}`;
}
