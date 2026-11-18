// Подключение вкладки «Встречи» к панели (Ф2): видимость вкладки (админ/не-админ),
// запоминание выбора (cc_local_model_tab), точка на вкладке по level из снимка,
// «узкая лента» (<340 px — неактивные вкладки только иконкой).
//
// Плюс Ф6: точка на «Стендах» (и то, что она видна без открытия вкладки — находка
// ревью Р-Ф2д: ферма опрашивается, пока смонтирована панель), и то, что доступное
// имя панели берётся из реестра.
//
// Доработка Ф2/Ф6 (дефекты №1/№3 приёмки Веры): точка тревоги на «Встречах» и на
// «Стендах» видна, не открывая вкладку (снимок опрашивается, пока панель смонтирована,
// и прекращается при закрытии панели, а не при уходе с вкладки); не-админ с сохранённой
// 'transcription' получает рабочую «Медиа», а не сломанную панель. Это поведение
// проверяется МОНТИРОВАНИЕМ панели (node + linkedom, паттерн ChildHintsPanel.test):
// мокнуты только опросы (их эндпоинты сетевые/админские), сама панель, переключатель
// и связка «снимок → точка» — настоящие.
//
// Остальное — окружение node: чистые функции прямыми вызовами, переключатель —
// renderToStaticMarkup (паттерн hardware.test.ts), имя панели — сторожем по исходнику.

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createRoot } from 'react-dom/client';
import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';
import type { WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import type { HardwareFarm } from './hardwareTypes';
import type { TranscriptionLevel, TranscriptionSnapshot } from './types';
import { loadTab, LocalModelPanel, resolveTab, transcriptionDot, TabSwitch } from './LocalModelPanel';

// === 1. Видимость вкладки: админ / не-админ / сохранённая transcription ===

describe('resolveTab — видимость вкладки «Встречи»', () => {
  it('админ: сохранённая transcription → transcription', () => {
    expect(resolveTab(true, 'transcription')).toBe('transcription');
  });
  it('не-админ + сохранённая transcription → media (дефект №3: не «text»)', () => {
    // 'text' тоже админский: /stats под [Authorize(Roles="admin")] → 403 →
    // «Не удалось получить данные» без выхода из состояния. Не-админ — всегда «Медиа».
    expect(resolveTab(false, 'transcription')).toBe('media');
  });
  it('не-админ + сохранённые hardware / text → media (тот же порядок)', () => {
    expect(resolveTab(false, 'hardware')).toBe('media');
    expect(resolveTab(false, 'text')).toBe('media');
    expect(resolveTab(false, 'media')).toBe('media');
  });
});

// === 2. Выбор вкладки помнится (тот же TAB_KEY, значение 'transcription') ===

describe('loadTab — запоминание вкладки «Встречи»', () => {
  afterEach(() => { delete (globalThis as { localStorage?: unknown }).localStorage; });
  it('cc_local_model_tab=transcription → transcription', () => {
    (globalThis as unknown as { localStorage: unknown }).localStorage = { getItem: () => 'transcription', setItem: () => {} };
    expect(loadTab()).toBe('transcription');
  });
  it('чужого значения нет → text (прежний дефолт)', () => {
    (globalThis as unknown as { localStorage: unknown }).localStorage = { getItem: () => 'nope', setItem: () => {} };
    expect(loadTab()).toBe('text');
  });
});

// === 3. Точка на вкладке: только показываем level из снимка (считает бэкенд) ===

function snap(level?: TranscriptionLevel): TranscriptionSnapshot {
  return { phase: 'idle', updatedAt: 1_763_000_000_000, ...(level ? { level } : {}) };
}

describe('transcriptionDot — точка по level снимка', () => {
  it('снимка нет / level ok / поля нет — точки нет', () => {
    expect(transcriptionDot(null)).toBeNull();
    expect(transcriptionDot(snap('ok'))).toBeNull();
    expect(transcriptionDot(snap())).toBeNull();
  });
  it('warn → warn (жёлтая), danger → danger (красная)', () => {
    expect(transcriptionDot(snap('warn'))).toBe('warn');
    expect(transcriptionDot(snap('danger'))).toBe('danger');
  });
});

// === 4. Переключатель: «Встречи» после «Стендов», точка, узкая лента ===

function switcherHtml(over: {
  value?: 'text' | 'media' | 'hardware' | 'transcription';
  dotLevel?: 'warn' | 'danger';
  transDotLevel?: TranscriptionLevel;
  narrow?: boolean;
} = {}): string {
  return renderToStaticMarkup(createElement(TabSwitch, {
    value: over.value ?? 'text',
    onChange: () => {},
    isMobile: false,
    ...(over.dotLevel ? { dotLevel: over.dotLevel } : {}),
    ...(over.transDotLevel ? { transDotLevel: over.transDotLevel } : {}),
    ...(over.narrow ? { narrow: true } : {}),
  }));
}

describe('TabSwitch — вкладка «Встречи»', () => {
  it('«Встречи» есть и идёт после «Стенды»', () => {
    const h = switcherHtml();
    expect(h).toContain('Встречи');
    expect(h.indexOf('Стенды')).toBeLessThan(h.indexOf('Встречи'));
  });
  it('danger — красная точка (C.danger); warn — жёлтая (C.warning); без уровня — точки нет', () => {
    expect(switcherHtml({ transDotLevel: 'danger' })).toContain('var(--c-danger)');
    expect(switcherHtml({ transDotLevel: 'warn' })).toContain('var(--c-warning)');
    // «Стенды» (dotLevel через tabIcon) — тот же контроль, что у «Встреч»: без него
    // точка этой вкладки вообще ничем не была покрыта (хвост ревью R-Ф2).
    expect(switcherHtml({ dotLevel: 'danger' })).toContain('var(--c-danger)');
    expect(switcherHtml({ dotLevel: 'warn' })).toContain('var(--c-warning)');
    expect(switcherHtml()).not.toContain('var(--c-danger)');
    expect(switcherHtml()).not.toContain('var(--c-warning)');
  });
  it('узкая лента (<340 px): неактивные — только иконка (aria-label + title), активная — с подписью', () => {
    const h = switcherHtml({ narrow: true });
    // Видимых подписей неактивных нет (текстовый узел «>Медиа<»), имя — в aria-label/title.
    expect(h).not.toContain('>Медиа<');
    expect(h).not.toContain('>Стенды<');
    expect(h).not.toContain('>Встречи<');
    expect(h).toContain('aria-label="Медиа"');
    expect(h).toContain('title="Встречи"');
    // Активная («LLM») остаётся с подписью.
    expect(h).toContain('>LLM<');
  });
});

// === 5. Доступное имя панели — из реестра, не литералом (хвост ревью R-Ф5) ===
// Сторожем по исходнику (паттерн §6 HardwareTab.test.ts): рендер самой панели
// потребовал бы всех её хуков и опросов, а проверяемое — форма атрибута. Литеральной
// строки имени в панели быть не должно: имя живёт в каталоге панелей, оттуда же
// берёт его шапка (PanelZone.tsx:128). Согласованность копий имени — в manifest.test.ts.
describe('доступное имя панели — из реестра', () => {
  it('aria-label читает мету каталога; литерального имени панели в исходнике нет', () => {
    const src = readFileSync(new URL('./LocalModelPanel.tsx', import.meta.url), 'utf8');
    expect(src).toContain('aria-label={panelMetaResolved(LOCAL_MODEL_PANEL).title}');
    expect(src).not.toMatch(/aria-label="/);
  });
});

// === 6. Поведение панели (монтирование, node + linkedom — паттерн ChildHintsPanel.test) ===
// Мокнуты эндпоинты и те опросы, что не несут проверяемого смысла: /stats, /media,
// /engines (админские к тому же проверяются моком «верно эндпоинту» — см.
// useLocalModelStats ниже). Панель, переключатель, resolveTab, useTranscription,
// useLocalModelFarm (настоящий хук, питается моком api) и связка «снимок → точка» —
// настоящие: на этом и ловятся дефекты №1/№3 и точка «Стендов» (Р-Ф2д).

const st = vi.hoisted(() => ({
  admin: true,
  // Снимок, который отдаёт эндпоинт: уровень подставляется по тесту (его считает
  // бэкенд — здесь только переносим).
  transLevel: null as 'ok' | 'warn' | 'danger' | null,
  // Счётчик обращений к эндпоинту снимка: на нём проверяется жизнь опроса.
  transCalls: 0,
  // То же для сводки фермы (точка на «Стендах»).
  farmLevel: null as 'ok' | 'warn' | 'danger' | null,
  farmCalls: 0,
}));

vi.mock('./useLocalModelStats', () => ({
  isLocalModelPanelVisible: (configured: boolean | null) => configured === true,
  useLocalModelAdmin: () => st.admin,
  getLocalModelAdmin: () => st.admin,
  // Верен эндпоинту: /stats — админский ([Authorize(Roles="admin")]), опрос
  // не-админа даёт 403 → поллер возвращает error='failed' → вкладка «LLM»
  // объясняет пустоту («Не удалось получить данные»). На этом и проверяется дефект №3.
  useLocalModelStats: (active: boolean) => ({
    data: null,
    loading: false,
    error: active && !st.admin ? ('failed' as const) : null,
    refetch: () => {},
  }),
}));
vi.mock('./useLocalModelWatcher', () => ({
  isLocalModelConfiguredAny: () => true,
  getLocalModelEngineStats: () => null,
}));
vi.mock('./useLocalModelMedia', () => ({
  useLocalModelMedia: () => ({ data: null, loading: false, refetch: () => {} }),
}));
vi.mock('./useLocalModelEngines', () => ({
  useLocalModelEngines: () => ({ engines: [], engine: 'main', setEngine: () => {} }),
}));
// Снимок фермы для мока: только поля, значимые для точки и выбора узла (уровни и
// пороги считает бэкенд — здесь только переносим).
function farmSnapshot(level: 'ok' | 'warn' | 'danger'): HardwareFarm {
  return {
    at: 1_763_000_000_000,
    level,
    farmNodes: [{
      key: 'worker', title: 'worker', roles: ['text'], engines: ['main'], media: false,
      status: 'ok', since: null, level, hottest: null, gpus: [],
      uptimeS: null, cpuLoadPerc: null, ramUsedPerc: null,
    }],
  };
}

vi.mock('../../lib/api', () => ({
  api: {
    localModel: {
      transcription: () => {
        st.transCalls++;
        return Promise.resolve(
          st.transLevel ? { phase: 'idle', updatedAt: 1_763_000_000_000, level: st.transLevel } : null,
        );
      },
      // Сводка фермы: её опрашивает настоящий useLocalModelFarm; уровень подставляется
      // по тесту, счётчик обращений — на нём проверяется жизнь опроса.
      hardwareFarm: () => {
        st.farmCalls++;
        return Promise.resolve(st.farmLevel ? farmSnapshot(st.farmLevel) : null);
      },
      // Отчёт узла: опрашивается только на активной вкладке «Стенды» — тесты её не открывают.
      hardware: () => Promise.resolve(null),
    },
  },
}));

// --- Окружение DOM (паттерн ChildHintsPanel.test) ---
const { window: win, document: doc } = parseHTML('<!doctype html><html><body></body></html>');
// Таймеры не заглушаем: linkedom-окно делегирует window.setInterval к globalThis
// (проверено: w.clearInterval === globalThis.clearInterval; а присвоение в окно
// пишет СКВОЗЬ него и подменяло бы сам глобальный интервал — рекурсия). Значит
// под vi.useFakeTimers() поллер видит фейковые, без них — настоящие.
(win as unknown as Record<string, unknown>).matchMedia = () => ({
  matches: false, addEventListener: () => {}, removeEventListener: () => {},
  addListener: () => {}, removeListener: () => {},
});
// useTranscription паузует опрос в скрытой вкладке браузера — тестовая «видима».
Object.defineProperty(doc, 'visibilityState', { value: 'visible', configurable: true });
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const panelCtx: WorkspacePanelDefCtx = { projectId: null, sessionId: null, isMobile: false, onClose: () => {} };

function storeTab(tab: string) {
  (globalThis as unknown as { localStorage: unknown }).localStorage = { getItem: () => tab, setItem: () => {} };
}

// Прогон микротасков до пустой очереди: цепочка «api → .then поллера → emit →
// ререндер React» длиннее одного await, но её точная длина — не контракт (магическое
// число краснело бы как «точки нет» при любом дополнительном .then). Поэтому —
// прогон с запасом, а где важен результат — ожидание по признаку (waitForDot).
async function drainMicrotasks() {
  for (let i = 0; ; i++) {
    await Promise.resolve();
    // Очередь пуста, когда два наблюдения подряд дают один и тот же конец: свой
    // continuation — последний в очереди, пока в неё никто не дописал.
    let settled = false;
    void Promise.resolve().then(() => { settled = true; });
    await Promise.resolve();
    if (settled) return;
  }
}

async function mountPanel() {
  const host = doc.createElement('div');
  doc.body.appendChild(host as unknown as HTMLElement);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(LocalModelPanel, { ctx: panelCtx })); });
  // Первый ответ поллера должен дойти до DOM — прогоняем цепочку внутри act,
  // иначе точка в DOM ещё не будет.
  await act(async () => { await drainMicrotasks(); });
  return {
    host: host as unknown as HTMLElement,
    async unmount() { await act(async () => { root.unmount(); }); },
  };
}

const buttonWithText = (host: HTMLElement, text: string) =>
  Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes(text)) ?? null;
// Точка-состояние (6 px) внутри кнопки вкладки: span с фоном-токеном.
const hasDot = (btn: Element, token: string) =>
  Array.from(btn.querySelectorAll('span')).some(s => (s.getAttribute('style') ?? '').includes(token));

// Ждать точку на вкладке по признаку, а не по счётчику микротасков: длина цепочки
// до DOM — не контракт. Пустых прогонов — максимум три, и тест идёт к своей
// проверке («точки нет») — ровно там, где это и должно проверяться.
async function waitForDot(host: HTMLElement, tabText: string, token: string) {
  for (let attempt = 0; attempt < 3; attempt++) {
    const btn = buttonWithText(host, tabText);
    if (btn && hasDot(btn, token)) return;
    await act(async () => { await drainMicrotasks(); });
  }
}

describe('панель: точка тревоги на «Встречах» без открытия вкладки (дефект №1)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0; st.farmLevel = null; st.farmCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('снимок level=danger — красная точка на «Встречах», панель открыта на «LLM»', async () => {
    st.admin = true; st.transLevel = 'danger';
    const m = await mountPanel(); current = m;
    // Активная вкладка — «LLM» (сохранённой нет → дефолт 'text'): точка не следствие
    // открытой «Встречи» — ровно то обещание, которое нарушал дефект №1.
    expect(buttonWithText(m.host, 'LLM')?.getAttribute('aria-pressed')).toBe('true');
    const meetBtn = buttonWithText(m.host, 'Встречи');
    expect(meetBtn).not.toBeNull();
    expect(hasDot(meetBtn!, 'var(--c-danger)')).toBe(true);
  });

  it('снимок level=warn — жёлтая точка; level=ok — точки нет', async () => {
    st.admin = true; st.transLevel = 'warn';
    const m = await mountPanel(); current = m;
    const meetBtn = buttonWithText(m.host, 'Встречи');
    expect(meetBtn).not.toBeNull();
    expect(hasDot(meetBtn!, 'var(--c-warning)')).toBe(true);
    await m.unmount();
    current = null;
    st.transLevel = 'ok';
    const m2 = await mountPanel(); current = m2;
    const meetBtn2 = buttonWithText(m2.host, 'Встречи');
    expect(meetBtn2).not.toBeNull();
    expect(hasDot(meetBtn2!, 'var(--c-warning)')).toBe(false);
    expect(hasDot(meetBtn2!, 'var(--c-danger)')).toBe(false);
  });
});

describe('панель: опрос снимка живёт с панелью, а не с вкладкой (дефект №1, c2)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0; st.farmLevel = null; st.farmCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('опрос идёт при закрытой «Встречах», переключение вкладок его не останавливает, закрытие панели — останавливает', async () => {
    vi.useFakeTimers();
    st.admin = true;
    const m = await mountPanel(); current = m;
    // Панель открыта на «LLM» («Встречи» не открывались) — опрос уже идёт.
    expect(st.transCalls).toBe(1);
    await act(async () => { vi.advanceTimersByTime(10_000); await drainMicrotasks(); });
    expect(st.transCalls).toBe(3); // 5-с интервал живёт, пока панель смонтирована
    // Уход на «Медиа» — опрос продолжается (дефект №1: раньше он жил только на активной вкладке).
    const mediaBtn = buttonWithText(m.host, 'Медиа');
    expect(mediaBtn).not.toBeNull();
    await act(async () => { mediaBtn!.click(); });
    await act(async () => { vi.advanceTimersByTime(5_000); await drainMicrotasks(); });
    expect(st.transCalls).toBe(4);
    // Закрытие панели — опрос прекращается (таймер погашен при размонтировании).
    await m.unmount(); current = null;
    await act(async () => { vi.advanceTimersByTime(20_000); await drainMicrotasks(); });
    expect(st.transCalls).toBe(4);
  });
});

describe('панель: точка тревоги на «Стендах» без открытия вкладки (Р-Ф2д)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0; st.farmLevel = null; st.farmCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('снимок фермы level=danger — красная точка на «Стендах», панель открыта на «LLM»', async () => {
    st.admin = true; st.farmLevel = 'danger';
    const m = await mountPanel(); current = m;
    // Активная вкладка — «LLM» (сохранённой нет → дефолт 'text'): точка не следствие
    // открытой «Стендов» — ровно то обещание, которое нарушал дефект.
    expect(buttonWithText(m.host, 'LLM')?.getAttribute('aria-pressed')).toBe('true');
    await waitForDot(m.host, 'Стенды', 'var(--c-danger)');
    const hwBtn = buttonWithText(m.host, 'Стенды');
    expect(hwBtn).not.toBeNull();
    expect(hasDot(hwBtn!, 'var(--c-danger)')).toBe(true);
  });

  it('снимок level=warn — жёлтая точка; level=ok — точки нет', async () => {
    st.admin = true; st.farmLevel = 'warn';
    const m = await mountPanel(); current = m;
    await waitForDot(m.host, 'Стенды', 'var(--c-warning)');
    const hwBtn = buttonWithText(m.host, 'Стенды');
    expect(hwBtn).not.toBeNull();
    expect(hasDot(hwBtn!, 'var(--c-warning)')).toBe(true);
    await m.unmount(); current = null;
    st.farmLevel = 'ok';
    const m2 = await mountPanel(); current = m2;
    await act(async () => { await drainMicrotasks(); });
    const hwBtn2 = buttonWithText(m2.host, 'Стенды');
    expect(hwBtn2).not.toBeNull();
    expect(hasDot(hwBtn2!, 'var(--c-warning)')).toBe(false);
    expect(hasDot(hwBtn2!, 'var(--c-danger)')).toBe(false);
  });
});

describe('панель: опрос фермы живёт с панелью, а не с вкладкой (Р-Ф2д, c2)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0; st.farmLevel = null; st.farmCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('опрос идёт при закрытых «Стендах»; закрытие панели — останавливает', async () => {
    vi.useFakeTimers();
    st.admin = true; st.farmLevel = 'ok';
    const m = await mountPanel(); current = m;
    // Панель открыта на «LLM» («Стенды» не открывались) — первый снимок уже взят.
    expect(st.farmCalls).toBe(1);
    // 2-с интервал живёт, пока панель смонтирована.
    await act(async () => { vi.advanceTimersByTime(2_000); await drainMicrotasks(); });
    expect(st.farmCalls).toBe(2);
    // Закрытие панели — опрос прекращается (таймер погашен при размонтировании).
    await m.unmount(); current = null;
    await act(async () => { vi.advanceTimersByTime(10_000); await drainMicrotasks(); });
    expect(st.farmCalls).toBe(2);
  });

  it('не-админ не опрашивает админские эндпоинты (ни ферму, ни снимок встреч)', async () => {
    vi.useFakeTimers();
    st.admin = false;
    const m = await mountPanel(); current = m;
    await act(async () => { vi.advanceTimersByTime(10_000); await drainMicrotasks(); });
    expect(st.farmCalls).toBe(0);
    expect(st.transCalls).toBe(0);
  });
});

describe('панель: не-админ с сохранённой «Встречи» получает рабочую панель (дефект №3)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0; st.farmLevel = null; st.farmCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('не-админ + localStorage transcription → «Медиа», без «Не удалось получить данные» и без «Встреч»', async () => {
    st.admin = false;
    storeTab('transcription');
    const m = await mountPanel(); current = m;
    // Панель живая (не заглушка): контейнер на месте.
    expect(m.host.querySelector('[role="complementary"]')).not.toBeNull();
    // Дефект №3: раньше resolveTab отдавал 'text' → 403 → «Не удалось получить данные».
    expect(m.host.textContent ?? '').not.toContain('Не удалось получить данные');
    // Вкладка «Встречи» не-админу не предлагается вовсе (переключателя нет).
    expect(m.host.textContent ?? '').not.toContain('Встречи');
    // И админский эндпоинт снимка он не опрашивает.
    expect(st.transCalls).toBe(0);
  });
});
