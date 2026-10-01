import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: мок API по ключу localStorage, window — источник события revealWorkspacePanel.
// Слушатель вкладки модуль ставит при загрузке, поэтому сама панель грузится ПОСЛЕ заглушек
const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(storage));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
const win = Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);

const { REVEAL_PANEL_EVENT } = await import('../../../lib/subsystems/registryCore');
const { loadCatalog } = await import('../thread/catalog');
const { __applyThreads, __resetThreadStore } = await import('../thread/threadStore');
const { __resetPanelChoice, setPanelChoice } = await import('./panelOp');
const { ImagesPanel } = await import('./ImagesPanel');

const ctx = (projectId: string | null) => ({ projectId, sessionId: 's1', isMobile: false, onClose: () => {} });
const render = (projectId: string | null) => renderToStaticMarkup(createElement(ImagesPanel, { ctx: ctx(projectId) }));
const reveal = (key: string, tab?: string) => win.dispatchEvent(new CustomEvent(REVEAL_PANEL_EVENT, { detail: { key, tab } }));
const selected = (html: string) => html.match(/aria-selected="true"[^>]*>([^<]*<[^>]*>)*?([^<]+)</)?.[2];

beforeEach(() => {
  __resetThreadStore();
  __resetPanelChoice();
});

describe('вкладка панели по событию revealWorkspacePanel', () => {
  it('без события — «Настройки»', () => {
    expect(selected(render('p1'))).toBe('Настройки');
  });

  it('событие пришло до монтирования — панель открывается на «Персонажах»', () => {
    reveal('images', 'characters');
    expect(selected(render('p1'))).toBe('Персонажи');
    // Событие забирается один раз: следующий монтаж — снова «Настройки»
    expect(selected(render('p1'))).toBe('Настройки');
  });

  it('событие чужой панели или без вкладки не трогает выбор', () => {
    reveal('audio', 'characters');
    reveal('images');
    expect(selected(render('p1'))).toBe('Настройки');
  });

  it('личный чат: на «Персонажах» — «Персонажи живут в проекте»', () => {
    reveal('images', 'characters');
    const html = render(null);
    expect(selected(html)).toBe('Персонажи');
    expect(html).toContain('Персонажи живут в проекте');
  });
});

describe('закреплённый низ', () => {
  const file = {
    id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
    currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z',
  };

  beforeEach(async () => {
    await loadCatalog('p1');
    __applyThreads('s1', 'p1', { focus: 't1', revision: 1, threads: [file] });
  });

  it('«Авто» при выбранной картинке: глагол «Изменить», «− N +» живой, цена в две строки', () => {
    const html = render('p1');
    expect(html).toContain('Изменить');
    expect(html).toContain('Сейчас это <b');
    // У «Авто» ориентира цены нет: до котировки — честное «уточняется»
    expect(html).toContain('Цена уточняется');
    expect(html).not.toContain('Эта операция даёт один вариант');
  });

  it('модель с ценой: итог и короткая расшифровка', () => {
    __applyThreads('s1', 'p1', {
      focus: 't1', revision: 2,
      threads: [{ ...file, settings: { provider: 'fal', model: 'fal-ai/flux-pro/kontext', count: 2, matchSourceSize: true } }],
    });
    const html = render('p1');
    expect(html).toContain('≈ $0.08');
    expect(html).toContain('2 × $0.04');
  });

  it('«Убрать фон»: ровно один вариант, «+» заперт с причиной', () => {
    setPanelChoice('p1', { op: 'removeBackground' });
    const html = render('p1');
    expect(html).toContain('Убрать фон');
    expect(html).toContain('Больше: Эта операция даёт один вариант');
    expect(html).toContain('Текст в поле ввода не нужен');
  });

  it('«По отмеченному» без отметок: причина в низу, кнопка погашена', () => {
    setPanelChoice('p1', { op: 'inpaint' });
    const html = render('p1');
    // Кнопка запуска низа — та, что с глаголом; у пилюли «По отмеченному» та же подсказка
    const run = html.match(/<button[^>]*>(?:(?!<\/button>).)*Изменить отмеченное<\/button>/)?.[0] ?? '';
    expect(run).toContain('title="Отметьте место кистью в редакторе картинки"');
    expect(run).toContain('disabled');
  });

  it('режим подбора — только у модели «Авто»', () => {
    expect(render('p1')).toContain('Режим подбора');
    __applyThreads('s1', 'p1', {
      focus: 't1', revision: 2,
      threads: [{ ...file, settings: { provider: 'fal', model: 'fal-ai/flux-pro/kontext', count: 2, matchSourceSize: true } }],
    });
    expect(render('p1')).not.toContain('Режим подбора');
  });

  it('на телефоне — шторка каркаса: «опустить до цены», сводка опущенной — с чем работаем', () => {
    const html = renderToStaticMarkup(createElement(ImagesPanel, { ctx: ctx('p1'), layout: 'sheet' }));
    expect(html).toContain('role="dialog"');
    expect(html).toContain('Опустить до цены — лента станет доступна');
    expect(html).toContain('Работаем с:');
  });
});
