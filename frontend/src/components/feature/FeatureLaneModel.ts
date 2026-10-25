// Модель дорожек «Хронологии» (GC-F2, план «Гант на Хронологии», макет feature-gantt-7
// §«Спецификация для фронта»). ЧИСТАЯ: на каждый этап конвейера — интервалы из журнала
// бэка (stats.timeline.intervalsByStage), их состояния, паузы между ними, зона «стоит»,
// план «N/M» и флажки возвратов (только у «Производства»). Рендер (GC-F5a/F5c) ничего
// не считает — только читает эту модель и позиционирует через axisFrac.
//
// Фронт НЕ пересчитывает конвейер и пороги: «стоит» — только из run.attention (stopped),
// «ждёт» — только из timeline.current (waiting); интервалы — только с бэка. Единственная
// арифметика модели — геометрия над данными бэка: минуты и паузы по границам интервалов
// (тот же spanMinutes, что у Ганта) и обрезка интервалов по границе окна.
//
// Обрезка по окну (находка GC-R-B): бэк НЕ клипит интервалы — живой хвост идущей задачи
// дотянут до «сейчас» (FeatureStageIntervals.cs), а у смерженной фичи окно кончается
// моментом мержа. Модель обрезает `to` границы окна: тогда и дорожка, и «Время шага на оси
// фичи» (GC-F7) считают доли по одной шкале, и полоса не уезжает за край. Интервал, целиком
// лежащий за правым краем, на дорожке не рисуется.
//
// Два разных состояния (замечание GC-R-F1): intervalsByStage === null (или поля нет) —
// журнала нет / он неполный, это approx: интервалов нет и это честно. Пустой словарь {} —
// журнал полный, ходов не было: этап честно пуст, approx НЕ ставим.

import type { FeatureRun, FeatureRound, FeatureStageExecutor, StageKey } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';
import type { StepProgress } from '../../lib/featureProgress';
import { minutesSince } from './FeatureStatsBits';

// Ключи стадии в порядке жизненного цикла (бэк: FeatureStage.Key).
const STAGE_KEYS: readonly StageKey[] = ['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];

// Ключ стадии приходит из разных мест с типом string (timeline.stages[].key,
// FeatureNow.stage). Модель приводит его к StageKey явно и экспортирует приведение:
// потребитель, который берёт стадию из timeline.stages и ищет её дорожку, не делает каст
// сам (замечание GC-R-F1). Неизвестный ключ — null: такой стадии в модели нет.
export function asStageKey(key: string | null | undefined): StageKey | null {
  return STAGE_KEYS.find(k => k === key) ?? null;
}

// Доля оси, которую занимает время живой фичи; остальное — зона «впереди» (GC-F5a).
// У закрытой/смерженной будущего нет — время занимает всю ось (1).
const LIVE_TIME_FRAC = 0.86;

export interface LaneAxis {
  // Границы окна — из таймлайна бэка (единственный честный источник границ).
  from: string;
  to: string;
  timeFrac: number;
}

// Доля времени t на оси: [0..timeFrac]. t за пределами окна ограничивается его границей —
// доля никогда не выходит за 1. Невалидная дата или вырожденная ось — 0.
export function axisFrac(t: string, axis: LaneAxis): number {
  const a = new Date(axis.from).getTime();
  const b = new Date(axis.to).getTime();
  const x = new Date(t).getTime();
  if (Number.isNaN(a) || Number.isNaN(b) || Number.isNaN(x) || b <= a) return 0;
  const clamped = Math.max(a, Math.min(b, x));
  return ((clamped - a) / (b - a)) * axis.timeFrac;
}

// Вид интервала: done — закрытый; live — живой хвост идущей задачи; wait — живой хвост,
// когда фича ждёт человека (timeline.current.kind === 'waiting'); stalled — живой хвост
// этапа, который стоит (run.attention.kind === 'stopped'). Пороги «стоит» фронт не считает.
export type LaneIntervalState = 'done' | 'live' | 'wait' | 'stalled';

export interface LaneInterval {
  from: string;
  // Конец, обрезанный границей окна (см. шапку файла).
  to: string;
  state: LaneIntervalState;
  // Исход последнего хода интервала (cancelled/interrupted/error) — риска на конце.
  lastOutcome: string | null;
  taskIds: string[];
  // Пауза до следующего интервала этого же этапа (минуты; 0 у последнего).
  pauseAfterMinutes: number;
}

// «N/M» этапа: сделано · в работе · впереди. Корзина зависит от этапа: у «Производства»
// и «Ревью» — по шагам (run.stepProgress: accepted / work+review / notCreated), у прочих
// — по задачам этапа (executors[].status: done / running|waiting|stopped / pending).
export interface LanePlan {
  done: number;
  inWork: number;
  ahead: number;
}

export interface StageLane {
  key: StageKey;
  // Интервалы этапа в хронологическом порядке, обрезанные по окну. Пусто — журнала нет
  // (approx) или этапа/его работы не было; skipped — тоже пусто.
  intervals: LaneInterval[];
  // Зона «стоит»: от attention.since до «сейчас». Только для этапа, на который указывает
  // тревога (stopped); у закрытой/смерженной — всегда null.
  stalled: { from: string; to: string } | null;
  // null — считать нечего: у prod/review нет stepProgress, у прочих нет задач этапа.
  plan: LanePlan | null;
  // Число интервалов (count) и сумма их минут после обрезки (totalMinutes).
  count: number;
  totalMinutes: number;
  // Этапа не было (state 'skipped', W4-DES-B): строка «этапа не было», без данных.
  skipped: boolean;
  // approx — журнала нет (intervalsByStage null/отсутствует): интервалов нет, потому что
  // они не считались. Пустой словарь журнала — НЕ approx (замечание GC-R-F1).
  approx: boolean;
  // Флажки возвратов — только у «Производства» (туда возвращается работа), у прочих [].
  returns: GanttReturnFlag[];
}

export interface StageLanesModel {
  // Ось из таймлайна; null — данных нет (нет stats/timeline).
  axis: LaneAxis | null;
  // Дорожки по стадиям из run.stages, кроме absent (этой стадии в конвейере нет —
  // дорожки нет). Ключ — StageKey: индексация lanes[key] типизирована.
  lanes: Partial<Record<StageKey, StageLane>>;
}

// Длительность интервала в минуты (0, если невалидно/отрицательно). Та же арифметика,
// что у Ганта и Статистики: одна на весь продукт (урок W5-R-F3b).
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

// Защита от битых чисел бэка: отрицательные/NaN обнуляем (тот же приём, что в featureProgress).
const nonNeg = (n: number): number => (Number.isFinite(n) && n > 0 ? n : 0);

// Флажок возврата: виток с вердиктом returned/accepted_with_remarks и закрытым completedAt.
// (Перенесён из FeatureGanttModel без изменения поведения — GC-F2.)
export interface GanttReturnFlag {
  // completedAt витка — точка флажка на оси времени.
  at: string;
  roundN: number;
  // PersonaId оценившего — для tooltip «{Имя} вернул {Ш3}».
  who: string | null;
  // Код возвращённого шага (у витков цепочки; у витков qa/final — null, шага в данных нет).
  stepRef: string | null;
  // Причина: Verification.Notes.
  reason: string | null;
  reviewChatId: string | null;
}

// Витки-возвраты: цепочки шагов + стадии qa/final. «Принят с замечаниями» — тоже возврат
// (работа вернулась к исполнителю); незакрытый виток (completedAt null) — не флажок.
// (Перенесён из FeatureGanttModel без изменения поведения — GC-F2.)
export function collectReturnFlags(run: FeatureRun): GanttReturnFlag[] {
  const flags: GanttReturnFlag[] = [];
  const push = (r: FeatureRound, stepRef: string | null) => {
    if ((r.verdict === 'returned' || r.verdict === 'accepted_with_remarks') && r.completedAt) {
      flags.push({
        at: r.completedAt,
        roundN: r.n,
        who: r.personaId ?? null,
        stepRef,
        reason: r.reason ?? null,
        reviewChatId: r.reviewChatId ?? null,
      });
    }
  };
  for (const chain of run.stepChains ?? []) {
    for (const r of chain.rounds ?? []) push(r, chain.stepRef ?? null);
  }
  for (const s of run.stages ?? []) {
    if (s.key !== 'qa' && s.key !== 'final') continue;
    for (const r of s.rounds ?? []) push(r, null);
  }
  flags.sort((a, b) => new Date(a.at).getTime() - new Date(b.at).getTime());
  return flags;
}

// Стадия, к которой относится задача: сначала taskIds стадии, потом запись run.now.
const resolveStageKey = (run: FeatureRun, taskId: string | null | undefined): StageKey | null => {
  if (!taskId) return null;
  for (const s of run.stages ?? []) {
    if ((s.taskIds ?? []).includes(taskId)) return s.key;
  }
  for (const n of run.now ?? []) {
    if (n.taskId === taskId) return asStageKey(n.stage);
  }
  return null;
};

// «N/M» по шагам (prod/review): accepted / work+review / notCreated. Пустой знаменатель —
// null: шагов ещё нет, показывать нечего.
function planFromStepProgress(sp: StepProgress | undefined): LanePlan | null {
  if (!sp) return null;
  const plan = { done: nonNeg(sp.accepted), inWork: nonNeg(sp.work) + nonNeg(sp.review), ahead: nonNeg(sp.notCreated) };
  return plan.done + plan.inWork + plan.ahead === 0 ? null : plan;
}

// «N/M» по задачам этапа (прочие стадии): done / running|waiting|stopped / pending.
// Задач нет — null.
function planFromExecutors(executors: FeatureStageExecutor[]): LanePlan | null {
  if (executors.length === 0) return null;
  const plan = { done: 0, inWork: 0, ahead: 0 };
  for (const x of executors) {
    if (x.status === 'done') plan.done += 1;
    else if (x.status === 'pending') plan.ahead += 1;
    else plan.inWork += 1; // running | waiting | stopped
  }
  return plan;
}

// Модель дорожек. `now` — текущее время (мс) для верха зоны «стоит»; тесты дают
// фиксированное, в работе — Date.now() на момент построения (как у buildGanttModel).
export function buildStageLanes(run: FeatureRun, stats: FeatureStats | null, now: number): StageLanesModel {
  const closed = run.closed === true;
  // «Живое» гаснет по state (как в баннере и Ганте): смерженная фича с открытым эпиком
  // не идёт; у закрытой/смерженной нет ни live, ни wait, ни «стоит», ни зоны «впереди».
  const finished = closed || run.state === 'merged' || run.state === 'closed';
  const tl = stats?.timeline;

  const axis: LaneAxis | null =
    tl?.from && tl?.to ? { from: tl.from, to: tl.to, timeFrac: finished ? 1 : LIVE_TIME_FRAC } : null;

  // approx — по null/отсутствию словаря (замечание GC-R-F1): пустой словарь — журнал
  // полный, ходов не было; это НЕ approx.
  const byStage = tl?.intervalsByStage;
  const approx = byStage == null;

  // «Стоит» — только явный stopped из attention (порог 30 мин — макетный, фронт его не
  // считает). Этап — на который указывает тревога: по attention.taskId (taskIds стадии,
  // запись run.now); не опознан — фолбэк на текущий этап (run.now[0]); нет ни того, ни
  // другого — ни одна дорожка не стоит.
  const att = finished ? null : (run.attention ?? null);
  let stoppedKey: StageKey | null = null;
  let stalledFrom: string | null = null;
  if (att?.kind === 'stopped' && att.since) {
    stoppedKey = resolveStageKey(run, att.taskId) ?? asStageKey(run.now?.[0]?.stage);
    stalledFrom = att.since;
  }

  // «Ждёт» — только из timeline.current (waiting). Живые интервалы ожидающей стадии —
  // 'wait'; задачу опознать не удалось — ждёт вся фича, 'wait' у всех живых интервалов.
  const cur = finished ? null : (tl?.current ?? null);
  const waiting = cur?.kind === 'waiting';
  const waitKey = cur && waiting ? resolveStageKey(run, cur.taskId) : null;

  const returnsByStage = collectReturnFlags(run);
  const nowIso = new Date(now).toISOString();

  const lanes: Partial<Record<StageKey, StageLane>> = {};
  for (const stage of run.stages ?? []) {
    // absent — стадии нет в конвейере этой фичи: дорожки нет (строку рисует рендер по
    // своему знанию о жизненном цикле). skipped — дорожка есть, но пустая: «этапа не было».
    if (stage.state === 'absent') continue;
    const skipped = stage.state === 'skipped';

    // Интервалы: только словаря бэка (не segments!), в хронологическом порядке, обрезанные
    // по окну (находка GC-R-B). За окном справа — не рисуется; живой хвост дотянут до края.
    const raw = skipped || !axis ? [] : (byStage?.[stage.key] ?? []);
    const sorted = [...raw]
      .filter(iv => new Date(iv.from).getTime() < new Date(axis!.to).getTime())
      .sort((a, b) => new Date(a.from).getTime() - new Date(b.from).getTime());
    const intervals: LaneInterval[] = [];
    for (const iv of sorted) {
      const to = new Date(iv.to).getTime() > new Date(axis!.to).getTime() ? axis!.to : iv.to;
      // Закрытая/смерженная фича: «живого» на закрытой оси нет — даже если бэк держит
      // стадию active с тоном run из-за открытого эпика/хвостов (намеренное поведение,
      // тесты бэка), дорожка рисует историю: все интервалы done.
      const state: LaneIntervalState = !iv.live || finished
        ? 'done'
        : stoppedKey && stage.key === stoppedKey
          ? 'stalled'
          : waiting && (waitKey === null || stage.key === waitKey)
            ? 'wait'
            : 'live';
      intervals.push({
        from: iv.from,
        to,
        state,
        lastOutcome: iv.lastOutcome ?? null,
        taskIds: iv.taskIds ?? [],
        // Пауза до следующего интервала того же этапа; у последнего — 0.
        pauseAfterMinutes: 0,
      });
    }
    for (let i = 0; i + 1 < intervals.length; i += 1) {
      intervals[i]!.pauseAfterMinutes = Math.max(0, minutesSince(intervals[i]!.to, new Date(intervals[i + 1]!.from).getTime()) ?? 0);
    }

    lanes[stage.key] = {
      key: stage.key,
      intervals,
      // Зона «стоит»: от attention.since до «сейчас». Только для стоящего этапа.
      stalled: stoppedKey && stage.key === stoppedKey && stalledFrom ? { from: stalledFrom, to: nowIso } : null,
      plan: skipped
        ? null
        : stage.key === 'prod' || stage.key === 'review'
          ? planFromStepProgress(run.stepProgress)
          : planFromExecutors(stage.executors ?? []),
      count: intervals.length,
      totalMinutes: intervals.reduce((a, iv) => a + spanMinutes(iv.from, iv.to), 0),
      skipped,
      approx,
      returns: stage.key === 'prod' ? returnsByStage : [],
    };
  }

  return { axis, lanes };
}
