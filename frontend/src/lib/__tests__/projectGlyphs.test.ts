import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { GlyphIcon, preloadGlyph } from '../projectGlyphs';

// Сторож против моргания значков (ADR-009 §7). Значок проекта грузится
// динамическим чанком, и штатный DynamicIcon из lucide держит загруженное
// в СВОЁМ состоянии: каждая новая монтировка начиналась с fallback, а
// смена проекта перемонтирует воркспейс целиком (key={project.id}) — весь
// док проектов моргал инициалами на каждое переключение.
//
// Проверяем ровно это свойство: уже загруженный значок рисуется в ПЕРВОМ
// же рендере. renderToStaticMarkup нарочно: он синхронный и НЕ выполняет
// эффекты — то есть видит ровно первый кадр монтировки, как док при
// переключении проекта. Вернётся покомпонентный стейт — тест покраснеет.

const Initials = () => createElement('span', null, 'ХЗ');
const render = (name: string) =>
  renderToStaticMarkup(createElement(GlyphIcon, { name, fallback: Initials, size: 24 }));

describe('GlyphIcon: кеш загруженных значков', () => {
  it('до загрузки показывает fallback, после — значок в первом же рендере', async () => {
    // Имя нарочно не из тех, что могли попасть в кеш соседними проверками.
    expect(render('rocket')).toContain('ХЗ');

    await preloadGlyph('rocket');

    const markup = render('rocket');
    expect(markup).toContain('<svg');
    expect(markup).not.toContain('ХЗ');
  });

  it('размеры и штрих доезжают до svg', async () => {
    await preloadGlyph('folder');
    const markup = render('folder');
    expect(markup).toContain('width="24"');
    expect(markup).toContain('stroke-width="2"');
  });

  it('имя вне набора lucide остаётся на fallback навсегда', async () => {
    await preloadGlyph('no-such-icon-name');
    expect(render('no-such-icon-name')).toContain('ХЗ');
  });
});
