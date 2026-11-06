// ComponentsPage (S2c-3): состояния панели — скелетон при загрузке, пустой список
// при видимой группе ядра, ошибка загрузки с «Повторить», запись (тумблер disabled,
// «Записываю…»), ошибка записи в строке с «Повторить», «ждёт перезапуска» с «Вернуть»,
// «выключен сейчас» без него, подтверждение при выключении используемого и его
// отсутствие при неиспользуемом, отсутствие тумблера у строк ядра.
//
// DOM — linkedom (паттерн tasksPanelRender.test.ts): настоящий Modal рендерится
// порталом в body, клики — настоящие. API мокнут: форма запроса/ответа — предмет
// lib/__tests__/subsystems.contract.test.ts, здесь — поведение экрана на данных.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Subsystem } from '../../../api/subsystems';

const m = vi.hoisted(() => ({
  get: vi.fn<(...a: unknown[]) => Promise<unknown>>(),
  put: vi.fn<(...a: unknown[]) => Promise<unknown>>(),
}));

vi.mock('../../../api/subsystems', () => ({
  subsystemsApi: { get: (...a: unknown[]) => m.get(...a), put: (...a: unknown[]) => m.put(...a) },
}));

// ── DOM: linkedom + недостающие браузерные API (matchMedia — десктоп) ──
const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  requestAnimationFrame: (cb: () => void) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: модули читают document при загрузке
// (вставка keyframes скелета в ComponentsPage).
const { ComponentsPage } = await import('../../../pages/ComponentsPage');

// ── поиск по дереву (обход свой: не зависим от поддержки селекторов linkedom) ──
type El = {
  nodeName: string;
  textContent?: string | null;
  children: El[];
  getAttribute: (n: string) => string | null;
  click: () => void;
};

function walk(n: El, fn: (el: El) => void) {
  for (const c of n.children) { fn(c); walk(c, fn); }
}
function byTestId(id: string): El | null {
  let hit: El | null = null;
  walk(doc.body as unknown as El, el => { if (!hit && el.getAttribute('data-testid') === id) hit = el; });
  return hit;
}
function byAttr(attr: string, value: string): El | null {
  let hit: El | null = null;
  walk(doc.body as unknown as El, el => { if (!hit && el.getAttribute(attr) === value) hit = el; });
  return hit;
}
function countByAttr(attr: string, value: string): number {
  let n = 0;
  walk(doc.body as unknown as El, el => { if (el.getAttribute(attr) === value) n += 1; });
  return n;
}
function buttonByText(text: string): El | null {
  let hit: El | null = null;
  walk(doc.body as unknown as El, el => {
    if (!hit && el.nodeName === 'BUTTON' && (el.textContent ?? '').trim() === text) hit = el;
  });
  return hit;
}
const bodyText = () => doc.body.textContent ?? '';

// ── данные: строка такой, какой её отдаёт бэк (record целиком, null-значения сериализованы) ──
function sys(key: string, over: Partial<Subsystem> = {}): Subsystem {
  return {
    key, title: `Раздел ${key}`, description: `Описание ${key}`,
    enabled: true, active: true, restartRequired: false, canDisable: true,
    lastUsedAt: null, usedLast24h: null,
    ...over,
  };
}

function httpError(status: number, body: unknown): Error & { status: number; body: unknown } {
  const e = new Error('Service Unavailable') as Error & { status: number; body: unknown };
  e.status = status;
  e.body = body;
  return e;
}

function deferred(): { promise: Promise<unknown>; resolve: (v: unknown) => void } {
  let resolve!: (v: unknown) => void;
  const promise = new Promise<unknown>(r => { resolve = r; });
  return { promise, resolve };
}

let root: Root | null = null;

function mount() {
  root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  act(() => { root!.render(createElement(ComponentsPage, { onClose: () => {} })); });
}

// Прогнать промис-цепочки до состояния React (ответ get/put → setState → рендер).
async function flush(times = 2) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

const switchFor = (title: string) => byAttr('aria-label', `Компонент ${title}`);

beforeEach(() => {
  m.get.mockReset();
  m.put.mockReset();
});

afterEach(async () => {
  if (root) { const r = root; root = null; await act(async () => { r.unmount(); }); }
});

describe('ComponentsPage', () => {
  it('загрузка: скелетон строк, пока списка нет; пришёл — строки вместо скелетона', async () => {
    const d = deferred();
    m.get.mockReturnValue(d.promise);
    mount();
    expect(byTestId('components-skeleton')).toBeTruthy();
    expect(byTestId('components-row-notes')).toBeNull();

    d.resolve([sys('notes')]);
    await flush();
    expect(byTestId('components-skeleton')).toBeNull();
    expect(byTestId('components-row-notes')).toBeTruthy();
  });

  it('пусто: «Отключаемых компонентов нет», и группа ядра всё равно видна', async () => {
    m.get.mockResolvedValue([sys('sessions', { canDisable: false })]);
    mount();
    await flush();
    expect(byTestId('components-empty')).toBeTruthy();
    expect(bodyText()).toContain('Отключаемых компонентов нет');
    expect(byTestId('components-core')).toBeTruthy();
    expect(bodyText()).toContain('Часть ядра');
  });

  it('ошибка загрузки: EmptyState с «Повторить»; клик перечитывает', async () => {
    m.get.mockRejectedValue(new Error('сервер не ответил'));
    mount();
    await flush();
    expect(byTestId('components-load-error')).toBeTruthy();
    expect(bodyText()).toContain('Не удалось загрузить список');

    m.get.mockResolvedValue([sys('notes')]);
    const btn = buttonByText('Повторить');
    expect(btn).toBeTruthy();
    await act(async () => { btn!.click(); });
    expect(m.get).toHaveBeenCalledTimes(2);
    expect(byTestId('components-row-notes')).toBeTruthy();
  });

  it('запись: тумблер disabled + «Записываю…»; строка меняется целым ответом PUT', async () => {
    m.get.mockResolvedValue([sys('notes', { usedLast24h: 0 })]);
    mount();
    await flush();
    const sw = switchFor('Раздел notes');
    expect(sw).toBeTruthy();
    // До клика тумблер живой (фокусируемый): отсутствие tabindex после клика —
    // и есть проверка disabled, а не совпадение текстов.
    expect(sw!.getAttribute('tabindex')).toBe('0');

    const d = deferred();
    m.put.mockReturnValue(d.promise);
    await act(async () => { sw!.click(); });
    expect(m.put).toHaveBeenCalledTimes(1);
    expect(m.put.mock.calls[0][0]).toBe('notes');
    expect(m.put.mock.calls[0][1]).toBe(false);
    expect(switchFor('Раздел notes')!.getAttribute('tabindex')).toBeNull();
    expect(bodyText()).toContain('Записываю…');

    d.resolve(sys('notes', { enabled: false, active: true, restartRequired: true, usedLast24h: 0 }));
    await flush();
    // Строка = ответ сервера: теперь это «ждёт перезапуска».
    expect(bodyText()).toContain('Выключится');
    expect(bodyText()).not.toContain('Записываю…');
  });

  it('ошибка записи в строке: причина как есть, «Положение не изменилось», «Повторить» повторяет запрос', async () => {
    m.get.mockResolvedValue([sys('notes', { usedLast24h: 0 })]);
    mount();
    await flush();
    m.put.mockRejectedValue(httpError(503, { code: 'write_failed', message: 'Нет прав на запись' }));
    await act(async () => { switchFor('Раздел notes')!.click(); });
    await flush();
    expect(bodyText()).toContain('Не удалось записать настройку');
    expect(bodyText()).toContain('Нет прав на запись');
    expect(bodyText()).toContain('Положение не изменилось');
    // Оптимистичного применения нет: тумблер остался в прежнем положении.
    expect(switchFor('Раздел notes')!.getAttribute('aria-checked')).toBe('true');

    m.put.mockResolvedValue(sys('notes', { enabled: false, active: true, restartRequired: true, usedLast24h: 0 }));
    const retry = buttonByText('Повторить');
    expect(retry).toBeTruthy();
    await act(async () => { retry!.click(); });
    expect(m.put).toHaveBeenCalledTimes(2);
    expect(m.put.mock.calls[1][1]).toBe(false);
    await flush();
    expect(bodyText()).not.toContain('Не удалось записать настройку');
  });

  it('«ждёт перезапуска» (выключен, но ещё работает): бейдж, строка и «Вернуть»; «Вернуть» = обратный toggle', async () => {
    m.get.mockResolvedValue([sys('audio', { enabled: false, active: true, restartRequired: true, usedLast24h: 5 })]);
    mount();
    await flush();
    expect(byTestId('components-banner')).toBeTruthy();
    expect(bodyText()).toContain('1 изменение ждёт перезапуска сервера');
    expect(bodyText()).toContain('«Раздел audio» выключится');
    expect(bodyText()).toContain('Выключится');
    expect(bodyText()).toContain('Вступит в силу после перезапуска');
    expect(bodyText()).toContain('сейчас ещё работает');

    m.put.mockResolvedValue(sys('audio', { enabled: true, active: true, usedLast24h: 5 }));
    const revert = buttonByText('Вернуть');
    expect(revert).toBeTruthy();
    await act(async () => { revert!.click(); });
    expect(m.put).toHaveBeenCalledWith('audio', true);
    await flush();
    // Ответ вернул совпадение enabled/active — расхождение и плашка гасятся.
    expect(byTestId('components-banner')).toBeNull();
    expect(bodyText()).not.toContain('Вернуть');
  });

  it('«ждёт перезапуска» (включён, но ещё не поднят): «Включится» и «сейчас выключен»', async () => {
    m.get.mockResolvedValue([sys('video', { enabled: true, active: false, restartRequired: true })]);
    mount();
    await flush();
    expect(bodyText()).toContain('Включится');
    expect(bodyText()).toContain('сейчас выключен');
    expect(bodyText()).toContain('1 изменение ждёт перезапуска сервера');
  });

  it('«выключен сейчас» (совпадение): нейтральный бейдж без ↻, без «Вернуть», без плашки', async () => {
    m.get.mockResolvedValue([sys('video', { enabled: false, active: false, usedLast24h: 0 })]);
    mount();
    await flush();
    expect(bodyText()).toContain('Выключен');
    expect(bodyText()).not.toContain('Вступит в силу');
    expect(bodyText()).not.toContain('Вернуть');
    expect(byTestId('components-banner')).toBeNull();
    // Использование без данных: «—» и «нет данных с запуска», а не «0».
    expect(bodyText()).toContain('нет данных с запуска');
  });

  it('подтверждение: выключение работающего с 37 обращениями — диалог с фактом и про диск; без него — нет записи', async () => {
    // 12 минут + секунда: формат «12 мин назад» не зависит от миллисекунд прогона.
    const last = new Date(Date.now() - 12 * 60000 - 1000).toISOString();
    m.get.mockResolvedValue([sys('audio', { usedLast24h: 37, lastUsedAt: last })]);
    mount();
    await flush();
    await act(async () => { switchFor('Раздел audio')!.click(); });
    // Диалог — до всякой записи: источник правды не тронут, пока человек не решил.
    expect(m.put).not.toHaveBeenCalled();
    expect(bodyText()).toContain('Выключить «Раздел audio»?');
    expect(bodyText()).toContain('37 обращений за сутки');
    expect(bodyText()).toContain('12 мин назад');
    expect(bodyText()).toContain('Файлы на диске останутся');

    m.put.mockResolvedValue(sys('audio', { enabled: false, active: true, restartRequired: true, usedLast24h: 37, lastUsedAt: last }));
    const ok = buttonByText('Выключить');
    expect(ok).toBeTruthy();
    await act(async () => { ok!.click(); });
    expect(m.put).toHaveBeenCalledTimes(1);
    expect(m.put.mock.calls[0][1]).toBe(false);
    await flush();
    expect(bodyText()).not.toContain('Файлы на диске останутся');
    expect(bodyText()).toContain('Выключится');
  });

  it('без подтверждения: выключение неиспользуемого (0 и null) проходит сразу, диалога нет', async () => {
    m.get.mockResolvedValue([
      sys('a', { usedLast24h: 0, lastUsedAt: new Date(Date.now() - 3600000).toISOString() }),
      sys('b', { usedLast24h: null }),
    ]);
    // Ответ записи — целая строка с записанным enabled: без стаба put вернул бы
    // undefined, и строка в items стала бы undefined.
    m.put.mockImplementation((key: unknown, enabled: unknown) =>
      Promise.resolve(sys(String(key), { enabled: Boolean(enabled), restartRequired: true, usedLast24h: 0 })));
    mount();
    await flush();
    await act(async () => { switchFor('Раздел a')!.click(); });
    expect(m.put).toHaveBeenCalledTimes(1);
    expect(m.put.mock.calls[0][0]).toBe('a');
    expect(bodyText()).not.toContain('Им пользовались');
    await act(async () => { switchFor('Раздел b')!.click(); });
    expect(m.put).toHaveBeenCalledTimes(2);
    expect(m.put.mock.calls[1][0]).toBe('b');
    expect(bodyText()).not.toContain('Им пользовались');
  });

  it('ядро: тумблера нет совсем (switch только у отключаемых), группа «Ядро» с замком', async () => {
    m.get.mockResolvedValue([
      sys('notes'),
      sys('sessions', { canDisable: false }),
      sys('projects', { canDisable: false }),
    ]);
    mount();
    await flush();
    // Ровно один switch на весь экран — у отключаемой строки; строкам ядра его не ставят.
    expect(countByAttr('role', 'switch')).toBe(1);
    expect(byTestId('components-core')).toBeTruthy();
    expect(bodyText()).toContain('Ядро · 2 · работает всегда');
    expect(bodyText()).toContain('Часть ядра');
  });
});
