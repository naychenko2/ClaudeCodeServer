// Модель ленты «Производство» (W4-2F1, вариант 2А2): чистая функция buildProdCards
// из FeatureRun строит карточки на шаг: порядок по старту, узлы цепочки со ссылками
// на чаты витков, счётчик циклов, текущий узел, причину возврата.
// Тесты: обычный цикл, возврат+доработка, два возврата, активная задача,
// «сейчас»-строка (текущая карточка), краевые (не начат, старые данные без rounds).

import { describe, it, expect } from 'vitest';
import { buildProdCards } from './FeatureProdCards';
import type { FeatureRun, FeatureTask, FeatureStepChain, FeatureRound, FeatureStage } from '../../types/featureRun';

// ── Фикстуры ───────────────────────────────────────────────────────────────────

function task(over: Partial<FeatureTask>): FeatureTask {
  return {
    id: 't', title: 'Задача', status: 'todo', role: null, stepRef: null,
    createdAt: null, completedAt: null, claudeStartedAt: null,
    ...over,
  };
}

function round(over: Partial<FeatureRound>): FeatureRound {
  return { n: 1, verdict: 'accepted', reviewChatId: null, ...over };
}

function chain(over: Partial<FeatureStepChain>): FeatureStepChain {
  return {
    stepRef: 'Ш1', lane: null, title: 'Шаг', personaId: null,
    taskIds: [], outcome: 'running', status: '', durationMinutes: 0,
    source: null, loops: 0, rounds: [], chatId: null,
    ...over,
  };
}

function run(over: Partial<FeatureRun>): FeatureRun {
  return {
    featureId: 'F', state: 'running',
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  };
}

// Стадия «Производство» с исполнителями (для ссылок «доработки» на чаты).
function prodStage(executors: Array<{ taskId: string; chatId: string | null }>): FeatureStage {
  return {
    key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [],
    runningSince: null, rounds: [], longestStepRef: null,
    firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false,
    executors: executors.map(x => ({
      personaId: null, taskId: x.taskId, taskRole: 'rework', stepRef: null, lane: null,
      status: 'done', chatId: x.chatId, chatArchived: false, modelLabel: null, modelFull: null,
    })),
  };
}

// ── Обычный цикл: работа → принят ──────────────────────────────────────────────

describe('обычный цикл', () => {
  it('работа принята: узлы «работа(ok) → принят(ok)», циклов нет, ссылки на чаты', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z', completedAt: '2026-10-20T11:00:00Z' })],
      reviews: [task({ id: 'r1', status: 'done', role: 'review' })],
      stepChains: [chain({
        stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1'],
        status: 'принят', outcome: 'accepted', chatId: 'chat-kira',
        rounds: [round({ n: 1, verdict: 'accepted', personaId: 'p-gleb', reviewChatId: 'chat-gleb' })],
      })],
    });
    const cards = buildProdCards(r);
    expect(cards).toHaveLength(1);
    const c = cards[0];
    expect(c.order).toBe(0);
    expect(c.cycles).toBe(0);
    expect(c.isCurrent).toBe(false);
    expect(c.returnReason).toBe(null);
    expect(c.nodes).toEqual([
      { kind: 'work', label: 'работа', state: 'ok', chatId: 'chat-kira' },
      { kind: 'accept', label: 'принят', state: 'ok', chatId: 'chat-gleb' },
    ]);
  });

  it('stepChains нет или пусто — пустой список (рендер своё пустое состояние)', () => {
    expect(buildProdCards(run({}))).toEqual([]);
    expect(buildProdCards(run({ stepChains: [] }))).toEqual([]);
  });
});

// ── Возврат + доработка ────────────────────────────────────────────────────────

describe('возврат и доработка', () => {
  it('один возврат: «работа(ok) → вернул(ret) → доработка(ok) → принят(ok)», cycles=1, причина возврата', () => {
    const r = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z', completedAt: '2026-10-20T11:00:00Z' }),
        task({ id: 's2', status: 'done', role: 'rework', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T12:00:00Z', completedAt: '2026-10-20T13:00:00Z' }),
      ],
      reviews: [task({ id: 'r1', status: 'done', role: 'review' })],
      stages: [prodStage([{ taskId: 's2', chatId: 'chat-kira-2' }])],
      stepChains: [chain({
        stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1', 's2'],
        status: 'принят', outcome: 'accepted', loops: 1, chatId: 'chat-kira',
        rounds: [
          round({ n: 1, verdict: 'returned', personaId: 'p-gleb', reviewChatId: 'chat-gleb', reason: 'нет тестов', completedAt: '2026-10-20T11:30:00Z' }),
          round({ n: 2, verdict: 'accepted', personaId: 'p-gleb', reviewChatId: 'chat-gleb-2' }),
        ],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.cycles).toBe(1);
    expect(c.returnReason).toBe('нет тестов');
    expect(c.nodes).toEqual([
      { kind: 'work', label: 'работа', state: 'ok', chatId: 'chat-kira' },
      { kind: 'return', label: 'вернул', state: 'ret', chatId: 'chat-gleb' },
      { kind: 'rework', label: 'доработка', state: 'ok', chatId: 'chat-kira-2' },
      { kind: 'accept', label: 'принят', state: 'ok', chatId: 'chat-gleb-2' },
    ]);
  });

  it('доработка без исполнителя в стадии — узел есть, ссылки нет (null, не кнопка)', () => {
    const r = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's2', status: 'done', role: 'rework', stepRef: 'Ш1' }),
      ],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1', 's2'], status: 'закрыт без повторного ревью', outcome: 'returned', loops: 1,
        rounds: [round({ n: 1, verdict: 'returned', reason: 'починить' })],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.nodes[2]).toEqual({ kind: 'rework', label: 'доработка', state: 'ok', chatId: null });
  });

  it('имя ревьюера через резолвер: «Глеб: вернул»; без резолвера — «вернул», без UUID', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'вернул', outcome: 'returned', loops: 1,
        rounds: [round({ n: 1, verdict: 'returned', personaId: 'p-gleb', reason: 'нет тестов' })],
      })],
    });
    const withName = buildProdCards(r, (id) => (id === 'p-gleb' ? 'Глеб' : null))[0];
    expect(withName.nodes[1].label).toBe('Глеб: вернул');
    const plain = buildProdCards(r)[0];
    expect(plain.nodes[1].label).toBe('вернул');
    expect(plain.nodes[1].label).not.toContain('p-gleb');
  });
});

// ── Два возврата ───────────────────────────────────────────────────────────────

describe('два возврата', () => {
  it('cycles=2, два «вернул» и две доработки в порядке витков', () => {
    const r = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's2', status: 'done', role: 'rework', stepRef: 'Ш1' }),
        task({ id: 's3', status: 'done', role: 'rework', stepRef: 'Ш1' }),
      ],
      stages: [prodStage([{ taskId: 's2', chatId: 'chat-2' }, { taskId: 's3', chatId: 'chat-3' }])],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1', 's2', 's3'], status: 'принят', outcome: 'accepted', loops: 2,
        rounds: [
          round({ n: 1, verdict: 'returned', reviewChatId: 'chat-r1', reason: 'первый' }),
          round({ n: 2, verdict: 'accepted_with_remarks', reviewChatId: 'chat-r2', reason: 'второй' }),
          round({ n: 3, verdict: 'accepted', reviewChatId: 'chat-r3' }),
        ],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.cycles).toBe(2);
    // Причина — последнего возврата.
    expect(c.returnReason).toBe('второй');
    const kinds = c.nodes.map(n => n.kind);
    expect(kinds).toEqual(['work', 'return', 'rework', 'return', 'rework', 'accept']);
    expect(c.nodes.map(n => n.chatId)).toEqual([null, 'chat-r1', 'chat-2', 'chat-r2', 'chat-3', 'chat-r3']);
    // «принят с замечаниями» тоже возврат: тон ret.
    expect(c.nodes[3].state).toBe('ret');
  });

  it('последний возврат без причины: returnReason = null, а не причина более раннего', () => {
    const r = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's2', status: 'done', role: 'rework', stepRef: 'Ш1' }),
      ],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1', 's2'], outcome: 'returned', loops: 2,
        rounds: [
          round({ n: 1, verdict: 'returned', reason: 'первый' }),
          round({ n: 2, verdict: 'returned' }), // последний виток — без причины
        ],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.returnReason).toBe(null);
  });
});

// ── Активная задача и «сейчас» ──────────────────────────────────────────────────

describe('активная задача', () => {
  it('идущая задача шага: узел «работа» — cur, карточка — isCurrent', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'inProgress', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      now: [{ taskId: 's1', stage: 'prod', title: 'Сделать X', stepRef: 'Ш1' }],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Сделать X', taskIds: ['s1'], status: 'идёт', outcome: 'running' })],
    });
    const c = buildProdCards(r)[0];
    expect(c.isCurrent).toBe(true);
    expect(c.nodes[0].state).toBe('cur');
    expect(c.awaitingReview).toBe(false);
  });

  it('идущий виток ревью: метка «текущий» на узле «работа», «ревью» — пунктирный', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      now: [{ taskId: 's1', stage: 'review', title: 'Ревью', stepRef: 'Ш1' }],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'ждёт ревью', outcome: 'pending',
        rounds: [round({ n: 1, verdict: 'running' })],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.isCurrent).toBe(true);
    expect(c.nodes[0]).toEqual({ kind: 'work', label: 'работа', state: 'cur', chatId: null });
    expect(c.nodes[1]).toEqual({ kind: 'review', label: 'ревью', state: 'pend', chatId: null });
  });

  it('идущий второй виток ревью после доработки: «текущий» на доработке, а не на давней работе', () => {
    const r = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 'r1', status: 'done', stepRef: 'Ш1' }),
      ],
      now: [{ taskId: 'r1', stage: 'review', title: 'Ревью', stepRef: 'Ш1' }],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1', 'r1'], status: 'ждёт ревью', outcome: 'pending',
        rounds: [
          round({ n: 1, verdict: 'returned', reason: 'нет тестов' }),
          round({ n: 2, verdict: 'running' }),
        ],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.nodes.map(n => [n.kind, n.state])).toEqual([
      ['work', 'ok'], ['return', 'ret'], ['rework', 'cur'], ['review', 'pend'],
    ]);
  });

  it('ждёт ревью: awaitingReview=true по статусу бэка; узел работы — ok', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'inProgress', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш1', taskIds: ['s1'], status: 'ждёт ревью', outcome: 'pending' })],
    });
    const c = buildProdCards(r)[0];
    expect(c.awaitingReview).toBe(true);
  });

  it('«сейчас»-строка: только у текущей карточки; порядок — по старту, без старта — в конце', () => {
    const r = run({
      steps: [
        task({ id: 's2', status: 'inProgress', stepRef: 'Ш2', claudeStartedAt: '2026-10-21T09:00:00Z' }),
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's3', status: 'todo', stepRef: 'Ш3' }),
      ],
      now: [{ taskId: 's2', stage: 'prod', title: 'Сделать Y', stepRef: 'Ш2' }],
      stepChains: [
        chain({ stepRef: 'Ш3', title: 'Не начат', taskIds: ['s3'], status: '', outcome: 'pending' }),
        chain({ stepRef: 'Ш1', title: 'Ранний', taskIds: ['s1'], status: 'принят', outcome: 'accepted' }),
        chain({ stepRef: 'Ш2', title: 'Идущий', taskIds: ['s2'], status: 'идёт', outcome: 'running' }),
      ],
    });
    const cards = buildProdCards(r);
    expect(cards.map(c => c.stepRef)).toEqual(['Ш1', 'Ш2', 'Ш3']);
    expect(cards.map(c => c.order)).toEqual([0, 1, 2]);
    expect(cards.map(c => c.isCurrent)).toEqual([false, true, false]);
  });
});

// ── Краевые ────────────────────────────────────────────────────────────────────

describe('краевые состояния', () => {
  it('шаг не начат: одна пунктирная «работа», без ссылок и циклов', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'todo', stepRef: 'Ш1' })],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Не начат', taskIds: ['s1'], status: '', outcome: 'pending' })],
    });
    const c = buildProdCards(r)[0];
    expect(c.startedAt).toBe(null);
    expect(c.cycles).toBe(0);
    expect(c.nodes).toEqual([{ kind: 'work', label: 'работа', state: 'pend', chatId: null }]);
  });

  it('старые данные без rounds: только «работа» по статусу, возвратов нет', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш1', taskIds: ['s1'], status: 'принят', outcome: 'accepted', rounds: undefined as unknown as FeatureRound[] })],
    });
    const c = buildProdCards(r)[0];
    expect(c.nodes).toEqual([{ kind: 'work', label: 'работа', state: 'ok', chatId: null }]);
    expect(c.cycles).toBe(0);
  });

  it('снятый виток (withdrawn) не строит узла', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'снят с выкладки', outcome: 'withdrawn',
        rounds: [round({ n: 1, verdict: 'withdrawn', reviewChatId: 'chat-r1' })],
      })],
    });
    const c = buildProdCards(r)[0];
    expect(c.nodes.map(n => n.kind)).toEqual(['work']);
  });
});
