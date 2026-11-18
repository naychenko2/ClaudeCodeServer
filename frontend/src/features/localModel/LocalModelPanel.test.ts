// Подключение вкладки «Встречи» к панели (Ф2): видимость вкладки (админ/не-админ),
// запоминание выбора (cc_local_model_tab), точка на вкладке по level из снимка,
// «узкая лента» (<340 px — неактивные вкладки только иконкой).
//
// Плюс Ф6: точка на «Стендах» и то, что доступное имя панели берётся из реестра.
//
// Доработка Ф2/Ф6 (дефекты №1/№3 приёмки Веры): точка тревоги на «Встречах» видна,
// не открывая вкладку (снимок опрашивается, пока панель смонтирована, и прекращается
// при закрытии панели, а не при уходе с вкладки); не-админ с сохранённой 'transcription'
// получает рабочую «Медиа», а не сломанную панель. Это поведение проверяется
// МОНТИРОВАНИЕМ панели (node + linkedom, паттерн ChildHintsPanel.test): мокнуты
// только опросы (их эндпоинты сетевые/админские), сама панель, переключатель и
// связка «снимок → точка» — настоящие.
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
// Мокнуты только опросы: их эндпоинты сетевые, а админские (/stats, /hardware,
// /transcription) к тому же проверяются моком «верно эндпоинту» (см. useLocalModelStats
// ниже). Панель, переключатель, resolveTab, useTranscription и связка «снимок →
// точка» — настоящие: на этом и ловятся дефекты №1/№3.

const st = vi.hoisted(() => ({
  admin: true,
  // Снимок, который отдаёт эндпоинт: уровень подставляется по тесту (его считает
  // бэкенд — здесь только переносим).
  transLevel: null as 'ok' | 'warn' | 'danger' | null,
  // Счётчик обращений к эндпоинту снимка: на нём проверяется жизнь опроса.
  transCalls: 0,
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
vi.mock('./useLocalModelHardware', () => ({
  useLocalModelHardware: () => ({ data: null, loading: false }),
  useLocalModelFarm: () => ({ data: null, loading: false }),
  DEFAULT_HARDWARE_WINDOW: '15m',
}));
vi.mock('../../lib/api', () => ({
  api: {
    localModel: {
      transcription: () => {
        st.transCalls++;
        return Promise.resolve(
          st.transLevel ? { phase: 'idle', updatedAt: 1_763_000_000_000, level: st.transLevel } : null,
        );
      },
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

async function mountPanel() {
  const host = doc.createElement('div');
  doc.body.appendChild(host as unknown as HTMLElement);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(LocalModelPanel, { ctx: panelCtx })); });
  // Первый ответ поллера — цепочка микротасков (api → .then поллера → emit → ререндер):
  // прогоняем её внутри act, иначе точка в DOM ещё не будет.
  await act(async () => { for (let i = 0; i < 4; i++) await Promise.resolve(); });
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

describe('панель: точка тревоги на «Встречах» без открытия вкладки (дефект №1)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0;
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
    st.admin = true; st.transLevel = null; st.transCalls = 0;
    delete (globalThis as { localStorage?: unknown }).localStorage;
    vi.useRealTimers();
  });

  it('опрос идёт при закрытой «Встречах», переключение вкладок его не останавливает, закрытие панели — останавливает', async () => {
    vi.useFakeTimers();
    st.admin = true;
    const m = await mountPanel(); current = m;
    // Панель открыта на «LLM» («Встречи» не открывались) — опрос уже идёт.
    expect(st.transCalls).toBe(1);
    await act(async () => { vi.advanceTimersByTime(10_000); for (let i = 0; i < 4; i++) await Promise.resolve(); });
    expect(st.transCalls).toBe(3); // 5-с интервал живёт, пока панель смонтирована
    // Уход на «Медиа» — опрос продолжается (дефект №1: раньше он жил только на активной вкладке).
    const mediaBtn = buttonWithText(m.host, 'Медиа');
    expect(mediaBtn).not.toBeNull();
    await act(async () => { mediaBtn!.click(); });
    await act(async () => { vi.advanceTimersByTime(5_000); for (let i = 0; i < 4; i++) await Promise.resolve(); });
    expect(st.transCalls).toBe(4);
    // Закрытие панели — опрос прекращается (таймер погашен при размонтировании).
    await m.unmount(); current = null;
    await act(async () => { vi.advanceTimersByTime(20_000); for (let i = 0; i < 4; i++) await Promise.resolve(); });
    expect(st.transCalls).toBe(4);
  });
});

describe('панель: не-админ с сохранённой «Встречи» получает рабочую панель (дефект №3)', () => {
  let current: { unmount: () => Promise<void> } | null = null;
  afterEach(async () => {
    await current?.unmount();
    current = null;
    st.admin = true; st.transLevel = null; st.transCalls = 0;
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
