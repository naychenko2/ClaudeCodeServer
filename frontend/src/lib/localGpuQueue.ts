// Чистый форматтер строки ожидания локальной GPU-генерации.
// Единая функция на все три редактора (картинка, звук, видео), чтобы
// формулировки не расходились. Источник правды по виду —
// docs/mockups/local-model-stats-media-cards.html, раздел «Спецификация для Киры».

import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

export type QueueKind = 'queued' | 'running' | 'busy' | 'unknown' | 'down';
export type ProgressMode = 'exact' | 'estimate' | 'indeterminate';

export interface QueueProgress {
  mode: ProgressMode;
  /** 0–100; undefined = нет значения (indeterminate) */
  fraction?: number;
}

export interface QueueLine {
  kind: QueueKind;
  text: string;
  progress?: QueueProgress;
}

// === Вспомогательные формatters ===

// Секунды → человекочитаемое: «40 с», «4 мин», «12 мин», «1 ч 5 мин».
function fmtDuration(sec: number): string {
  if (sec < 0) sec = 0;
  const s = Math.round(sec);
  if (s < 60) return `${s} с`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return rm > 0 ? `${h} ч ${rm} мин` : `${h} ч`;
}

// unix-мс → «14:32» (локальные часы:минуты).
function fmtClock(ms: number): string {
  const d = new Date(ms);
  const h = String(d.getHours()).padStart(2, '0');
  const m = String(d.getMinutes()).padStart(2, '0');
  return `${h}:${m}`;
}

// Русские порядковые: 1-я, 2-я, 3-я, 4-я…
function ordinal(n: number): string {
  return `${n}-я`;
}

// busy.kind → человекочитаемое имя задачи.
const BUSY_KIND_LABELS: Record<string, string> = {
  image: 'картинка',
  video: 'видео',
  audio: 'звук',
  train: 'обучение',
  transcribe: 'транскрибация',
  instance: 'другой инстанс AI Home',
  external: 'внешняя задача',
};

function busyKindLabel(kind: string): string {
  return BUSY_KIND_LABELS[kind] ?? 'задача';
}

// === Определение состояния ===

function determineKind(info: LocalGpuQueueInfo): QueueKind {
  if (info.downSince != null) return 'down';
  if (info.busy != null) return 'busy';
  // M2: running=true — задача идёт (не «в очереди»); ahead решает только когда running не true
  if (info.running === true) return 'running';
  // ahead: null = «в очереди, позиция неизвестна»; undefined = «данных нет»
  if (info.ahead !== undefined) return 'queued';
  if (info.steps != null || info.percent != null || info.remainingSeconds != null) return 'running';
  return 'unknown';
}

// === Форматтеры по состояниям ===

// Состояние 1: в очереди.
// ahead=0 → «Следующая · старт ≈ через 40 с»
// ahead>0 → «В очереди · 2-я · старт ≈ через 4 мин»
// startInSeconds=null → «В очереди · 2-я» (без обещания времени)
// ahead=null → «В очереди» (без номеров)
function formatQueued(info: LocalGpuQueueInfo, compact: boolean): { text: string; progress: QueueProgress } {
  const ahead = info.ahead;
  const eta = info.startInSeconds;

  let text: string;
  if (ahead === undefined || ahead === null) {
    // Позиция неизвестна — без номеров. Но при известном startInSeconds
    // всё равно показываем ETA (решение по спорному вопросу из ревью).
    text = eta != null
      ? (compact ? `В очереди · ≈ ${fmtDuration(eta)}` : `В очереди · старт ≈ через ${fmtDuration(eta)}`)
      : 'В очереди';
  } else {
    // Защита: бэк шлёт целые, но дробное/отрицательное clamp'им в целое ≥ 0
    const posAhead = Math.max(0, Math.floor(ahead));
    if (posAhead === 0) {
      if (compact) {
        text = eta != null ? `Следующая · ≈ ${fmtDuration(eta)}` : 'Следующая';
      } else {
        text = eta != null ? `Следующая · старт ≈ через ${fmtDuration(eta)}` : 'Следующая';
      }
    } else {
      const pos = ordinal(posAhead + 1);
      if (compact) {
        text = eta != null ? `В очереди · ${pos} · ≈ ${fmtDuration(eta)}` : `В очереди · ${pos}`;
      } else {
        text = eta != null ? `В очереди · ${pos} · старт ≈ через ${fmtDuration(eta)}` : `В очереди · ${pos}`;
      }
    }
  }

  // Полоса: muted estimate, всегда 0 — «прошло / (прошло + ETA)», без elapsed считаем 0
  const fraction = 0;
  return { text, progress: { mode: 'estimate', fraction } };
}

// Состояние 2/4: считается (running).
// Шаги: «Шаг 12 из 25 · осталось ≈ 30 с» / «12 из 25 · ≈ 30 с»
// stage: «этап 2 · шаг 3 из 8»
// Без ETA: «Шаг 12 из 25 · время не замерено» / «12 из 25 · без оценки»
// Без шагов: «Идёт · время не замерено»
function formatRunning(info: LocalGpuQueueInfo, compact: boolean): { text: string; progress: QueueProgress } {
  const steps = info.steps;
  const remaining = info.remainingSeconds;
  const percent = info.percent;
  const percentExact = info.percentExact;

  // Текст по шагам
  let stepText = '';
  if (steps != null && steps.total > 0) {
    if (steps.stage != null) {
      stepText = compact
        ? `${steps.stage} · ${steps.step}/${steps.total}`
        : `этап ${steps.stage} · шаг ${steps.step} из ${steps.total}`;
    } else {
      stepText = compact
        ? `${steps.step} из ${steps.total}`
        : `Шаг ${steps.step} из ${steps.total}`;
    }
  }

  // Время
  let timeText = '';
  if (remaining != null && remaining > 0) {
    timeText = compact ? `≈ ${fmtDuration(remaining)}` : `осталось ≈ ${fmtDuration(remaining)}`;
  } else if (remaining != null && remaining === 0) {
    // remainingSeconds=0 — почти готово, не пишем «0 с»
    timeText = '';
  } else {
    // Нет ETA
    if (steps != null && steps.total > 0) {
      timeText = compact ? 'без оценки' : 'время не замерено';
    } else {
      // Нет ни шагов, ни ETA: «Идёт · время не замерено».
      // percent — это процент текущей ноды, НЕ минуты: elapsed из него не выводим.
      // (В эту ветку stepText уже пуст — шаги null/total=0, строка 131.)
      timeText = compact ? 'Идёт · без оценки' : 'Идёт · время не замерено';
    }
  }

  // Собираем текст
  let text: string;
  if (stepText && timeText) {
    text = `${stepText} · ${timeText}`;
  } else if (stepText) {
    text = stepText;
  } else if (timeText) {
    text = timeText;
  } else {
    text = 'Идёт';
  }

  // Полоса:
  // Точные шаги → exact, fraction = step/total, предел 99
  // Нет шагов, но percent с percentExact=true → exact, fraction = percent, предел 99
  // Нет шагов и percentExact=false/null, но есть percent или ETA → estimate, предел 95
  // Нет ничего → indeterminate
  let progress: QueueProgress;
  if (steps != null && steps.total > 0) {
    const frac = Math.max(0, Math.min(99, Math.round((steps.step / steps.total) * 100)));
    progress = { mode: 'exact', fraction: frac };
  } else if (percent != null && percentExact === true) {
    // Точный процент ноды без шагов: exact, fraction = percent, предел 99
    const frac = Math.max(0, Math.min(99, Math.round(percent)));
    progress = { mode: 'exact', fraction: frac };
  } else if (remaining != null && remaining > 0) {
    // Оценка: нет точных шагов, но есть ETA → пунктир, предел 95
    // fraction: используем percent если есть, иначе 50 (середина)
    const frac = percent != null ? Math.min(95, Math.round(percent)) : 50;
    progress = { mode: 'estimate', fraction: frac };
  } else if (percent != null) {
    // Оценка: нет шагов и ETA, но есть percent (percentExact=false/null) → estimate, предел 95
    const frac = Math.max(0, Math.min(95, Math.round(percent)));
    progress = { mode: 'estimate', fraction: frac };
  } else {
    progress = { mode: 'indeterminate' };
  }

  return { text, progress };
}

// Состояние 3: впереди тяжёлая задача.
// Единая форма без двоеточия для вида задачи (обе формы):
// «Карта занята видео · ≈ 12 мин».
// Без ETA: «Карта занята долгой задачей: видео» (двоеточие — как в макете).
function formatBusy(info: LocalGpuQueueInfo, _compact: boolean): { text: string; progress: QueueProgress } {
  const busy = info.busy!;
  const label = busyKindLabel(busy.kind);
  const remaining = busy.remainingSeconds;

  let text: string;
  if (remaining != null && remaining > 0) {
    const timeStr = `≈ ${fmtDuration(remaining)}`;
    // Единая форма без двоеточия для вида задачи (спецификация, макет п.3):
    // «Карта занята видео · ≈ 12 мин» — в обеих формах.
    text = `Карта занята ${label} · ${timeStr}`;
  } else {
    // Без ETA: двоеточие после «долгой задачей» — как в макете.
    // «Карта занята долгой задачей: видео» — в обеих формах.
    text = `Карта занята долгой задачей: ${label}`;
  }

  // Полоса: как в очереди (muted estimate)
  const fraction = 0;
  return { text, progress: { mode: 'estimate', fraction } };
}

// Состояние 5: стенд недоступен.
// «Локальная генерация недоступна с 14:32» / «Локально недоступно с 14:32»
function formatDown(info: LocalGpuQueueInfo, compact: boolean): { text: string; progress?: QueueProgress } {
  const ds = info.downSince;
  // downSince не конечное (NaN) → не рисуем «NaN:NaN», а вид без времени
  if (ds == null || !Number.isFinite(ds)) {
    return { text: compact ? 'Локально недоступно' : 'Локальная генерация недоступна' };
  }
  const time = fmtClock(ds);
  const text = compact ? `Локально недоступно с ${time}` : `Локальная генерация недоступна с ${time}`;
  return { text };
}

// Состояние unknown: нет данных.
function formatUnknown(): { text: string; progress?: QueueProgress } {
  return { text: 'Идёт' };
}

// === Публичная функция ===

export function describeLocalGpuQueue(info: LocalGpuQueueInfo, opts: { compact: boolean; now: number }): QueueLine {
  const kind = determineKind(info);

  switch (kind) {
    case 'down':
      return { kind, ...formatDown(info, opts.compact) };
    case 'busy':
      return { kind, ...formatBusy(info, opts.compact) };
    case 'queued':
      return { kind, ...formatQueued(info, opts.compact) };
    case 'running':
      return { kind, ...formatRunning(info, opts.compact) };
    case 'unknown':
      return { kind, ...formatUnknown() };
  }
}
