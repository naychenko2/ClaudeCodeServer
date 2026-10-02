import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { Unplug } from 'lucide-react';

vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

const { SegmentedControl } = await import('./Segmented');
const { InlineSegmented } = await import('./InlineSegmented');
const { IconSegmented } = await import('./IconSegmented');
const { Select } = await import('./Select');
const { Notice } = await import('./Notice');
const { Menu, MenuItem } = await import('./Menu');

const noop = () => {};
const ab = [{ value: 'a', label: 'Альфа' }, { value: 'b', label: 'Бета' }];
const html = (el: Parameters<typeof renderToStaticMarkup>[0]) => renderToStaticMarkup(el);

// Эталоны записаны на коде ДО добавления опций: без новых пропов разметка обязана
// совпадать байт в байт — под этими контролами десятки потребителей
describe('без новых пропов разметка прежняя', () => {
  it('SegmentedControl', () => {
    expect(html(createElement(SegmentedControl<string>, { value: 'a', options: ab, onChange: noop }))).toMatchInlineSnapshot(`"<div style="display:flex;flex-wrap:wrap;gap:8px"><button style="flex:1 1 calc(50.0000% - 8px);padding:9px 4px;border-radius:10px;border:none;cursor:pointer;font-size:13px;font-weight:600;font-family:inherit;background:var(--c-accent);color:var(--c-on-accent);transition:background 0.15s, color 0.15s">Альфа</button><button style="flex:1 1 calc(50.0000% - 8px);padding:9px 4px;border-radius:10px;border:none;cursor:pointer;font-size:13px;font-weight:600;font-family:inherit;background:var(--c-bg-panel);color:var(--c-text-secondary);transition:background 0.15s, color 0.15s">Бета</button></div>"`);
  });
  it('InlineSegmented', () => {
    expect(html(createElement(InlineSegmented<string>, { value: 'b', options: ab, onChange: noop }))).toMatchInlineSnapshot(`"<div style="display:flex;gap:2px;background:var(--c-bg-selected);border-radius:10px;padding:2px;flex-shrink:0;opacity:1"><button aria-pressed="false" style="display:inline-flex;align-items:center;gap:4px;border:none;border-radius:7px;cursor:pointer;font-family:inherit;font-size:11px;font-weight:600;padding:4px 8px;min-height:32px;background:transparent;color:var(--c-text-muted);transition:background 0.12s, color 0.12s">Альфа</button><button aria-pressed="true" style="display:inline-flex;align-items:center;gap:4px;border:none;border-radius:7px;cursor:pointer;font-family:inherit;font-size:11px;font-weight:600;padding:4px 8px;min-height:32px;background:var(--c-accent-light);color:var(--c-accent);transition:background 0.12s, color 0.12s">Бета</button></div>"`);
    expect(html(createElement(InlineSegmented<string>, { value: null, options: ab, onChange: noop, disabled: true, isMobile: true }))).toMatchInlineSnapshot(`"<div style="display:flex;gap:2px;background:var(--c-bg-selected);border-radius:10px;padding:2px;flex-shrink:0;opacity:0.55;cursor:wait"><button disabled="" aria-pressed="false" style="display:inline-flex;align-items:center;gap:4px;border:none;border-radius:7px;cursor:wait;font-family:inherit;font-size:11px;font-weight:600;padding:4px 8px;min-height:40px;background:transparent;color:var(--c-text-muted);transition:background 0.12s, color 0.12s">Альфа</button><button disabled="" aria-pressed="false" style="display:inline-flex;align-items:center;gap:4px;border:none;border-radius:7px;cursor:wait;font-family:inherit;font-size:11px;font-weight:600;padding:4px 8px;min-height:40px;background:transparent;color:var(--c-text-muted);transition:background 0.12s, color 0.12s">Бета</button></div>"`);
  });
  it('IconSegmented', () => {
    const opts = ab.map(o => ({ ...o, icon: createElement('i') }));
    expect(html(createElement(IconSegmented<string>, { value: 'a', options: opts, onChange: noop }))).toMatchInlineSnapshot(`"<span style="position:relative;display:flex;flex-shrink:0;gap:2px;padding:2px;background:var(--c-track);border-radius:8px"><span aria-hidden="true" style="position:absolute;top:2px;left:2px;width:28px;height:20px;border-radius:6px;background:var(--c-bg-white);box-shadow:var(--shadow-thumb);transform:translateX(0px);transition:transform 0.18s cubic-bezier(0.4, 0, 0.2, 1)"></span><button aria-pressed="true" title="Альфа" style="position:relative;width:28px;height:20px;padding:0;display:flex;align-items:center;justify-content:center;border:none;border-radius:6px;cursor:default;background:transparent;color:var(--c-text-secondary);transition:background 0.12s"><i></i></button><button aria-pressed="false" title="Бета" style="position:relative;width:28px;height:20px;padding:0;display:flex;align-items:center;justify-content:center;border:none;border-radius:6px;cursor:pointer;background:transparent;color:var(--c-text-secondary);transition:background 0.12s"><i></i></button></span>"`);
    expect(html(createElement(IconSegmented<string>, { value: 'b', options: opts, onChange: noop, quiet: true }))).toMatchInlineSnapshot(`"<span style="position:relative;display:flex;flex-shrink:0;gap:2px;padding:2px;background:transparent;border-radius:8px"><span aria-hidden="true" style="position:absolute;top:2px;left:2px;width:28px;height:20px;border-radius:6px;background:var(--c-bg-selected);box-shadow:none;transform:translateX(30px);transition:transform 0.32s cubic-bezier(.32,.72,0,1)"></span><button aria-pressed="false" title="Альфа" style="position:relative;width:28px;height:20px;padding:0;display:flex;align-items:center;justify-content:center;border:none;border-radius:6px;cursor:pointer;background:transparent;color:var(--c-text-muted);transition:background 0.12s, color 0.2s"><i></i></button><button aria-pressed="true" title="Бета" style="position:relative;width:28px;height:20px;padding:0;display:flex;align-items:center;justify-content:center;border:none;border-radius:6px;cursor:default;background:transparent;color:var(--c-text-heading);transition:background 0.12s, color 0.2s"><i></i></button></span>"`);
  });
  it('Select', () => {
    expect(html(createElement(Select<string>, {
      value: 'a', onChange: noop, placeholder: 'Выберите', options: [...ab, { value: 'c', label: 'Гамма', disabled: true }],
    }))).toMatchInlineSnapshot(`"<div style="position:relative;display:flex;align-items:center"><select style="appearance:none;-webkit-appearance:none;-moz-appearance:none;width:100%;box-sizing:border-box;background:var(--c-bg-white);border:1px solid var(--c-border);border-radius:12px;padding:10px 34px 10px 13px;font-size:14px;color:var(--c-text-heading);font-family:&#x27;Hanken Grotesk&#x27;, -apple-system, BlinkMacSystemFont, &#x27;Segoe UI&#x27;, sans-serif;outline:none;cursor:pointer;opacity:1;box-shadow:none;transition:border-color 0.15s, box-shadow 0.15s"><option value="">Выберите</option><option value="a" selected="">Альфа</option><option value="b">Бета</option><option value="c" disabled="">Гамма</option></select><span style="position:absolute;right:12px;pointer-events:none;color:var(--c-text-muted);display:flex"><svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="lucide lucide-chevron-down" aria-hidden="true"><path d="m6 9 6 6 6-6"></path></svg></span></div>"`);
  });
  it('Notice', () => {
    expect(html(createElement(Notice, { icon: Unplug, title: 'Заголовок' }, 'текст'))).toMatchInlineSnapshot(`"<div role="status" style="display:flex;align-items:flex-start;gap:8px;padding:8px 12px;border-radius:8px;border:1px solid var(--c-warning);background:var(--c-warning-bg);color:var(--c-warning-text);font-family:&#x27;Hanken Grotesk&#x27;, -apple-system, BlinkMacSystemFont, &#x27;Segoe UI&#x27;, sans-serif;font-size:12px;line-height:1.45"><svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="lucide lucide-unplug" aria-hidden="true" style="flex-shrink:0;margin-top:2px"><path d="m19 5 3-3"></path><path d="m2 22 3-3"></path><path d="M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z"></path><path d="M7.5 13.5 10 11"></path><path d="M10.5 16.5 13 14"></path><path d="m12 6 6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z"></path></svg><div style="min-width:0"><div style="font-weight:600">Заголовок</div>текст</div></div>"`);
    expect(html(createElement(Notice, { icon: Unplug, tone: 'danger' }, 'текст'))).toMatchInlineSnapshot(`"<div role="status" style="display:flex;align-items:flex-start;gap:8px;padding:8px 12px;border-radius:8px;border:1px solid var(--c-danger-border);background:var(--c-danger-bg);color:var(--c-danger-text);font-family:&#x27;Hanken Grotesk&#x27;, -apple-system, BlinkMacSystemFont, &#x27;Segoe UI&#x27;, sans-serif;font-size:12px;line-height:1.45"><svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="lucide lucide-unplug" aria-hidden="true" style="flex-shrink:0;margin-top:2px"><path d="m19 5 3-3"></path><path d="m2 22 3-3"></path><path d="M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z"></path><path d="M7.5 13.5 10 11"></path><path d="M10.5 16.5 13 14"></path><path d="m12 6 6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z"></path></svg><div style="min-width:0">текст</div></div>"`);
  });
  it('Menu', () => {
    expect(html(createElement(Menu, { onClose: noop }, 'пункт'))).toMatchInlineSnapshot(`"<div style="position:fixed;inset:0;z-index:50"></div><div style="z-index:51;background:var(--c-bg-white);border:1px solid var(--c-border);border-radius:12px;box-shadow:var(--shadow-dropdown);padding:5px;min-width:200px;display:flex;flex-direction:column;max-width:min(380px, calc(100vw - 16px));position:absolute;top:30px;right:0">пункт</div>"`);
    expect(html(createElement(Menu, { onClose: noop, align: 'left', bottom: 40, minWidth: 260 }, 'пункт'))).toMatchInlineSnapshot(`"<div style="position:fixed;inset:0;z-index:50"></div><div style="z-index:51;background:var(--c-bg-white);border:1px solid var(--c-border);border-radius:12px;box-shadow:var(--shadow-dropdown);padding:5px;min-width:260px;display:flex;flex-direction:column;max-width:min(380px, calc(100vw - 16px));position:absolute;bottom:40px;left:0">пункт</div>"`);
  });
  it('MenuItem', () => {
    expect(html(createElement(MenuItem, { icon: createElement('i'), label: 'Песня про кота', onClick: noop }))).toMatchInlineSnapshot(`"<button style="display:flex;align-items:center;gap:10px;width:100%;text-align:left;background:none;border:none;border-radius:8px;padding:9px 10px;cursor:pointer;color:var(--c-text-primary);font-size:13.5px;font-family:&#x27;Hanken Grotesk&#x27;, -apple-system, BlinkMacSystemFont, &#x27;Segoe UI&#x27;, sans-serif"><span style="display:inline-flex;align-items:center;width:15px;height:15px;flex-shrink:0;color:inherit"><i></i></span><span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">Песня про кота</span></button>"`);
  });
});

// Кнопки сегментов по порядку: [открывающий тег, ...]
const buttons = (s: string) => s.match(/<button[^>]*>/g) ?? [];

describe('опции сегментов disabled / muted / title', () => {
  const opts = [
    { value: 'a', label: 'Альфа' },
    { value: 'b', label: 'Бета', disabled: true, title: 'Нельзя' },
    { value: 'c', label: 'Гамма', muted: true, title: 'Сначала выберите звук' },
  ];

  it('SegmentedControl: disabled выключен, muted кликабелен и приглушён, title — подсказка', () => {
    const [a, b, c] = buttons(html(createElement(SegmentedControl<string>, { value: 'a', options: opts, onChange: noop })));
    expect(a).not.toContain('disabled');
    expect(a).not.toContain('title=');
    expect(b).toContain('disabled=""');
    expect(b).toContain('title="Нельзя"');
    expect(b).toContain('cursor:not-allowed');
    expect(c).not.toContain('disabled');
    expect(c).toContain('title="Сначала выберите звук"');
    // muted — цвет обычного сегмента без прозрачности плюс пунктирная рамка
    expect(c).toContain('color:var(--c-text-secondary)');
    expect(c).toContain('border:1px dashed var(--c-text-muted)');
    expect(c).toContain('padding:8px 3px');
    expect(c).not.toContain('opacity');
    expect(b).toContain('opacity:0.45');
    expect(b).not.toContain('dashed');
  });

  it('SegmentedControl: выбранный muted-сегмент не гасится', () => {
    const [, , c] = buttons(html(createElement(SegmentedControl<string>, { value: 'c', options: opts, onChange: noop })));
    expect(c).toContain('color:var(--c-on-accent)');
    expect(c).not.toContain('opacity');
    expect(c).not.toContain('dashed');
  });

  it('InlineSegmented: опция гасится отдельно от группы', () => {
    const [a, b, c] = buttons(html(createElement(InlineSegmented<string>, { value: 'a', options: opts, onChange: noop })));
    expect(a).not.toContain('disabled');
    expect(b).toContain('disabled=""');
    expect(b).toContain('title="Нельзя"');
    expect(b).toContain('opacity:0.4');
    expect(c).not.toContain('disabled');
    expect(c).toContain('border:1px dashed var(--c-text-muted)');
    expect(c).toContain('padding:3px 7px');
    expect(c).not.toContain('opacity');
    expect(b).not.toContain('dashed');
    expect(c).toContain('title="Сначала выберите звук"');
  });

  it('InlineSegmented: disabled группы по-прежнему гасит все сегменты курсором ожидания', () => {
    const bs = buttons(html(createElement(InlineSegmented<string>, { value: 'a', options: opts, onChange: noop, disabled: true })));
    expect(bs.every(b => b.includes('disabled=""') && b.includes('cursor:wait'))).toBe(true);
  });

  it('IconSegmented: title перекрывает label, disabled выключает, muted кликабелен', () => {
    const io = opts.map(o => ({ ...o, icon: createElement('i') }));
    const [a, b, c] = buttons(html(createElement(IconSegmented<string>, { value: 'a', options: io, onChange: noop })));
    expect(a).toContain('title="Альфа"');
    expect(a).not.toContain('disabled');
    expect(b).toContain('disabled=""');
    expect(b).toContain('title="Нельзя"');
    expect(b).toContain('opacity:0.35');
    expect(c).not.toContain('disabled');
    expect(c).toContain('title="Сначала выберите звук"');
    expect(c).toContain('border:1px dashed var(--c-text-muted)');
    expect(c).not.toContain('opacity');
    expect(b).not.toContain('dashed');
  });
});

describe('группы в Select', () => {
  it('пункты собираются в optgroup на месте первого, без группы — на своих местах, disabled у группы', () => {
    const s = html(createElement(Select<string>, {
      value: 'speak', onChange: noop, options: [
        { value: 'speak', label: 'Озвучка', group: 'Новый звук' },
        { value: 'denoise', label: 'Убрать шум', group: { label: 'С выбранным звуком', disabled: true } },
        { value: 'song', label: 'Песня', group: 'Новый звук' },
        { value: 'concat', label: 'Склеить' },
        { value: 'separate', label: 'Стемы', group: 'С выбранным звуком' },
      ],
    }));
    const body = s.slice(s.indexOf('<select'), s.indexOf('</select>'));
    expect(body.replace(/<select[^>]*>/, '').replace(/ selected=""/g, '')).toBe(
      '<optgroup label="Новый звук"><option value="speak">Озвучка</option><option value="song">Песня</option></optgroup>'
      + '<optgroup label="С выбранным звуком" disabled=""><option value="denoise">Убрать шум</option><option value="separate">Стемы</option></optgroup>'
      + '<option value="concat">Склеить</option>',
    );
  });

  it('группа без disabled не выключается', () => {
    const s = html(createElement(Select<string>, { value: 'a', onChange: noop, options: [{ value: 'a', label: 'А', group: { label: 'Г' } }] }));
    expect(s).toContain('<optgroup label="Г"><option');
  });
});

describe('тон info у Notice', () => {
  it('рамка и иконка — C.info, фон — C.infoBg, текст основной', () => {
    const s = html(createElement(Notice, { icon: Unplug, tone: 'info' }, 'Выбор снят'));
    expect(s).toContain('border:1px solid var(--c-info)');
    expect(s).toContain('background:var(--c-info-bg)');
    expect(s).toContain('color:var(--c-text-primary)');
    expect(s).toMatch(/<svg[^>]*style="[^"]*color:var\(--c-info\)/);
  });
});

describe('Menu во всю ширину', () => {
  it('обычный режим: по ширине родителя, вверх от bottom', () => {
    const s = html(createElement(Menu, { onClose: noop, bottom: 40, fullWidth: true }, 'пункт'));
    const card = s.slice(s.lastIndexOf('<div'));
    for (const rule of ['position:absolute', 'left:0', 'right:0', 'min-width:0', 'max-width:none', 'bottom:40px']) expect(card).toContain(rule);
  });

  it('anchor-режим: окно с полями 8 px', () => {
    // anchor уходит порталом в document.body — в node его нет, портал подменён выше
    vi.stubGlobal('document', { body: null });
    const anchor = { top: 700, bottom: 730, left: 100, right: 160 } as DOMRect;
    const s = html(createElement(Menu, { onClose: noop, anchor, fullWidth: true }, 'пункт'));
    vi.stubGlobal('document', undefined);
    const card = s.slice(s.lastIndexOf('<div'));
    for (const rule of ['position:fixed', 'left:8px', 'right:8px', 'min-width:0', 'max-width:none', 'bottom:206px']) expect(card).toContain(rule);
  });
});

describe('вторая строка у MenuItem', () => {
  it('hint — отдельной строкой под подписью, приглушённым цветом, обе строки с многоточием', () => {
    const s = html(createElement(MenuItem, { label: 'Песня про кота', hint: '2:14 · агент · 3 мин назад', onClick: noop }));
    expect(s).toMatch(/flex-direction:column[^>]*><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">Песня про кота<\/span><span style="[^"]*color:var\(--c-text-muted\)[^"]*">2:14 · агент · 3 мин назад<\/span>/);
  });
});

describe('крупный IconSegmented и aria-pressed', () => {
  const io = [{ value: 'a', label: 'А', icon: createElement('i') }, { value: 'b', label: 'Б', icon: createElement('i') }];
  it('size="lg": кнопки и ползунок 40×40, шаг ползунка 42', () => {
    const s = html(createElement(IconSegmented<string>, { value: 'b', options: io, onChange: noop, size: 'lg' }));
    expect(s.match(/<button[^>]*width:40px;height:40px/g)).toHaveLength(2);
    expect(s).toMatch(/aria-hidden="true" style="[^"]*width:40px;height:40px[^"]*translateX\(42px\)/);
  });
  it('aria-pressed — только у выбранного сегмента', () => {
    const s = html(createElement(IconSegmented<string>, { value: 'b', options: io, onChange: noop }));
    expect(s.match(/aria-pressed="(true|false)"/g)).toEqual(['aria-pressed="false"', 'aria-pressed="true"']);
  });
});

describe('выравнивание Menu по началу якоря', () => {
  const anchor = { top: 400, bottom: 440, left: 90, right: 150 } as DOMRect;
  const card = (props: Record<string, unknown>) => {
    vi.stubGlobal('document', { body: null });
    const s = html(createElement(Menu, { onClose: noop, anchor, minWidth: 300, ...props }, 'пункт'));
    vi.stubGlobal('document', undefined);
    return s.slice(s.lastIndexOf('<div'));
  };
  it('по умолчанию — правый край у правого края якоря, вниз, раз снизу влезает', () => {
    const c = card({});
    expect(c).toContain('left:8px');
    expect(c).toContain('top:446px');
  });
  it('anchorAlign="start" + preferUp: левый край у левого края якоря, вверх над ним', () => {
    const c = card({ anchorAlign: 'start', preferUp: true });
    expect(c).toContain('left:90px');
    expect(c).toContain('bottom:506px');
  });
  it('preferUp без места сверху открывается вниз', () => {
    vi.stubGlobal('document', { body: null });
    const s = html(createElement(Menu, { onClose: noop, anchor: { ...anchor, top: 100, bottom: 140 } as DOMRect, preferUp: true, maxHeight: 300 }, 'п'));
    vi.stubGlobal('document', undefined);
    expect(s.slice(s.lastIndexOf('<div'))).toContain('top:146px');
  });
});

describe('плитка иконки и перенос второй строки у MenuItem', () => {
  it('iconTile — плитка C.bgInset размером iconSize; hintWrap — без многоточия', () => {
    const s = html(createElement(MenuItem, { icon: createElement('i'), iconSize: 26, iconTile: true, label: 'logo.png', hint: 'версия 2 · правили последней', hintWrap: true, onClick: noop }));
    expect(s).toMatch(/width:26px;height:26px;[^"]*background:var\(--c-bg-inset\)/);
    const hint = s.slice(s.lastIndexOf('<span'));
    expect(hint).toContain('overflow-wrap:anywhere');
    expect(hint).not.toContain('nowrap');
  });
});
