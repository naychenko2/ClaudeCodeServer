// П2 «Мой шаг» (FeatureStepView) — рендер тела для чата шага (постановка F2, п. 8):
// тревога с действием, карточка шага (плашка статуса), «Цепочка шага», полоса «Фича»,
// подвал «этот чат — роль · Страница фичи». Окружение node: renderToStaticMarkup.
//
// Что защищается в первую очередь (замечания ревью): карточка и цепочка не спорят с
// тревогой («стоит» вместо «идёт»), код шага не дублируется, полоса и подвал на месте.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { FeatureStepView } from './FeatureStepView';
import type { FeatureRun, FeatureStepChain } from '../../../types/featureRun';

const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>([['p-kira', 'Кира'], ['p-gleb', 'Глеб']]) }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

function chain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'Ш8', title: 'Вёрстка шапки', lane: 'front', personaId: 'p-kira',
    taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24,
    rounds: [], loops: 0, chatId: null, ...over,
  } as FeatureStepChain;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1', state: 'running',
    epic: { id: 'epic-1', title: 'Правда о прогрессе', status: 'todo' },
    steps: [{ id: 't-8', title: 'Ш8: Вёрстка шапки', role: 'step', stepRef: 'Ш8', lane: 'front', status: 'inProgress' }],
    reviews: [], qa: [], final: null, merge: null,
    stepProgress: { planState: 'cut', total: 9, accepted: 2, review: 4, work: 2, notCreated: 1, withdrawn: 0, unlinkedReworks: 0, inMaster: 4 },
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

function render(r: FeatureRun, c: FeatureStepChain, over: Record<string, unknown> = {}): string {
  return renderToStaticMarkup(createElement(FeatureStepView, {
    run: r, chain: c, sessionId: 's-me', chatTaskId: 't-8', isMobile: false,
    onOpenPage: () => {}, onClose: () => {}, ...over,
  }));
}

describe('FeatureStepView: тело П2 «Мой шаг»', () => {
  it('без тревоги: карточка «идёт · 24 мин», цепочка «Ш8 — идёт», дубля кода шага нет', () => {
    const c = chain();
    const html = render(run({ stepChains: [c] }), c);
    expect(html).not.toContain('feature-step-alarm');
    // Название задачи начинается с «Ш8:» (так оно в трекере) — в карточке остаётся один номер.
    expect(html).toContain('Ш8 · Вёрстка шапки');
    expect(html).not.toContain('Ш8 · Ш8');
    expect(html).toContain('идёт · 24 мин');
    expect(html).toContain('Ш8 — идёт');
  });

  it('тревога на этом шаге: блок «Стоит · Кира» с «Открыть чат», карточка и цепочка — «стоит», а не «идёт»', () => {
    const c = chain();
    const r = run({
      state: 'stopped',
      attention: { kind: 'stopped', taskId: 't-8', since: '2026-10-29T10:08:00Z', reason: 'auth_failed', personaId: 'p-kira', linkedSessionId: 's-me' },
      stepChains: [c],
    });
    const html = render(r, c);
    expect(html).toContain('feature-step-alarm');
    expect(html).toContain('Стоит · Кира');
    expect(html).toContain('Открыть чат');
    // Плашка статуса и первая строка цепочки говорят то же, что тревога выше.
    expect(html).toContain('стоит');
    expect(html).toContain('Ш8 — стоит');
    expect(html).not.toContain('идёт');
  });

  it('код не дублируется, когда перед ним имя фичи: «LLM-wiki · S4b-3: …» → «S4b-3 · LLM-wiki · …» (п. 6)', () => {
    const c = chain({ stepRef: 'S4b-3', title: 'LLM-wiki · S4b-3: разбор транскрипта' });
    const html = render(run({ stepChains: [c] }), c);
    expect(html).toContain('S4b-3 · LLM-wiki · разбор транскрипта');
    expect(html).not.toContain('S4b-3:');
  });

  it('тревога wait на этом шаге: карточка «ждёт вас · время», цепочка «— ждёт вас», а не «принят» (п. 7)', () => {
    const c = chain({ outcome: 'accepted', status: 'принят', acceptedAt: '2026-10-29T13:40:00Z' });
    const r = run({
      state: 'waiting',
      attention: { kind: 'wait', taskId: 't-8', since: '2026-10-29T11:30:00Z', reason: 'нужен ваш ответ', personaId: 'p-kira', linkedSessionId: 's-me' },
      stepChains: [c],
    });
    const html = render(r, c);
    expect(html).toContain('feature-step-alarm');
    // Печатается то же, что говорит тревога выше: «ждёт вас» с attention.since,
    // а не «принят 13:40» рядом с тревогой.
    // «принято N» встречается в aria полосы — про корзину, не про статус; проверяем саму плашку.
    const statusHtml = /data-testid="feature-step-status"([\s\S]*?)<\/span>/.exec(html)?.[1] ?? '';
    expect(statusHtml).toContain('ждёт вас');
    expect(statusHtml).not.toContain('принят');
    expect(html).toContain('Ш8 — ждёт вас');
  });

  it('тревога вне этого шага (чужая задача): карточка остаётся «идёт»', () => {
    const c = chain();
    const r = run({
      state: 'stopped',
      attention: { kind: 'stopped', taskId: 't-другая', since: null, reason: null, personaId: 'p-gleb', linkedSessionId: 's-other' },
      stepChains: [c],
    });
    const html = render(r, c);
    expect(html).toContain('Стоит · Глеб');
    expect(html).toContain('идёт · 24 мин');
    expect(html).toContain('Ш8 — идёт');
  });

  it('цепочка шага: витки (кто, исход, когда) и незакрытое ревью, ждущее этот шаг', () => {
    const c = chain({
      rounds: [{ n: 1, verdict: 'returned', reviewTaskId: 'rv-1', personaId: 'p-gleb', startedAt: null, completedAt: '2026-10-29T11:00:00Z', reason: null, reviewChatId: null }],
    });
    const r = run({
      stepChains: [c],
      reviews: [{ id: 'rv-2', title: 'Ревью шапки', role: 'review', status: 'todo', reviewOf: ['t-8'], personaId: 'p-gleb' }],
    });
    const html = render(r, c);
    expect(html).toContain('Цепочка шага');
    expect(html).toContain('виток 1 — Глеб, вернул');
    expect(html).toContain('ревью — Глеб, после сдачи');
  });

  it('«Фича»: строка с процентом и полоса трекера (бэковые корзины, «в master» — реальное число)', () => {
    const c = chain();
    const html = render(run({ stepChains: [c] }), c);
    expect(html).toContain('feature-step-feature');
    expect(html).toContain('feature-step-bar');
    expect(html).toContain('22 %'); // 2 из 9 — та же формула, что на странице фичи
    expect(html).toContain('Готовность 22 %: 4 в master, 2 принято, 4 в ревью, 2 в работе, 1 не заведено');
  });

  it('на 360 длинный бейдж переносится второй строкой: название занимает всю первую (flex-basis 100%), бейдж не сжимает её до «Ш8 · П…» (п. 4)', () => {
    const c = chain();
    const html = render(run({ stepChains: [c] }), c, { isMobile: true });
    const card = /data-testid="feature-step-card"[\s\S]*?<div style="([^"]*)"/.exec(html);
    expect(card?.[1]).toContain('flex-wrap:wrap');
    const title = /data-testid="feature-step-title"[^>]*style="([^"]*)"/.exec(html)?.[1] ?? '';
    expect(title).toContain('flex-basis:100%');
    // На десктопе строка одна: ни переноса, ни basis.
    const d = render(run({ stepChains: [c] }), c);
    expect(d.match(/data-testid="feature-step-card"[\s\S]*?<div style="([^"]*)"/)?.[1]).not.toContain('flex-wrap');
    expect(d.match(/data-testid="feature-step-title"[^>]*style="([^"]*)"/)?.[1]).not.toContain('flex-basis');
  });

  it('подвал: роль этого чата по его задаче и переход на страницу фичи', () => {
    const c = chain();
    const html = render(run({ stepChains: [c] }), c);
    expect(html).toContain('этот чат —');
    expect(html).toContain('data-testid="feature-truth-role"');
    expect(html).toContain('Ш8 · фронт');
    expect(html).toContain('Страница фичи');
  });
});
