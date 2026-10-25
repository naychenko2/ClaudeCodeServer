// Тесты карточки «Сводка» (волна 6, шаг F5, макет feature-summary-1):
// · 8 kind: строка «Сейчас» в DOM равна выходу briefNowText (F2), подстрока «Сейчас» и
//   «Сделано» — выходам briefSubText/briefDoneText (R-F5: слова перенесены в модель),
//   тег — выходу featureRunStatusTag (единая точка правды тега); ожидаемых строк в тесте
//   нет — они берутся из тех же моделей, что и в блоке;
// · служебные состояния: скелетон с aria-busy, «Сводка не загрузилась» + полоска,
//   brief = null и статов нет → блока нет;
// · «Мерж…» / «Открыть финальное ревью» — только waiting_you и только isMobile;
//   каждая кнопка — по своему пропу независимо (R-F5 п.6);
// · легенда — из MAIN-сегментов общей модели ленты, «⫽» — только при настоящем разрыве;
// · Grep-сторож: в исходнике блока нет прямого обращения к полю состояния, полосе стадий
//   и полям таймлайна — фронт ничего не пересчитывает.
//
// Окружение node (без jsdom): fakeDom (mount), как в FeatureBriefLegend.test.ts.
// «Сейчас» — фиксированное системное время; даты fixtures — локальные компоненты Date
// (форматтеры читают локальные геттеры — TZ-независимо, CI Linux = dev Windows).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FeatureBriefBlock } from './FeatureBriefBlock';
import { briefDoneText, briefEstimateText, briefNowText, briefSubText } from './FeatureBriefModel';
import { featureRunStatusTag } from './FeatureStatusTag';
import { stageLabel } from './FeatureStatsBits';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureBriefBlockProps } from './FeatureBriefBlock';
import type { FeatureBrief, FeatureBriefKind } from '../../lib/featurePage';
import type { FeatureStats } from '../../types/featureStats';

const HERE = dirname(fileURLToPath(import.meta.url));

// «Сейчас» по часам макета: 25.10.2026 14:10 (локальное).
const NOW = new Date(2026, 9, 25, 14, 10).getTime();
const at = (day: number, hh: number, mm = 0, ss = 0): string => new Date(2026, 9, day, hh, mm, ss).toISOString();

const NAMES: Record<string, string> = { 'p-gleb': 'Глеб', 'p-vera': 'Вера', 'p-kira': 'Кира', 'p-sofya': 'Софья' };
const nameOf = (id: string): string | null => NAMES[id] ?? null;

function brief(over: Partial<FeatureBrief> = {}): FeatureBrief {
  return {
    kind: 'running',
    now: null,
    reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [],
    elapsedMinutes: null,
    estimate: null,
    queue: null,
    note: null,
    computedAt: at(25, 14, 8),
    ...over,
  };
}

// Статистика с валидным таймлайном (тот же shape, что в FeatureMiniSummary.test.ts).
function stats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-25T10:00:00Z',
      to: '2026-10-25T14:10:00Z',
      stages: [{ key: 'prod', startedAt: '2026-10-25T10:00:00Z', barEnd: null }],
      segments: [
        { from: '2026-10-25T10:00:00Z', to: '2026-10-25T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
      ],
      current: { kind: 'waiting', since: '2026-10-25T14:00:00Z', taskId: 't-prod' },
      totals: {
        elapsedMinutes: 250, workWallMinutes: 30, agentsSumMinutes: 30, notWorkingMinutes: 220,
        waitingMinutes: 60, idleMinutes: 100, waitingCount: 1, longestWaitMinutes: 60,
        errorCount: 0, errorMinutes: 0,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: {
      inputTokens: 100000, outputTokens: 20000, cacheReadTokens: 0, cacheCreationTokens: 0,
      costUsd: 1.5, costPartial: false, hiddenCostModels: [],
      from: '2026-10-25T10:00:00Z',
      shared: null,
    },
    ...over,
  };
}

function render(props: Partial<FeatureBriefBlockProps> = {}) {
  const calls: string[] = [];
  const noop = (k: string) => () => { calls.push(k); };
  const { container } = mount(createElement(FeatureBriefBlock, {
    brief: null,
    stats: null,
    onOpenStats: noop('stats'),
    onRetry: noop('retry'),
    onMerge: noop('merge'),
    onOpenFinal: noop('final'),
    ...props,
  }));
  return { c: container, calls };
}

function click(c: ReturnType<typeof render>['c'], testId: string) {
  const node = findNodeByAttr(c, 'data-testid', testId);
  expect(node, `кнопка ${testId}`).not.toBeNull();
  act(() => { (reactProps(node!)!.onClick as () => void)(); });
}

beforeEach(() => {
  installFakeDom();
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => {
  uninstallFakeDom();
  vi.useRealTimers();
});

// ── 1. Восемь kind: строка и тег — выход моделей, не строки теста ────────────────────

interface Case {
  name: string;
  kind: FeatureBriefKind;
  over?: Partial<FeatureBrief>;
  mergedAt?: string;
  // Ожидаемый тег — выход featureRunStatusTag (единой точки правды) для канонического
  // состояния kind; «в очереди» — новый вариант макета, состояния для него нет.
  tag: string;
}

const CASES: Case[] = [
  {
    name: 'running', kind: 'running',
    over: { now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) } },
    tag: featureRunStatusTag('running', stageLabel('review')).label,
  },
  { name: 'waiting_you (мерж)', kind: 'waiting_you', over: { reason: 'мерж' }, tag: featureRunStatusTag('waiting').label },
  { name: 'error', kind: 'error', over: { reason: 'сохранение журнала упало' }, tag: featureRunStatusTag('stopped').label },
  { name: 'paused', kind: 'paused', over: { reason: 'manual', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) } }, tag: featureRunStatusTag('paused').label },
  { name: 'queued', kind: 'queued', tag: 'в очереди' },
  { name: 'draft', kind: 'draft', tag: featureRunStatusTag('draft').label },
  { name: 'idle', kind: 'idle', tag: featureRunStatusTag('idle').label },
  { name: 'closed (смержена)', kind: 'closed', mergedAt: at(24, 18, 5), tag: featureRunStatusTag('merged').label },
  { name: 'closed (без мержа)', kind: 'closed', tag: featureRunStatusTag('closed').label },
];

describe('FeatureBriefBlock: восемь kind', () => {
  for (const cs of CASES) {
    it(`${cs.name}: строка «Сейчас» = выход briefNowText, тег = выход резолвера тега`, () => {
      const b = brief({ kind: cs.kind, ...cs.over });
      const mergedAt = cs.mergedAt ?? null;
      const { c } = render({ brief: b, mergedAt, nameOf });
      const line = findNodeByAttr(c, 'data-testid', 'brief-now-line');
      expect(line, 'строка «Сейчас»').not.toBeNull();
      expect(textNodes(line!).join(''), 'строка расходится с выходом F2').toBe(briefNowText(b, { nameOf, mergedAt }));
      const tag = findNodeByAttr(c, 'data-testid', 'brief-tag');
      expect(tag, 'тег состояния').not.toBeNull();
      expect(textNodes(tag!).join(''), 'тег расходится с резолвером').toBe(cs.tag);
    });
  }

  it('строка «Сейчас» — aria-live=polite, карточка — section с именем (макет §Поведение)', () => {
    const { c } = render({ brief: brief({ kind: 'idle' }) });
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block')!;
    expect(sec.attributes['aria-label'], 'имя карточки').toBe('Сводка состояния фичи');
    const line = findNodeByAttr(c, 'data-testid', 'brief-now-line')!;
    expect(line.attributes['aria-live'], 'переход этапа зачитается').toBe('polite');
  });

  it('подстрока «Сейчас» в DOM равна выходу briefSubText (слова модели, R-F5)', () => {
    const b = brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 58) } });
    const { c } = render({ brief: b, nameOf });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-now-sub')!).join('')).toBe(briefSubText(b, NOW));
  });

  it('running без now — строки «Сейчас» и подстроки нет (поля нет — строку не рисуем)', () => {
    const { c } = render({ brief: brief({ kind: 'running' }) });
    expect(findNodeByAttr(c, 'data-testid', 'brief-now-line'), 'строки нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-now-sub'), 'подстроки нет').toBeNull();
  });
});

// ── 2. «Сделано» и «Оценка» ──────────────────────────────────────────────────────────

describe('«Сделано» и «Оценка»', () => {
  it('«Сделано» в DOM равно выходу briefDoneText (слова модели, R-F5)', () => {
    const b = brief({ done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' } });
    const { c } = render({ brief: b });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-done-line')!).join('')).toBe(briefDoneText(b.done).map(p => p.text).join(''));
  });

  it('loops = 0 — хвоста про витки нет', () => {
    const b = brief({ done: { stepsAccepted: 5, stepsTotal: 5, loops: 0, planState: 'cut' } });
    const { c } = render({ brief: b });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-done-line')!).join('')).toBe(briefDoneText(b.done).map(p => p.text).join(''));
  });

  it('queued — «Оценка» карточки: строки модели («Оценка — после старта», «в очереди 25 мин»)', () => {
    const b = brief({ kind: 'queued', elapsedMinutes: 25 });
    const { c } = render({ brief: b });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-estimate-main')!).join('')).toBe(briefEstimateText(b).main);
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-estimate-fact')!).join('')).toBe(briefEstimateText(b).fact);
  });

  it('«Оценка»: «≈ … осталось» + «идёт N» из elapsedMinutes (макет run)', () => {
    const b = brief({
      elapsedMinutes: 70,
      estimate: { remainingMinutes: 150, p25: 120, p75: 180, source: 'history', sampleSize: 4 },
    });
    const { c } = render({ brief: b });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-estimate-main')!).join('')).toBe('≈ 2 ч 30 мин осталось');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-estimate-fact')!).join('')).toBe('идёт 1 ч 10 мин');
  });

  it('estimate = null — «Оценки пока нет», «≈ 0» нигде в карточке (критерий F2)', () => {
    const { c } = render({ brief: brief({ elapsedMinutes: 70 }) });
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block')!;
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-estimate-main')!).join('')).toBe('Оценки пока нет');
    expect(textNodes(sec).join(' ')).not.toMatch(/≈\s*0/);
  });

  it('closed — подпись блока меняется на «Итог»: «шла 6 ч 40 мин» (макет closed)', () => {
    const { c } = render({ brief: brief({ kind: 'closed', elapsedMinutes: 400 }), mergedAt: at(24, 18, 5) });
    const facts = textNodes(findNodeByAttr(c, 'data-testid', 'brief-facts')!).join(' ');
    expect(facts).toContain('Итог');
    expect(facts).toContain('шла 6 ч 40 мин');
  });
});

// ── 3. Голос оркестратора ────────────────────────────────────────────────────────────

describe('Голос оркестратора', () => {
  const fresh = { text: 'Идём по плану', at: at(25, 13, 56), byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'review', stale: false };

  it('свежий — плашка с текстом и подписью «Имя · ведёт фичу · N мин назад»', () => {
    const { c } = render({ brief: brief({ note: fresh }), nameOf });
    const voice = findNodeByAttr(c, 'data-testid', 'brief-voice');
    expect(voice, 'плашка голоса').not.toBeNull();
    const all = textNodes(voice!).join(' ');
    expect(all).toContain('Идём по плану');
    expect(all).toContain('Софья · ведёт фичу · 14 мин назад');
    expect(findNodeByAttr(c, 'data-testid', 'brief-voice-stale'), 'метки устаревшего нет').toBeNull();
  });

  it('устаревший в паузе — метка «до паузы» рядом с подписью, текст не спрятан (макет hold)', () => {
    const { c } = render({
      brief: brief({ kind: 'paused', reason: 'manual', note: { ...fresh, at: at(25, 13, 25), stale: true } }),
      nameOf,
    });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice-stale')!).join('')).toBe('до паузы');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice')!).join(' ')).toContain('Идём по плану');
  });

  it('голос человека (percoны нет) — часть подписи с именем опускается, плашка остаётся', () => {
    const { c } = render({ brief: brief({ note: { ...fresh, byPersonaId: null, byUser: true } }), nameOf });
    const by = textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice-by')!).join('');
    expect(by).toBe('ведёт фичу · 14 мин назад');
  });

  it('note = null — плашки голоса нет вовсе (макет §3: «оркестратор молчит» не пишем)', () => {
    const { c } = render({ brief: brief() });
    expect(findNodeByAttr(c, 'data-testid', 'brief-voice')).toBeNull();
  });
});

// ── 4. Служебные состояния ───────────────────────────────────────────────────────────

describe('Служебные состояния', () => {
  it('loading — скелетон с aria-busy=«true», без текста (ни нулей, ни прочерков)', () => {
    const { c } = render({ loading: true });
    const sk = findNodeByAttr(c, 'data-testid', 'feature-brief-skeleton');
    expect(sk, 'скелетон').not.toBeNull();
    expect(sk!.attributes['aria-busy'], 'aria-busy').toBe('true');
    expect(textNodes(sk!).join('').trim(), 'нулей и прочерков нет').toBe('');
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-block'), 'карточки под скелетоном нет').toBeNull();
  });

  it('brief = null при живых статах — «Сводка не загрузилась» + «Повторить», полоска остаётся', () => {
    const { c, calls } = render({ brief: null, stats: stats() });
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block');
    expect(sec, 'карточка есть').not.toBeNull();
    const all = textNodes(sec!).join(' ');
    expect(all).toContain('Сводка не загрузилась');
    expect(findNodeByAttr(c, 'data-testid', 'mini-timeline'), 'полоска остаётся').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-life'), 'хвост «Жизнь фичи» остаётся').not.toBeNull();
    click(c, 'brief-retry');
    expect(calls, '«Повторить» зовёт перезапрос').toContain('retry');
  });

  it('brief = null и статов нет — блока нет (как у нынешней мини-сводки)', () => {
    const { c } = render({ brief: null, stats: null });
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-block')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-skeleton')).toBeNull();
  });

  it('«Статистика ›» в хвосте — зовёт onOpenStats', () => {
    const { c, calls } = render({ brief: null, stats: stats() });
    click(c, 'brief-go-stats');
    expect(calls).toContain('stats');
  });
});

// ── 5. Кнопки «ждёт вас»: только waiting_you и только мобильная раскладка ────────────

describe('Кнопки «Мерж…» и «Открыть финальное ревью»', () => {
  const waiting = brief({ kind: 'waiting_you', reason: 'мерж', next: [{ stage: 'merge', personaId: null }] });

  it('waiting_you на десктопе — кнопок в карточке нет (на десктопе «Мерж…» — в шапке страницы)', () => {
    const { c } = render({ brief: waiting, nameOf, isMobile: false });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final')).toBeNull();
  });

  it('waiting_you на мобиле — обе кнопки есть и зовут свои швы', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), '«Мерж…»').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), '«Открыть финальное ревью»').not.toBeNull();
    click(c, 'brief-merge');
    click(c, 'brief-open-final');
    expect(calls).toEqual(expect.arrayContaining(['merge', 'final']));
  });

  it('«Мерж…» рисуется при одном onMerge: без живого чата финала onOpenFinal не передан (R-F5 п.6)', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true, onOpenFinal: undefined });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), '«Мерж…» при одном onMerge').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), 'без onOpenFinal второй кнопки нет').toBeNull();
    click(c, 'brief-merge');
    expect(calls, '«Мерж…» зовёт свой шов').toContain('merge');
  });

  it('«Открыть финальное ревью» рисуется при одном onOpenFinal: onMerge не передан', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true, onMerge: undefined });
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), '«Открыть финальное ревью» при одном onOpenFinal').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), 'без onMerge кнопки мержа нет').toBeNull();
    click(c, 'brief-open-final');
    expect(calls, 'кнопка зовёт свой шов').toContain('final');
  });

  it('running на мобиле — кнопок мержа нет (не состояние «ждёт вас»)', () => {
    const { c } = render({
      brief: brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 5) } }),
      nameOf, isMobile: true,
    });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge')).toBeNull();
  });
});

// ── 6. Легенда: MAIN-сегменты общей модели, «⫽» — только при разрыве ─────────────────

describe('Легенда полоски', () => {
  it('виды — из MAIN-сегментов: ревью-дорожка в легенду не попадает', () => {
    const s = stats({
      timeline: {
        ...stats().timeline,
        segments: [
          { from: '2026-10-25T10:00:00Z', to: '2026-10-25T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
          { from: '2026-10-25T10:30:00Z', to: '2026-10-25T10:40:00Z', kind: 'idle', track: 'review', stage: 'review', taskId: 't-rev' },
        ],
      },
    });
    const { c } = render({ brief: null, stats: s });
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-work'), 'main-вид').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-idle'), 'вида с ревью-дорожки нет').toBeNull();
  });

  it('пауза длиннее порога — «⫽» есть; короче порога — нет', () => {
    const base = stats();
    const withBreak = stats({
      timeline: {
        ...base.timeline,
        segments: [
          { from: '2026-10-25T10:00:00Z', to: '2026-10-25T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
          { from: '2026-10-25T13:00:00Z', to: '2026-10-25T13:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
        ],
      },
    });
    const cBreak = render({ brief: null, stats: withBreak }).c;
    expect(findNodeByAttr(cBreak, 'data-testid', 'brief-legend-break'), 'разрыв > 2 ч').not.toBeNull();

    const noBreak = stats({
      timeline: {
        ...base.timeline,
        // Конец таймлайна близко к последнему сегменту: иначе хвост 11:30→14:10 сам
        // дал бы законный разрыв > 2 ч после последнего блока.
        to: '2026-10-25T11:40:00Z',
        current: { kind: 'waiting', since: '2026-10-25T11:35:00Z', taskId: 't-prod' },
        segments: [
          { from: '2026-10-25T10:00:00Z', to: '2026-10-25T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
          { from: '2026-10-25T11:00:00Z', to: '2026-10-25T11:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
        ],
      },
    });
    const cNo = render({ brief: null, stats: noBreak }).c;
    expect(findNodeByAttr(cNo, 'data-testid', 'brief-legend-break'), 'разрыва ≤ 2 ч нет').toBeNull();
  });
});

// ── 7. Grep-сторож: блок ничего не пересчитывает ─────────────────────────────────────

describe('Grep-сторож (инвариант 3: фронт не пересчитывает)', () => {
  const src = readFileSync(join(HERE, 'FeatureBriefBlock.tsx'), 'utf8');

  it('в исходнике блока нет прямого обращения к полю состояния, полосе стадий и полям таймлайна', () => {
    expect(/run\.state/.test(src), 'блок трогает поле состояния напрямую').toBe(false);
    expect(/\.stages/.test(src), 'блок трогает полосу стадий').toBe(false);
    expect(/stats\.timeline/.test(src), 'блок трогает таймлайн напрямую').toBe(false);
  });

  it('перенесённых слов в исходнике блока нет: формулировка живёт в модели (R-F5)', () => {
    for (const w of ['шагов приняты', 'без вас фича стоит', 'исполнители не запускаются', 'фича закрыта', 'Оценки пока нет', 'Оценка — после старта']) {
      expect(src.includes(w), `блок содержит «${w}»`).toBe(false);
    }
  });
});
