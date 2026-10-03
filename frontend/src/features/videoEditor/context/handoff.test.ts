import { beforeEach, describe, expect, it, vi } from 'vitest';

// Передача хода в «Картинки»/«Звук»: ответ хранилища на несовпадение ревизии не должен пролетать молча
const kit = vi.hoisted(() => ({
  setPrimary: vi.fn(), showToast: vi.fn(), presetAction: vi.fn(), setContextReturn: vi.fn(), revealContextPanel: vi.fn(),
}));
vi.mock('aihome_shell/kit', () => ({
  ...kit,
  getChatContextState: () => ({ primary: null }),
  objectKey: () => 'k',
}));

vi.mock('./state', () => ({ FILM_KIND: 'video-film', SCENE_KIND: 'video-scene' }));

const { sceneToImages } = await import('./handoff');

const args = { sessionId: 's1', sceneId: 'sc1', sceneName: 'утро', threadId: 't1', draw: true, reveal: false };

beforeEach(() => { vi.clearAllMocks(); });

describe('передача хода: исходы setPrimary', () => {
  it('conflict — тост «Контекст только что поменяли», ход не передан', async () => {
    kit.setPrimary.mockResolvedValue('conflict');
    expect(await sceneToImages(args)).toBe(false);
    expect(kit.showToast).toHaveBeenCalledWith('Контекст только что поменяли', expect.any(String), 'info');
  });

  it('failed — тост с причиной ошибки', async () => {
    kit.setPrimary.mockResolvedValue('failed');
    expect(await sceneToImages(args)).toBe(false);
    expect(kit.showToast).toHaveBeenCalledWith('Не удалось открыть картинку в контексте', '', 'error');
  });

  it('ok — без тоста', async () => {
    kit.setPrimary.mockResolvedValue('ok');
    expect(await sceneToImages(args)).toBe(true);
    expect(kit.showToast).not.toHaveBeenCalled();
  });
});
