import { beforeEach, describe, expect, it, vi } from 'vitest';

// Телефонная шторка «Картинки»: «Редактировать» из дерева шлёт revealWorkspacePanel до
// перехода в чат — просьба ждёт полосу нужного чата. Окружение node: window — EventTarget,
// слушатель модуль ставит при загрузке, поэтому он грузится ПОСЛЕ заглушки
const win = Object.assign(new EventTarget(), {
  innerWidth: 360, innerHeight: 780,
  matchMedia: () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);

const { revealWorkspacePanel } = await import('../../../lib/subsystems/registryCore');
const { __resetSheetReveal, subscribeSheetReveal, takeSheetReveal } = await import('./sheetReveal');

beforeEach(() => { __resetSheetReveal(); });

describe('ожидаемое раскрытие шторки', () => {
  it('просьба до монтирования полосы не теряется: полоса того же чата забирает её один раз', () => {
    revealWorkspacePanel('images', undefined, 's1');
    expect(takeSheetReveal('s1')).toBe(true);
    expect(takeSheetReveal('s1')).toBe(false);
  });

  it('полоса другого чата просьбу не забирает — она ждёт свой чат', () => {
    revealWorkspacePanel('images', undefined, 's2');
    expect(takeSheetReveal('s1')).toBe(false);
    expect(takeSheetReveal('s2')).toBe(true);
  });

  it('просьба без чата (чип «Персонаж») — любой полосе; чужие панели пропускаются', () => {
    revealWorkspacePanel('sound', undefined, 's1');
    expect(takeSheetReveal('s1')).toBe(false);
    revealWorkspacePanel('images', 'characters');
    expect(takeSheetReveal('s1')).toBe(true);
  });

  it('смонтированная полоса узнаёт о просьбе через подписку', () => {
    let calls = 0;
    const off = subscribeSheetReveal(() => { calls++; });
    revealWorkspacePanel('images', undefined, 's1');
    off();
    revealWorkspacePanel('images', undefined, 's1');
    expect(calls).toBe(1);
  });
});
