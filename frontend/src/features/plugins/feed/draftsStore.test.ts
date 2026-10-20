// Кэш черновиков (draftsStore): перечитывание списка при «грязном» флаге (m4) и
// защита от устаревшего ответа предпросмотра при обратном порядке завершения (m3).
//
// Окружение node: чистые функции стора (refreshDraft → loadList/loadPreview) без React.
// api.plugins.* — управляемые deferred-проми, чтобы держать загрузку «в полёте».

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PluginDraftPreview, PluginDraftSummary } from '../../../lib/api';

vi.mock('../../../lib/api', () => ({
  api: {
    plugins: {
      draftsList: vi.fn(),
      draftPreview: vi.fn(),
    },
  },
}));
vi.mock('../../../lib/signalr', () => ({ onMessage: () => () => {} }));

const { api } = await import('../../../lib/api');
const { refreshDraft, bump, getDraftSnapshot, __resetDraftsStore } = await import('./draftsStore');

const SLUG = 'my-plugin';

const PREVIEW: PluginDraftPreview = {
  slug: SLUG, name: 'My Plugin', version: '1.0.0',
  panels: [], commands: [], skills: [], requestedPermissions: [], secrets: [], files: [],
  validation: { isValid: true, problems: [] },
};
const SUMMARY: PluginDraftSummary = {
  slug: SLUG, id: 'local-1/my-plugin', version: '1.0.0', updatedAt: '2026-10-01T00:00:00Z',
  problems: 0, installedSameVersion: false, rejected: false, expiresAt: null,
};

// Deferred-проми: держим загрузку «в полёте» и завершаем в нужном моменте
function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
// Пропустить очередь микротасков (чтобы .then/.catch промисов выполнились)
const tick = () => new Promise<void>(r => setTimeout(r, 0));

beforeEach(() => {
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
  vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
  __resetDraftsStore();
});
afterAll(() => { vi.restoreAllMocks(); });

describe('draftsStore: перечитывание списка (m4)', () => {
  it('refresh во время идущей loadList → второй вызов draftsList', async () => {
    const d1 = deferred<PluginDraftSummary[]>();
    const d2 = deferred<PluginDraftSummary[]>();
    vi.mocked(api.plugins.draftsList)
      .mockReturnValueOnce(d1.promise as never)
      .mockReturnValueOnce(d2.promise as never);

    refreshDraft(SLUG); // загрузка #1 в полёте
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    refreshDraft(SLUG); // во время полёта → _listDirty; guard не даёт второй вызов сразу
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    d1.resolve([SUMMARY]); // завершение #1 → ре-чит → загрузка #2
    await tick();
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(2);

    d2.resolve([SUMMARY]); // закрыть вторую, чтобы не висело
    await tick();
  });

  // Путь события (bump), а не refreshDraft: событие во время идущей loadList ставит
  // _listDirty — иначе void loadList() упрёт в гард и перечитывание после полёта потеряется.
  // Мутация: убрать `if (_listInflight) _listDirty = true` из bump → этот тест красный.
  it('bump (событие) во время идущей loadList → _listDirty → второй вызов draftsList', async () => {
    const d1 = deferred<PluginDraftSummary[]>();
    const d2 = deferred<PluginDraftSummary[]>();
    vi.mocked(api.plugins.draftsList)
      .mockReturnValueOnce(d1.promise as never)
      .mockReturnValueOnce(d2.promise as never);

    refreshDraft(SLUG); // загрузка #1 в полёте
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    bump(SLUG); // событие во время полёта → _listDirty
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    d1.resolve([SUMMARY]); // завершение #1 → видит _listDirty → ре-чит → загрузка #2
    await tick();
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(2);

    d2.resolve([SUMMARY]);
    await tick();
  });
});

describe('draftsStore: устаревший ответ предпросмотра (m3)', () => {
  it('обратный порядок завершения: свежий (force) не затирает устаревший', async () => {
    // refreshDraft → loadPreview(slug, true). Первый force (gen 1) в полёте, второй (gen 2)
    // завершается ПЕРВЫМ; устаревший gen 1, завершающийся позже, не должен затереть gen 2.
    const stale = deferred<PluginDraftPreview>();
    const fresh = deferred<PluginDraftPreview>();
    vi.mocked(api.plugins.draftPreview)
      .mockReturnValueOnce(stale.promise as never) // gen 1
      .mockReturnValueOnce(fresh.promise as never); // gen 2

    refreshDraft(SLUG); // gen 1 в полёте
    refreshDraft(SLUG); // gen 2 в полёте (force)

    fresh.resolve({ ...PREVIEW, version: '2.0.0' }); // gen 2 завершается ПЕРВЫМ
    await tick();
    stale.resolve({ ...PREVIEW, version: '1.0.0' }); // gen 1 (устаревший) — позже
    await tick();

    // Итог: свежий (2.0.0), а не устаревший (1.0.0)
    expect(getDraftSnapshot(SLUG).preview?.version).toBe('2.0.0');
  });
});

// === Шаг 2: 404 preview отклонённого черновика (дефект 6579d346) ===
// После «Отклонить» refreshDraft перечитывает список; если черновик отклонён,
// preview не запрашивается (иначе 404 в консоли). Мутация: вернуть запрос → красный.
describe('draftsStore: preview отклонённого черновика (шаг 2)', () => {
  const SUMMARY_REJECTED: PluginDraftSummary = { ...SUMMARY, rejected: true };

  it('отклонён → draftPreview НЕ вызывается (нет 404)', async () => {
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY_REJECTED]);
    await refreshDraft(SLUG);
    expect(vi.mocked(api.plugins.draftPreview)).not.toHaveBeenCalled();
  });

  it('активен → draftPreview вызывается (нормальный случай)', async () => {
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    await refreshDraft(SLUG);
    expect(vi.mocked(api.plugins.draftPreview)).toHaveBeenCalledTimes(1);
  });
});
