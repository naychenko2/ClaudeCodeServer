// Режим «Исполнение фичи»: агрегат фичи + источник данных.
//
// `projectFeatureRun` — чистая функция: из сырых задач трекера (эпик + дочерние)
// считает `FeatureRun`. На бэке ту же роль выполняет `FeatureProjector.Build`; пока
// REST `GET /api/tasks/features?sessionId=` не готов, фронт берёт сырые задачи из
// фикстуры и проецирует их сам. Источник данных изолирован в `loadFeatureTasks`,
// поэтому подмена на REST не тронет ни проектор, ни компоненты.

import { useSyncExternalStore } from 'react';
import type { FeatureRun, FeatureRound, FeatureStage, FeatureStageStatus, FeatureState, FeatureStep, FeatureTask, Verdict } from '../types/featureRun';

// Порядок и подписи стадий конвейера (единый источник для проектора и вида).
const STAGE_DEFS: { key: FeatureStage['key']; name: string }[] = [
  { key: 'cut', name: 'Разрез' },
  { key: 'plan', name: 'План' },
  { key: 'prod', name: 'Производство' },
  { key: 'review', name: 'Ревью' },
  { key: 'check', name: 'Проверка' },
  { key: 'final', name: 'Финальное ревью' },
  { key: 'merge', name: 'Мерж' },
];
const STAGE_NAME: Record<FeatureStage['key'], string> = {
  cut: 'Разрез', plan: 'План', prod: 'Производство', review: 'Ревью',
  check: 'Проверка', final: 'Финальное ревью', merge: 'Мерж',
};

// === Время: строки МСК 'YYYY-MM-DDTHH:MM' → минуты от эпохи (UTC) ===
function tm(s: string | null | undefined): number | null {
  if (!s) return null;
  const [d, h] = s.split('T');
  if (!d || !h) return null;
  const [y, mo, da] = d.split('-').map(Number);
  const [hh, mm] = h.split(':').map(Number);
  if ([y, mo, da, hh, mm].some(n => Number.isNaN(n))) return null;
  return Date.UTC(y, mo - 1, da, hh, mm) / 60000;
}

function maxTime(tasks: FeatureTask[]): number | null {
  let m: number | null = null;
  for (const t of tasks) {
    for (const s of [t.created, t.completed]) {
      const v = tm(s);
      if (v !== null && (m === null || v > m)) m = v;
    }
  }
  return m;
}

const isProdRole = (r?: string | null) => r === 'step' || r === 'rework';

// Состояние задачи на момент `now`: 'hidden' (ещё не создана), 'run' (идёт), 'done'.
function taskState(t: FeatureTask, now: number): 'hidden' | 'run' | 'done' {
  const c = tm(t.created), d = tm(t.completed);
  if (c === null) return d !== null && d <= now ? 'done' : 'hidden';
  if (c > now) return 'hidden';
  if (d === null || d > now) return 'run';
  return 'done';
}

// Вердикт витка: явный из verification, иначе 'running' (открыт), иначе null.
function roundVerdict(t: FeatureTask, now: number): Verdict | 'running' | null {
  const v = t.verification?.verdict;
  if (v) return v;
  return taskState(t, now) === 'run' ? 'running' : null;
}

// Русские множественные формы: plural(1,'шаг','шага','шагов') → 'шаг'.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}

// Агрегат фичи. `epic` — задача-эпик (role=feature), `tasks` — все задачи фичи
// (включая эпик). `now` — момент времени; по умолчанию — последний в данных.
export function projectFeatureRun(epic: FeatureTask, tasks: FeatureTask[], now?: number): FeatureRun | null {
  const nowT = now ?? maxTime([epic, ...tasks]);
  if (nowT === null) return null;

  const kids = tasks.filter(t => t.featureId === epic.id);
  const byId = new Map(tasks.map(t => [t.id, t]));
  const stepOf = (id: string): string | null => {
    const t = byId.get(id);
    return t && t.stepRef ? t.stepRef : null;
  };

  // Шаги: группируем производственные задачи (step/rework) по stepRef, порядок —
  // по первому появлению. Ревью прицепляем к шагу по reviewOf (ревью пачкой — к первому).
  const stepOrder: string[] = [];
  const stepInfo = new Map<string, { lane: string | null; name: string; prod: FeatureTask[]; reviews: FeatureTask[] }>();
  for (const t of kids) {
    if (!isProdRole(t.role)) continue;
    const key = t.stepRef ?? t.id;
    if (!stepInfo.has(key)) {
      stepOrder.push(key);
      stepInfo.set(key, { lane: t.lane ?? null, name: t.title, prod: [], reviews: [] });
    }
    const s = stepInfo.get(key)!;
    s.prod.push(t);
    if (t.lane) s.lane = t.lane;
  }
  for (const t of kids) {
    if (t.role !== 'review') continue;
    const target = (t.reviewOf ?? []).map(stepOf).find(Boolean);
    const s = target ? stepInfo.get(target) : undefined;
    if (s) s.reviews.push(t);
  }

  const steps: FeatureStep[] = [];
  for (const key of stepOrder) {
    const s = stepInfo.get(key)!;
    const visible = [...s.prod, ...s.reviews].filter(t => taskState(t, nowT) !== 'hidden');
    if (!visible.length) continue;
    const rounds: FeatureRound[] = s.reviews
      .filter(t => taskState(t, nowT) !== 'hidden')
      .sort((a, b) => (tm(a.created) ?? 0) - (tm(b.created) ?? 0))
      .map((t, i) => ({
        n: i + 1,
        verdict: roundVerdict(t, nowT) ?? 'running',
        who: t.persona ?? null,
        at: t.completed ?? t.created ?? null,
        reason: t.verification?.notes ?? null,
        ghost: !t.created,
      }));
    const run = visible.some(t => taskState(t, nowT) === 'run');
    const pendingReview = !run && s.reviews.some(t => taskState(t, nowT) === 'hidden');
    const status: FeatureStep['status'] = run ? 'run' : pendingReview ? 'await' : 'done';
    const ret = rounds.filter(r => r.verdict === 'returned' || r.verdict === 'accepted_with_remarks').length;
    steps.push({ id: key, lane: s.lane, name: s.name, status, rounds, ret, closed: null });
  }

  // Линии: уникальные значения lane у дочерних задач (подписи — как есть).
  const laneSet = new Map<string, string>();
  for (const t of kids) if (t.lane) laneSet.set(t.lane, t.lane);
  const lanes = [...laneSet.entries()].map(([key, label]) => ({ key, label }));

  // Прогресс: принято шагов / всего.
  const done = steps.filter(s => s.status === 'done').length;
  const total = steps.length;

  // Счётчики вердиктов по всем виткам.
  const allRounds = steps.flatMap(s => s.rounds);
  const verdicts = {
    accepted: allRounds.filter(r => r.verdict === 'accepted').length,
    acceptedWithRemarks: allRounds.filter(r => r.verdict === 'accepted_with_remarks').length,
    returned: allRounds.filter(r => r.verdict === 'returned').length,
    withdrawn: allRounds.filter(r => r.verdict === 'withdrawn').length,
  };
  // ↺: каждое ревью считаем один раз, даже если оно легло на несколько шагов.
  const seen = new Set<string>();
  let ret = 0;
  for (const t of kids) {
    if (t.role !== 'review') continue;
    const v = t.verification?.verdict;
    if ((v === 'returned' || v === 'accepted_with_remarks') && !seen.has(t.id)) { seen.add(t.id); ret++; }
  }

  // Финал: вердикт финального ревью определяет `ready`.
  const finalTasks = kids.filter(t => t.role === 'final');
  const finalVerdict = finalTasks.map(t => t.verification?.verdict).find(Boolean) as Verdict | undefined;
  const ready = finalVerdict === 'accepted' || finalVerdict === 'accepted_with_remarks';

  // Состояние: смержена → done; ждёт человека (эпик открыт, всё принято) → wait;
  // спайк закрыт → closed; иначе → run.
  const merged = epic.completed ?? null;
  let state: FeatureState;
  if (merged) state = 'done';
  else if (epic.status === 'done' && total > 0 && done === total) state = 'wait';
  else if (epic.kind === 'spike' && epic.status === 'done') state = 'closed';
  else state = 'run';

  // «Сейчас»: идущие задачи (кроме карточек-дефектов на человеке).
  const running = kids
    .filter(t => taskState(t, nowT) === 'run' && t.kind !== 'card')
    .map(t => ({
      who: t.persona ?? '—',
      title: t.title,
      stepId: t.stepRef ?? undefined,
    }));

  // Стадии конвейера: статус и витки по ролям. prod — из шагов, merge — из состояния.
  const roundsOf = (role: string): FeatureRound[] =>
    kids.filter(t => t.role === role && taskState(t, nowT) !== 'hidden')
      .sort((a, b) => (tm(a.created) ?? 0) - (tm(b.created) ?? 0))
      .map((t, i) => ({
        n: i + 1,
        verdict: roundVerdict(t, nowT) ?? 'running',
        who: t.persona ?? null,
        at: t.completed ?? t.created ?? null,
        reason: t.verification?.notes ?? null,
        ghost: !t.created,
      }));
  const stageOf = (role: string): FeatureStage => {
    const list = kids.filter(t => t.role === role);
    if (!list.length) return { key: role as FeatureStage['key'], name: STAGE_NAME[role as FeatureStage['key']], status: 'none', meta: null };
    const anyRun = list.some(t => taskState(t, nowT) === 'run');
    const who = [...new Set(list.map(t => t.persona).filter(Boolean))].join(', ');
    return {
      key: role as FeatureStage['key'],
      name: STAGE_NAME[role as FeatureStage['key']],
      status: anyRun ? 'run' : 'done',
      meta: `${who} · ${list.length} ${plural(list.length, 'задача', 'задачи', 'задач')}`,
      rounds: roundsOf(role),
    };
  };
  const prodRun = steps.some(s => s.status === 'run');
  const prodStatus: FeatureStageStatus = prodRun ? 'run' : (steps.length ? (steps.every(s => s.status === 'done') ? 'done' : 'run') : 'todo');
  let merge: { status: FeatureStageStatus; meta: string | null };
  if (merged) merge = { status: 'done', meta: 'влита в master' };
  else if (state === 'wait') merge = { status: 'wait', meta: 'ждёт решения о мерже' };
  else merge = { status: 'todo', meta: null };
  const stages: FeatureStage[] = STAGE_DEFS.map(def => {
    if (def.key === 'prod') return { key: def.key, name: def.name, status: prodStatus, meta: `${steps.length} ${plural(steps.length, 'шаг', 'шага', 'шагов')}` };
    if (def.key === 'merge') return { key: def.key, name: def.name, status: merge.status, meta: merge.meta };
    const role = def.key === 'check' ? 'qa' : def.key;
    return stageOf(role);
  });

  return {
    id: epic.id,
    name: epic.title,
    branch: epic.worktreeBranch ?? null,
    kind: epic.kind ?? null,
    lanes,
    state,
    progress: { done, total },
    verdicts,
    ret,
    ready,
    awaitingHumanSince: epic.awaitingHumanSince ?? null,
    awaitingHumanReason: epic.awaitingHumanReason ?? null,
    merged,
    running,
    steps,
    stages,
  };
}

// Все фичи из набора задач: каждая задача-эпик (role=feature) + её дочерние.
export function projectFeatureRuns(tasks: FeatureTask[], now?: number): FeatureRun[] {
  const epics = tasks.filter(t => t.role === 'feature');
  const runs: FeatureRun[] = [];
  for (const epic of epics) {
    const run = projectFeatureRun(epic, tasks, now);
    if (run) runs.push(run);
  }
  return runs;
}

// === Источник данных ===
// Пока REST не готов — фикстура. `loadFeatureTasks` возвращает сырые задачи;
// подмена на `api.getFeatures(sessionId)` не тронет проектор и компоненты.
import featureRunsFixture from '../__fixtures__/feature-runs.json';

export type FeatureSource = 'fixture' | 'rest';

// Состояние источника: задачи по чату + откуда они взяты. useSyncExternalStore,
// чтобы компонент перерисовывался, когда источник поменяется (REST-ответ).
let _tasks: FeatureTask[] = featureRunsFixture as FeatureTask[];
let _source: FeatureSource = 'fixture';
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

export function setFeatureTasks(tasks: FeatureTask[], source: FeatureSource) {
  _tasks = tasks;
  _source = source;
  emit();
}

export function getFeatureTasks(): FeatureTask[] { return _tasks; }
export function getFeatureSource(): FeatureSource { return _source; }

// Хук: агрегаты фич чата. Пока REST не пришёл — фикстура (UI не падает).
export function useFeatureRuns(): FeatureRun[] {
  const tasks = useSyncExternalStore(
    (fn) => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _tasks,
    () => featureRunsFixture as FeatureTask[],
  );
  return projectFeatureRuns(tasks);
}
