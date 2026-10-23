// Карточка в ленте зовёт общий useDraftDiff с ревизией из draftsStore: событие
// plugin_draft_changed при открытом сравнении → повторный запрос draftDiff.
// DraftCard подменён заглушкой, которая отдаёт наружу onLoadDiff и diff.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginDraftDiff, PluginDraftPreview, PluginDraftSummary } from '../../../lib/api';

type Msg = { type: string; slug: string; revision: number };
const handlers: ((m: Msg) => void)[] = [];
vi.mock('../../../lib/signalr', () => ({
  onMessage: (h: (m: Msg) => void) => { handlers.push(h); return () => {}; },
}));
vi.mock('../../../lib/api', () => ({
  api: { plugins: { draftsList: vi.fn(), draftPreview: vi.fn(), draftDiff: vi.fn() } },
}));

let card: { diff: PluginDraftDiff | null; onLoadDiff?: () => void };
vi.mock('../DraftCard', () => ({
  DraftCard: (p: typeof card) => { card = p; return null; },
}));

const { api } = await import('../../../lib/api');
const { setFlagLocal, setAllFlags } = await import('../../../lib/featureFlags');
const { installFakeDom, mount, uninstallFakeDom } = await import('../../../components/chat/__tests__/fakeDom');
const { DraftFeedCard } = await import('./DraftFeedCard');
const { __resetDraftsStore } = await import('./draftsStore');

const SLUG = 'weather';
const SUMMARY = {
  slug: SLUG, id: 'u/weather', version: '1.0.0', updatedAt: '2026-10-01T00:00:00Z',
  problems: 0, installedSameVersion: false, rejected: false, expiresAt: null,
} as PluginDraftSummary;
const PREVIEW = { slug: SLUG, name: 'W', version: '1.0.0' } as unknown as PluginDraftPreview;
const roots: { unmount: () => void }[] = [];

function diffOf(tag: string): PluginDraftDiff {
  return { tag } as unknown as PluginDraftDiff;
}

// Даём цепочке промисов (список → предпросмотр → diff) дойти
async function settle() {
  await act(async () => {
    for (let i = 0; i < 10; i++) await new Promise(r => setTimeout(r, 0));
  });
}

beforeEach(() => {
  installFakeDom();
  handlers.length = 0;
  setAllFlags({});
  setFlagLocal('plugins-build-yourself', true);
  __resetDraftsStore();
  vi.mocked(api.plugins.draftsList).mockReset().mockResolvedValue([SUMMARY]);
  vi.mocked(api.plugins.draftPreview).mockReset().mockResolvedValue(PREVIEW);
  vi.mocked(api.plugins.draftDiff).mockReset();
});
afterEach(() => { for (const r of roots.splice(0)) act(() => r.unmount()); });
afterAll(() => { uninstallFakeDom(); });

describe('DraftFeedCard: сравнение и правки агента', () => {
  it('сравнение открыто → plugin_draft_changed → повторный draftDiff, свежий diff', async () => {
    vi.mocked(api.plugins.draftDiff)
      .mockResolvedValueOnce(diffOf('old'))
      .mockResolvedValueOnce(diffOf('new'));
    roots.push(mount(createElement(DraftFeedCard, { slug: SLUG, fallback: null, projectId: null, chatId: 'c' })).root);
    await settle();
    await act(async () => { card.onLoadDiff?.(); });
    await settle();
    expect(card.diff).toEqual(diffOf('old'));

    await act(async () => { for (const h of handlers) h({ type: 'plugin_draft_changed', slug: SLUG, revision: 2 }); });
    await settle();
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(2);
    expect(card.diff).toEqual(diffOf('new'));
  });
});
