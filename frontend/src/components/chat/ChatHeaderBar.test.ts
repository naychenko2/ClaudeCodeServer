// FP-C3: фича — часть шапки (вариант А макета chat-feature-fit-v1), а не отдельная полоса.
// Проп feature = три части от FeatureChatCard: subtitle (на месте подзаголовка героя),
// edge (нижняя кромка шапки — трекер 2 px), alarm (кнопка тревоги первой в правом кластере).
// «Задано» = «у чата есть фича»: чат БЕЗ фичи — шапка ровно как раньше (линия тулбара на
// месте, подзаголовок со своими бейджами); у чата С фичей подзаголовок занят строкой фичи,
// а линия «при прокрутке» заменена кромкой-трекером — две кромки разом не рисуем. У закрытой
// фичи кромки нет (edge=null) — остаётся обычная линия шапки.
//
// Окружение node: renderToStaticMarkup. Пиксели высоты здесь не меряются — структурные
// признаки (узлы частей, линия тулбара) их прямые следствия; живой замер — ui_probe.
// Первый div ответа в тулбарной ветке — корень Toolbar (при отсутствии кромки и контекста
// возвращается ровно он), поэтому его style — то, что рисует линию шапки.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { ChatHeaderBar } from './ChatHeaderBar';
import { C } from '../../lib/design';
import { __test as backNav } from '../../lib/featureBackNav';
import { CHAT_ACTION_ORDER, CHAT_ACTION_LABELS, CARD_ACTIONS_HIDDEN_BY_DEFAULT, HEADER_ACTIONS_HIDDEN_BY_DEFAULT } from '../../lib/chatActions';
import type { Session } from '../../types';

// Линия «при прокрутке» тулбара — маркер того, что шапка живёт как раньше: её ставит
// ChatHeaderBar (transparent в начале ленты, C.borderLight при прокрутке). При кромке-трекере
// тулбар гасит свою (noBorder).
const SCROLL_LINE = `border-bottom:1px solid ${C.borderLight}`;

// Стор аккаунта провайдера — не предмет теста, а его useSyncExternalStore не отдаёт
// server snapshot (для SSR-рендера в тесте нужен). Мок — пустой ответ.
vi.mock('../../lib/accountUsage', () => ({
  useAccountUsage: () => null,
  accountSnapshotsFor: () => [],
}));

const session = {
  id: 's-1', mode: 'default', status: 'active', messageCount: 0,
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
} as Session;

const base = {
  session,
  hasMessages: false,
  online: true,
  cost: { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 },
  falCost: { total: 0, count: 0, byModel: new Map() },
  glifCost: { count: 0, credits: 0, hasCredits: false, byType: new Map() },
  billing: 'api' as const,
  rateWindows: [],
  ctxEstimate: { window: 200000, fresh: false, level: 'normal' as const },
  isWaiting: false,
  isCompacting: false,
  canCompact: false,
  onCompact: () => {},
};

// Стиль ПЕРВОГО div ответа (корень тулбара в тулбарной ветке) — по нему видно, где линия.
function rootStyle(html: string): string {
  const m = html.match(/^<div[^>]*style="([^"]*)"/);
  return m ? m[1] : '';
}

function render(over: Record<string, unknown>): string {
  return renderToStaticMarkup(createElement(ChatHeaderBar, { ...base, ...over }));
}

// Три части фичи — маркеры вместо настоящих (их рисует FeatureChatCard, тут важно ЧТО и ГДЕ).
const sub = createElement('span', { 'data-testid': 'feature-sub' }, 'строка фичи');
const edge = createElement('i', { 'data-testid': 'feature-edge' });
const alarm = createElement('b', { 'data-testid': 'feature-alarm' }, 'Ответить');
const parts = (over: Partial<{ subtitle: unknown; edge: unknown; alarm: unknown }> = {}) =>
  ({ subtitle: sub, edge, alarm, ...over });

describe('ChatHeaderBar: фича как часть шапки', () => {
  it('без фичи: ни кромки, ни частей фичи — ответ это один тулбар со своей линией', () => {
    const html = render({ scrolled: true });
    expect(html).not.toContain('chat-feature-edge');
    expect(html).not.toContain('feature-sub');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('фича передана как null (так делает FeatureChatCard у чата без фичи): то же, что «не задана»', () => {
    const html = render({ scrolled: true, feature: null });
    expect(html).not.toContain('chat-feature-edge');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('без фичи, но со строкой контекста: линия тулбара не пропадает', () => {
    const html = render({ scrolled: true, contextBar: createElement('span', { 'data-testid': 'ctx' }, 'материалы') });
    expect(html).toContain('data-testid="ctx"');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('с фичей: строка на месте подзаголовка, кромка-трекер под тулбаром, тревога в правом кластере; своей линии у тулбара нет', () => {
    const html = render({ scrolled: true, feature: parts() });
    expect(html).toContain('data-testid="feature-sub"');
    expect(html).toContain('data-testid="feature-alarm"');
    expect(html).toContain('data-testid="chat-feature-edge"');
    expect(html).not.toContain('chat-feature-bar'); // отдельной полосы больше нет
    const style = rootStyle(html);
    expect(style).toContain('border-bottom:none');
    expect(style).not.toContain(SCROLL_LINE);
    // Кромка идёт ПОСЛЕ тулбара (нижняя кромка шапки), а не внутри него.
    expect(html.indexOf('data-testid="chat-feature-edge"')).toBeGreaterThan(html.indexOf('data-testid="feature-sub"'));
    expect(html.indexOf('data-testid="chat-feature-edge"')).toBeGreaterThan(html.indexOf('data-testid="feature-alarm"'));
  });

  it('закрытая фича (кромки нет): строка есть, а обычная линия шапки при прокрутке остаётся', () => {
    const html = render({ scrolled: true, feature: parts({ edge: null, alarm: null }) });
    expect(html).toContain('data-testid="feature-sub"');
    expect(html).not.toContain('chat-feature-edge');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('строка фичи заменяет мета-подзаголовок (метки агента и пр.): у чата без фичи они на месте', () => {
    const agent = { name: 'Агент-Икс', color: 'blue' };
    expect(render({ agent })).toContain('Агент-Икс');
    const html = render({ agent, feature: parts() });
    expect(html).not.toContain('Агент-Икс');
    expect(html).toContain('feature-sub');
  });

  it('остров (hero) с фичей: кромка внутри колонки шапки вместо её линии, строка и тревога на местах', () => {
    const html = render({ island: true, scrolled: true, feature: parts() });
    expect(html).toContain('data-testid="chat-feature-edge"');
    expect(html).toContain('data-testid="feature-sub"');
    expect(html).toContain('data-testid="feature-alarm"');
    expect(rootStyle(html)).toContain('border-bottom:none');
  });

  // Кромка — последняя строка шапки: стоит ПОСЛЕ строки контекста (материалов), а не режет шапку
  // между заголовком и чипами. Мутация «вернуть кромку до contextBar» краснит оба теста порядка.
  const ctx = createElement('span', { 'data-testid': 'ctx' }, 'материалы');
  const EDGE = 'data-testid="chat-feature-edge"';

  it('остров: при feature.edge и contextBar кромка после строки контекста', () => {
    const html = render({ island: true, scrolled: true, feature: parts(), contextBar: ctx });
    expect(html.indexOf(EDGE)).toBeGreaterThan(html.indexOf('data-testid="ctx"'));
  });

  it('тулбар: при feature.edge и contextBar кромка после строки контекста, у строки контекста нет своей линии', () => {
    const html = render({ scrolled: true, feature: parts(), contextBar: ctx });
    expect(html.indexOf(EDGE)).toBeGreaterThan(html.indexOf('data-testid="ctx"'));
    const ctxRow = html.slice(0, html.indexOf('data-testid="ctx"')).match(/<div[^>]*style="([^"]*)"[^>]*>(?:(?!<div)[\s\S])*$/);
    expect(ctxRow?.[1]).toContain('border-bottom:none');
    expect(ctxRow?.[1]).not.toContain(`border-bottom:1px solid ${C.border}`);
  });

  it('без feature.edge строка контекста сохраняет свою линию, порядок прежний', () => {
    const html = render({ scrolled: true, feature: parts({ edge: null }), contextBar: ctx });
    expect(html).not.toContain(EDGE);
    expect(html).toContain(`border-bottom:1px solid ${C.border}`);
  });

  it('остров без фичи: узла кромки нет — hero-шапка не обрастает пустым div, линия при прокрутке на месте', () => {
    const html = render({ island: true, scrolled: true });
    expect(html).not.toContain('chat-feature-edge');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });
});

// FS-FC-chip (макет v5 §4): при строке фичи в шапке чип «к фиче» в тулбаре не рисуется —
// возврат ведёт «К фиче» поповера (список фич даёт ChatPanel). Скрытие только по совпадению
// featureId указателя. Мутация «не передавать hideForFeatureIds в FeatureBackChip» — первый
// тест красный (чип рисуется). Чипу нужен projectId чата — он в chatSession ниже.
describe('ChatHeaderBar: скрытие чипа возврата при строке фичи', () => {
  const chatSession = { ...session, projectId: 'p1' } as Session;
  beforeEach(() => { backNav.reset(); });
  function renderWith(over: Record<string, unknown>): string {
    return renderToStaticMarkup(createElement(ChatHeaderBar, { ...base, session: chatSession, ...over }));
  }
  const withFeature = { feature: parts() };
  it('featureBarFeatureIds=[f1] и указатель на f1: чипа нет', () => {
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-1' }, 'Моя фича');
    const html = renderWith({ ...withFeature, featureBarFeatureIds: ['f1'] });
    expect(html, 'вторая кнопка возврата рядом со строкой фичи').not.toContain('feature-back-chip');
  });
  it('тот же указатель без пропа: чип рисуется (контроль: гасит проп, а не указатель)', () => {
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-1' }, 'Моя фича');
    expect(renderWith(withFeature)).toContain('feature-back-chip');
  });
  it('указатель на другую фичу (f2), строка на f1: чип рисуется', () => {
    backNav.recordFeatureReturn('f2', { kind: 'chat', id: 's-1' }, 'Другая фича');
    expect(renderWith({ ...withFeature, featureBarFeatureIds: ['f1'] })).toContain('feature-back-chip');
  });
});

describe('ChatHeaderBar: действие «Вики» каталога', () => {
  it('ключ wiki стоит сразу после dossier, подпись «Вики»', () => {
    expect(CHAT_ACTION_ORDER[CHAT_ACTION_ORDER.indexOf('dossier') + 1]).toBe('wiki');
    expect(CHAT_ACTION_LABELS.wiki).toBe('Вики');
  });
  it('умолчания: в плитке «Вики» под глазиком, в шапке/мобиле/на стене — на виду', () => {
    expect(CARD_ACTIONS_HIDDEN_BY_DEFAULT).toContain('wiki');
    expect(HEADER_ACTIONS_HIDDEN_BY_DEFAULT).not.toContain('wiki');
  });
  it('без подсистемы «Заметки» и флага в шапке нет ни кнопки, ни экземпляра меню «Вики»', () => {
    const html = renderToStaticMarkup(createElement(ChatHeaderBar, base as never));
    expect(html).not.toContain('wiki-chat-menu');
    expect(html).not.toContain('Действия «Вики»');
  });
});
