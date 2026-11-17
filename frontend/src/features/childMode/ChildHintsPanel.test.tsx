// KM-F7: содержимое панели «Как мы учимся» — состояния лесенки и сигнал «Мне неприятно».
// Окружение — node + linkedom, как в ChildHome.test: проверяются состояния ступеней
// (data-state + цвета маркеров из токенов), порядок и вызовы childApi. Мобильная ветка —
// переключателем matchMedia, тач-цель — по inline min-height.
// «Необработанных rejection нет» не слушается отдельно (как в соседних .test.tsx волны):
// обработчик сигнала весь в try/catch, а неохваченный rejection сам краснит прогон vitest.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile читает window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// childApi: ответ по индексу вызова: нет записи — promise держится in-flight (на этом
// проверяются гонка ответа и блокировка кнопки); Error — отказ с status/body, как его
// выносит request() из lib/offline.
let calls: (string | undefined)[] = [];
let answers: (Error | object)[] = [];
let pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let callIdx = 0;

vi.mock('./api', () => ({
  childApi: {
    stopSignal: (sessionId?: string) => {
      const i = callIdx++;
      calls.push(sessionId);
      const a = answers[i];
      if (a instanceof Error) return Promise.reject(a);
      if (a) return Promise.resolve(a);
      return new Promise((res, rej) => { pending.set(i, { resolve: res, reject: rej }); });
    },
  },
}));

const { ChildHintsPanel } = await import('./ChildHintsPanel');

// Дословный текст info-плашки из макета kids-shell-v2.html (panelBody).
const SENT_TEXT = 'Папе пришёл сигнал, что нужна помощь. Ваш разговор он не читает. Ты ни в чём не виновата.';
const RETRY_TEXT = 'Не получилось отправить, попробуй ещё раз';

const httpErr = (status: number, code?: string) =>
  Object.assign(new Error('отказ'), { status, body: code ? { code } : {} });

async function mount(step: number, sessionId = 'chat-1') {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(ChildHintsPanel, { step, sessionId })); });
  return {
    host: host as HTMLElement,
    // Тот же смонтированный экземпляр с новым sessionId: на этом проверяется
    // «сигнал отправлен — по sessionId», а не сброс перемонтированием.
    async rerender(nextStep: number, nextSessionId: string) {
      await act(async () => { root.render(createElement(ChildHintsPanel, { step: nextStep, sessionId: nextSessionId })); });
    },
  };
}

const steps = (host: HTMLElement) => Array.from(host.querySelectorAll('[data-testid="hint-step"]')) as HTMLElement[];
const stateOf = (host: HTMLElement) => steps(host).map((s) => s.getAttribute('data-state'));
const markerOf = (stepEl: HTMLElement) => stepEl.firstElementChild as HTMLElement;
const signalButton = (host: HTMLElement) => host.querySelector('[data-testid="stop-signal"]') as HTMLButtonElement | null;

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
}

// Ответ + выгнать цепочку then/catch обработчика (depth hops микротасков с запасом).
async function settle(fn: () => void) {
  await act(async () => {
    fn();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

beforeEach(() => {
  calls = []; answers = []; pending = new Map(); callIdx = 0;
  mobile = false;
});

describe('ChildHintsPanel: лесенка', () => {
  it('ступени идут 1→2→3 сверху вниз: «Намёк», «Похожий пример», «Один шаг вместе»', async () => {
    const { host } = await mount(0);
    // textContent ступени включает номер маркера — он же проверяет нумерацию 1..3
    expect(steps(host).map((s) => (s.textContent ?? '').trim())).toEqual(['1Намёк', '2Похожий пример', '3Один шаг вместе']);
  });

  it('step=1: одна пройденная (successBg), одна текущая (info), одна будущая (приглушённая)', async () => {
    const { host } = await mount(1);
    expect(stateOf(host)).toEqual(['done', 'cur', 'future']);
    const [done, cur, future] = steps(host);
    // Цвета — токены из макета: пройденный маркер на successBg, текущая строка на infoBg
    // с маркером на info, будущий — bgInset/textMuted.
    expect(markerOf(done).style.background).toBe('var(--c-success-bg)');
    expect(markerOf(done).style.color).toBe('var(--c-success-text)');
    expect(cur.style.background).toBe('var(--c-info-bg)');
    expect(markerOf(cur).style.background).toBe('var(--c-info)');
    expect(markerOf(cur).style.color).toBe('var(--c-on-accent)');
    expect(markerOf(future).style.background).toBe('var(--c-bg-inset)');
    expect(markerOf(future).style.color).toBe('var(--c-text-muted)');
    // Прошлая ступень — галка, будущие — номера 1..3
    expect(done.querySelector('svg')).toBeTruthy();
    expect(markerOf(cur).textContent).toBe('2');
    expect(markerOf(future).textContent).toBe('3');
  });

  it('step=0 — текущая первая, пройденных нет; step=3 — все пройдены, текущей нет', async () => {
    const zero = await mount(0);
    expect(stateOf(zero.host)).toEqual(['cur', 'future', 'future']);
    const all = await mount(3);
    expect(stateOf(all.host)).toEqual(['done', 'done', 'done']);
  });
});

describe('ChildHintsPanel: сигнал «Мне неприятно»', () => {
  it('202: вместо кнопки — info-плашка дословно из макета; необработанных rejection нет', async () => {
    const { host } = await mount(1);
    await click(signalButton(host)!);
    expect(calls).toEqual(['chat-1']);
    await settle(() => pending.get(0)!.resolve({ accepted: true }));
    expect(host.innerHTML).toContain(SENT_TEXT);
    expect(signalButton(host)).toBeNull();
  });

  it('429 too_frequent: то же состояние, что при 202 — сигнал уже ушёл', async () => {
    const { host } = await mount(1);
    await click(signalButton(host)!);
    await settle(() => pending.get(0)!.reject(httpErr(429, 'too_frequent')));
    expect(host.innerHTML).toContain(SENT_TEXT);
    expect(signalButton(host)).toBeNull();
  });

  it('double click sends one signal: после первого клика кнопка заблокирована до ответа', async () => {
    const { host } = await mount(1);
    const btn = signalButton(host)!;
    await click(btn);
    expect(btn.disabled).toBe(true); // блокировка до ответа — механизм контракта
    await click(btn);
    expect(calls.length).toBe(1);
    await settle(() => pending.get(0)!.resolve({ accepted: true }));
    expect(host.innerHTML).toContain(SENT_TEXT);
  });

  it('отказ 500: warning-плашка и кнопка снова доступна; повторный клик шлёт второй сигнал', async () => {
    const { host } = await mount(1);
    await click(signalButton(host)!);
    await settle(() => pending.get(0)!.reject(httpErr(500)));
    expect(host.innerHTML).toContain(RETRY_TEXT);
    expect(host.innerHTML).not.toContain(SENT_TEXT);
    const btn = signalButton(host)!;
    expect(btn.disabled).toBe(false);
    await click(btn);
    expect(calls.length).toBe(2);
  });

  it('сигнал отправлен — по sessionId: смена чата до ответа — info-плашки нет и поздний ответ её не поднимает', async () => {
    const { host, rerender } = await mount(1, 'chat-1');
    await click(signalButton(host)!);
    await rerender(1, 'chat-2');
    expect(host.innerHTML).not.toContain(SENT_TEXT);
    await settle(() => pending.get(0)!.resolve({ accepted: true }));
    expect(host.innerHTML).not.toContain(SENT_TEXT); // поздний ответ старого чата не показан
    expect(signalButton(host)).toBeTruthy(); // новый чат — со своей кнопкой
  });

  it('сигнал отправлен — по sessionId: смена чата после ответа — плашка старого чата не показана', async () => {
    const { host, rerender } = await mount(1, 'chat-1');
    await click(signalButton(host)!);
    await settle(() => pending.get(0)!.resolve({ accepted: true }));
    expect(host.innerHTML).toContain(SENT_TEXT);
    await rerender(1, 'chat-2');
    expect(host.innerHTML).not.toContain(SENT_TEXT);
    expect(signalButton(host)).toBeTruthy();
  });
});

describe('ChildHintsPanel: мобила (360)', () => {
  it('тач-цель «Мне неприятно» ≥ 40px', async () => {
    mobile = true;
    const { host } = await mount(1);
    const btn = signalButton(host)!;
    expect(Number.parseFloat(btn.style.minHeight), 'тач-цель «Мне неприятно»').toBeGreaterThanOrEqual(40);
  });
});
