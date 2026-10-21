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

// Клик по data-testid: вызываем onClick из reactProps.
function click(container: FNode, testId: string): void {
  const node = findNodeByAttr(container, 'data-testid', testId);
  expect(node, `node ${testId}`).not.toBeNull();
  const props = reactProps(node!);
  expect(props?.onClick, `onClick on ${testId}`).toBeTruthy();
  (props!.onClick as () => void)();
}

// Дождаться flush'а microtasks (fetch текста: resolve/reject + setState).
async function flush() {
  await act(async () => { await new Promise(r => setTimeout(r, 10)); });
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
    (reactProps(btn!)!.onClick as () => void)();
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
    (reactProps(findNodeByAttr(container as FNode, 'data-testid', 'mockup-open-area')!)!.onClick as () => void)();
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
    (reactProps(btn!)!.onClick as () => void)();
    await flush();
    // Повторный запрос ушёл.
    expect(mockTaskGet.calls.length).toBeGreaterThan(before);
  });
});
