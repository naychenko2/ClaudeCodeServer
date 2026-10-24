// buildPillModel (feature/pillModel.ts): текст и тон по run.state — 9 состояний
// (макет turn-plan-feature-popup-1, S[state].pill) + loading/error («фича · …») + null.
//
// Окружение node (без jsdom): getPersonaById — мок (hoisted-карта id→имя).
// Чистая функция: рендер компонента (TurnPlanPill) — в TurnPlanPill.test.ts.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureNow, FeatureRun, FeatureStage, FeatureTask } from '../../../types/featureRun';
import { buildPillModel } from './pillModel';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
}));
beforeEach(() => { personaMap.clear(); });

// ── Фикстуры ─────────────────────────────────────────────────────────────────

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Агрегат фичи: state по умолчанию 'running'; остальное «тихое».
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator' }),
    steps: [], reviews: [], qa: [], final: null, merge: null,
    state: 'running',
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  } as FeatureRun;
}

// Активная стадия (для «{этап}» в тексте); key — по нужному тесту.
function activeStage(key: FeatureStage['key']): FeatureStage {
  return {
    key, state: 'active', passes: 1, taskIds: [], rounds: [],
    firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [],
  };
}

// «Сейчас»: идущая задача (стадия + исполнитель).
function now(stage: string, personaId: string | null): FeatureNow {
  return { taskId: 'n', stage, personaId, title: 'Шаг', startedAt: null, linkedSessionId: null };
}

function setNames() {
  personaMap.set('denis', 'Денис');
  personaMap.set('kira', 'Кира');
}

// ── 9 состояний: текст (pre/bold/post) и тон ─────────────────────────────────

describe('buildPillModel: 9 состояний (макет S[state].pill)', () => {
  it('not_started → «Не начата», нейтральный', () => {
    const m = buildPillModel([run({ state: 'not_started' })], false, null)!;
    expect(m.state).toBe('not_started');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('');
    expect(m.bold).toBe('Не начата');
    expect(m.post).toBe('');
  });

  it('idle → «Простаивает», нейтральный', () => {
    const m = buildPillModel([run({ state: 'idle' })], false, null)!;
    expect(m.state).toBe('idle');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('');
    expect(m.bold).toBe('Простаивает');
    expect(m.post).toBe('');
  });

  it('running → «фича · {этап} · {кто}», нейтральный', () => {
    setNames();
    const m = buildPillModel([run({
      state: 'running',
      stages: [activeStage('prod')],
      now: [now('prod', 'kira'), now('prod', 'denis')],
    })], false, null)!;
    expect(m.state).toBe('running');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('Производство');
    expect(m.post).toBe(' · Кира, Денис');
  });

  it('running: никто не идёт (now пуст) — без суффикса имён', () => {
    const m = buildPillModel([run({ state: 'running', stages: [activeStage('prod')] })], false, null)!;
    expect(m.bold).toBe('Производство');
    expect(m.post).toBe('');
  });

  it('running: активной стадии нет — дефолт «Разрез»', () => {
    const m = buildPillModel([run({ state: 'running' })], false, null)!;
    expect(m.bold).toBe('Разрез');
    expect(m.post).toBe('');
  });

  it('waiting → «фича · {этап} · ждёт вас», тон wait, aria «фича ждёт вас»', () => {
    const m = buildPillModel([run({ state: 'waiting', stages: [activeStage('prod')] })], false, null)!;
    expect(m.state).toBe('waiting');
    expect(m.tone).toBe('wait');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('Производство');
    expect(m.post).toBe(' · ждёт вас');
    expect(m.ariaExtra).toBe('фича ждёт вас');
  });

  it('stopped → «фича · стоит · {имя}», тон stop, aria «исполнитель остановлен»', () => {
    setNames();
    const m = buildPillModel([run({
      state: 'stopped',
      attention: { kind: 'stopped', personaId: 'denis', reason: 'таймаут' },
    })], false, null)!;
    expect(m.state).toBe('stopped');
    expect(m.tone).toBe('stop');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('стоит');
    expect(m.post).toBe(' · Денис');
    expect(m.ariaExtra).toBe('исполнитель остановлен');
  });

  it('stopped: исполнитель не резолвится — «фича · стоит» (без имени)', () => {
    const m = buildPillModel([run({
      state: 'stopped',
      attention: { kind: 'stopped', personaId: 'ghost', reason: 'таймаут' },
    })], false, null)!;
    expect(m.bold).toBe('стоит');
    expect(m.post).toBe('');
  });

  it('merge_pending → «фича · Мерж · ждёт согласия», тон wait, aria «ждёт согласия на мерж»', () => {
    const m = buildPillModel([run({ state: 'merge_pending' })], false, null)!;
    expect(m.state).toBe('merge_pending');
    expect(m.tone).toBe('wait');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('Мерж');
    expect(m.post).toBe(' · ждёт согласия');
    expect(m.ariaExtra).toBe('ждёт согласия на мерж');
  });

  it('merging → «фича · Мерж · идёт · {имя}», нейтральный', () => {
    setNames();
    const m = buildPillModel([run({
      state: 'merging',
      merge: task({ id: 'm1', personaId: 'mark' }),
    })], false, null)!;
    // mark не в personaMap → имя не резолвится: « · идёт» без имени.
    expect(m.state).toBe('merging');
    expect(m.tone).toBe('neutral');
    expect(m.bold).toBe('Мерж');
    expect(m.post).toBe(' · идёт');
  });

  it('merging: имя мержа резолвится — « · идёт · {имя}»', () => {
    personaMap.set('mark', 'Марк');
    const m = buildPillModel([run({
      state: 'merging',
      merge: task({ id: 'm1', personaId: 'mark' }),
    })], false, null)!;
    expect(m.post).toBe(' · идёт · Марк');
  });

  it('merged → «фича · смержена», тон ok', () => {
    const m = buildPillModel([run({ state: 'merged' })], false, null)!;
    expect(m.state).toBe('merged');
    expect(m.tone).toBe('ok');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('смержена');
    expect(m.post).toBe('');
  });

  it('closed → «фича · закрыта», нейтральный', () => {
    const m = buildPillModel([run({ state: 'closed' })], false, null)!;
    expect(m.state).toBe('closed');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('закрыта');
    expect(m.post).toBe('');
  });
});

// ── loading / error (run ещё нет): «фича · …» ───────────────────────────────

describe('buildPillModel: загрузка / ошибка (run ещё нет)', () => {
  it('loading: «фича · …», нейтральный', () => {
    const m = buildPillModel([], true, null)!;
    expect(m.state).toBe('loading');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('фича · …');
    expect(m.bold).toBe('');
    expect(m.post).toBe('');
  });

  it('error: «фича · …», нейтральный', () => {
    const m = buildPillModel([], false, 'Сервер не ответил')!;
    expect(m.state).toBe('error');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('фича · …');
  });
});

// ── null: фич нет и загрузка/ошибка не идёт ──────────────────────────────────

describe('buildPillModel: null', () => {
  it('пусто и не загрузка → null', () => {
    expect(buildPillModel([], false, null)).toBeNull();
  });
});

// ── всегда runs[0]: выбор фичи в сторе модель не меняет ───────────────────────

describe('buildPillModel: всегда runs[0]', () => {
  it('две фичи: модель по runs[0], не по runs[1]', () => {
    const a = run({ featureId: 'a', state: 'running', stages: [activeStage('prod')], now: [now('prod', null)] });
    const b = run({ featureId: 'b', state: 'merged' });
    const m = buildPillModel([a, b], false, null)!;
    expect(m.state).toBe('running'); // runs[0] = a, не b (merged)
  });
});
