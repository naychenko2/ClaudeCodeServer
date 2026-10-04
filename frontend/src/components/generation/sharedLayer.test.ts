import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: окно с matchMedia для useIsMobile (Badge), портал меню — на месте
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

const { createReleaseUndo, RELEASE_UNDO_MS } = await import('./useReleaseUndo');
const { groupExecutorRows, ExecutorList, ExecutorSummaryRow } = await import('./ExecutorList');
const { ReleaseNotice, setFabRaise, FAB_RAISE_VAR } = await import('./ReleaseNotice');

const html = (el: Parameters<typeof renderToStaticMarkup>[0]) => renderToStaticMarkup(el);
const noop = () => {};

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

describe('подъём круга AI над плашками «Вернуть»', () => {
  const fakeRoot = () => {
    const props = new Map<string, string>();
    const root = { style: {
      setProperty: (k: string, v: string) => { props.set(k, v); },
      removeProperty: (k: string) => { props.delete(k); },
    } } as unknown as HTMLElement;
    return { root, get: () => props.get(FAB_RAISE_VAR) };
  };

  it('уход одной из двух плашек не сбрасывает подъём, пока видна вторая', () => {
    const { root, get } = fakeRoot();
    const a = Symbol('a'), b = Symbol('b');
    setFabRaise(a, 40, root);
    setFabRaise(b, 60, root);
    expect(get()).toBe('60px');
    setFabRaise(b, null, root);
    expect(get()).toBe('40px');
    setFabRaise(a, null, root);
    expect(get()).toBeUndefined();
  });

  it('подъём — максимум из живых плашек, а не последняя записанная', () => {
    const { root, get } = fakeRoot();
    const a = Symbol('a'), b = Symbol('b');
    setFabRaise(a, 80, root);
    setFabRaise(b, 40, root);
    expect(get()).toBe('80px');
    setFabRaise(a, 50, root);
    expect(get()).toBe('50px');
    setFabRaise(a, null, root);
    setFabRaise(b, null, root);
    expect(get()).toBeUndefined();
  });
});
