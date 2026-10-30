// Строка В2 «Трек» (FeatureBanner): чистая модель строки featureRowModel + общий
// rendering-проверки тела сводки (FeatureTruthBody: «Сейчас», «· ещё N» при нескольких
// фичах, «этот чат», подвал). Окружение node: renderToStaticMarkup, чистые вызовы.
//
// Состояния из постановки: идёт / ждёт вас / стоит; несколько фич у чата; фича без
// трекера (полосы нет — это featureTruthModel.test).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { FeatureBanner, featureRowModel, HOVER_CSS } from './FeatureBanner';
import { FeatureTruthBody } from './FeatureTruthBody';
import { FLAGS, setFlagLocal } from '../../../lib/featureFlags';
import { __test as indexStore } from '../../../lib/featuresIndex';
import type { FeatureIndexFeature } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>([['p-kira', 'Кира'], ['p-gleb', 'Глеб']]) }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Правда о прогрессе', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [], chips: ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  } as FeatureIndexFeature;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: { id: 'f1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

describe('featureRowModel: состояния строки', () => {
  it('идёт: «Сейчас {Имя} — {Ш8 · задача} +N» из run.now (первый — первый по бэку)', () => {
    const r = run({
      now: [
        { taskId: 't-8', stage: 'prod', personaId: 'p-kira', stepRef: 'Ш8', title: 'Вёрстка шапки', startedAt: null, linkedSessionId: null },
        { taskId: 't-9', stage: 'prod', personaId: 'p-gleb', stepRef: 'Ш9', title: 'E2E', startedAt: null, linkedSessionId: null },
      ],
    });
    const m = featureRowModel(feat(), r, false);
    expect(m.alarm).toBeNull();
    expect(m.nowName).toBe('Кира');
    expect(m.nowWhat).toBe('Ш8 · Вёрстка шапки');
    expect(m.nowRest).toBe(1);
  });

  it('фолбэк без run: имена индекса (nowPersonaIds), «что» не выдумывается', () => {
    const m = featureRowModel(feat({ nowPersonaIds: ['p-kira'] }), null, false);
    expect(m.nowName).toBe('Кира');
    expect(m.nowWhat).toBeNull();
    expect(m.nowRest).toBe(0);
  });

  it('ждёт вас: фраза warn с причиной (десктоп) / с именем (мобила), действие «Ответить»', () => {
    const f = feat({ state: 'waiting', attention: { kind: 'wait', since: null, reason: 'Полина спрашивает про отступы', personaId: 'p-kira', sessionId: 's-1', taskId: 't-1' } });
    const d = featureRowModel(f, null, false);
    expect(d.alarm).toBe('wait');
    expect(d.alarmText).toBe('Ждёт вас · Полина спрашивает про отступы');
    expect(d.actionLabel).toBe('Ответить');
    const m = featureRowModel(f, null, true);
    expect(m.alarmText).toBe('Ждёт вас · Кира');
    expect(m.actionLabel).toBe('Ответить');
  });

  it('стоит: «Стоит · {Имя}» danger, действие «Чат {Имя}»', () => {
    const f = feat({ state: 'stopped', attention: { kind: 'stopped', since: null, reason: null, personaId: 'p-gleb', sessionId: 's-2', taskId: 't-2' } });
    const m = featureRowModel(f, null, false);
    expect(m.alarm).toBe('stop');
    expect(m.alarmText).toBe('Стоит · Глеб');
    expect(m.actionLabel).toBe('Открыть чат');
  });

  it('merge_pending: «Ждёт согласия на мерж», действие «К мержу»', () => {
    const m = featureRowModel(feat({ state: 'merge_pending' }), null, false);
    expect(m.alarm).toBe('wait');
    expect(m.alarmText).toBe('Ждёт согласия на мерж');
    expect(m.actionLabel).toBe('К мержу');
  });

  it('смержена/закрыта: слово состояния из общего statusPhrase (без даты, как в пилюле)', () => {
    expect(featureRowModel(feat({ state: 'merged' }), null, false).statusWord).toBe('смержена');
    expect(featureRowModel(feat({ state: 'closed' }), null, false).statusWord).toBe('закрыта');
  });

  it('пауза с очередью: «В очереди · 2 из 3» (общий queueLabelParts), не «Сейчас»', () => {
    const m = featureRowModel(feat({ state: 'paused', holdReason: 'queue', queue: { position: 2, length: 3 } }), null, false);
    expect(m.statusWord).toBe('В очереди');
    expect(m.statusWordPost).toBe(' · 2 из 3');
  });
});

describe('FeatureTruthBody: сводка (раскрытие В2 = попап П1)', () => {
  function renderBody(r: FeatureRun, over: Record<string, unknown> = {}): string {
    return renderToStaticMarkup(createElement(FeatureTruthBody, {
      run: r, sessionId: 's-me', isMobile: false, roleText: 'Ш8 · фронт', onOpenPage: () => {}, ...over,
    }));
  }

  it('«Сейчас · N»: строки с именами и меткой «этот чат» на своей задаче', () => {
    const r = run({
      now: [
        { taskId: 't-8', stage: 'prod', personaId: 'p-kira', stepRef: 'Ш8', title: 'Вёрстка шапки', startedAt: null, linkedSessionId: 's-me' },
        { taskId: 't-9', stage: 'prod', personaId: 'p-gleb', stepRef: 'Ш9', title: 'E2E', startedAt: null, linkedSessionId: 's-other' },
      ],
    });
    const html = renderBody(r);
    expect(html).toContain('Сейчас · 2');
    expect(html).toContain('Кира');
    expect(html).toContain('· этот чат');
    expect(html).toContain('этот чат —');
    expect(html).toContain('Ш8 · фронт');
  });

  it('несколько фич у чата: «· ещё N» с aria (цикл по фичам — колбэк вызывающего)', () => {
    const html = renderBody(run(), { more: 2, onCycle: () => {} });
    expect(html).toContain('· ещё 2');
    expect(html).toContain('ещё 2 фичи');
  });

  it('пустые секции не рисуются: нет now/оценки/трекера/расхождений — ни «Сейчас», ни пары, ни метра, ни «расходится»', () => {
    const html = renderBody(run());
    expect(html).not.toContain('Сейчас ·');
    expect(html).not.toContain('feature-truth-pair');
    expect(html).not.toContain('feature-truth-meter');
    expect(html).not.toContain('feature-truth-disc');
    // подвал — всегда (роль этого чата + переход)
    expect(html).toContain('feature-truth-page');
  });

  it('расхождения — свёрнутая строка «Трекер расходится · N» (список — по тапу, aria-expanded)', () => {
    const r = run({ tracker: [{ kind: 'unreviewed', taskId: 't-5', stepRef: 'Ш5', since: null }] });
    const html = renderBody(r);
    expect(html).toContain('Трекер расходится · 1');
    expect(html).toContain('aria-expanded="false"');
  });
});

// РЕНДЕР самой строки В2 (постановка F2, п. 8): идёт / ждёт вас / стоит — на живом
// компоненте с индексом стора (run не запрошен: строка честно деградирует до данных
// индекса). Паспел и раскрытие здесь не проверяются — это FeatureTruthBody выше.
describe('FeatureBanner: рендер строки В2', () => {
  function seedIndex(f: FeatureIndexFeature, flagsOn = true): void {
    setFlagLocal(FLAGS.turnPlanFeatureMode, flagsOn);
    setFlagLocal(FLAGS.featuresOverview, flagsOn);
    indexStore.setIndex('p1', {
      features: [f],
      chats: { 's-me': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш8', lane: 'front' }] },
    });
  }
  function renderRow(): string {
    return renderToStaticMarkup(createElement(FeatureBanner, {
      sessionId: 's-me', projectId: 'p1', isMobile: false, chatTaskId: 't-8', onFocusComposer: () => {},
    }));
  }
  const progress = { stepsAccepted: 2, stepsTotal: 9, stepsReview: 4, stepsWork: 2, stepsNotCreated: 1, planState: 'cut' as const };

  it('идёт: название + «Сейчас {Имя}», процент и полоса; без run — только то, что несёт индекс', () => {
    seedIndex(feat({ ...progress, state: 'running', nowPersonaIds: ['p-kira'] }));
    const html = renderRow();
    expect(html).toContain('data-testid="feature-banner"');
    expect(html).toContain('Правда о прогрессе');
    expect(html).toContain('Сейчас');
    expect(html).toContain('Кира');
    expect(html).toContain('22 %');
    expect(html).toContain('data-testid="feature-row-bar"');
  });

  it('ждёт вас: фраза warn и действие «Ответить» (без имени)', () => {
    seedIndex(feat({
      ...progress,
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'Полина спрашивает про отступы', personaId: 'p-kira', sessionId: 's-me', taskId: 't-8' },
    }));
    const html = renderRow();
    expect(html).toContain('data-banner-alarm="wait"');
    expect(html).toContain('Ждёт вас · Полина спрашивает про отступы');
    expect(html).toContain('Ответить');
  });

  it('стоит: фраза danger и действие «Открыть чат» (без имени)', () => {
    seedIndex(feat({
      ...progress,
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: 'auth_failed', personaId: 'p-gleb', sessionId: 's-other', taskId: 't-8' },
    }));
    const html = renderRow();
    expect(html).toContain('data-banner-alarm="stop"');
    expect(html).toContain('Стоит · Глеб');
    expect(html).toContain('Открыть чат');
  });

  it('флаги выключены — строки нет вовсе (источник не зовётся, слот пустой)', () => {
    seedIndex(feat({ ...progress, state: 'running', nowPersonaIds: ['p-kira'] }), false);
    expect(renderRow()).toBe('');
  });

  // Дизайн-ревью F2, круг 2: hover — в классе с @media (hover:hover), без инлайнового
  // фона; высота верхней строки одинакова в обоих состояниях; первым режется название.
  const styleOf = (html: string, testId: string): string | undefined =>
    new RegExp(`data-testid="${testId}"[^>]*style="([^"]*)"`).exec(html)?.[1];

  it('шеврон: фон только в классе с @media (hover:hover); в inline-стиле его нет (п. 2)', () => {
    seedIndex(feat({ ...progress, state: 'running', nowPersonaIds: ['p-kira'] }));
    const html = renderRow();
    const btn = /<button[^>]*data-testid="feature-row-expand"[^>]*>/.exec(html)?.[0] ?? '';
    expect(btn).toContain('cc-feature-row-hover');
    expect(btn).not.toContain('background');
    expect(HOVER_CSS).toContain('.cc-feature-row-hover{background:transparent}');
    expect(HOVER_CSS).toContain('@media (hover:hover)');
    expect(HOVER_CSS).toContain('.cc-feature-row-hover:hover{background:var(--c-bg-selected);}');
  });

  it('верхняя строка: minHeight 24 в обоих состояниях — «идёт» и тревога с кнопкой (п. 3)', () => {
    seedIndex(feat({ ...progress, state: 'running', nowPersonaIds: ['p-kira'] }));
    expect(renderRow()).toContain('min-height:24px');
    seedIndex(feat({
      ...progress,
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: 'auth_failed', personaId: 'p-gleb', sessionId: 's-other', taskId: 't-8' },
    }));
    expect(renderRow()).toContain('min-height:24px');
  });
});
