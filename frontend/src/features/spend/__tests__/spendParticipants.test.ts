// Расход по участникам общего проекта (S2a-F4): карточка «Участники» — только владельцу,
// у участника — Notice «видны только ваши ходы», личный проект — как раньше.
// SpendScreen монтируется целиком, api.spend мокается шпионами.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { createElement } from 'react';
import { act } from 'react';
import { api } from 'aihome_shell/kit';
import { SpendScreen } from '../SpendScreen';
import type { SpendCardRow, SpendOverviewResponse } from '../../../types';
import { mount, installFakeDom, uninstallFakeDom, textNodes, reactProps } from '../../../components/chat/__tests__/fakeDom';

const tok = (n: number) => ({ input: n, output: 0, cacheRead: 0, cacheCreation: 0, total: n });
const row = (key: string, name: string, n: number, extra: Partial<SpendCardRow> = {}): SpendCardRow =>
  ({ key, name, meta: null, tokens: tok(n), turns: 2, falGenerations: 0, ...extra });

function overview(): SpendOverviewResponse {
  return {
    from: '2026-10-01', to: '2026-10-31', detailDays: 30, windowStart: '2026-10-01', allUsers: false,
    totals: tok(900), turns: 4, falGenerations: 0,
    byDay: [{ date: '2026-10-31', aggregated: false, total: 900, bySource: { 'chat-turn': 900 }, falGenerations: 0 }],
    cards: { projects: [row('p1', 'Общий', 900)], models: [], chats: [], personas: [], sources: [], providers: [] },
    topTurns: [],
  };
}

async function render(props: { isAdmin?: boolean; sharedOwner?: boolean; sharedMember?: boolean }, users: SpendCardRow[]) {
  vi.spyOn(api.spend, 'overview').mockResolvedValue(overview());
  const pivot = vi.spyOn(api.spend, 'pivot').mockResolvedValue({ nodes: users.map(u => ({ ...u, hasDetail: true })) });
  const { container } = mount(createElement(SpendScreen, { ctx: {}, isAdmin: false, onClose: () => {}, embedded: true, ...props }));
  await act(async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); });
  return { texts: textNodes(container).join('|'), pivot };
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { vi.restoreAllMocks(); uninstallFakeDom(); });

const USERS = [row('u-1', 'anna', 600, { privateChats: { count: 2, tokens: 400000 } }), row('u-2', 'boris', 300)];

describe('расход по участникам', () => {
  it('владелец общего проекта: карточка «Участники» с авторами ходов, переключателя «Мои/Все» нет', async () => {
    const { texts, pivot } = await render({ sharedOwner: true }, USERS);
    expect(texts).toContain('Участники');
    expect(texts).toContain('anna');
    expect(texts).toContain('boris');
    expect(texts).not.toContain('Все');
    expect(texts).not.toContain('видны только ваши ходы');
    expect(pivot.mock.calls[0][0]).toContain('groupBy=user');
  });

  it('личный проект: карточки нет, разрез user не запрашивается, Notice нет', async () => {
    const { texts, pivot } = await render({}, USERS);
    expect(texts).not.toContain('Участники');
    expect(pivot).not.toHaveBeenCalled();
    expect(texts).not.toContain('видны только ваши ходы');
  });

  it('участник чужого проекта: Notice «видны только ваши ходы», карточки нет', async () => {
    const { texts } = await render({ sharedMember: true }, USERS);
    expect(texts).toContain('видны только ваши ходы');
    expect(texts).not.toContain('Участники');
  });

  it('privateChats пришло — подстрока «из них приватные чаты (2)», у строки без поля её нет', async () => {
    const { texts } = await render({ sharedOwner: true }, USERS);
    expect(texts).toContain('из них приватные чаты (2) — 400k');
    expect(texts).not.toContain('[object Object]');
    expect(texts.match(/из них приватные чаты/g)).toHaveLength(1);
  });

  it('privateChats не пришло — подстроки нет вовсе', async () => {
    const { texts } = await render({ sharedOwner: true }, [row('u-1', 'anna', 600)]);
    expect(texts).toContain('anna');
    expect(texts).not.toContain('приватные чаты');
  });
});

// Клик по первому узлу с текстом: поднимаемся до ближайшего предка с onClick
async function clickText(container: Parameters<typeof textNodes>[0], text: string) {
  let hit: any = null;
  const walk = (n: any) => { if (hit) return; if (n.nodeType === 3 && n.nodeValue === text) { hit = n; return; } n.childNodes.forEach(walk); };
  walk(container);
  for (let n = hit?.parentNode; n; n = n.parentNode) {
    const h = reactProps(n)?.onClick as (() => void) | undefined;
    if (h) { await act(async () => { h(); for (let i = 0; i < 5; i++) await Promise.resolve(); }); return; }
  }
}
const userFilterCalls = (spies: Array<{ mock: { calls: unknown[][] } }>) =>
  spies.flatMap(s => s.mock.calls.map(c => String(c[0]))).filter(q => /[?&]user=/.test(q));

describe('участники: фильтра по чужому user у владельца нет', () => {
  async function renderOwner() {
    const ov = vi.spyOn(api.spend, 'overview').mockResolvedValue(overview());
    const pivot = vi.spyOn(api.spend, 'pivot').mockResolvedValue({ nodes: USERS.map(u => ({ ...u, hasDetail: true })) });
    const turns = vi.spyOn(api.spend, 'turns').mockResolvedValue({ items: [], total: 0 } as never);
    const { container } = mount(createElement(SpendScreen, { ctx: {}, isAdmin: false, onClose: () => {}, embedded: true, sharedOwner: true }));
    await act(async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); });
    return { container, spies: [ov, pivot, turns] };
  }

  it('клик по строке карточки «Участники» — анализ без user=<id>, группировка по пользователю', async () => {
    const { container, spies } = await renderOwner();
    await clickText(container, 'anna');
    expect(userFilterCalls(spies)).toEqual([]);
    expect(spies[1].mock.calls.some(c => String(c[0]).includes('groupBy=user'))).toBe(true);
  });

  it('клик по узлу уровня «пользователь» в дереве не строит фильтр user', async () => {
    const { container, spies } = await renderOwner();
    await clickText(container, 'anna');
    const before = spies.flatMap(s => s.mock.calls).length;
    await clickText(container, 'anna');
    expect(userFilterCalls(spies)).toEqual([]);
    expect(spies.flatMap(s => s.mock.calls).length).toBe(before);
  });
});
