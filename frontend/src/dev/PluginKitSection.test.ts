// Секция кита панели плагина на витрине UI-кита (S11):
//  (1) инжектируемый <style> = вшитый шрифт (S10) + девять классов кита (S9)
//      + токены текущей темы (buildKitStyle — чистая функция, фиксированный набор);
//  (2) демо несёт ВСЕ девять классов в разметке и ровно ОДИН голый <button>
//      без класса (опт-ин: кит к нему не применяется);
//  (3) секция подписана «панель» — кит панели, а не контролы приложения;
//  (4) флаг plugins-mvp off → нейтральная строка, демо (и кнопки) нет.
//
// Окружение node, без jsdom: фейк-DOM (паттерн fakeDom). ?raw для .css в vitest
// даёт пустую строку (см. bridge.test.ts) — font.css/controls.css замочены
// содержимым реальных файлов (fs). getComputedStyle в node заглушен → токены {};
// блок токенов проверяется в buildKitStyle отдельно от рендера.
//
// Мутации: убрать .cc-input из controls.css → «девять классов» краснеет;
// class у голого button → «ровно один голый» краснеет; «панель» из заголовка
// убрать → «подпись» краснеет.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { FLAGS, setAllFlags, setFlagLocal } from '../lib/featureFlags';

// ?raw для .css в vitest — пустая строка: подменяем содержимым реальных файлов
vi.mock('../../public/plugin-kit/font.css?raw', () => ({
  default: readFileSync(fileURLToPath(new URL('../../public/plugin-kit/font.css', import.meta.url)), 'utf8'),
}));
vi.mock('../../public/plugin-kit/controls.css?raw', () => ({
  default: readFileSync(fileURLToPath(new URL('../../public/plugin-kit/controls.css', import.meta.url)), 'utf8'),
}));

const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../components/chat/__tests__/fakeDom'
);
type FNode = Parameters<typeof textNodes>[0];
const { PluginKitSection, buildKitStyle, KIT_CLASSES, CC_TOKEN_MAP } = await import('./PluginKitSection');

const mountedRoots: { unmount: () => void }[] = [];

// Текст <style> в поддереве (React кладёт CSS в text-ноду потомком элемента)
function styleText(container: FNode): string {
  let out = '';
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.nodeName === 'STYLE') {
      for (const c of x.childNodes) if (c.nodeType === 3) out += c.nodeValue;
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return out;
}

// Все значения class-атрибутов элементов поддерева (разметка, не текст <style>)
function classValues(container: FNode): string[] {
  const out: string[] = [];
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.attributes['class'] !== undefined) out.push(x.attributes['class']);
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return out;
}

// Элементы по тегу
function nodesByTag(container: FNode, tag: string): FNode[] {
  const out: FNode[] = [];
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.nodeName === tag) out.push(x);
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return out;
}

function renderSection() {
  installFakeDom();
  const { root, container } = mount(createElement(PluginKitSection));
  mountedRoots.push(root);
  return container;
}

beforeEach(() => { setAllFlags({}); });
afterEach(() => {
  for (const r of mountedRoots.splice(0, mountedRoots.length)) act(() => r.unmount());
});
afterAll(() => { uninstallFakeDom(); });

// === buildKitStyle: шрифт (S10) + девять классов (S9) + токены темы ===
describe('buildKitStyle', () => {
  it('вшитый шрифт (S10): @font-face Hanken Grotesk', () => {
    const style = buildKitStyle({});
    expect(style).toContain('@font-face');
    expect(style).toContain('Hanken Grotesk');
  });

  it('все девять классов кита (S9) на месте', () => {
    const style = buildKitStyle({});
    for (const c of KIT_CLASSES) expect(style, c).toContain(c);
  });

  /* eslint-disable design/no-raw-color -- фикстуры: hex — сами значения токенов темы, а не цвет UI */
  it('токены текущей темы — в :root-блоке (как <style id="cc-tokens"> панели)', () => {
    const style = buildKitStyle({ '--cc-bg': '#FBF8F2', '--cc-fg': '#39332B' });
    expect(style).toContain(':root {');
    expect(style).toContain('--cc-bg: #FBF8F2;');
    expect(style).toContain('--cc-fg: #39332B;');
  });
  /* eslint-enable design/no-raw-color */

  it('пустой набор токенов — :root-блока нет (как buildTokensStyle панели)', () => {
    expect(buildKitStyle({})).not.toContain(':root');
  });
});

// === Рендер: флаг on — демо с девятью классами, голым button и подписью «панель» ===
describe('PluginKitSection: флаг plugins-mvp on', () => {
  it('демо: все девять классов в разметке и в <style>', () => {
    setFlagLocal(FLAGS.pluginsMvp, true);
    const container = renderSection();

    const markup = classValues(container).join(' ');
    for (const c of KIT_CLASSES) expect(markup, c + ' в разметке').toContain(c.slice(1));
    expect(styleText(container), 'классы в <style>').toContain('.cc-btn');
    expect(styleText(container), 'шрифт в <style>').toContain('@font-face');
  });

  it('опт-ин: ровно один голый <button> без класса (кит к нему не применяется)', () => {
    setFlagLocal(FLAGS.pluginsMvp, true);
    const container = renderSection();

    const buttons = nodesByTag(container, 'BUTTON');
    const bare = buttons.filter(b => b.attributes['class'] === undefined);
    expect(buttons.length).toBeGreaterThan(1);
    expect(bare.length, 'голых кнопок ровно одна').toBe(1);
    expect(textNodes(bare[0]).join('')).toBe('Голый button');
  });

  it('подпись: кит ПАНЕЛИ, а не контролы приложения', () => {
    setFlagLocal(FLAGS.pluginsMvp, true);
    const container = renderSection();
    const text = textNodes(container).join(' ');
    expect(text).toContain('Кит панели плагина');
    expect(text).toContain('а не контролы приложения');
  });
});

// === Рендер: флаг off — нейтральная строка, демо (и кнопки) нет ===
describe('PluginKitSection: флаг plugins-mvp off', () => {
  it('флаг выключен → нейтральная строка, без демо и без кнопок', () => {
    const container = renderSection();
    const text = textNodes(container).join(' ');
    expect(text).toContain('Флаг выключен');
    expect(nodesByTag(container, 'BUTTON').length).toBe(0);
  });
});

// === R-Design-2: кит — контраст бейджей, состояния, поверхности, тач-цели ===
// Карта токенов (CC_TOKEN_MAP) проверяется юнитом: новые --cc-* обязаны ссылаться на
// парные --c-* хоста, а не на сырой тон. CSS кита (controls.css, ?raw = реальный файл)
// проверяем по содержимому: бейджи по --cc-*-text, input/card по --cc-surface,
// кнопка/hover/active по --cc-bg-panel/--cc-bg-selected, invalid/loading, min-height 40.
// Мутации: --cc-success-text → '--c-success' → «парные текстовые» краснеет;
// background .cc-input → var(--cc-bg) → «поверхности» краснеет; убрать min-height: 40px
// у .cc-btn → «тач-цель» краснеет.
describe('R-Design-2: карта токенов кита (CC_TOKEN_MAP)', () => {
  it('бейджи: парные текстовые токены и рамка danger (не сырой тон)', () => {
    expect(CC_TOKEN_MAP['--cc-success-text']).toBe('--c-success-text');
    expect(CC_TOKEN_MAP['--cc-warning-text']).toBe('--c-warning-text');
    expect(CC_TOKEN_MAP['--cc-danger-text']).toBe('--c-danger-text');
    expect(CC_TOKEN_MAP['--cc-danger-border']).toBe('--c-danger-border');
  });

  it('поверхности и состояния: surface/panel/selected/accent-soft → --c-*', () => {
    expect(CC_TOKEN_MAP['--cc-surface']).toBe('--c-bg-white');
    expect(CC_TOKEN_MAP['--cc-bg-panel']).toBe('--c-bg-panel');
    expect(CC_TOKEN_MAP['--cc-bg-selected']).toBe('--c-bg-selected');
    expect(CC_TOKEN_MAP['--cc-accent-soft']).toBe('--c-accent-soft');
  });
});

describe('R-Design-2: CSS кита (controls.css)', () => {
  // ?raw в vitest даёт пустую строку, поэтому buildKitStyle({}) здесь — реальный
  // controls.css (замочен fs-импортом выше); читаем его содержимое.
  const css = () => buildKitStyle({});

  it('бейджи: текст по парным --cc-*-text; info — основной текст (парного токена нет)', () => {
    const s = css();
    expect(s).toContain('color: var(--cc-success-text)');
    expect(s).toContain('color: var(--cc-warning-text)');
    expect(s).toContain('color: var(--cc-danger-text)');
    expect(s).toMatch(/\.cc-badge--info\s*\{[^}]*color:\s*var\(--cc-fg\)/);
  });

  it('поверхности: .cc-input и .cc-card на --cc-surface, а не на общем --cc-bg', () => {
    const s = css();
    expect(s).toMatch(/\.cc-input\s*\{[^}]*background:\s*var\(--cc-surface\)/);
    expect(s).toMatch(/\.cc-card\s*\{[^}]*background:\s*var\(--cc-surface\)/);
  });

  it('кнопка и hover/выбранный списка — приподнятые слои, а не утопленный inset', () => {
    const s = css();
    expect(s).toMatch(/\.cc-btn\s*\{[^}]*background:\s*var\(--cc-bg-panel\)/);
    expect(s).toMatch(/\.cc-list-item:hover\s*\{[^}]*background:\s*var\(--cc-bg-selected\)/);
    expect(s).toMatch(/\.cc-list-item\.is-active\s*\{[^}]*background:\s*var\(--cc-bg-selected\)/);
  });

  it('выбранный элемент списка отличается от hover: кольцо акцентом', () => {
    const s = css();
    expect(s).toMatch(/\.cc-list-item\.is-active\s*\{[^}]*box-shadow:\s*inset 0 0 0 1\.5px var\(--cc-accent\)/);
  });

  it('состояния: .cc-input--invalid (dangerBorder) и .cc-btn--loading (спиннер + accentSoft)', () => {
    const s = css();
    expect(s).toMatch(/\.cc-input--invalid\s*\{[^}]*border-color:\s*var\(--cc-danger-border\)/);
    expect(s).toMatch(/\.cc-btn--loading\s*\{[^}]*background:\s*var\(--cc-accent-soft\)/);
    expect(s).toContain('@keyframes cc-cc-spin');
  });

  it('тач-цель ≥ 40px: min-height: 40px у .cc-btn, .cc-btn--sm, .cc-list-item', () => {
    const s = css();
    expect(s).toMatch(/\.cc-btn\s*\{[^}]*min-height:\s*40px/);
    expect(s).toMatch(/\.cc-btn--sm\s*\{[^}]*min-height:\s*40px/);
    expect(s).toMatch(/\.cc-list-item\s*\{[^}]*min-height:\s*40px/);
  });

  it('hover-primary не затемняет заливку (background остаётся --cc-accent, нет color-mix…black)', () => {
    const s = css();
    expect(s).toMatch(/\.cc-btn--primary:hover\s*\{[^}]*background:\s*var\(--cc-accent\)/);
  });
});
