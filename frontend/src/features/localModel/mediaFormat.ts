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

// === История (ADR-030) ===

// Месяцы в родительном падеже для «13 октября»
const MONTHS_GEN = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];

// Секунды → человек: 0 → «0 с», <60 → «42 с», <3600 → «1 мин 20 с» (ровно по минуте
// → «6 мин»), больше → «1 ч 05 мин». null/некорректно → «—».
export function formatDurationHuman(sec?: number | null): string {
  if (sec == null || !Number.isFinite(sec) || sec < 0) return '—';
  const s = Math.round(sec);
  if (s < 60) return `${s} с`;
  if (s < 3600) {
    const m = Math.floor(s / 60);
    const r = s % 60;
    return r > 0 ? `${m} мин ${r} с` : `${m} мин`;
  }
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return `${h} ч ${String(m).padStart(2, '0')} мин`;
}

// ISO-8601 UTC → «14:32» (местное время). Не задано/некорректно → ''
export function formatClockFromIso(iso?: string | null): string {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// ISO → «14:30:52» (строка «Поставлена» карточки)
export function formatClockSecFromIso(iso?: string | null): string {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
}

// ISO → «15 октября, 14:32» (подзаголовок карточки)
export function formatDayTimeFromIso(iso?: string | null): string {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return '';
  return `${d.getDate()} ${MONTHS_GEN[d.getMonth()]}, ${formatClockFromIso(iso)}`;
}

// Метка группы по дням: «Сегодня» / «Вчера» / «13 октября» (без года).
// nowMs — для тестов (группировка считается от «сейчас»).
export function dayGroupLabel(iso?: string | null, nowMs = Date.now()): string {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return '';
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const today = new Date(nowMs); today.setHours(0, 0, 0, 0);
  const diff = Math.round((today.getTime() - day.getTime()) / 86400000);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Вчера';
  return `${day.getDate()} ${MONTHS_GEN[day.getMonth()]}`;
}

// === Вид и операция ===

const KIND_LABEL: Record<string, string> = {
  image: 'Картинка', video: 'Видео', audio: 'Звук', train: 'Обучение', other: 'Генерация',
};

// kind → подпись; неизвестный/отсутствующий — «Генерация»
export function historyKindLabel(kind?: string | null): string {
  return (kind != null && KIND_LABEL[kind]) || 'Генерация';
}

// op → человекочитаемая операция (макет: «Картинка · по тексту»).
// Неизвестный op — как есть: опечатка бэкенда не должна рвать строку.
const OP_LABEL: Record<string, string> = {
  generate_image: 'по тексту',
  edit_image: 'правка',
  face_detail: 'правка лица',
  text_to_video: 'по тексту',
  image_to_video: 'из картинки',
  reference_to_video: 'по референсам',
  video_upscale: 'апскейл',
  video_inpaint: 'правка видео',
  music_generate: 'песня',
  music_edit: 'правка музыки',
  speech: 'озвучка',
  voice_convert: 'конверт голоса',
  voice_train: 'обучение голоса',
  audio_separate: 'разделение',
  audio_to_midi: 'в MIDI',
  audio_enhance: 'улучшение',
  transcribe: 'транскрипт',
  lora_train: 'обучение LoRA',
  lora_delete: 'удаление LoRA',
  editor_inputs: 'входы редактора',
};

export function historyOpLabel(op?: string | null): string {
  if (!op) return '';
  return OP_LABEL[op] ?? op;
}

// Строка 1 записи: «Картинка · по тексту»; без операции — только вид
export function historyRowTitle(kind?: string | null, op?: string | null): string {
  const kindLabel = historyKindLabel(kind);
  const opLabel = historyOpLabel(op);
  return opLabel ? `${kindLabel} · ${opLabel}` : kindLabel;
}

// === 410: причины «результат больше не доступен» ===

// reason → человекочитаемый текст (макет §5). Неизвестный/отсутствующий reason —
// общий текст «Файл недоступен.»
const GONE_REASON: Record<string, string> = {
  expired: 'Истёк срок хранения: рабочие файлы редактора живут 7 дней.',
  missing: 'Файл удалили.',
  project_gone: 'Проект удалён.',
  local_project: 'Проект переехал на устройство — его файлы больше не на сервере.',
  source_disabled: 'Редактор, в котором делали генерацию, выключен на этом сервере.',
};

export function goneReasonText(reason?: string | null): string {
  if (reason == null || reason === '') return 'Файл недоступен.';
  return GONE_REASON[reason] ?? 'Файл недоступен.';
}

// === Распознанные причины ошибки ===

// Распознанная причина → человеческая фраза (макет §5). Остальные — общий текст.
export function humanErrorPhrase(error?: string | null): string {
  if (error && /out of memory/i.test(error)) return 'Ошибка: не хватило памяти видеокарты.';
  return 'Ошибка при расчёте.';
}

// === Сводка исходов ===

// «Готово 290 · отменено 4 · ошибок 12 · прервано 6» — только ненулевые части;
// все нули — null (строку не показываем).
export function historySummaryLine(counters: { outcomes: Record<string, number> } | null | undefined): string | null {
  if (!counters) return null;
  const parts: string[] = [];
  const add = (n: number | undefined, label: string) => { if (n != null && n > 0) parts.push(`${label} ${n}`); };
  add(counters.outcomes.completed, 'Готово');
  add(counters.outcomes.cancelled, 'отменено');
  add(counters.outcomes.failed, 'ошибок');
  add(counters.outcomes.interrupted, 'прервано');
  return parts.length ? parts.join(' · ') : null;
}

// === Строка 3 записи: «masha · очередь 1 мин 20 с · расчёт 42 с» ===

// Ожидание в очереди = started − enqueued (по часам бэкенда). startedAt не задан
// (прервано до старта) — null («неизвестно»).
export function historyWaitSeconds(enqueuedAt?: string | null, startedAt?: string | null): number | null {
  if (!enqueuedAt || !startedAt) return null;
  const a = new Date(enqueuedAt).getTime();
  const b = new Date(startedAt).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return null;
  return Math.max(0, Math.round((b - a) / 1000));
}

// «очередь 1 мин 20 с · расчёт 42 с»: без времени старта — «очередь —»,
// без времени расчёта — часть «расчёт» не показываем.
export function historyWaitLine(enqueuedAt?: string | null, startedAt?: string | null, runSeconds?: number | null): string {
  const wait = historyWaitSeconds(enqueuedAt, startedAt);
  const waitPart = wait == null ? 'очередь —' : `очередь ${formatDurationHuman(wait)}`;
  if (runSeconds == null) return waitPart;
  return `${waitPart} · расчёт ${formatDurationHuman(runSeconds)}`;
}

// === Параметры и время карточки ===

// Seed → «2 914 337 016» (пробелы между группами по три); null — «не задан»
export function seedFormatted(seed?: number | null): string {
  if (seed == null || !Number.isFinite(seed)) return 'не задан';
  return String(Math.trunc(seed)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

// Число вариантов (batch): из params.count; не число/отсутствует — 1
export function historyVariantCount(params?: Record<string, unknown> | null): number {
  const n = params?.count;
  return typeof n === 'number' && Number.isFinite(n) && n >= 1 ? Math.round(n) : 1;
}

// Строки «Параметры» карточки (макет §4): модель, операция, размер/длительность,
// варианты, шаги, seed, lora. Порядок фиксирован; строки без значения не выводим,
// кроме seed («не задан» — его отсутствие в макете показано явно).
export interface HistoryParamRow { key: string; value: string; mono?: boolean }

function num(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

export function historyParamsRows(record: {
  model?: string | null; op?: string | null;
  params?: Record<string, unknown> | null;
}): HistoryParamRow[] {
  const p = record.params ?? {};
  const rows: HistoryParamRow[] = [];
  if (record.model) rows.push({ key: 'Модель', value: record.model, mono: true });
  const op = historyOpLabel(record.op);
  if (op) rows.push({ key: 'Операция', value: op });
  const width = num(p.width), height = num(p.height);
  if (width != null && height != null) rows.push({ key: 'Размер', value: `${width} × ${height}`, mono: true });
  const duration = num(p.durationSec);
  if (duration != null) rows.push({ key: 'Длительность', value: formatDurationHuman(duration), mono: true });
  const count = historyVariantCount(record.params);
  if (count > 1) rows.push({ key: 'Вариантов', value: String(count), mono: true });
  const steps = num(p.steps);
  if (steps != null) rows.push({ key: 'Шаги', value: String(steps), mono: true });
  const seed = num(p.seed);
  rows.push({ key: 'Seed', value: seedFormatted(seed), mono: seed != null });
  const lora = typeof p.lora === 'string' ? p.lora : null;
  if (lora) rows.push({ key: 'LoRA', value: lora, mono: true });
  return rows;
}

// Строки «Время и карта» (макет §4): поставлена, ожидание, расчёт, карта.
// Неизвестные значения — текстом C.textMuted italic (na: true), не моно-шрифтом.
export interface HistoryTimeRow { key: string; value: string; na?: boolean; mono?: boolean }

export function historyTimeRows(record: {
  enqueuedAt?: string | null; startedAt?: string | null;
  runSeconds?: number | null;
}): HistoryTimeRow[] {
  const rows: HistoryTimeRow[] = [];
  if (record.enqueuedAt) rows.push({ key: 'Поставлена', value: formatClockSecFromIso(record.enqueuedAt), mono: true });
  const wait = historyWaitSeconds(record.enqueuedAt, record.startedAt);
  rows.push({ key: 'Ожидание', value: wait == null ? 'неизвестно' : formatDurationHuman(wait), na: wait == null, mono: wait != null });
  rows.push({ key: 'Расчёт', value: record.runSeconds == null ? 'не начинался' : formatDurationHuman(record.runSeconds), na: record.runSeconds == null, mono: record.runSeconds != null });
  // Карта: ComfyUI не сообщает, на каком устройстве считал конкретный прогон (ADR-030)
  rows.push({ key: 'Карта', value: 'не сообщает', na: true });
  return rows;
}

// === Период и хвост ленты ===

export type HistoryPeriod = 'today' | '24h' | '7d';

// Период → подпись хвоста: «Это все генерации за 7 дней»
export function historyPeriodTail(period: HistoryPeriod): string {
  switch (period) {
    case 'today': return 'Это все генерации за сегодня';
    case '24h': return 'Это все генерации за 24 часа';
    case '7d': return 'Это все генерации за 7 дней';
  }
}

// Период → ISO-момент начала (from): 7 дней / 24 часа назад, «сегодня» —
// местная полночь. nowMs — для тестов.
export function historyPeriodFrom(period: HistoryPeriod, nowMs = Date.now()): string {
  const d = new Date(nowMs);
  if (period === 'today') { d.setHours(0, 0, 0, 0); return d.toISOString(); }
  const ms = period === '24h' ? 24 * 3600 * 1000 : 7 * 24 * 3600 * 1000;
  return new Date(nowMs - ms).toISOString();
}

// === Маркер живых завершений ===

// Снимок живых данных → маркер «что завершилось»: строка lastHour (момент
// завершения + операция) + число идущих и ждущих. Изменение маркера — повод
// тихо перечитать первую страницу истории.
export function liveCompletionMarker(media: {
  lastHour?: { finishedAt: number; op?: string }[];
  running?: unknown[];
  queue?: unknown[];
} | null | undefined): string {
  if (!media) return '';
  const done = (media.lastHour ?? []).map(d => `${d.finishedAt}:${d.op ?? ''}`).join('|');
  return `${done}|${media.running?.length ?? 0}|${media.queue?.length ?? 0}`;
}

// === Бейдж исхода ===

// status → бейдж строки/карточки. Завершённые — по исходу; идущие/ждущие —
// нейтральные «идёт»/«ждёт»; unknown — без бейджа.
export interface HistoryOutcomeBadge { label: string; tone: 'ok' | 'cancel' | 'fail' | 'int' | 'neutral' }

export function historyOutcomeBadge(status?: string | null): HistoryOutcomeBadge | null {
  switch (status) {
    case 'completed': return { label: 'готово', tone: 'ok' };
    case 'cancelled': return { label: 'отменено', tone: 'cancel' };
    case 'failed': return { label: 'ошибка', tone: 'fail' };
    case 'interrupted': return { label: 'прервано', tone: 'int' };
    case 'running': return { label: 'идёт', tone: 'neutral' };
    case 'queued': return { label: 'ждёт', tone: 'neutral' };
    default: return null;
  }
}
