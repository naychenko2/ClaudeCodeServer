import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { Pencil, Sparkles } from 'lucide-react';

// Окружение node: окно с matchMedia для useIsMobile (Badge), портал меню — на месте
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

const { pickRows } = await import('./pickSort');
const { GenerationPickMenu } = await import('./GenerationPickMenu');
const { createReleaseUndo, RELEASE_UNDO_MS } = await import('./useReleaseUndo');
const { handleModeClick, GenerationModeSwitch } = await import('./GenerationModeSwitch');
const { groupExecutorRows, ExecutorList, ExecutorSummaryRow } = await import('./ExecutorList');
const { ReleaseNotice } = await import('./ReleaseNotice');

const html = (el: Parameters<typeof renderToStaticMarkup>[0]) => renderToStaticMarkup(el);
const noop = () => {};

describe('pickRows — отбор и порядок строк меню', () => {
  const items = [
    { id: 'a', at: 100 },
    { id: 'b', at: 300 },
    { id: 'c', at: 200 },
    { id: 'd', at: 300 },
  ];

  it('свежие сверху, при равном времени выше более поздний в списке', () => {
    expect(pickRows(items).map(r => r.id)).toEqual(['d', 'b', 'c', 'a']);
  });

  it('скрытые и уже выбранная строка не попадают в меню', () => {
    const rows = pickRows([...items, { id: 'e', at: 999, hidden: true }], { excludeId: 'b' });
    expect(rows.map(r => r.id)).toEqual(['d', 'c', 'a']);
  });

  it('отметка «правили последней» — ровно у lastId', () => {
    const rows = pickRows(items, { lastId: 'c' });
    expect(rows.filter(r => r.last).map(r => r.id)).toEqual(['c']);
    expect(pickRows(items).some(r => r.last)).toBe(false);
  });

  it('потолок отрезает самые старые', () => {
    expect(pickRows(items, { limit: 2 }).map(r => r.id)).toEqual(['d', 'b']);
    expect(pickRows(items, { limit: 0 })).toEqual([]);
  });

  it('повтор id схлопывается в самую свежую запись', () => {
    const rows = pickRows([{ id: 'a', at: 100, n: 1 }, { id: 'a', at: 500, n: 2 }, { id: 'b', at: 300, n: 3 }]);
    expect(rows.map(r => [r.id, r.n])).toEqual([['a', 2], ['b', 3]]);
  });

  it('исходный массив не меняется', () => {
    const src = [{ id: 'x', at: 1 }, { id: 'y', at: 2 }];
    pickRows(src, { lastId: 'x' });
    expect(src).toEqual([{ id: 'x', at: 1 }, { id: 'y', at: 2 }]);
  });
});

describe('GenerationPickMenu', () => {
  const base = { title: 'Что править?', subtitle: 'Картинки этого чата, свежие сверху', onPick: noop, onClose: noop, emptyText: 'В этом чате пока нет картинок', emptyHint: 'Прикрепите через «＋»' };

  it('пустое меню: строка «пока нет», доп. пункты и подвал остаются, разделитель только перед ними', () => {
    const out = html(createElement(GenerationPickMenu, {
      ...base, rows: [],
      extras: [{ key: 'concat', label: 'Склеить несколько…', disabled: true, onClick: noop }],
      footer: 'Или «Работать с этой» на карточке в ленте',
    }));
    expect(out).toContain('В этом чате пока нет картинок');
    expect(out).toContain('Прикрепите через «＋»');
    expect(out).toContain('Склеить несколько…');
    expect(out).toContain('disabled=""');
    expect(out).toContain('Или «Работать с этой» на карточке в ленте');
    expect(out.match(/<button/g)).toHaveLength(1);
  });

  it('пустое меню без доп. пунктов — без разделителя и кнопок', () => {
    const out = html(createElement(GenerationPickMenu, { ...base, rows: [] }));
    expect(out).toContain('В этом чате пока нет картинок');
    expect(out).not.toContain('<button');
    expect(out).not.toContain('margin:4px 6px');
  });

  it('строки по порядку, миниатюра, отметка во второй строке и акцентная кромка', () => {
    const out = html(createElement(GenerationPickMenu, {
      ...base,
      rows: [
        { id: 'hero', name: 'hero.png', sub: 'версия 3', thumb: '/t/hero.png', mark: 'правили последней' },
        { id: 'logo', name: 'logo.png', sub: 'версия 1', thumb: '/t/logo.png' },
      ],
    }));
    expect(out).not.toContain('пока нет');
    expect(out.indexOf('hero.png')).toBeLessThan(out.indexOf('logo.png'));
    expect(out).toContain('версия 3 · правили последней');
    expect(out.split('inset 3px 0 0').length - 1).toBe(1);
    expect(out).toContain('src="/t/hero.png"');
    expect(out).toContain('width:26px;height:26px');
  });

  it('плитка 26×26 на bg-inset у всех строк — с миниатюрой, без неё и у доп. пунктов; кромка прямая; мета переносится', () => {
    const out = html(createElement(GenerationPickMenu, {
      ...base,
      rows: [
        { id: 'hero', name: 'hero.png', thumb: '/t/hero.png' },
        { id: 'logo', name: 'logo.png', sub: 'версия 2', icon: createElement('i'), mark: 'правили последней' },
      ],
      extras: [{ key: 'up', label: 'С компьютера…', icon: createElement('i'), onClick: noop }],
    }));
    expect(out.match(/width:26px;height:26px;flex-shrink:0;[^"]*background:var\(--c-bg-inset\)/g)).toHaveLength(3);
    expect(out).toMatch(/box-shadow:inset 3px 0 0 var\(--c-accent\)"/);
    expect(out).toContain('overflow-wrap:anywhere');
  });

  it('якорь — левый край сегмента, меню над ним', () => {
    vi.stubGlobal('document', { body: null });
    const out = html(createElement(GenerationPickMenu, { ...base, rows: [], anchor: { top: 700, bottom: 740, left: 90, right: 150 } as DOMRect }));
    vi.stubGlobal('document', undefined);
    const card = out.slice(out.indexOf('<div', out.indexOf('<div') + 1));
    expect(card).toContain('left:90px');
    expect(card).toContain('bottom:206px');
  });
});

describe('createReleaseUndo — таймер плашки «Вернуть»', () => {
  afterEach(() => { vi.useRealTimers(); });

  it('снятие человеком — плашка живёт 4 с и гаснет сама', () => {
    vi.useFakeTimers();
    const ctl = createReleaseUndo<string>();
    const seen: unknown[] = [];
    ctl.subscribe(() => seen.push(ctl.current()?.snapshot ?? null));
    ctl.release({ snapshot: 'hero', text: 'Картинка снята' }, true);
    expect(ctl.current()?.text).toBe('Картинка снята');
    vi.advanceTimersByTime(RELEASE_UNDO_MS - 1);
    expect(ctl.current()).not.toBeNull();
    vi.advanceTimersByTime(1);
    expect(ctl.current()).toBeNull();
    expect(seen).toEqual(['hero', null]);
    expect(RELEASE_UNDO_MS).toBe(4000);
  });

  it('снятие агентом плашку не показывает и гасит прежнюю', () => {
    vi.useFakeTimers();
    const ctl = createReleaseUndo<string>();
    ctl.release({ snapshot: 'a', text: 't' }, false);
    expect(ctl.current()).toBeNull();
    ctl.release({ snapshot: 'a', text: 't' }, true);
    ctl.release({ snapshot: 'b', text: 't' }, false);
    expect(ctl.current()).toBeNull();
  });

  it('«Вернуть» отдаёт снимок один раз и гасит плашку', () => {
    vi.useFakeTimers();
    const ctl = createReleaseUndo<string>();
    ctl.release({ snapshot: 'hero', text: 't' }, true);
    expect(ctl.undo()).toBe('hero');
    expect(ctl.current()).toBeNull();
    expect(ctl.undo()).toBeNull();
  });

  it('повторное снятие перезапускает отсчёт с новым снимком', () => {
    vi.useFakeTimers();
    const ctl = createReleaseUndo<string>();
    ctl.release({ snapshot: 'a', text: 't' }, true);
    vi.advanceTimersByTime(3000);
    ctl.release({ snapshot: 'b', text: 't' }, true);
    vi.advanceTimersByTime(3000);
    expect(ctl.current()?.snapshot).toBe('b');
    vi.advanceTimersByTime(1000);
    expect(ctl.current()).toBeNull();
  });

  it('после «Вернуть» и dispose таймер больше не будит подписчиков', () => {
    vi.useFakeTimers();
    const ctl = createReleaseUndo<string>();
    const fn = vi.fn();
    ctl.subscribe(fn);
    ctl.release({ snapshot: 'a', text: 't' }, true);
    ctl.dispose();
    vi.advanceTimersByTime(RELEASE_UNDO_MS * 2);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});

describe('GenerationModeSwitch — клик по сегменту', () => {
  const options = [
    { value: 'create' as const, label: 'Создать', icon: Sparkles },
    { value: 'edit' as const, label: 'Править', icon: Pencil, muted: true, title: 'Править — сначала выберите картинку' },
  ];
  const rect = { x: 1 } as unknown as DOMRect;

  it('приглушённый сегмент открывает меню с якорем вместо смены режима', () => {
    const onChange = vi.fn();
    const onMutedClick = vi.fn();
    handleModeClick({ options, value: 'edit', onChange, onMutedClick, anchor: () => rect });
    expect(onMutedClick).toHaveBeenCalledWith('edit', rect);
    expect(onChange).not.toHaveBeenCalled();
  });

  it('обычный сегмент меняет режим, якорь не считается', () => {
    const onChange = vi.fn();
    const onMutedClick = vi.fn();
    const anchor = vi.fn(() => rect);
    handleModeClick({ options, value: 'create', onChange, onMutedClick, anchor });
    expect(onChange).toHaveBeenCalledWith('create');
    expect(onMutedClick).not.toHaveBeenCalled();
    expect(anchor).not.toHaveBeenCalled();
  });

  it('без onMutedClick приглушённый сегмент меняет режим как обычно', () => {
    const onChange = vi.fn();
    handleModeClick({ options, value: 'edit', onChange, anchor: () => rect });
    expect(onChange).toHaveBeenCalledWith('edit');
  });

  it('широкий вид — подписи с иконками, узкий — только иконки с подсказками', () => {
    const wide = html(createElement(GenerationModeSwitch<'create' | 'edit'>, { value: 'create', options, onChange: noop }));
    expect(wide).toContain('>Создать<');
    expect(wide).toContain('>Править<');
    expect(wide).toContain('dashed');
    const narrow = html(createElement(GenerationModeSwitch<'create' | 'edit'>, { value: 'create', options, onChange: noop, compact: true }));
    expect(narrow).not.toContain('>Создать<');
    expect(narrow).toContain('title="Создать"');
    expect(narrow).toContain('title="Править — сначала выберите картинку"');
  });

  it('на телефоне сегменты 40×40, вне телефона — прежние 28×20', () => {
    const phone = html(createElement(GenerationModeSwitch<'create' | 'edit'>, { value: 'create', options, onChange: noop, compact: true, isMobile: true }));
    expect(phone.match(/<button[^>]*width:40px;height:40px/g)).toHaveLength(2);
    const desk = html(createElement(GenerationModeSwitch<'create' | 'edit'>, { value: 'create', options, onChange: noop, compact: true }));
    expect(desk.match(/<button[^>]*width:28px;height:20px/g)).toHaveLength(2);
  });
});

describe('ExecutorList — группы и строки', () => {
  const rows = [
    { id: 'fal-kontext', group: 'cloud' as const, name: 'fal · FLUX Kontext', price: '$0.04 / шт.' },
    { id: 'qwen-edit', group: 'local' as const, name: 'Qwen-Image Edit', sub: 'правка по 1–16 образцам', price: 'бесплатно · ~50 с' },
    { id: 'auto', group: 'auto' as const, name: 'Авто', price: 'бесплатно', badges: [{ label: 'сейчас Qwen', tone: 'success' as const }] },
    { id: 'soul', group: 'cloud' as const, name: 'Higgsfield · Soul', sub: 'фото людей', price: '2 кр. / шт.', disabled: true, reason: 'не умеет «Изменить» — только новая картинка' },
  ];

  it('порядок групп Авто → своя видеокарта → облако, пустые пропускаются', () => {
    expect(groupExecutorRows(rows).map(g => [g.group, g.rows.map(r => r.id)])).toEqual([
      ['auto', ['auto']], ['local', ['qwen-edit']], ['cloud', ['fal-kontext', 'soul']],
    ]);
    expect(groupExecutorRows(rows.filter(r => r.group === 'cloud')).map(g => g.group)).toEqual(['cloud']);
  });

  it('радио-строки: выбранная отмечена, серая — с причиной вместо подписи и без цены', () => {
    const out = html(createElement(ExecutorList, { rows, value: 'qwen-edit', onChange: noop }));
    expect(out).toContain('Бесплатно на своей видеокарте');
    expect(out).toContain('>Облако<');
    expect(out.match(/aria-checked="true"/g)).toHaveLength(1);
    const tags = out.match(/<button[^>]*>/g) ?? [];
    expect(tags).toHaveLength(4);
    expect(tags[3]).toContain('disabled=""');
    expect(tags[3]).toContain('title="не умеет «Изменить» — только новая картинка"');
    expect(tags.slice(0, 3).some(t => t.includes('disabled'))).toBe(false);
    expect(out).toContain('не умеет «Изменить» — только новая картинка');
    expect(out).not.toContain('фото людей');
    expect(out).not.toContain('2 кр. / шт.');
    expect(out).toContain('сейчас Qwen');
  });

  it('замок перед именем — только у закрытой строки', () => {
    const out = html(createElement(ExecutorList, { rows: [...rows.slice(0, 3), { ...rows[3], locked: true }], value: 'auto', onChange: noop }));
    expect(out.match(/data-executor-lock/g)).toHaveLength(1);
    expect(out.indexOf('data-executor-lock')).toBeGreaterThan(out.indexOf('fal · FLUX Kontext'));
    expect(out.indexOf('data-executor-lock')).toBeLessThan(out.indexOf('Higgsfield · Soul'));
    expect(html(createElement(ExecutorList, { rows, value: 'auto', onChange: noop }))).not.toContain('data-executor-lock');
  });

  it('подпись и причина переносятся, а не режутся многоточием', () => {
    const out = html(createElement(ExecutorList, { rows, value: 'auto', onChange: noop }));
    const reason = out.match(/<span style="([^"]*)">не умеет/)![1];
    expect(reason).toContain('overflow-wrap:anywhere');
    expect(reason).not.toContain('nowrap');
  });

  it('сводка «Чем: Авто · локально · модель · цена»', () => {
    const out = html(createElement(ExecutorSummaryRow, { name: 'Авто', parts: ['локально', 'Qwen-Image Edit'], price: { label: 'бесплатно', tone: 'success' }, open: false, onToggle: noop }));
    expect(out).toContain('>Чем<');
    expect(out).toContain('Авто</b> · локально · Qwen-Image Edit');
    expect(out).toContain('бесплатно');
    expect(out).toContain('aria-expanded="false"');
  });
});

describe('ReleaseNotice', () => {
  it('плашка info с текстом и кнопкой «Вернуть» справа', () => {
    const out = html(createElement(ReleaseNotice, { text: 'Картинка снята — дальше рисуем новую', onUndo: noop }));
    expect(out).toContain('var(--c-info-bg)');
    expect(out).toContain('Картинка снята — дальше рисуем новую');
    expect(out).toContain('>Вернуть<');
    expect(out).toContain('flex:1');
  });

  it('на телефоне «Вернуть» высотой от 40, на широком — компактная', () => {
    const btn = (isMobile: boolean) => html(createElement(ReleaseNotice, { text: 'т', onUndo: noop, isMobile })).match(/<button[^>]*>/)![0];
    expect(btn(true)).toContain('min-height:40px');
    expect(btn(false)).toContain('height:24px');
  });
});
