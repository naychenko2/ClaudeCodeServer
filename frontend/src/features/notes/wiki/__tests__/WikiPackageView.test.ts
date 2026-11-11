// Карточка спорной операции показывает доказательства (С2 Ш6b): утверждение, цитаты по
// порядку массива, вид страницы подписью PAGE_TYPE, для state — «на {asOf}» строкой как есть.
// У старой операции (все поля null) блока нет вовсе.
//
// Мутации (вывод — в отчёте задачи):
//  (1) quotes.map → quotes.slice(0, 1) → «цитаты — по порядку» краснеет;
//  (2) убрать условие kind === 'state' у asOf → «kind=decision без даты» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom, textNodes } from '../../../../components/chat/__tests__/fakeDom';
import { WikiPackageView } from '../WikiPackageView';

vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(() => Promise.resolve(null)),
  readStoredUserId: vi.fn(() => 'u1'),
}));

const op = (over: Record<string, unknown>) => ({
  index: 0, op: 'supersede', page: 'projects/p1/gleb', target: 'Глеб ревьюит только бэкенд.',
  text: 'Закрывает ревью фронта.', class: 'session×supersede', decision: 'inbox',
  reason: 'Спорно', baseHashes: {}, inputTokens: null, outputTokens: null,
  humanDecision: null, humanReason: null,
  claim: null, quotes: null, kind: null, asOf: null,
  ...over,
});

const pkg = (o: Record<string, unknown>) => ({
  id: 'p1', section: 'projects/p1', status: 'proposed', statusReason: null,
  createdAt: '2026-10-30T03:10:00Z', updatedAt: '2026-10-30T03:10:00Z',
  sources: [{ origin: 'session', ref: 's1' }], operations: [o],
});

const roots: Root[] = [];
function render(o: Record<string, unknown>) {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPackageView, { pkg: pkg(o) as never, onBack: () => {} }));
  roots.push(root);
  return container;
}
const texts = (c: ReturnType<typeof mount>['container']) => textNodes(c).map(t => t.trim()).filter(Boolean);
const all = (c: ReturnType<typeof mount>['container']) => texts(c).join(' ');

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('карточка операции: доказательства (Ш6b)', () => {
  it('утверждение, цитаты по порядку, вид и «на asOf» для state', () => {
    const c = render(op({
      claim: 'Глеб теперь ревьюит и фронт', quotes: ['первая цитата', 'вторая', 'третья'],
      kind: 'state', asOf: 'unknown-дата?',
    }));
    const t = texts(c);
    expect(all(c)).toContain('Глеб теперь ревьюит и фронт');
    // порядок цитат — по массиву
    const i1 = t.indexOf('первая цитата'), i2 = t.indexOf('вторая'), i3 = t.indexOf('третья');
    expect(i1).toBeGreaterThanOrEqual(0);
    expect(i2).toBeGreaterThan(i1);
    expect(i3).toBeGreaterThan(i2);
    expect(all(c)).toContain('Состояние · на unknown-дата?');
  });

  it('kind=decision без даты: подпись вида, «на» нет', () => {
    const c = render(op({ kind: 'decision', asOf: '2026-01-01' }));
    expect(all(c)).toContain('Решение');
    expect(all(c)).not.toContain('на 2026-01-01');
  });

  it('старая операция (все null): блока доказательств нет', () => {
    const c = render(op({}));
    expect(all(c)).not.toContain('Утверждение:');
    expect(all(c)).not.toContain('Цитаты:');
  });
});
