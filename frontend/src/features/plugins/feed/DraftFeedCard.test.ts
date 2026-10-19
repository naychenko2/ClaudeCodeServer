// Карточка черновика в ленте (шаг 4.5b, ADR-031): запись plugin_draft рисуется карточкой,
// 404 → нейтральная строка, без флага → не рисуется, событие обновляет карточку, из данных
// записи используется только slug.
//
// Окружение node: асинхронные загрузки (список + предпросмотр) и событие — на минимальном
// фейк-DOM (createRoot + act, паттерн fakeDom).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { ChatItem, ServerMessage } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { PluginDraftPreview, PluginDraftSummary } from '../../../lib/api';

// onMessage — захватываем подписчика, чтобы «сдвинуть» событие в тесте
const handlers: ((m: ServerMessage) => void)[] = [];
vi.mock('../../../lib/signalr', () => ({
  onMessage: (h: (m: ServerMessage) => void) => { handlers.push(h); return () => {}; },
}));

// api.plugins.* — фиксированные ответы; значения подменяются в beforeEach
vi.mock('../../../lib/api', () => ({
  api: {
    plugins: {
      draftsList: vi.fn(),
      draftPreview: vi.fn(),
      draftAccept: vi.fn(),
      draftReject: vi.fn(),
      draftRestore: vi.fn(),
      grant: vi.fn(),
      setSecret: vi.fn(),
    },
  },
}));

const { api } = await import('../../../lib/api');
const { setAllFlags, setFlagLocal } = await import('../../../lib/featureFlags');
const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../../../components/chat/__tests__/fakeDom'
);
const { DraftAnchor } = await import('./records');
const { __resetDraftsStore } = await import('./draftsStore');

// === Фиксированные данные ===
const SLUG = 'local-1/my-plugin';

const PREVIEW: PluginDraftPreview = {
  slug: SLUG,
  name: 'My Plugin',
  version: '1.0.0',
  panels: [],
  commands: [],
  skills: [],
  requestedPermissions: ['notes:read'],
  secrets: [],
  files: [],
  validation: { isValid: true, problems: [] },
};

const SUMMARY: PluginDraftSummary = {
  slug: SLUG,
  id: 'local-1/my-plugin',
  version: '1.0.0',
  updatedAt: '2026-10-01T00:00:00Z',
  problems: 0,
  installedSameVersion: false,
  rejected: false,
  expiresAt: null,
};

// Запись plugin_draft: в данных только slug
function recordItem(data: Record<string, unknown>, fallback = 'Черновик: my-plugin'): ChatItem {
  return { kind: 'module_record', module: 'plugins', recordType: 'plugin_draft', data, fallback } as ChatItem;
}

function ctxFor(item: ChatItem): ChatItemToolCtx {
  return { item, online: true, projectId: 'proj-1', sessionId: 'chat-1', persona: null };
}

// Монтирует якорь и даёт асинхронным загрузкам (список + предпросмотр) дойти
async function renderAnchor(item: ChatItem) {
  installFakeDom();
  const { container } = mount(createElement(DraftAnchor, { ctx: ctxFor(item) }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  handlers.length = 0;
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftAccept).mockReset();
  vi.mocked(api.plugins.draftReject).mockReset();
  vi.mocked(api.plugins.draftRestore).mockReset();
  vi.mocked(api.plugins.grant).mockReset();
  vi.mocked(api.plugins.setSecret).mockReset();
  setAllFlags({});
  __resetDraftsStore();
});

afterAll(() => { uninstallFakeDom(); });

// === Запись рисуется карточкой ===
describe('DraftAnchor: запись plugin_draft', () => {
  it('флаг on + черновик в списке → карточка (имя, «Принять», бейдж «Черновик»)', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain('My Plugin');
    expect(text()).toContain('Принять');
    expect(text()).toContain('Черновик');
  });

  it('из данных записи используется только slug: лишние поля не влияют на рендер', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG, extra: 'ignored', other: 42 }));
    // та же карточка, что и с чистым { slug }
    expect(text()).toContain('My Plugin');
    expect(text()).toContain('Принять');
  });
});

// === 404: slug не в списке владельца → нейтральная строка ===
describe('DraftAnchor: 404', () => {
  it('slug не в списке → «Черновик «slug» больше недоступен», без ошибок и кнопок', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([]); // slug не найден
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain(`Черновик «${SLUG}» больше недоступен`);
    // без кнопок карточки
    expect(text()).not.toContain('Принять');
  });
});

// === Без флага → не рисуется ===
describe('DraftAnchor: флаг plugins-build-yourself', () => {
  it('флаг off → карточка не рисуется (даже если черновик в списке)', async () => {
    setFlagLocal('plugins-build-yourself', false);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    // карточка (с «Принять»/именем) не на месте
    expect(text()).not.toContain('Принять');
    expect(text()).not.toContain('My Plugin');
  });

  it('флаг off + есть fallback → нейтральная строка fallback', async () => {
    setFlagLocal('plugins-build-yourself', false);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }, 'Черновик: my-plugin'));
    expect(text()).toContain('Черновик: my-plugin');
    expect(text()).not.toContain('Принять');
  });
});

// === Событие plugin_draft_changed обновляет карточку ===
describe('DraftAnchor: событие plugin_draft_changed', () => {
  it('событие по slug → подсветка «Черновик обновлён агентом»', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain('My Plugin');

    // «сдвигаем» событие: все подписчики получают plugin_draft_changed по нашему slug
    await act(async () => {
      for (const h of handlers) h({ type: 'plugin_draft_changed', slug: SLUG, revision: 2 } as ServerMessage);
      await new Promise(r => setTimeout(r, 0));
    });

    expect(text()).toContain('Черновик обновлён агентом');
  });
});
