import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { LUCIDE_ICON_NAME_SET, isLucideIconName } from '../../../lib/projectGlyphs';

// Сторож расхождения наборов имён (ADR-009 §5). Бэкенд подбирает значок из своего
// белого списка (`lucide-icon-names.g.txt`, ~2000 имён), фронт рисует значок по имени.
// Пока плитка резолвила имя через рукописную карту GLYPHS (89 имён), 7 из 10 боевых
// проектов получали ПУСТУЮ плитку: значка нет, инициалы не рисуются — ветка показа
// значка уже выбрана. Отказ молчаливый, в консоль ничего не падает, поэтому дефект
// жил до ручной приёмки. Тест ловит любое новое расхождение.

const here = path.dirname(fileURLToPath(import.meta.url));
// Путь только через path.join от расположения теста: CI гоняет на ubuntu, литералы
// с обратным слэшем там считаются относительными именами
const whitelistPath = path.join(
  here, '..', '..', '..', '..', '..',
  'backend', 'ClaudeHomeServer', 'Services', 'ProjectIcons', 'lucide-icon-names.g.txt');

function backendNames(): string[] {
  return readFileSync(whitelistPath, 'utf8')
    .split(/\r?\n/)
    .map(line => line.trim())
    // Шапка «сгенерировано, руками не править» и пустые строки — не имена
    .filter(line => line.length > 0 && !line.startsWith('#'));
}

describe('белый список значков бэкенда и набор фронта', () => {
  it('список бэкенда читается и не пуст', () => {
    expect(backendNames().length).toBeGreaterThan(1000);
  });

  it('каждое имя, которое может подобрать бэкенд, фронт умеет нарисовать', () => {
    const unknown = backendNames().filter(name => !LUCIDE_ICON_NAME_SET.has(name));
    expect(unknown, `имена без компонента на фронте: ${unknown.slice(0, 20).join(', ')}`).toEqual([]);
  });

  // Регрессия боя 06.09.2026: эти имена подобраны реальным проектам и НЕ входили
  // в рукописную карту — на экране была пустая цветная плитка
  it.each(['heart-pulse', 'flame', 'calendar-days', 'landmark', 'circuit-board', 'party-popper', 'message-circle'])(
    'имя «%s» известно фронту', name => {
      expect(isLucideIconName(name)).toBe(true);
    });

  it('мусорное имя фронту неизвестно — плитка уходит на инициалы', () => {
    expect(isLucideIconName('nope-nope-nope')).toBe(false);
    expect(isLucideIconName('')).toBe(false);
  });
});
