// buildPillModel (feature/pillModel.ts): текст и тон по run.state — 9 состояний
// (макет turn-plan-feature-popup-1, S[state].pill) + loading/error («фича · …») + null.
//
// Окружение node (без jsdom): getPersonaById — мок (hoisted-карта id→имя).
// Чистая функция: рендер компонента (TurnPlanPill) — в TurnPlanPill.test.ts.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureNow, FeatureQueue, FeatureRun, FeatureStage, FeatureTask } from '../../../types/featureRun';
import { buildPillModel } from './pillModel';
import { featureRunStatusTag } from '../../feature/FeatureStatusTag';
import { queueLabelParts } from '../../../lib/chatGroups';

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

// ── Очередь фич (волна 6, feature-queue-1): «В очереди · N из M» ──────────────
// Пауза с причиной queue — тот же тон, тревоги нет (макет feature-queue-1, таблица
// «Подпись»). Позицию считает бэк (run.queue), фронт только показывает.

describe('buildPillModel: очередь фич (holdReason=queue)', () => {
  it('queue {2,3} → «фича · В очереди · 2 из 3», нейтральный', () => {
    const m = buildPillModel([run({
      state: 'paused',
      hold: { reason: 'queue', since: '2026-10-25T10:00:00Z' },
      queue: { position: 2, length: 3 },
    })], false, null)!;
    expect(m.state).toBe('paused');
    expect(m.tone).toBe('neutral');
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('В очереди');
    expect(m.post).toBe(' · 2 из 3');
    expect(m.ariaExtra).toBeNull();
  });

  it('queue не пришёл (бэк ещё не отдал) → «В очереди» без цифр', () => {
    const m = buildPillModel([run({ state: 'paused', hold: { reason: 'queue' } })], false, null)!;
    expect(m.bold).toBe('В очереди');
    expect(m.post).toBe('');
  });

  it('holdReason=manual → прежние значения: не очередь (даже с queue в данных)', () => {
    const m = buildPillModel([run({
      state: 'paused',
      hold: { reason: 'manual', since: '2026-10-25T10:00:00Z' },
      queue: { position: 2, length: 3 },
    })], false, null)!;
    // Поведение прежней паузы (запасной путь модели) — задача Q5D его не меняет;
    // главное: подпись очереди не протекла в manual.
    expect(m.bold).not.toContain('В очереди');
    expect(m.post).not.toContain('из');
    expect(m.bold).toBe('Не начата');
    expect(m.pre).toBe('');
  });
});

// ── R-Q5D-A: единый источник подписи очереди (пилюля = тег) ───────────────────
// Текст формата «N из M» — из общего хелпера queueLabel (chatGroups): пилюля и тег
// статуса для одной очереди дают побайтно один текст. Пилюля набирает leading-слово
// с большой (макет .tpp .x b) — сравниваем текст, регистр leading-слова — вопрос
// рендера, не источника.

describe('R-Q5D-A: подпись пилюли = подпись тега (один источник — queueLabel)', () => {
  // Текст очереди из модели пилюли: leading-слово в нижний регистр + суффикс как есть.
  const pillLabel = (m: NonNullable<ReturnType<typeof buildPillModel>>): string =>
    m.bold.charAt(0).toLowerCase() + m.bold.slice(1) + m.post;

  it('«в очереди · 5 из 7» — пилюля и тег побайтно равны', () => {
    const m = buildPillModel([run({
      state: 'paused',
      hold: { reason: 'queue', since: '2026-10-25T10:00:00Z' },
      queue: { position: 5, length: 7 },
    })], false, null)!;
    const tag = featureRunStatusTag('paused', null, { holdReason: 'queue', queue: { position: 5, length: 7 } });
    expect(pillLabel(m)).toBe(tag.label);
    expect(pillLabel(m)).toBe('в очереди · 5 из 7');
  });

  it('queue не пришёл: «в очереди» без цифр — так же, как тег', () => {
    const m = buildPillModel([run({ state: 'paused', hold: { reason: 'queue' } })], false, null)!;
    const tag = featureRunStatusTag('paused', null, { holdReason: 'queue' });
    expect(pillLabel(m)).toBe(tag.label);
    expect(pillLabel(m)).toBe('в очереди');
  });
});

// ── R-Q5D-F: пилюля на структурный API хелпера (RV-K-Q5D-B, минор 3) ────────────
// statusPhrase (общий источник слов пилюли и плашки) для очереди берёт ГОТОВЫЕ части
// queueLabelParts: жирное слово и суффикс. В пилюле нет своей константы «В очереди»,
// своей копии формата «N из M» и разбора строки по разделителю — третий источник
// формулировки не мог вернуться. Тот же образец, что плашка (R-Q5D-E).
// Мутация (в отчёте): вернуть разбор строки и зашитое слово в statusPhrase →
// сторож исходника краснеет.

describe('R-Q5D-F: части пилюли = структура queueLabelParts', () => {
  it('bold и post пилюли = queueLabelParts(queue).bold/.post побайтно', () => {
    const q: FeatureQueue = { position: 5, length: 7 };
    const parts = queueLabelParts(q);
    const m = buildPillModel([run({
      state: 'paused',
      hold: { reason: 'queue', since: '2026-10-25T10:00:00Z' },
      queue: q,
    })], false, null)!;
    expect(m.bold).toBe(parts.bold);
    expect(m.post).toBe(parts.post);
    expect(m.bold + m.post).toBe(`${parts.bold}${parts.post}`);
  });

  it('queue не пришёл: части без цифр — ровно как у хелпера', () => {
    const parts = queueLabelParts(null);
    const m = buildPillModel([run({ state: 'paused', hold: { reason: 'queue' } })], false, null)!;
    expect(m.bold).toBe(parts.bold);
    expect(m.post).toBe(parts.post);
    expect(m.post).toBe('');
  });

  it('в исходнике пилюли нет разбора строки подписи и нет доступа к строке целиком', () => {
    const src = readFileSync(fileURLToPath(new URL('./pillModel.ts', import.meta.url)), 'utf8');
    // Запрещаем ПРАВИЛО, а не конкретное написание: иначе возврат старой реализации
    // (слово из кусков + разбор через split) проходит мимо сторожа.
    expect(src, 'пилюля разбирает строку подписи по разделителю').not.toMatch(/\.(split|slice|substring|substr|indexOf|lastIndexOf)\(/);
    expect(src, 'пилюля берёт строку подписи вместо частей').not.toMatch(/\.label\b/);
    expect(src, 'пилюля берёт строку подписи вместо частей (хелпер)').not.toMatch(/\bqueueLabel\(/);
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
