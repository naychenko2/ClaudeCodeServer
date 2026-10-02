import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node: окно — EventTarget, как у остальных тестов показа панелей
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1280, innerHeight: 800 }));

const { revealWorkspacePanel } = await import('./subsystems/registryCore');
const store = await import('./genPanelReturn');
const { parseSoundPreset } = await import('../features/audioEditor/panel/preset');
const { parseImagesPreset } = await import('../features/imageEditor/panel/preset');

// Читаем состояние без React: тот же стор, что у хуков
const peek = (key: string) => store.__peek(key);

beforeEach(() => { store.__resetGenPanelReturn(); });

describe('приём preset и returnTo панелью-получателем', () => {
  const preset = { mode: 'music', op: 'song', duration: 32, style: 'тёплый эмбиент', instrumental: true };
  const returnTo = { key: 'videoEditor', tab: 'film', label: 'К фильму «утро»' };

  it('заготовка и ссылка назад доезжают до стора получателя прозрачно', () => {
    expect(revealWorkspacePanel('sound', 'settings', { preset, returnTo })).toBe(true);
    expect(peek('sound')).toEqual({ preset, returnTo });
  });

  it('заготовка разбирается один раз, ссылка остаётся до возврата', () => {
    revealWorkspacePanel('sound', 'settings', { preset, returnTo });
    store.consumePreset('sound');
    expect(peek('sound')).toEqual({ preset: null, returnTo });
  });

  it('показ без returnTo снимает прежнюю ссылку', () => {
    revealWorkspacePanel('sound', 'settings', { preset, returnTo });
    revealWorkspacePanel('sound', 'settings');
    expect(peek('sound').returnTo).toBeNull();
  });

  it('«↩» возвращает вызвавшую панель на её вкладку и забывает ссылку', () => {
    const seen: unknown[] = [];
    (window as unknown as EventTarget).addEventListener('cc-reveal-panel', e => seen.push((e as CustomEvent).detail));
    revealWorkspacePanel('sound', 'settings', { returnTo });
    seen.length = 0;
    store.returnToOrigin('sound', returnTo, 's1');
    expect(seen).toEqual([{ key: 'videoEditor', tab: 'film', sessionId: 's1' }]);
    expect(peek('sound').returnTo).toBeNull();
  });

  it('подпись возврата: своя или по вкладке', () => {
    expect(store.returnLabel(returnTo)).toBe('К фильму «утро»');
    expect(store.returnLabel({ key: 'videoEditor', tab: 'scene' })).toBe('К сцене');
    expect(store.returnLabel({ key: 'videoEditor' })).toBe('Назад');
  });
});

describe('разбор заготовок', () => {
  it('«Звук»: музыка, операция, длительность, стиль, «Инструментал»', () => {
    expect(parseSoundPreset({ mode: 'music', op: 'song', duration: 31.6, style: ' эмбиент ', instrumental: true, from: 'под фильм «утро»' }))
      .toEqual({ mode: 'music', op: 'song', durationSec: 32, style: 'эмбиент', instrumental: true, from: 'под фильм «утро»' });
  });

  it('«Звук»: не музыка, мусор и чужая операция отбрасываются', () => {
    expect(parseSoundPreset({ mode: 'voice' })).toBeNull();
    expect(parseSoundPreset(null)).toBeNull();
    expect(parseSoundPreset({ mode: 'music', op: 'speak', duration: -3 })).toEqual({ mode: 'music' });
  });

  it('«Картинки»: нить в работу', () => {
    expect(parseImagesPreset({ thread: 't1' })).toEqual({ threadId: 't1' });
    expect(parseImagesPreset({ thread: '' })).toBeNull();
  });
});
