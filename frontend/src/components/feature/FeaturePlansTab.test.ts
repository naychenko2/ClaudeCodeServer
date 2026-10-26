// Вкладка «Планы и макеты» (волна 4, вариант 5В): сетка карточек-превью (3 колонки,
// на 360 — одна), клик по карточке — читалка/карточка на всю ширину с возвратом к сетке;
// порядок документов (cut → design → plan → consult), карточка design с меткой «дизайн»,
// неизвестная роль не роняет сетку, hasText=false → «Текста пока нет»,
// exists=false не открывает файл, mockupsUnavailable='device', счётчик вкладки,
// ошибка загрузки текста + «Повторить».
//
// Окружение node (без jsdom): fakeDom (mount) + mock api.tasks.get (текст документа).

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePlansTab, plansTabCount } from './FeaturePlansTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { api } from '../../lib/api';
import { getBackNav, clearBackNav, featureBackNavFromTask } from '../../lib/featureBackNav';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

type FNode = ReturnType<typeof findNodeByAttr> extends infer T ? (T extends null ? never : NonNullable<T>) : never;

// Управляемый ответ api.tasks.get (текст документа): result / error / calls.
const mockTaskGet = vi.hoisted(() => ({
  result: null as { resultMarkdown?: string } | null,
  error: null as (Error & { status?: number }) | null,
  calls: [] as string[],
}));
vi.mock('../../lib/api', () => ({
  api: {
    tasks: {
      get: (id: string) => {
        mockTaskGet.calls.push(id);
        if (mockTaskGet.error) return Promise.reject(mockTaskGet.error);
        return Promise.resolve(mockTaskGet.result ?? { resultMarkdown: '' });
      },
    },
  },
}));

// openTaskInSection (lib/tasks) — запись вызовов: переход в задачу обязан нести проект эпика.
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

function plan(over: Partial<FeaturePlan> = {}): FeaturePlan {
  return {
    taskId: 't1', role: 'plan', title: 'План', authorPersonaId: null,
    createdAt: '2026-10-18T06:00:00Z', completedAt: '2026-10-19T12:00:00Z',
    hasText: true, mockupPaths: [],
    ...over,
  };
}

function mockup(over: Partial<FeatureMockup> = {}): FeatureMockup {
  return {
    path: 'docs/mockups/x.html', title: 'X', status: 'approved',
    files: [{ path: 'docs/mockups/x.html', exists: true }],
    source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
    ...over,
  };
}

function pageData(over: Partial<FeaturePage> = {}): FeaturePage {
  return {
    run: {
      featureId: 'f1', state: 'running',
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    } as FeaturePage['run'],
    events: [], eventsTotal: 0,
    ...over,
  };
}

// Клик по data-testid: вызываем onClick из reactProps, в act() (иначе React
// ругается: обновление состояния вне act).
function click(container: FNode, testId: string): void {
  const node = findNodeByAttr(container, 'data-testid', testId);
  expect(node, `node ${testId}`).not.toBeNull();
  const props = reactProps(node!);
  expect(props?.onClick, `onClick on ${testId}`).toBeTruthy();
  act(() => { (props!.onClick as () => void)(); });
}

// Дождаться flush'а microtasks (fetch текста: resolve/reject + setState).
async function flush() {
  await act(async () => { await new Promise(r => setTimeout(r, 10)); });
}

// Собрать текст всех узлов с данным data-testid (в порядке DOM).
function collectText(container: FNode, testId: string): string[] {
  const out: string[] = [];
  (function walk(n: FNode): void {
    if (n.nodeType === 1 && n.getAttribute('data-testid') === testId) out.push(textNodes(n).join(''));
    for (const c of n.childNodes) walk(c);
  })(container);
  return out;
}

// Текст статус-чипа В СТРОКЕ макета с заданным title (путь): привязывает чип к
// конкретной версии, а не к «есть оба чипа» в списке. Строка не найдена → null.
function chipTextInRow(container: FNode, rowTitle: string): string | null {
  let row: FNode | null = null;
  (function walk(n: FNode): void {
    if (row) return;
    if (n.nodeType === 1 && n.getAttribute('data-testid') === 'mockup-row' && n.getAttribute('title') === rowTitle) { row = n; return; }
    for (const c of n.childNodes) walk(c);
  })(container);
  if (!row) return null;
  let chipText: string | null = null;
  (function walkChip(n: FNode): void {
    if (chipText != null) return;
    if (n.nodeType === 1 && n.getAttribute('data-testid') === 'mockup-status-chip') { chipText = textNodes(n).join(''); return; }
    for (const c of n.childNodes) walkChip(c);
  })(row);
  return chipText;
}

// Клик по строке макета с заданным title (путь): ищет mockup-row и вызывает onClick.
function clickRowByTitle(container: FNode, rowTitle: string): void {
  let row: FNode | null = null;
  (function walk(n: FNode): void {
    if (row) return;
    if (n.nodeType === 1 && n.getAttribute('data-testid') === 'mockup-row' && n.getAttribute('title') === rowTitle) { row = n; return; }
    for (const c of n.childNodes) walk(c);
  })(container);
  expect(row, `строка макета ${rowTitle} не найдена`).not.toBeNull();
  const props = reactProps(row!);
  expect(props?.onClick, `onClick у строки ${rowTitle}`).toBeTruthy();
  act(() => { (props!.onClick as () => void)(); });
}

describe('FeaturePlansTab', () => {
  beforeEach(() => {
    installFakeDom();
    mockTaskGet.result = { resultMarkdown: 'текст' };
    mockTaskGet.error = null;
    mockTaskGet.calls = [];
    mockOpenTask.calls = [];
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Порядок документов: cut → design → plan → consult ===
  // Мутация «design после plan» (порядок в FeaturePlansTab) краснеет: Дизайн не rows[1].
  it('документы в порядке cut → design → plan → consult', () => {
    const page = pageData({
      plans: [
        plan({ taskId: 'p4', role: 'consult', title: 'Консультация' }),
        plan({ taskId: 'p1', role: 'plan', title: 'План' }),
        plan({ taskId: 'p3', role: 'design', title: 'Дизайн' }),
        plan({ taskId: 'p2', role: 'cut', title: 'Разрез' }),
      ],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    const rows = (function collect(n: FNode, out: string[] = []): string[] {
      if (n.nodeType === 1 && n.getAttribute('data-testid') === 'plan-row') {
        out.push(textNodes(n).join(' '));
      }
      for (const c of n.childNodes) collect(c, out);
      return out;
    })(container as FNode);
    expect(rows.length).toBe(4);
    expect(rows[0]).toContain('Разрез');
    expect(rows[1]).toContain('Дизайн');
    expect(rows[2]).toContain('План');
    expect(rows[3]).toContain('Консультация');
  });

  // === 1a. Роль design: карточка рендерится, глиф и метка читалки — «дизайн» ===
  // Мутация (убрать design из PLAN_ROLE_LABEL) краснеет: aria-label и метка не «дизайн».
  it('карточка design: глиф и метка «дизайн»', async () => {
    const page = pageData({ plans: [plan({ taskId: 'd1', role: 'design', title: 'Дизайн интерфейса' })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    const rows = collectText(container, 'plan-row');
    expect(rows.length).toBe(1);
    expect(rows[0]).toContain('Дизайн интерфейса');
    // Значок роли — с доступным именем «дизайн» (тот же текст, что в roleLabel задач).
    const glyph = findNodeByAttr(container as FNode, 'data-testid', 'plan-role-glyph');
    expect(glyph, 'глиф роли design не найден').not.toBeNull();
    expect(glyph!.getAttribute('aria-label')).toBe('дизайн');
    // Читалка: метка роли — «дизайн».
    click(container as FNode, 'plan-row');
    await flush();
    const role = findNodeByAttr(container as FNode, 'data-testid', 'plan-reader-role');
    expect(role, 'метка роли в читалке не найдена').not.toBeNull();
    expect(textNodes(role!).join('')).toBe('дизайн');
  });

  // === 1b. Неизвестная роль из ответа не роняет сетку: её карточки нет, остальные — на месте ===
  // Мутация «не фильтровать по роли» (рендерить все plans) краснеет: RoleGlyph падает
  // на незнакомой роли (деструктурирование undefined в PLAN_ROLE_STYLE[role]).
  it('неизвестная роль не роняет сетку', () => {
    const page = pageData({
      plans: [
        plan({ taskId: 'p1', role: 'cut', title: 'Разрез' }),
        plan({ taskId: 'x1', role: 'audit' as FeaturePlan['role'], title: 'Аудит' }),
      ],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid-docs'), 'сетка документов не смонтирована').not.toBeNull();
    const rows = collectText(container, 'plan-row');
    expect(rows.length).toBe(1);
    expect(rows[0]).toContain('Разрез');
  });

  // === 2. hasText=false → «Текста пока нет» (читалка) ===
  it('hasText=false → «Текста пока нет»', async () => {
    const page = pageData({ plans: [plan({ hasText: false })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    // 1280: выбрать план → читалка.
    click(container as FNode, 'plan-row');
    await flush();
    const noText = findNodeByAttr(container as FNode, 'data-testid', 'plan-reader-no-text');
    expect(noText).not.toBeNull();
    expect(textNodes(noText!).join('')).toContain('Текста пока нет');
  });

  // === 3. На 360 (isMobile) клик по плану → полноширинный экран с «‹ Все документы» ===
  it('на 360 клик по плану → экран документа', async () => {
    const page = pageData({ plans: [plan({ title: 'Мой план' })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: true }));
    // Сначала сетка (кнопка возврата ещё нет).
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-back-to-list')).toBeNull();
    click(container as FNode, 'plan-row');
    await flush();
    // Экран документа: кнопка возврата + читалка.
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-back-to-list')).not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader')).not.toBeNull();
  });

  // === 4. exists=false → «Открыть макет» не вызывает onOpenFile ===
  it('exists=false → onOpenFile не вызывается', async () => {
    const onOpenFile = vi.fn();
    const page = pageData({
      mockups: [mockup({
        path: 'docs/mockups/x.html', title: 'X',
        files: [{ path: 'docs/mockups/x.html', exists: false }],
      })],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false, onOpenFile }));
    // Выбрать макет (1280) → карточка.
    click(container as FNode, 'mockup-row');
    await flush();
    // «Открыть макет» disabled (нет существующего html) → клик не вызывает onOpenFile.
    const btn = findNodeByAttr(container as FNode, 'data-testid', 'mockup-open-area');
    expect(btn).not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    await flush();
    expect(onOpenFile).not.toHaveBeenCalled();
  });

  // === 5. exists=true → onOpenFile вызывается с путём ===
  it('exists=true → onOpenFile(путь)', async () => {
    const onOpenFile = vi.fn();
    const page = pageData({
      mockups: [mockup({
        path: 'docs/mockups/x.html', title: 'X',
        files: [{ path: 'docs/mockups/x.html', exists: true }],
      })],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false, onOpenFile }));
    click(container as FNode, 'mockup-row');
    await flush();
    act(() => { (reactProps(findNodeByAttr(container as FNode, 'data-testid', 'mockup-open-area')!)!.onClick as () => void)(); });
    await flush();
    expect(onOpenFile).toHaveBeenCalledWith('docs/mockups/x.html');
  });

  // === 6. mockupsUnavailable='device' → «Макеты локального проекта…» ===
  it('mockupsUnavailable=device → плашка «на устройстве»', () => {
    const page = pageData({ plans: [plan()], mockups: [mockup()], mockupsUnavailable: 'device' });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    const device = findNodeByAttr(container as FNode, 'data-testid', 'mockup-device');
    expect(device).not.toBeNull();
    expect(textNodes(device!).join('')).toContain('на устройстве');
  });

  // === 6a. Дедуп планов (major 2): из двух планов — только последний по createdAt ===
  it('дедуп планов: из двух планов — только последний по createdAt', () => {
    const page = pageData({
      plans: [
        plan({ taskId: 'p-old', role: 'plan', title: 'Старый план', createdAt: '2026-10-01T00:00:00Z' }),
        plan({ taskId: 'p-new', role: 'plan', title: 'Новый план', createdAt: '2026-10-20T00:00:00Z' }),
      ],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    const rows = collectText(container, 'plan-row');
    expect(rows.length).toBe(1);
    expect(rows[0]).toContain('Новый план');
    expect(rows[0]).not.toContain('Старый план');
  });

  // === 6b. «Заменён» (major 3): прошлая версия — чип «заменён», не статус из шапки ===
  it('прошлая версия → чип «заменён» (не статус из шапки)', async () => {
    const page = pageData({
      mockups: [
        mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1', status: 'approved' }),
        mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2', status: 'approved' }),
      ],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    // Раскрыть «Прошлые версии» — там прошлая версия foo-1.
    click(container as FNode, 'mockup-past-toggle');
    await flush();
    // Чип привязан к КОНКРЕТНОЙ версии: foo-1 (прошлая) → «заменён», foo-2 (текущая) → «утверждён».
    // Мутация «чип не различает текущую/прошлую» (оба «утверждён» или оба «заменён») краснеет.
    expect(chipTextInRow(container as FNode, 'docs/mockups/foo-1.html'), 'foo-1 (прошлая) не «заменён»').toBe('заменён');
    expect(chipTextInRow(container as FNode, 'docs/mockups/foo-2.html'), 'foo-2 (текущая) не «утверждён»').toBe('утверждён');
  });

  // === 6d. «Заменён» в КАРТОЧКЕ (деталь): прошлая версия → чип «заменён» (не статус) ===
  // Мутация selectedMockupReplaced=false (FeaturePlansTab) краснеет: карточка покажет
  // статус из шапки («утверждён»), а не «заменён».
  it('карточка деталей: прошлая версия → чип «заменён» (мутация selectedMockupReplaced=false краснеет)', async () => {
    const page = pageData({
      mockups: [
        mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1', status: 'approved' }),
        mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2', status: 'approved' }),
      ],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    // Раскрыть «Прошлые версии» и выбрать прошлую версию foo-1 → карточка деталей.
    click(container as FNode, 'mockup-past-toggle');
    await flush();
    clickRowByTitle(container as FNode, 'docs/mockups/foo-1.html');
    await flush();
    // Карточка деталей смонтирована.
    const card = findNodeByAttr(container as FNode, 'data-testid', 'mockup-card');
    expect(card, 'карточка макета (деталь) не смонтирована').not.toBeNull();
    // Статус-чип В КАРТОЧКЕ — «заменён» (прошлая версия), не «утверждён» (статус из шапки).
    const chip = (function findChipInCard(n: FNode): FNode | null {
      if (n.nodeType === 1 && n.getAttribute('data-testid') === 'mockup-status-chip') return n;
      for (const c of n.childNodes) { const r = findChipInCard(c); if (r) return r; }
      return null;
    })(card!);
    expect(chip, 'статус-чип в карточке не найден').not.toBeNull();
    expect(textNodes(chip!).join(''), 'чип в карточке не «заменён»').toBe('заменён');
  });

  // === 6c. device без планов (major 4): плашка, а не «нет ничего» ===
  it('device без планов → плашка «на устройстве», не «нет ничего»', () => {
    const page = pageData({ plans: [], mockups: [], mockupsUnavailable: 'device' });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    const device = findNodeByAttr(container as FNode, 'data-testid', 'mockup-device');
    expect(device, 'плашка «на устройстве» показана').not.toBeNull();
    const allEmpty = findNodeByAttr(container as FNode, 'data-testid', 'plans-all-empty');
    expect(allEmpty, '«нет ничего» не показано').toBeNull();
  });

  // === 6e. Возврат к сетке (вариант 5В): выбранный план заменён данными → снова сетка,
  // пустой детали нет. Мутация «нет фолбэка на сетку» (рендерить деталь при selectedPlan=null)
  // краснеет: тест не видит plans-grid.
  it('выбранный план заменён → возврат к сетке (пустой детали нет)', async () => {
    const pageA = pageData({ plans: [plan({ taskId: 'a', title: 'План A' })] });
    const { root, container } = mount(createElement(FeaturePlansTab, { page: pageA, isMobile: false }));
    // Сетка — состояние по умолчанию; открыть план A — читалка.
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid'), 'сетки нет в начале').not.toBeNull();
    click(container as FNode, 'plan-row');
    await flush();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader'), 'читалка плана A не открыта').not.toBeNull();
    // Обновление данных: план A заменён планом B (дедуп сменил последний plan).
    const pageB = pageData({ plans: [plan({ taskId: 'b', title: 'План B' })] });
    act(() => { root.render(createElement(FeaturePlansTab, { page: pageB, isMobile: false })); });
    await flush();
    // Пустой детали нет: снова сетка.
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid'), 'после смены плана сетка не показана (6e)').not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader'), 'после смены плана читалка осталась (6e)').toBeNull();
  });

  // === 7. Счётчик вкладки = видимые карточки (хвост ревью R-M3) ===
  // Мутация «считать все page.plans» (старый plansTabCount) краснеет: в этом ответе
  // есть старый план (скрыт дедупом) и неизвестная роль — карточек 2, а не 4.
  it('plansTabCount = видимые документы + живые макеты (дедуп и неизвестные роли не считаются)', () => {
    const page = pageData({
      plans: [
        plan({ taskId: 'p-old', role: 'plan', title: 'Старый план', createdAt: '2026-10-01T00:00:00Z' }),
        plan({ taskId: 'p-new', role: 'plan', title: 'Новый план', createdAt: '2026-10-20T00:00:00Z' }),
        plan({ taskId: 'x1', role: 'audit' as FeaturePlan['role'], title: 'Аудит' }),
        plan({ taskId: 'c1', role: 'cut', title: 'Разрез' }),
      ],
      mockups: [
        mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1', status: 'approved' }),
        mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2', status: 'approved' }),
        mockup({ path: 'docs/mockups/bar.html', title: 'bar', status: 'approved' }),
      ],
    });
    // Видимые документы: cut + p-new (p-old скрыт дедупом планов, audit — неизвестная роль) = 2;
    // живые макета: foo-2 (текущий в группе) + bar = 2 → 4.
    expect(plansTabCount(page)).toBe(4);
    // Тот же счёт, что реально рисует сетка: ровно 2 карточки документов.
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    expect(collectText(container, 'plan-row').length, 'счётчик разошёлся с числом карточек').toBe(2);
  });

  // === 7a. Ответ только из неизвестной роли: счётчик 0 и пустой раздел, не «1» (R-M3) ===
  // Мутация «считать все page.plans» краснеет: бейдж показал бы 1 при пустом разделе.
  it('ответ только из неизвестной роли: счётчик 0, раздел «Документов и макетов пока нет»', () => {
    const page = pageData({ plans: [plan({ taskId: 'x1', role: 'audit' as FeaturePlan['role'], title: 'Аудит' })] });
    expect(plansTabCount(page)).toBe(0);
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-all-empty'), 'пустой раздел не показан').not.toBeNull();
  });

  // === 8. Markdown через ОБЩИЙ рендер (лента): GFM-таблица → <table> ===
  // Мутация: заменить MarkdownContent на свой наивный парсер → <table> не появится.
  it('markdown через общий рендер: GFM-таблица → <table>', async () => {
    mockTaskGet.result = {
      resultMarkdown: '| A | B |\n|---|---|\n| 1 | 2 |\n',
    };
    const page = pageData({ plans: [plan({ taskId: 't-md', hasText: true })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    click(container as FNode, 'plan-row');
    await flush();
    // react-markdown (remark-gfm) рендерит таблицу как <table>; наивный парсер — нет.
    const hasTable = (function walk(n: FNode): boolean {
      if (n.nodeType === 1 && n.nodeName === 'TABLE') return true;
      for (const c of n.childNodes) if (walk(c)) return true;
      return false;
    })(container as FNode);
    expect(hasTable).toBe(true);
  });

  // === 9. Ошибка загрузки текста + «Повторить» ===
  it('ошибка загрузки текста → «Повторить» (повторный запрос)', async () => {
    mockTaskGet.error = new Error('boom') as Error & { status?: number };
    const page = pageData({ plans: [plan({ taskId: 't-err', hasText: true })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    click(container as FNode, 'plan-row');
    await flush();
    const err = findNodeByAttr(container as FNode, 'data-testid', 'plan-reader-error');
    expect(err).not.toBeNull();
    // «Повторить» — кнопка внутри plan-reader-error.
    const btn = (function findRetry(n: FNode): FNode | null {
      if (n.nodeType === 1 && n.nodeName === 'BUTTON' && textNodes(n).join('').includes('Повторить')) return n;
      for (const c of n.childNodes) { const r = findRetry(c); if (r) return r; }
      return null;
    })(container as FNode);
    expect(btn).not.toBeNull();
    const before = mockTaskGet.calls.length;
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    await flush();
    // Повторный запрос ушёл.
    expect(mockTaskGet.calls.length).toBeGreaterThan(before);
  });

  // === 10. Вариант 5В: клик по карточке плана — читалка на всю ширину, возврата к
  // колонке-списку 240 px нет; есть возврат к сетке. Мутация «клик не открывает читалку»
  // (onSelect не ставит selected) краснеет: plan-reader не появляется. Мутация «оставить
  // старую раскладку» (колонка 240 px) краснеет на hasNarrowColumn.
  it('клик по карточке открывает читалку во всю ширину, возврат к сетке', async () => {
    const page = pageData({
      plans: [plan({ taskId: 'p1', role: 'cut', title: 'Разрез' }), plan({ taskId: 'p2', role: 'plan', title: 'План' })],
      mockups: [mockup({ path: 'docs/mockups/m1.html', title: 'M1' })],
    });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    // Сетка видна: обе группы карточек, читалки нет.
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid-docs'), 'сетки документов нет').not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid-mockups'), 'сетки макетов нет').not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader')).toBeNull();
    // Клик — читалка на всю ширину: сетки и узкой колонки 240 px нет.
    click(container as FNode, 'plan-row');
    await flush();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader'), 'читалка не открылась (5В)').not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid'), 'сетка не исчезла после клика').toBeNull();
    const hasNarrowColumn = (function walk(n: FNode): boolean {
      if (n.nodeType === 1) {
        const props = reactProps(n);
        if (props?.style && (props.style as Record<string, unknown>).width === 240) return true;
        for (const c of n.childNodes) if (walk(c)) return true;
      }
      return false;
    })(container as FNode);
    expect(hasNarrowColumn, 'читалка осталась в колонке рядом со списком 240 px').toBe(false);
    // Возврат к сетке.
    click(container as FNode, 'plans-back-to-list');
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-grid'), 'после возврата сетки нет').not.toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader')).toBeNull();
  });

  // === 11. Вариант 5В: на 360 сетка в одну колонку, на десктопе — в три ===
  // Мутация «одинаковые колонки на мобиле» (gridColumns не читает isMobile) краснеет:
  // тест видит repeat(3, minmax(0, 1fr)) вместо 1fr.
  it('на 360 сетка в одну колонку, на десктопе — в три', () => {
    const page = pageData({ plans: [plan()], mockups: [mockup()] });
    const colsOf = (root: FNode, testId: string): string | undefined => {
      const node = findNodeByAttr(root, 'data-testid', testId);
      expect(node, testId).not.toBeNull();
      return (reactProps(node!)!.style as Record<string, unknown>).gridTemplateColumns as string | undefined;
    };
    const mobile = mount(createElement(FeaturePlansTab, { page, isMobile: true }));
    expect(colsOf(mobile.container as FNode, 'plans-grid-docs'), 'на 360 сетка документов не в одну колонку').toBe('1fr');
    expect(colsOf(mobile.container as FNode, 'plans-grid-mockups'), 'на 360 сетка макетов не в одну колонку').toBe('1fr');
    const desktop = mount(createElement(FeaturePlansTab, { page, isMobile: false }));
    expect(colsOf(desktop.container as FNode, 'plans-grid-docs')).toBe('repeat(3, minmax(0, 1fr))');
    expect(colsOf(desktop.container as FNode, 'plans-grid-mockups')).toBe('repeat(3, minmax(0, 1fr))');
  });

  // === Переход в задачу: проект эпика, а не личный «Календарь» и не проект страницы ===
  it('читалка плана: «задача» → openTaskInSection с projectId эпика (не страницы)', async () => {
    const page = pageData({ plans: [plan({ taskId: 'plan-t' })] });
    (page.run as { epic?: unknown }).epic = { id: 'f1', title: 'Фича', status: 'todo', projectId: 'proj-epic' };
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false, featureId: 'f1', projectId: 'proj-page' }));
    click(container as FNode, 'plan-row');
    await flush();
    clearBackNav();
    click(container as FNode, 'nav-target-task');
    expect(mockOpenTask.calls).toEqual([{ id: 'plan-t', projectId: 'proj-epic' }]);
    // Точка возврата: на задаче рисуется чип «← к фиче · Планы».
    expect(getBackNav()).toMatchObject({ from: 'f1', fromKind: 'feature', to: { kind: 'task', id: 'plan-t' }, fromTab: 'plans' });
    expect(featureBackNavFromTask('plan-t')).toMatchObject({ featureId: 'f1', tab: 'plans' });
  });

  it('карточка макета: «задача макета» → openTaskInSection с projectId эпика', async () => {
    const page = pageData({ mockups: [mockup({ taskId: 'mock-t' })] });
    (page.run as { epic?: unknown }).epic = { id: 'f1', title: 'Фича', status: 'todo', projectId: 'proj-epic' };
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: false, featureId: 'f1' }));
    click(container as FNode, 'mockup-row');
    await flush();
    clearBackNav();
    click(container as FNode, 'nav-target-task');
    expect(mockOpenTask.calls).toEqual([{ id: 'mock-t', projectId: 'proj-epic' }]);
    expect(getBackNav()).toMatchObject({ from: 'f1', fromKind: 'feature', to: { kind: 'task', id: 'mock-t' }, fromTab: 'plans' });
    expect(featureBackNavFromTask('mock-t')).toMatchObject({ featureId: 'f1', tab: 'plans' });
  });
});
