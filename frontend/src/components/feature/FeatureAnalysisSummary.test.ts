// Резюме под строкой анализа (W5-F2, спецификация features-overview-5.md §«Точка 6 · резюме»).
// Рендер-тесты через FeatureAnalysisLine (кнопка «Анализ моделью» — там): fakeDom + мок
// api.featureSummary с журналом вызовов.
//
// Покрытие:
//   · резюме нет, пока не нажал кнопку; первый клик — запрос без force (кэш бэка);
//   · «Обновить» — запрос с force=true;
//   · повторный клик после «Свернуть» — БЕЗ нового запроса (результат в состоянии);
//   · 502 — спокойная заглушка с причиной и «Повторить», без toast-цепочек.
//
// Мутация (в отчёте): в FeatureAnalysisLine «Обновить» → fetchSummary(false) —
// тест «Обновить — запрос с force» краснеет.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureAnalysisLine } from './FeatureAnalysisLine';
import { parseSummary } from './FeatureAnalysisSummary';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureSummaryResult } from '../../lib/api';

// === Мок запроса резюме: журнал вызовов + управляемый ответ ===
const mockSummary = vi.hoisted(() => ({
  calls: [] as { force: boolean }[],
  result: null as FeatureSummaryResult | null,
  error: null as (Error & { status?: number; body?: { error?: string } }) | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    tasks: {
      featureSummary: (_fid: string, force?: boolean) => {
        mockSummary.calls.push({ force: !!force });
        if (mockSummary.error) return Promise.reject(mockSummary.error);
        return Promise.resolve(mockSummary.result!);
      },
    },
  },
}));
vi.mock('../../lib/openChat', () => ({ openChatById: async () => {} }));
vi.mock('../../lib/personas', () => ({
  ensurePersonasLoaded: () => Promise.resolve(),
  getPersonaById: () => undefined,
  usePersonas: () => [],
  usePersonasVersion: () => 0,
  personaInitials: () => '',
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));

// Худший случай строки — «idle»: действие ровно одно, «Анализ моделью».
function mkRun(): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'idle',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
  };
}

const SUMMARY: FeatureSummaryResult = {
  text: '## Сделано\n- закрыт шаг W5-F1\n- строка анализа на «Статистике»\n## Где застряло\n- ждём ревью W5-F2\n## Дальше\n- тег в шапке',
  generatedAt: '2026-10-24T09:00:00Z',
  model: 'claude-opus-4-8',
  cached: true,
};

function render() {
  installFakeDom();
  const { container } = mount(createElement(FeatureAnalysisLine, { run: mkRun() }));
  return container;
}

// Дать промисам мока разрешиться.
async function flush() {
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
}

function clickLineSummary(c: Element) {
  const btn = findNodeByAttr(c, 'data-testid', 'analysis-line-summary');
  expect(btn, 'кнопка «Анализ моделью» в строке').not.toBeNull();
  act(() => { (reactProps(btn!)!.onClick as () => void)(); });
}

describe('FeatureAnalysisSummary (W5-F2) через строку анализа', () => {
  beforeEach(() => {
    mockSummary.calls = [];
    mockSummary.result = SUMMARY;
    mockSummary.error = null;
  });
  afterEach(() => {
    uninstallFakeDom();
  });

  // === 1. Резюме только по кнопке; первый клик — без force (берётся кэш) ===
  it('без клика резюме нет; клик «Анализ моделью» — запрос без force', async () => {
    const c = render();
    expect(findNodeByAttr(c, 'data-testid', 'feature-analysis-summary'), 'до клика резюме нет').toBeNull();
    clickLineSummary(c);
    await flush();
    expect(mockSummary.calls.length, 'один запрос').toBe(1);
    expect(mockSummary.calls[0].force, 'первый клик — без force').toBe(false);
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'после клика резюме раскрыто').not.toBeNull();
    const all = textNodes(box!).join('');
    expect(all, 'три постоянных заголовка').toContain('Сделано');
    expect(all, 'модель в шапке мелким текстом').toContain('claude-opus-4-8');
  });

  // === 2. «Обновить» — запрос с force (мутация: force=false красит этот тест) ===
  it('«Обновить» — повторный запрос с force=true', async () => {
    const c = render();
    clickLineSummary(c);
    await flush();
    const refresh = findNodeByAttr(c, 'data-testid', 'summary-refresh');
    expect(refresh, 'кнопка «Обновить» в шапке резюме').not.toBeNull();
    act(() => { (reactProps(refresh!)!.onClick as () => void)(); });
    await flush();
    expect(mockSummary.calls.length, 'второй запрос').toBe(2);
    expect(mockSummary.calls[1].force, '«Обновить» — force=true').toBe(true);
  });

  // === 3. Повторный клик после «Свернуть» — не-запрос: результат в состоянии ===
  it('свернул и открыл снова — нового запроса нет (результат в состоянии)', async () => {
    const c = render();
    clickLineSummary(c);
    await flush();
    const collapse = findNodeByAttr(c, 'data-testid', 'summary-collapse');
    expect(collapse, 'кнопка «Свернуть»').not.toBeNull();
    act(() => { (reactProps(collapse!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-analysis-summary'), 'после «Свернуть» резюме закрыто').toBeNull();
    // Кнопка строки вернулась — клик берёт состояние, не плодит запрос.
    clickLineSummary(c);
    await flush();
    expect(mockSummary.calls.length, 'запрос был ровно один').toBe(1);
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'резюме снова раскрыто из состояния').not.toBeNull();
  });

  // === 4. 502 — спокойная заглушка с причиной и «Повторить» ===
  it('502 — заглушка «Не удалось составить анализ: …» и кнопка «Повторить»', async () => {
    mockSummary.error = Object.assign(new Error('the model did not answer'), { status: 502, body: { error: 'the model did not answer' } });
    const c = render();
    clickLineSummary(c);
    await flush();
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'заглушка есть').not.toBeNull();
    const all = textNodes(box!).join('');
    expect(all, 'текст с причиной от бэка').toContain('Не удалось составить анализ: the model did not answer');
    expect(findNodeByAttr(c, 'data-testid', 'summary-retry'), 'кнопка «Повторить»').not.toBeNull();
    // Повтор после 502 — без force: если модель успела ответить, берём кэш.
    const retry = findNodeByAttr(c, 'data-testid', 'summary-retry');
    act(() => { (reactProps(retry!)!.onClick as () => void)(); });
    await flush();
    expect(mockSummary.calls[1].force, '«Повторить» — без force').toBe(false);
  });

  // === 5. Разбор текста: заголовки и голые пункты (чистая функция) ===
  it('parseSummary: три раздела по заголовкам; без заголовков — одна колонка', () => {
    const withHeads = parseSummary(SUMMARY.text);
    expect(withHeads.map(s => s.title), 'заголовки постоянные').toEqual(['Сделано', 'Где застряло', 'Дальше']);
    expect(withHeads[0].items.length, 'пункты первого раздела').toBe(2);
    const bare = parseSummary('- пункт один\n- пункт два');
    expect(bare.length, 'без заголовков — одна колонка').toBe(1);
    expect(bare[0].title, 'без заголовка').toBeNull();
    expect(bare[0].items, 'пункты собраны').toEqual(['пункт один', 'пункт два']);
  });
});
