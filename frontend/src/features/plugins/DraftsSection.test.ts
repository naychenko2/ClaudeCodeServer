// Секция «Черновики от агента» (шаг 4.4): «Принять…» → диалог согласия; сбой выдачи
// прав (grant) — ошибка видна, диалог остаётся открытым, список перечитан (черновик
// уже установлен accept'ом и не должен висеть в списке).
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom); диалог
// согласия — портал в document.body.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project } from '../../types';
import type { PluginDraftPreview, PluginDraftSummary } from '../../lib/api';

// onMessage — заглушка (события в этом тесте не сдвигаем)
vi.mock('../../lib/signalr', () => ({
  onMessage: () => () => {},
}));

// api.plugins.* — фиксированные ответы; значения подменяются в тесте
vi.mock('../../lib/api', () => ({
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

const { api } = await import('../../lib/api');
const { setAllFlags, setFlagLocal } = await import('../../lib/featureFlags');
const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { DraftsSection } = await import('./DraftsSection');

// === Фиксированные данные ===
const SLUG = 'local-1/my-plugin';

const PROJECT: Project = {
  id: 'proj-1',
  name: 'Тест',
  rootPath: '/test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

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

// === Хелперы (типы нод fakeDom не экспортируются — через сигнатуру textNodes) ===
type FNode = Parameters<typeof textNodes>[0];

// Весь текст поддерева (для проверок видимости)
function textOf(n: FNode): string {
  let out = '';
  const walk = (x: FNode) => {
    if (x.nodeType === 3) out += x.nodeValue;
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

// Кнопка по её точной подписи: React держит props на узле под ключом __reactProps$<random>
function findButton(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x) === label) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Монтирует секцию и даёт асинхронной загрузке (список + предпросмотр) дойти
async function renderSection() {
  installFakeDom();
  const { container } = mount(createElement(DraftsSection, { project: PROJECT }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftAccept).mockReset();
  vi.mocked(api.plugins.draftReject).mockReset();
  vi.mocked(api.plugins.draftRestore).mockReset();
  vi.mocked(api.plugins.grant).mockReset();
  vi.mocked(api.plugins.setSecret).mockReset();
  setAllFlags({});
});

afterAll(() => { uninstallFakeDom(); });

describe('DraftsSection: «Принять…» (диалог согласия)', () => {
  it('сбой grant → ошибка видна, диалог остаётся открытым, список перечитан (мутация: убрать try/catch)', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockResolvedValue({
      id: 'local-1/my-plugin',
      version: '1.0.0',
      sha256: 'abc12345',
      source: 'upload:my-plugin',
      enabled: true,
      requested: ['notes:read'],
      granted: [],
      consentRequired: true,
      newPermissions: [],
      secrets: [],
    });
    vi.mocked(api.plugins.grant).mockRejectedValue(new Error('Сбой сети'));

    const { container } = await renderSection();

    // «Принять…» на карточке → диалог согласия (портал в document.body)
    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); });

    const body = (globalThis as { document: { body: FNode } }).document.body;
    const install = findButton(body, 'Установить');
    expect(install, 'кнопка «Установить» не найдена').not.toBeNull();
    await act(async () => { install!.onClick(); });

    // grant упал: ошибка видна в диалоге, диалог не закрыт,
    // список перечитан (черновик уже установлен — не должен висеть в списке)
    expect(textOf(body)).toContain('Сбой сети');
    expect(findButton(body, 'Установить'), 'диалог закрылся после ошибки').not.toBeNull();
    expect(vi.mocked(api.plugins.draftsList).mock.calls.length).toBeGreaterThanOrEqual(2);
  });
});
