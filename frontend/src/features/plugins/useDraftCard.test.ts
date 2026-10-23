// Обёртка карточки черновика (useDraftCard): событие plugin_draft_changed по slug сбрасывает
// сравнение и, если окно файлов открыто (loadDiff звали), запрашивает diff заново.
// Окружение node: фейк-DOM (createRoot + act), паттерн остальных тестов плагинов.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginDraftDiff, PluginDraftSummary } from '../../lib/api';

type ServerMessageLike = { type: string; slug: string; revision: number };

const handlers: ((m: ServerMessageLike) => void)[] = [];
vi.mock('../../lib/signalr', () => ({
  onMessage: (h: (m: ServerMessageLike) => void) => { handlers.push(h); return () => {}; },
}));
vi.mock('../../lib/api', () => ({
  api: { plugins: { draftPreview: vi.fn(), draftDiff: vi.fn() } },
}));

const { api } = await import('../../lib/api');
const { useDraftCard } = await import('./useDraftCard');
const { installFakeDom, mount, uninstallFakeDom } = await import('../../components/chat/__tests__/fakeDom');

const SUMMARY = { slug: 'weather' } as unknown as PluginDraftSummary;
let latest: ReturnType<typeof useDraftCard>;
const roots: { unmount: () => void }[] = [];

function Probe() {
  latest = useDraftCard(SUMMARY);
  return null;
}

function diffOf(tag: string): PluginDraftDiff {
  return { tag } as unknown as PluginDraftDiff;
}

function emit(slug: string) {
  for (const h of handlers) h({ type: 'plugin_draft_changed', slug, revision: 2 });
}

beforeEach(() => {
  installFakeDom();
  handlers.length = 0;
  vi.mocked(api.plugins.draftPreview).mockReset().mockResolvedValue({} as never);
  vi.mocked(api.plugins.draftDiff).mockReset();
});
afterEach(() => { for (const r of roots.splice(0)) act(() => r.unmount()); });
afterAll(() => { uninstallFakeDom(); });

describe('useDraftCard: событие plugin_draft_changed', () => {
  it('окно открыто → событие по slug → повторный draftDiff со свежим содержимым', async () => {
    vi.mocked(api.plugins.draftDiff)
      .mockResolvedValueOnce(diffOf('old'))
      .mockResolvedValueOnce(diffOf('new'));
    roots.push(mount(createElement(Probe)).root);
    await act(async () => { latest.loadDiff(); });
    expect(latest.diff).toEqual(diffOf('old'));

    await act(async () => { emit('weather'); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(2);
    expect(latest.diff).toEqual(diffOf('new'));
  });

  it('событие по чужому slug → diff не трогаем', async () => {
    vi.mocked(api.plugins.draftDiff).mockResolvedValue(diffOf('old'));
    roots.push(mount(createElement(Probe)).root);
    await act(async () => { latest.loadDiff(); });
    await act(async () => { emit('other'); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(1);
    expect(latest.diff).toEqual(diffOf('old'));
  });
});
