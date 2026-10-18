// Шторка фичи (FeatureBannerSheet, мобила 360 px; макет features-overview.html, sheetStrip).
//
// Два уровня:
//   1 — ЧИСТЫЕ функции: linkRank (сила связи), featureChatLinks (чаты фичи из индекса),
//      chatRoleLabel (подпись роли), sheetChatRows (список «Чаты фичи»: свои чаты,
//      ключевой первым, «чужие» не показывать). Без DOM.
//   2 — РЕНДЕР шторки (fakeDom + mount, т.к. Modal — портал в document.body):
//      «Этот чат: {роль}», кнопка «В ключевой чат фичи» (не ключевой) / нет (ключевой),
//      конвейер (FeatureRunView, когда run есть), «Чаты фичи · N» + список (свои,
//      ключевой первым, «чужие» скрыты), закрытие по крестику (onClose).
//
// Окружение node (без jsdom): fakeDom даёт document/window; api — mock (список чатов).

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { Session } from '../../../types';
import type { FeatureBannerModel } from './FeatureBanner';
import {
  FeatureBannerSheet, linkRank, featureChatLinks, chatRoleLabel, sheetChatRows,
} from './FeatureBannerSheet';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../__tests__/fakeDom';

// === Мок api: список чатов (sessions.list / chats.list) — управляемый ===
const { sessionList } = vi.hoisted(() => ({ sessionList: [] as Session[] }));
vi.mock('../../../lib/api', () => ({
  api: {
    sessions: { list: async () => sessionList },
    chats: { list: async () => [] },
    tasks: { execute: async () => {}, featuresIndex: async () => ({ features: [], chats: {} }) },
    personas: { list: async () => [] },
  },
}));
// === Мок lib/personas: ensurePersonasLoaded — no-op (иначе joinUserGroup → localStorage) ===
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: async () => {} };
});
// === Мок signalr: onMessage/onPreviewMessage — no-op (иначе getConnection() строит
// реальное соединение '/hubs/session', которое в node не резолвится) ===
vi.mock('../../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});

// === Фикстуры ===

function rail(): FeatureIndexFeature['rail'] {
  return [
    { key: 'cut', state: 'done', tone: null },
    { key: 'plan', state: 'done', tone: null },
    { key: 'prod', state: 'active', tone: 'run' },
    { key: 'review', state: 'pending', tone: null },
    { key: 'qa', state: 'pending', tone: null },
    { key: 'final', state: 'pending', tone: null },
    { key: 'merge', state: 'pending', tone: null },
  ];
}

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: rail(),
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 1,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 3,
    ...over,
  };
}

// Связи «чат × фича»: ключевой (orchestrator), исполнитель (Ш2 · бэк), куратор (чужой).
const keyLink: FeatureChatLink = { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null };
const execLink: FeatureChatLink = { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' };
const otherLink: FeatureChatLink = { featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null };

function index(): FeaturesIndex {
  return {
    features: [feat()],
    chats: {
      'chat-key': [keyLink],
      'chat-exec': [execLink],
      'chat-other': [otherLink],
    },
  };
}

// Собственные чаты пользователя: ключевой + исполнитель. 'chat-other' — «чужой» (не в списке).
function sessions(): Session[] {
  return [
    { id: 'chat-key', name: 'Ключевой чат' } as Session,
    { id: 'chat-exec', name: 'Чат исполнителя' } as Session,
  ];
}

// Модель плашки: не-ключевой чат (исполнитель Ш2 · бэк) → кнопка «Ключевой чат».
function modelNonKey(): FeatureBannerModel {
  return {
    tone: 'run', glyph: 'run', status: 'Производство · идёт', counter: '1/3 ↺1',
    role: 'Ш2 · бэк', isKey: false, button: { label: 'Ключевой чат', kind: 'key' },
  };
}
// Модель плашки: ключевой чат → кнопки «Ключевой чат» нет (в шторке — «Конвейер» в плашке).
function modelKey(): FeatureBannerModel {
  return {
    tone: 'run', glyph: 'run', status: 'Производство · идёт', counter: '1/3 ↺1',
    role: 'ключевой', isKey: true, button: { label: 'Конвейер', kind: 'pipeline' },
  };
}

// === 1: чистые функции ===

describe('linkRank: сила связи (key > curator > orchestrator > executor)', () => {
  it('orchestrator < curator < key; executor с ролью задачи — внутри executor', () => {
    expect(linkRank({ featureId: 'f1', kind: 'key', taskRole: null, stepRef: null, lane: null }))
      .toBeLessThan(linkRank({ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }));
    expect(linkRank({ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }))
      .toBeLessThan(linkRank({ featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null }));
    expect(linkRank({ featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null }))
      .toBeLessThan(linkRank({ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }));
  });

  it('executor: роль задачи влияет на порядок (cut < plan < step < … < merge)', () => {
    const cut = linkRank({ featureId: 'f1', kind: 'executor', taskRole: 'cut', stepRef: null, lane: null });
    const step = linkRank({ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: null, lane: null });
    const merge = linkRank({ featureId: 'f1', kind: 'executor', taskRole: 'merge', stepRef: null, lane: null });
    expect(cut).toBeLessThan(step);
    expect(step).toBeLessThan(merge);
  });
});

describe('featureChatLinks: чаты фичи из индекса, в порядке linkRank', () => {
  it('возвращает все (id, link) фичи в порядке linkRank (curator < orchestrator < executor)', () => {
    // KIND_ORDER: curator=1, orchestrator=2, executor=3 → ascending: other(curator), key(orchestrator), exec(executor).
    const links = featureChatLinks(index(), 'f1');
    expect(links.map(l => l.id)).toEqual(['chat-other', 'chat-key', 'chat-exec']);
  });
  it('фильтрует по featureId (другие фичи не в списке)', () => {
    const idx: FeaturesIndex = {
      features: [feat()],
      chats: {
        'chat-key': [keyLink],
        'chat-x': [{ featureId: 'f2', kind: 'executor', taskRole: 'step', stepRef: 'Ш9', lane: 'бэк' }],
      },
    };
    expect(featureChatLinks(idx, 'f1').map(l => l.id)).toEqual(['chat-key']);
  });
});

describe('chatRoleLabel: подпись роли строки чата', () => {
  it('исполнитель с stepRef+lane → «Ш2 · бэк»', () => {
    expect(chatRoleLabel(execLink)).toBe('Ш2 · бэк');
  });
  it('куратор/оркестратор без stepRef/lane → роль по виду', () => {
    expect(chatRoleLabel(otherLink)).toBe('куратор');
    expect(chatRoleLabel(keyLink)).toBe('оркестратор');
  });
});

describe('sheetChatRows: список «Чаты фичи» (свои, ключевой первым, «чужие» скрыты)', () => {
  it('показывает только свои чаты; «чужой» (не в sessions) не в списке', () => {
    const rows = sheetChatRows(index(), feat(), sessions());
    expect(rows.map(r => r.session.id)).toEqual(['chat-key', 'chat-exec']);
    // chat-other — «чужой» (не в sessions) → отфильтрован.
    expect(rows.some(r => r.session.id === 'chat-other')).toBe(false);
  });

  it('ключевой чат — первым, остальные — в порядке linkRank', () => {
    const rows = sheetChatRows(index(), feat(), sessions());
    expect(rows[0].session.id).toBe('chat-key');
  });

  it('пустые sessions → пустой список', () => {
    expect(sheetChatRows(index(), feat(), [])).toEqual([]);
  });
});

// === 2: рендер шторки (fakeDom + mount) ===

type AnyNode = { nodeType: number; nodeName: string; childNodes: AnyNode[]; style: Record<string, string> };
function allText(n: AnyNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue ?? '');
  for (const c of n.childNodes) allText(c, out);
  return out;
}
function flatText(n: AnyNode): string {
  return allText(n as AnyNode).join(' ');
}
// Контент шторки живёт в document.body (Modal — портал), не в container'е mount.
// Whitespace нормализуем: allText склеивает соседние текстовые узлы через ' ', что даёт
// двойные пробелы на стыках (напр. «Чаты фичи · » + «2»); в реальном DOM пробел один.
function bodyText(): string {
  const body = (globalThis as { document: { body: AnyNode } }).document.body;
  return flatText(body).replace(/\s+/g, ' ');
}

describe('FeatureBannerSheet: рендер (fakeDom)', () => {
  beforeEach(() => {
    installFakeDom();
    sessionList.length = 0;
    sessionList.push(...sessions());
  });
  afterEach(() => uninstallFakeDom());

  it('не-ключевой чат: «Этот чат: {роль}», кнопка «В ключевой чат фичи», «Чаты фичи · N» + свои чаты (ключевой первым)', async () => {
    mount(createElement(FeatureBannerSheet, {
      feature: feat(), model: modelNonKey(), projectId: 'p1', index: index(), run: null,
      onGoKey: () => {}, onClose: () => {},
    }));
    // Фетч списка чатов (useEffect) — даём промису выполниться.
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const text = bodyText();
    // Роль этого чата.
    expect(text).toContain('Этот чат:');
    expect(text).toContain('Ш2 · бэк');
    // Кнопка перехода в ключевой чат (не-ключевой чат).
    expect(text).toContain('В ключевой чат фичи');
    // Заголовок списка + свои чаты (ключевой первым), «чужой» не показан.
    expect(text).toContain('Чаты фичи · 2');
    expect(text).toContain('Ключевой чат');
    expect(text).toContain('Чат исполнителя');
    expect(text).not.toContain('chat-other');
    // Ключевой чат — раньше исполнителя в разметке.
    const iKey = text.indexOf('Ключевой чат');
    const iExec = text.indexOf('Чат исполнителя');
    expect(iKey).toBeGreaterThanOrEqual(0);
    expect(iExec).toBeGreaterThanOrEqual(0);
    expect(iKey).toBeLessThan(iExec);
  });

  it('архивный ключевой чат: кнопка «В ключевой чат фичи (в архиве)» (R3, минор 1)', async () => {
    mount(createElement(FeatureBannerSheet, {
      feature: feat({ keyArchived: true }), model: modelNonKey(), projectId: 'p1', index: index(), run: null,
      onGoKey: () => {}, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(bodyText()).toContain('В ключевой чат фичи (в архиве)');
  });

  it('ключевой чат: кнопки «В ключевой чат фичи» нет (этот чат и есть ключевой)', async () => {
    mount(createElement(FeatureBannerSheet, {
      feature: feat(), model: modelKey(), projectId: 'p1', index: index(), run: null,
      onGoKey: () => {}, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const text = bodyText();
    expect(text).toContain('Этот чат:');
    expect(text).toContain('ключевой');
    expect(text).not.toContain('В ключевой чат фичи');
  });

  it('конвейер: run есть → карточка фичи (FeatureRunView) в шторке', async () => {
    const run = {
      featureId: 'f1',
      epic: { id: 'f1', title: 'Эпик фичи', status: 'inProgress' as const },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      awaitingHumanSince: null, awaitingHumanReason: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    } as unknown as import('../../../types/featureRun').FeatureRun;
    mount(createElement(FeatureBannerSheet, {
      feature: feat(), model: modelKey(), projectId: 'p1', index: index(), run,
      onGoKey: () => {}, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    // FeatureRunView рендерит название эпика (шапка карточки).
    expect(bodyText()).toContain('Эпик фичи');
  });

  it('закрытие: клик по крестику (aria-label «Закрыть») вызывает onClose', async () => {
    let closed = 0;
    const { root } = mount(createElement(FeatureBannerSheet, {
      feature: feat(), model: modelNonKey(), projectId: 'p1', index: index(), run: null,
      onGoKey: () => {}, onClose: () => { closed++; },
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    // Крестик — IconButton в Modal (aria-label «Закрыть»). Ищем его в document.body (портал).
    const body = (globalThis as { document: { body: AnyNode } }).document.body;
    const closeBtn = findCloseButton(body);
    expect(closeBtn, 'крестик «Закрыть» не найден').not.toBeNull();
    act(() => { (reactProps(closeBtn!)!.onClick as () => void)(); });
    expect(closed).toBe(1);
    root.unmount();
  });
});

// Крестик Modal — IconButton с aria-label «Закрыть»; ищем по aria-label в дереве body.
function findCloseButton(root: AnyNode): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && (x as { getAttribute?: (n: string) => string | null }).getAttribute?.('aria-label') === 'Закрыть') {
      hit = x; return;
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return hit;
}
