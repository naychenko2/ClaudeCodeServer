// Взаимоисключение панелей генерации (ADR-021 §3): «Картинки» и «Звук» на экране
// одновременно не живут — открытие одной закрывает другую любым путём открытия,
// а с остальными панелями обе соседствуют по обычной модели зоны.
import { describe, it, expect } from 'vitest';
import {
  sanitizeZones, openPanelIn, togglePanelIn, revealPanel, moveAcrossAt, moveAcrossToNewColumn,
  replacePanelWith, closeRivals, zoneOf, type PanelZones,
} from './panelStackState';
import { CHAT_RIGHT_KEYS, WORKSPACE_KEYS, PANEL_HOME, panelRivals } from './panelCatalog';

function zones(left: string[][], right: string[][], stash: { left?: string[][]; right?: string[][] } = {}): PanelZones {
  return sanitizeZones({
    left: { layout: left, stash: stash.left ?? [] },
    right: { layout: right, stash: stash.right ?? [] },
  });
}

describe('каталог панелей генерации', () => {
  it('images и sound — правые, есть в проекте и в правой зоне личного чата', () => {
    for (const k of ['images', 'sound'] as const) {
      expect(PANEL_HOME[k]).toBe('right');
      expect(WORKSPACE_KEYS).toContain(k);
      expect(CHAT_RIGHT_KEYS).toContain(k);
    }
  });

  it('соперники: images ↔ sound, у прочих панелей соперников нет', () => {
    expect(panelRivals('images')).toEqual(['sound']);
    expect(panelRivals('sound')).toEqual(['images']);
    expect(panelRivals('files')).toEqual([]);
  });
});

describe('взаимоисключение images/sound', () => {
  it('клик по рельсе: открытие картинок закрывает звук, соседи остаются', () => {
    const z = togglePanelIn(zones([], [['files', 'sound']]), 'right', 'images');
    expect(zoneOf(z, 'sound')).toBeNull();
    expect(zoneOf(z, 'images')).toBe('right');
    expect(zoneOf(z, 'files')).toBe('right');
  });

  it('открытие звука закрывает картинки и в соседней зоне', () => {
    const z = openPanelIn(zones([['images']], [['files']]), 'right', 'sound');
    expect(zoneOf(z, 'images')).toBeNull();
    expect(zoneOf(z, 'sound')).toBe('right');
  });

  it('внешний показ (reveal) закрывает соперника', () => {
    const { zones: z } = revealPanel(zones([], [['images']]), 'sound');
    expect(zoneOf(z, 'images')).toBeNull();
    expect(zoneOf(z, 'sound')).toBe('right');
  });

  it('дроп из рельсы в направляющую и в новую колонку закрывает соперника', () => {
    const a = moveAcrossAt(zones([], [['files', 'images']]), 'sound', 'right', 0, 0);
    expect(zoneOf(a, 'images')).toBeNull();
    expect(zoneOf(a, 'sound')).toBe('right');
    const b = moveAcrossToNewColumn(zones([], [['files', 'images']]), 'sound', 'right', 1);
    expect(zoneOf(b, 'images')).toBeNull();
    expect(zoneOf(b, 'sound')).toBe('right');
  });

  it('дроп в колонку рядом с одиноким соперником: индекс колонки не съезжает', () => {
    // Картинки — единственная панель первой колонки; звук бросают во вторую (к «Файлам»)
    const z = moveAcrossAt(zones([], [['images'], ['files']]), 'sound', 'right', 1, 1);
    expect(z.right.layout).toEqual([['files', 'sound']]);
  });

  it('дроп звука прямо на картинки: звук занимает их слот', () => {
    const z = replacePanelWith(zones([], [['files', 'images']]), 'sound', 'images');
    expect(z.right.layout).toEqual([['files', 'sound']]);
  });

  it('соперник уходит и из спрятанного набора — разворот не вернёт его вторым', () => {
    const z = openPanelIn(zones([], [['files']], { left: [['sound']] }), 'right', 'images');
    expect(z.left.stash.flat()).not.toContain('sound');
  });

  it('сохранённая раскладка с обеими панелями чинится при чтении', () => {
    const z = zones([], [['images', 'sound']]);
    expect(z.right.layout.flat().filter(k => k === 'images' || k === 'sound')).toHaveLength(1);
  });

  it('с прочими панелями правило ничего не делает', () => {
    const before = zones([], [['files', 'images']]);
    expect(closeRivals(before, 'tasks')).toBe(before);
  });
});
