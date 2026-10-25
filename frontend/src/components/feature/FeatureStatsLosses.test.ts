// «Главные потери» (ST-F7). Рендер-тесты на fakeDom: пустое — пунктирная заметка;
// живая карточка 1 — тон infoBg/info и никакого перехода (у longestWait пока нет
// taskId); без витков карточки 2 нет; клик по карточке 2 — setFeaturePageTab('timeline')
// и focusFeatureStage ровно один раз; карточка 3 — «дольше всех сейчас», у закрытой
// фичи — самый долгий этап; числа — formatDuration/plural, доли — проценты.
//
// Мутации (проверены прогоном): (1) убрать тон живой карточки (в cardStyle всегда
// bgCard/borderLight) — краснеет тест «живая карточка — infoBg/info»; (2) в focusStep
// звать focusFeatureStage(card.stepRef) вместо ключа этапа — краснеет тест «клик по
// карточке 2»; (3) убрать ветку empty — краснеет тест «пустое — пунктир»; (4) убрать
// LINK_CARD_EXTRA из стиля карточки-ссылки — краснеет тест «карточка-ссылка —
// textAlign left, cursor pointer» (fakeDom не знает вычисленных стилей, UA-центрирование
// без явного сброса прошёл бы молча).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { C, R, FS, SP } from '../../lib/design';
import { FeatureStatsLosses } from './FeatureStatsLosses';
import { formatTokens } from './FeatureStatsFormat';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureStepChain } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

const mockPersonas = vi.hoisted(() => [
  { id: 'p-kira', name: 'Кира', role: 'Frontend' },
  { id: 'p-vera', name: 'Вера', role: 'QA' },
]);
// Переход карточки 2 — моким: считаем вызовы, а не состояние стора.
const mockTab = vi.hoisted(() => ({ calls: [] as string[] }));
const mockFocus = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return { ...orig, usePersonas: () => mockPersonas };
});
vi.mock('../../lib/featurePage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/featurePage')>();
  return {
    ...orig,
    setFeaturePageTab: (t: string) => { mockTab.calls.push(t); },
    focusFeatureStage: (key: string) => { mockFocus.calls.push(key); },
  };
});

// Фиксированное «сейчас»: даты в фикстуре — относительно него, минуты детерминированы.
const NOW = Date.parse('2026-10-24T12:00:00Z');

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

// Полные totals: есть ожидание (longestWait 120) — карточка 1 не пустая.
function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'approx',
    truncated: false,
    timeline: {
      from: '2026-10-23T00:00:00Z',
      to: '2026-10-24T12:00:00Z',
      stages: [],
      segments: [],
      current: null,
      totals: {
        elapsedMinutes: 400, workWallMinutes: 250, agentsSumMinutes: 300, notWorkingMinutes: 150,
        waitingMinutes: 65, idleMinutes: 70, waitingCount: 2, longestWaitMinutes: 120,
        errorCount: 1, errorMinutes: 15,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

// Нечего показать: ни ожидания, ни витков, ни идущих.
function mkEmptyStats(): FeatureStats {
  return mkStats({
    timeline: {
      from: '2026-10-24T11:00:00Z',
      to: '2026-10-24T12:00:00Z',
      stages: [],
      segments: [],
      current: null,
      totals: {
        elapsedMinutes: 10, workWallMinutes: 8, agentsSumMinutes: 8, notWorkingMinutes: 2,
        waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
        errorCount: null, errorMinutes: null,
      },
    },
  });
}

function mkChain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'ST-F7',
    title: 'Выпилить блок',
    personaId: 'p-kira',
    taskIds: ['t-step'],
    outcome: 'returned',
    status: 'Работа',
    durationMinutes: 65,
    loops: 2,
    rounds: [
      { n: 1, verdict: 'returned', reviewChatId: null },
      { n: 2, verdict: 'accepted', reviewChatId: null },
    ],
    chatId: null,
    ...over,
  };
}

function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod',
    state: 'active',
    passes: 1,
    taskIds: ['t-step'],
    rounds: [],
    firstStartedAt: null,
    lastCompletedAt: null,
    barEnd: null,
    approxMinutes: 322,
    overlapsActive: false,
    executors: [],
    ...over,
  };
}

function mountLosses(run: FeatureRun, stats: FeatureStats, isMobile?: boolean) {
  return mount(createElement(FeatureStatsLosses, { run, stats, isMobile, nowMs: NOW }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
}

beforeEach(() => {
  installFakeDom();
  mockTab.calls.length = 0;
  mockFocus.calls.length = 0;
});
afterEach(() => { uninstallFakeDom(); });

describe('нечего показать (к3)', () => {
  it('empty — пунктир с точным текстом, карточек нет', () => {
    const { container } = mountLosses(mkRun(), mkEmptyStats());
    const note = findNodeByAttr(container, 'data-testid', 'losses-empty');
    expect(note, 'пунктирной заметки нет').toBeTruthy();
    expect(textOf(note)).toContain('Потери появятся с первым ожиданием, возвратом или ошибкой');
    // Стиль заметки — пунктирная рамка (как «нет данных» в герое).
    expect(styleOf(note).border).toBe(`1px dashed ${C.dashed}`);
    expect(findNodeByAttr(container, 'data-testid', 'loss-card-1'), 'карточка 1 при пустом').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'loss-card-2'), 'карточка 2 при пустом').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'loss-card-3'), 'карточка 3 при пустом').toBeNull();
  });

  // Мутация (3): убрать ветку empty (всегда renderCards) — этот тест краснеет.
  it('метка секции «Главные потери» — FS.xxs UPPERCASE', () => {
    const { container } = mountLosses(mkRun(), mkEmptyStats());
    const block = findNodeByAttr(container, 'data-testid', 'stats-losses');
    const label = block!.children[0].style as Record<string, string>;
    expect(label.fontSize).toBe(`${FS.xxs}px`);
    expect(label.textTransform).toBe('uppercase');
    expect(label.color).toBe(C.textMuted);
  });
});

describe('карточка 1 — ожидание (к1)', () => {
  it('живая (attention.kind=wait): фон infoBg, рамка info, «ждёт сейчас», без перехода', () => {
    const run = mkRun({
      attention: { kind: 'wait', taskId: 't-1', since: new Date(NOW - 90 * 60_000).toISOString(), reason: 'ждёт ответа' },
    });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-1');
    expect(card, 'карточки 1 нет').toBeTruthy();
    const text = textOf(card);
    expect(text).toContain('Ждёт вас сейчас');
    expect(text).toContain('1 ч 30 мин'); // formatDuration(90)
    expect(text).toContain('всего ожиданий: 2');
    const st = styleOf(card);
    expect(st.background).toBe(C.infoBg);
    expect(st.border).toBe(`1px solid ${C.info}`);
    // Перехода в чат нет: бэк ещё не отдаёт longestWait.taskId (ST-B2).
    expect(findNodeByAttr(card!, 'data-testid', 'nav-target-chat'), 'переход в чат появился').toBeNull();
    expect(findNodeByAttr(card!, 'data-testid', 'nav-target-task'), 'переход в задачу появился').toBeNull();
  });

  // Мутация (1): тон живой карточки убран (cardStyle всегда bgCard) — краснеет тест выше.
  it('не живая: «Дольше всего ждали вас» на bgCard/borderLight', () => {
    const { container } = mountLosses(mkRun(), mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-1');
    expect(card, 'карточки 1 нет').toBeTruthy();
    const text = textOf(card);
    expect(text).toContain('Дольше всего ждали вас');
    expect(text).toContain('2 ч'); // formatDuration(120)
    const st = styleOf(card);
    expect(st.background).toBe(C.bgCard);
    expect(st.border).toBe(`1px solid ${C.borderLight}`);
  });

  it('count=null — «ожиданий не считалось», без выдуманных цифр', () => {
    const stats = mkStats({
      timeline: {
        from: '2026-10-23T00:00:00Z',
        to: '2026-10-24T12:00:00Z',
        stages: [],
        segments: [],
        current: null,
        totals: {
          elapsedMinutes: 400, workWallMinutes: 250, agentsSumMinutes: 300, notWorkingMinutes: 150,
          waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: 120,
          errorCount: null, errorMinutes: null,
        },
      },
    });
    const { container } = mountLosses(mkRun(), stats);
    const text = textOf(findNodeByAttr(container, 'data-testid', 'loss-card-1'));
    expect(text).toContain('ожиданий не считалось');
  });
});

describe('карточка 2 — витки (к1, к2)', () => {
  it('loops=0 — карточки 2 нет', () => {
    const run = mkRun({ stepChains: [mkChain({ loops: 0 })] });
    const { container } = mountLosses(run, mkStats());
    expect(findNodeByAttr(container, 'data-testid', 'loss-card-2'), 'карточка 2 при loops=0').toBeNull();
  });

  it('содержимое: «ST-F7 · 2 возврата», контекст шага, значки ✓/↩ по числу витков', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-2');
    expect(card, 'карточки 2 нет').toBeTruthy();
    const text = textOf(card);
    expect(text).toContain('Больше всего витков');
    expect(text).toContain('ST-F7 · 2 возврата'); // plural: 2 — «возврата»
    expect(text).toContain('Выпилить блок · шёл 1 ч 05 мин');
    const rounds = findNodeByAttr(card!, 'data-testid', 'loss-rounds');
    expect(rounds, 'ряд витков не найден').toBeTruthy();
    expect(rounds!.children.length, 'число значков витков').toBe(2);
    expect(text).toContain('✓');
    expect(text).toContain('↩');
  });

  it('склонение: 1 возврат / 5 возвратов', () => {
    for (const [n, word] of [[1, '1 возврат'], [5, '5 возвратов']] as const) {
      const run = mkRun({ stepChains: [mkChain({ loops: n })] });
      const { container } = mountLosses(run, mkStats());
      const text = textOf(findNodeByAttr(container, 'data-testid', 'loss-card-2'));
      expect(text, `«${word}» не найдено`).toContain(word);
    }
  });

  it('клик по карточке 2 — setFeaturePageTab("timeline") и фокус этапа шага, ровно один раз', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-2');
    expect(card, 'карточки 2 нет').toBeTruthy();
    act(() => { (reactProps(card!)!.onClick as () => void)(); });
    // Мок вызван ровно один раз, цель — этап, содержащий первую задачу цепочки.
    expect(mockTab.calls, 'вызовов setFeaturePageTab').toEqual(['timeline']);
    expect(mockFocus.calls, 'вызовов focusFeatureStage').toEqual(['prod']);
  });

  // Мутация (2): focusFeatureStage(card.stepRef) вместо ключа этапа — тест выше краснеет.
  it('карточка 2 — кнопка с aria-label цели перехода', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-2');
    expect(card!.attributes?.['aria-label']).toBe('Открыть цепочку шага ST-F7');
  });

  // Мутация (4): убрать LINK_CARD_EXTRA из стиля кнопки — тест краснеет: без явного
  // сброса UA-центрирование и cursor: default остались бы незамеченными.
  it('карточка-ссылка — textAlign left и cursor pointer (сброс UA-стилей кнопки)', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-2');
    expect(card, 'карточки 2 нет').toBeTruthy();
    const st = styleOf(card!);
    expect(st.textAlign, 'текст карточки-ссылки центрируется UA').toBe('left');
    expect(st.cursor, 'курсор карточки-ссылки не pointer').toBe('pointer');
  });
});

describe('карточка 3 (к1)', () => {
  it('longestNow: «Дольше всех сейчас», минуты и доля — процентами, не 0.42', () => {
    const stats = mkStats({
      longestNow: {
        taskId: 't-live', chatId: 's-1', personaId: 'p-kira', stepRef: 'ST-F6',
        minutes: 90, tokens: 1200, share: 0.42,
      },
    });
    const { container } = mountLosses(mkRun(), stats);
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-3');
    expect(card, 'карточки 3 нет').toBeTruthy();
    const text = textOf(card);
    expect(text).toContain('Дольше всех сейчас');
    expect(text).toContain('ST-F6 · 1 ч 30 мин');
    expect(text).toContain('Кира');
    expect(text).toContain('42 %');
    expect(text, 'токены карточки').toContain(formatTokens(1200));
    expect(text, 'доля записана долей, а не процентом').not.toContain('0.42');
  });

  it('share=null — «доля расхода не считалась»', () => {
    const stats = mkStats({
      longestNow: { taskId: 't-live', chatId: null, personaId: 'p-vera', stepRef: null, minutes: 20, tokens: null, share: null },
    });
    const { container } = mountLosses(mkRun(), stats);
    const text = textOf(findNodeByAttr(container, 'data-testid', 'loss-card-3'));
    expect(text).toContain('доля расхода не считалась');
    expect(text).not.toContain('%');
  });

  it('закрытая фича — «Самый долгий этап» по max approxMinutes', () => {
    const run = mkRun({
      state: 'merged',
      closed: true,
      stages: [
        mkStage({ approxMinutes: 322 }),
        mkStage({ key: 'qa', state: 'done', taskIds: [], approxMinutes: 40 }),
      ],
    });
    const { container } = mountLosses(run, mkStats());
    const card = findNodeByAttr(container, 'data-testid', 'loss-card-3');
    expect(card, 'карточки 3 нет').toBeTruthy();
    const text = textOf(card);
    expect(text).toContain('Самый долгий этап');
    expect(text).toContain('Производство · 5 ч 22 мин'); // stageLabel('prod'), formatDuration(322)
  });
});

describe('раскладка и токены (к4)', () => {
  it('десктоп — три колонки; карточка — bgCard/borderLight/R.lg с тенью', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats());
    const block = findNodeByAttr(container, 'data-testid', 'stats-losses');
    const grid = block!.children[1].style as Record<string, string>;
    expect(grid.gridTemplateColumns).toBe('repeat(3, 1fr)');
    const st = styleOf(findNodeByAttr(container, 'data-testid', 'loss-card-1'));
    expect(st.borderRadius).toBe(`${R.lg}px`);
    expect(st.boxShadow).toBe('var(--shadow-card)');
  });

  it('isMobile — одна колонка, плотные поля, число FS.xl', () => {
    const run = mkRun({ stepChains: [mkChain()], stages: [mkStage()] });
    const { container } = mountLosses(run, mkStats(), true);
    const block = findNodeByAttr(container, 'data-testid', 'stats-losses');
    const grid = block!.children[1].style as Record<string, string>;
    expect(grid.gridTemplateColumns).toBe('1fr');
    expect(grid.gap).toBe(`${SP.sm}px`);
    const card = styleOf(findNodeByAttr(container, 'data-testid', 'loss-card-1'));
    expect(card.padding).toBe(`${SP.md}px`);
    const big = styleOf(findNodeByAttr(container, 'data-testid', 'loss-big-1'));
    expect(big.fontSize).toBe(`${FS.xl}px`);
  });

  it('число живой карточки — FONT.serif FS.h2 на десктопе', () => {
    const run = mkRun({ attention: { kind: 'wait', taskId: 't-1', since: new Date(NOW - 5 * 60_000).toISOString() } });
    const { container } = mountLosses(run, mkStats());
    const big = findNodeByAttr(container, 'data-testid', 'loss-big-1')!.style as Record<string, string>;
    expect(big.fontSize).toBe(`${FS.h2}px`);
    expect(big.fontFamily).toContain('PT Serif');
  });
});
