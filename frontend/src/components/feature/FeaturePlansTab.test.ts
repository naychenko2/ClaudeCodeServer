// Вкладка «Планы и макеты» (волна 3): порядок документов (cut → plan → consult),
// hasText=false → «Текста пока нет», на 360 → экран документа, exists=false не открывает
// файл, mockupsUnavailable='device', счётчик вкладки, ошибка загрузки текста + «Повторить».
//
// Окружение node (без jsdom): fakeDom (mount) + mock api.tasks.get (текст документа).

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePlansTab, plansTabCount } from './FeaturePlansTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { api } from '../../lib/api';
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
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Порядок документов: cut → plan → consult ===
  it('документы в порядке cut → plan → consult', () => {
    const page = pageData({
      plans: [
        plan({ taskId: 'p3', role: 'consult', title: 'Консультация' }),
        plan({ taskId: 'p1', role: 'plan', title: 'План' }),
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
    expect(rows.length).toBe(3);
    expect(rows[0]).toContain('Разрез');
    expect(rows[1]).toContain('План');
    expect(rows[2]).toContain('Консультация');
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

  // === 3. На 360 (isMobile) клик по плану → экран документа с «‹ Все документы» ===
  it('на 360 клик по плану → экран документа', async () => {
    const page = pageData({ plans: [plan({ title: 'Мой план' })] });
    const { container } = mount(createElement(FeaturePlansTab, { page, isMobile: true }));
    // Сначала список (кнопка возврата ещё нет).
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

  // === 6e. Авто-выбор нового плана (minor 1): смена последнего плана → деталь не пустеет ===
  // selected инициализируется один раз (ленивый useState): если данные обновились и дедуп
  // сменил последний plan, selectedPlan=null → деталь пустела бы («Выберите документ»). Эффект
  // авто-выбирает первый доступный план. Мутация «убрать эффект» (selected не обновляется)
  // краснеет: деталь остаётся пустой.
  it('авто-выбор: смена последнего плана после обновления → деталь выбирает новый план (не пустеет)', async () => {
    const pageA = pageData({ plans: [plan({ taskId: 'a', title: 'План A' })] });
    const { root, container } = mount(createElement(FeaturePlansTab, { page: pageA, isMobile: false }));
    // Начальный выбор: единственный план A открыт (читалка), деталь не пуста.
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-detail-empty'), 'деталь пуста в начале').toBeNull();
    // Обновление данных: план A заменён планом B (дедуп сменил последний plan).
    const pageB = pageData({ plans: [plan({ taskId: 'b', title: 'План B' })] });
    act(() => { root.render(createElement(FeaturePlansTab, { page: pageB, isMobile: false })); });
    await flush();
    // Деталь не пустеет: авто-выбран новый план B (читалка открыта, не «Выберите документ»).
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plans-detail-empty'), 'деталь пустая после смены плана (minor 1)').toBeNull();
    expect(findNodeByAttr(container as FNode, 'data-testid', 'plan-reader'), 'читалка плана B не открыта (minor 1)').not.toBeNull();
  });

  // === 7. Счётчик вкладки: документы + живые макеты ===
  it('plansTabCount = документы + живые макеты', () => {
    const page = pageData({
      plans: [plan({ taskId: 'a' }), plan({ taskId: 'b' })],
      mockups: [
        mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1', status: 'approved' }),
        mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2', status: 'approved' }),
        mockup({ path: 'docs/mockups/bar.html', title: 'bar', status: 'approved' }),
      ],
    });
    // 2 документа + 2 живых макета (foo-2 текущий, bar) = 4.
    expect(plansTabCount(page)).toBe(4);
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
});
