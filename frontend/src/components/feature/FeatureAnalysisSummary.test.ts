// Резюме под строкой анализа (W5-F2, спецификация features-overview-5.md §«Точка 6 · резюме»).
// Рендер-тесты через FeatureAnalysisLine (кнопка «Анализ моделью» — там): fakeDom + мок
// api.featureSummary с журналом вызовов.
//
// Покрытие:
//   · резюме нет, пока не нажал кнопку; первый клик — запрос без force (кэш бэка);
//   · «Обновить» — запрос с force=true;
//   · повторный клик после «Свернуть» — БЕЗ нового запроса (результат в состоянии);
//   · 502 — спокойная заглушка с причиной и «Повторить», без toast-цепочек;
//   · смена featureId — состояние резюме сброшено (доработка W5-F2a, вердикт W5-R-F2 п.3);
//   · смена featureId — висящий запрос ОТМЕНЁН (signal.aborted), запрос новой фичи ушёл (W5-R-F2a, п.2/4);
//   · ответ старого запроса, пришедший после смены фичи, не липнет: клик по новой фиче
//     плодит новый запрос (W5-R-F2c, п.1);
//   · размонтирование — висящий запрос отменён (cleanup эффекта, W5-R-F2c, п.2);
//   · старый запрос отклонён по abort, пока новый «идёт» — finally не трёт состояние нового
//     запроса: страховка равенства контроллера (W5-R-F2a, п.3);
//   · на 360 кнопки «Остановить», «Обновить», «Свернуть» — minHeight 40 (п.4),
//     «Повторить» в заглушке 502 — тоже 40 (W5-R-F2a, п.1);
//   · число событий: «по N событиям» в шапке (W4-AN-EC); бейдж устаревания — просто
//     «устарело», без числа (решение продакта); если бэк eventCount не несёт (старая
//     версия) — числа в шапке нигде нет;
//   · полная строка шапки с пробелами вокруг «·» — проверяется целиком, не подстроками
//     (вердикт W4-R-AN-EC, п.2).
//
// Мутации (в отчёте): в FeatureAnalysisLine «Обновить» → fetchSummary(false) —
// тест «Обновить — запрос с force» краснеет; удаление сброса состояния при смене
// featureId красит тест «смена featureId — состояния нет»; удаление abort() из
// useEffect красит тест про отмену висящего запроса; удаление guards в finally
// (просто setSummaryRunning(false)) красит тест про «идёт» нового запроса; удаление
// btnStyle и style у «Повторить» красит тесты про minHeight 40; удаление проверки
// «текущий запрос» в .then красит тест «ответ старого запроса не липнет» (проверено
// прогоном); удаление cleanup-стрелки в useEffect красит тест про отмену при
// размонтировании; удаление показа «по N событиям» в шапке красит тест про число
// событий; склейка шапки без пробела перед «·» (join('· ')) красит тест полной
// строки шапки (проверено прогоном).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureAnalysisLine } from './FeatureAnalysisLine';
import { parseSummary } from './FeatureAnalysisSummary';
import { formatClock } from './FeatureStatsBits';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureSummaryResult } from '../../lib/api';

// === Мок запроса резюме: журнал вызовов + управляемый ответ ===
const mockSummary = vi.hoisted(() => ({
  calls: [] as { fid: string; force: boolean; signal?: AbortSignal }[],
  result: null as FeatureSummaryResult | null,
  error: null as (Error & { status?: number; body?: { error?: string } }) | null,
  // true — запрос «висит» (не завершается, пока его не отменят): состояние «идёт».
  pending: false,
  // true — ответ «уже в пути»: abort не reject'ит промис, ответ приходит по resolve
  // (тест моделирует порядок явно — ответ приходит после смены фичи).
  late: false,
  resolve: null as ((v: FeatureSummaryResult) => void) | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    tasks: {
      featureSummary: (fid: string, force?: boolean, signal?: AbortSignal) => {
        mockSummary.calls.push({ fid, force: !!force, signal });
        if (mockSummary.pending) {
          return new Promise<FeatureSummaryResult>((resolve, reject) => {
            mockSummary.resolve = resolve;
            if (signal && !mockSummary.late) {
              // Висящий запрос отклоняется по abort — как настоящий fetch.
              signal.addEventListener('abort', () => {
                reject(Object.assign(new Error('aborted'), { name: 'AbortError' }));
              });
            }
          });
        }
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
function mkRun(featureId = 'f-1', lastActivityAt?: string): FeatureRun {
  return {
    featureId,
    state: 'idle',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...(lastActivityAt ? { lastActivityAt } : {}),
  };
}

const SUMMARY: FeatureSummaryResult = {
  text: '## Сделано\n- закрыт шаг W5-F1\n- строка анализа на «Статистике»\n## Где застряло\n- ждём ревью W5-F2\n## Дальше\n- тег в шапке',
  generatedAt: '2026-10-24T09:00:00Z',
  model: 'claude-opus-4-8',
  cached: true,
  eventCount: 12,
};

// render возвращает и root: тест смены фичи перерендеривает тот же экземпляр
// с другим run — так же делает WorkspacePage при переходе между фичами.
function render(opts?: { isMobile?: boolean; lastActivityAt?: string }) {
  installFakeDom();
  const { root, container } = mount(
    createElement(
      FeatureAnalysisLine,
      { run: mkRun('f-1', opts?.lastActivityAt), isMobile: !!opts?.isMobile },
    ),
  );
  return { root, c: container };
}

// minHeight из inline-стиля узла (React приводит числа к px).
function minHeightPx(el: Element): number {
  const raw = (el as { style?: Record<string, string> }).style?.minHeight ?? '';
  return Number.parseInt(raw, 10);
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
    mockSummary.pending = false;
    mockSummary.late = false;
    mockSummary.resolve = null;
  });
  afterEach(() => {
    uninstallFakeDom();
  });

  // === 1. Резюме только по кнопке; первый клик — без force (берётся кэш) ===
  it('без клика резюме нет; клик «Анализ моделью» — запрос без force', async () => {
    const { c } = render();
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
    const { c } = render();
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
    const { c } = render();
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
    const { c } = render();
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

  // === 6. Смена featureId — состояние сброшено (вердикт W5-R-F2, п.3) ===
  it('смена featureId — резюме закрыто, кнопка «Анализ моделью» снова в строке, запрос — для новой фичи', async () => {
    const { root, c } = render();
    clickLineSummary(c);
    await flush();
    expect(findNodeByAttr(c, 'data-testid', 'feature-analysis-summary'), 'резюме для f-1 раскрыто').not.toBeNull();

    // WorkspacePage при смене фичи переиспользует этот же экземпляр — перерендер
    // того же компонента с другим run.
    act(() => { root.render(createElement(FeatureAnalysisLine, { run: mkRun('f-2') })); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-analysis-summary'), 'после смены фичи резюме нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'analysis-line-summary'), 'кнопка «Анализ моделью» вернулась').not.toBeNull();
    // Клик по новой фиче — новый запрос, привязанный к новой featureId.
    clickLineSummary(c);
    await flush();
    expect(mockSummary.calls.length, 'второй запрос').toBe(2);
    expect(mockSummary.calls[1].fid, 'запрос — для новой фичи').toBe('f-2');
  });

  // === 7. «Повторить» в заглушке 502 — тач-цель 40 на 360 (вердикт W5-R-F2a, п.1) ===
  it('на 360 «Повторить» в заглушке 502 — minHeight 40', async () => {
    mockSummary.error = Object.assign(new Error('the model did not answer'), { status: 502, body: { error: 'the model did not answer' } });
    const { c } = render({ isMobile: true });
    clickLineSummary(c);
    await flush();
    const retry = findNodeByAttr(c, 'data-testid', 'summary-retry');
    expect(retry, 'кнопка «Повторить» в заглушке').not.toBeNull();
    expect(minHeightPx(retry!), 'тач-цель 40 на мобиле').toBe(40);
  });

  // === 8. Смена featureId — висящий запрос отменён (вердикт W5-R-F2a, п.2 и п.4) ===
  it('смена featureId — старый запрос отменён по signal, запрос новой фичи ушёл', async () => {
    mockSummary.pending = true;
    const { root, c } = render();
    clickLineSummary(c);
    expect(mockSummary.calls.length, 'запрос для f-1 ушёл').toBe(1);
    // Перерендер с другим run — тот же путь, что у WorkspacePage при смене фичи.
    act(() => { root.render(createElement(FeatureAnalysisLine, { run: mkRun('f-2') })); });
    expect(mockSummary.calls[0].signal?.aborted, 'висящий запрос отменён').toBe(true);
    // Кнопка вернулась — клик плодит запрос уже для новой фичи.
    clickLineSummary(c);
    await flush();
    expect(mockSummary.calls.length, 'второй запрос').toBe(2);
    expect(mockSummary.calls[1].fid, 'запрос — для новой фичи').toBe('f-2');
  });

  // === 9. Страховка finally (вердикт W5-R-F2a, п.3) ===
  it('старый запрос отклонён по abort, пока новый «идёт» — состояние «идёт» не сбито', async () => {
    mockSummary.pending = true;
    const { root, c } = render();
    clickLineSummary(c);                      // запрос f-1 — «висит»
    act(() => { root.render(createElement(FeatureAnalysisLine, { run: mkRun('f-2') })); }); // отмена
    clickLineSummary(c);                      // новый запрос f-2 — уже «идёт»
    await flush();                           // старый finally исполняется после клика
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'резюме открыто').not.toBeNull();
    // Без страховки старый finally сбросил бы summaryRunning — «идёт» превратилось бы
    // в пустое «готово». С ней — кнопка «Остановить» на месте.
    expect(findNodeByAttr(c, 'data-testid', 'summary-stop'), 'новый запрос всё ещё «идёт»').not.toBeNull();
  });

  // === 11. Ответ старого запроса не липнет под новой фичей (вердикт W5-R-F2c, п.1) ===
  it('ответ старого запроса, пришедший после смены фичи, не липнет — клик по новой фиче плодит запрос', async () => {
    // Ответ «уже в пути»: abort эффекта его не гасит — промис разрешится позже.
    mockSummary.pending = true;
    mockSummary.late = true;
    const { root, c } = render();
    clickLineSummary(c);
    expect(mockSummary.calls.length, 'запрос для f-1 ушёл').toBe(1);
    // Перерендер с новой фичей: состояние сброшено (коммит), отмена — в cleanup эффекта.
    act(() => { root.render(createElement(FeatureAnalysisLine, { run: mkRun('f-2') })); });
    expect(mockSummary.calls[0].signal?.aborted, 'запрос отменён').toBe(true);
    // Ответ f-1 приходит после смены фичи: проверка «текущая фича» в .then его отвергает.
    // Без неё summaryData липнет, и клик по новой фиче не запросил бы.
    act(() => { mockSummary.resolve!(SUMMARY); });
    await flush();
    clickLineSummary(c);
    await flush();
    expect(mockSummary.calls.length, 'новый запрос для f-2').toBe(2);
    expect(mockSummary.calls[1].fid, 'запрос — для новой фичи').toBe('f-2');
  });

  // === 12. Размонтирование — висящий запрос отменён (вердикт W5-R-F2c, п.2) ===
  it('размонтирование — висящий запрос отменён по signal', async () => {
    mockSummary.pending = true;
    const { root, c } = render();
    clickLineSummary(c);
    expect(mockSummary.calls.length, 'запрос ушёл').toBe(1);
    act(() => { root.unmount(); });
    expect(mockSummary.calls[0].signal?.aborted, 'уход со страницы отменяет запрос').toBe(true);
  });

  // === 13. Тач-цель 40 на 360 (вердикт W5-R-F2, п.4) ===
  it('на 360 кнопки «Остановить», «Обновить» и «Свернуть» — minHeight 40', async () => {
    // «Идёт»: кнопка «Остановить» — тоже 40.
    mockSummary.pending = true;
    const { c } = render({ isMobile: true });
    clickLineSummary(c);
    const stop = findNodeByAttr(c, 'data-testid', 'summary-stop');
    expect(stop, '«Остановить» в состоянии «идёт»').not.toBeNull();
    expect(minHeightPx(stop!), 'тач-цель 40 на мобиле').toBe(40);

    // «Готово»: кнопки шапки — тоже 40.
    mockSummary.pending = false;
    const { c: c2 } = render({ isMobile: true });
    clickLineSummary(c2);
    await flush();
    for (const id of ['summary-refresh', 'summary-collapse']) {
      const btn = findNodeByAttr(c2, 'data-testid', id);
      expect(btn, `кнопка ${id} в шапке резюме`).not.toBeNull();
      expect(minHeightPx(btn!), 'тач-цель 40 на мобиле').toBe(40);
    }
  });

  // === 14. Полная строка шапки (вердикт W4-R-AN-EC, п.2; мутация: убрать пробел перед
  //     «·» в склейке — тест краснеет, проверено прогоном) ===
  it('шапка — «анализ · <время> · по 12 событиям · claude-opus-4-8» целиком, с пробелами вокруг «·»', async () => {
    const { c } = render();
    clickLineSummary(c);
    await flush();
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'резюме раскрыто').not.toBeNull();
    const all = textNodes(box!).join('');
    // Время берём тем же formatClock, что и компонент: тест не зависит от часовой зоны.
    const header = `анализ · ${formatClock(SUMMARY.generatedAt)} · по 12 событиям · claude-opus-4-8`;
    expect(all, 'полная строка шапки').toContain(header);
  });

  // === 15. Бейдж устаревания без числа (решение продакта; мутация: вернуть число — краснеет) ===
  it('устаревшее резюме — бейдж «устарело», без числа событий', async () => {
    // Активность фичи позже генерации — бейдж есть, но только слово «устарело».
    const { c } = render({ lastActivityAt: '2026-10-24T12:00:00Z' });
    clickLineSummary(c);
    await flush();
    const box = findNodeByAttr(c, 'data-testid', 'feature-analysis-summary');
    expect(box, 'резюме раскрыто').not.toBeNull();
    const all = textNodes(box!).join('');
    expect(all, 'бейдж «устарело»').toContain('устарело');
    expect(all, 'числа событий в бейдже нет').not.toContain('устарело · 12');
  });

  // === 16. Формы числительных и ответ старой версии бэка без eventCount ===
  it('eventCount 1 — «по 1 событию» в шапке; без eventCount — числа в шапке нет; бейдж всегда без числа', async () => {
    mockSummary.result = { ...SUMMARY, eventCount: 1 };
    const { c } = render({ lastActivityAt: '2026-10-24T12:00:00Z' });
    clickLineSummary(c);
    await flush();
    const all = textNodes(findNodeByAttr(c, 'data-testid', 'feature-analysis-summary')!).join('');
    expect(all, 'единичная форма в шапке').toContain('по 1 событию');
    expect(all, 'бейдж без числа').toContain('устарело');
    expect(all, 'числа в бейдже нет').not.toContain('устарело · 1');

    // Старая версия сервера поля не несёт — в шапке числа нет, бейдж просто «устарело».
    mockSummary.result = { text: SUMMARY.text, generatedAt: SUMMARY.generatedAt, model: null, cached: true };
    const { c: c2 } = render({ lastActivityAt: '2026-10-24T12:00:00Z' });
    clickLineSummary(c2);
    await flush();
    const all2 = textNodes(findNodeByAttr(c2, 'data-testid', 'feature-analysis-summary')!).join('');
    // Путь без eventCount тоже проверяется полной строкой: «анализ · <время> · локальная модель».
    const header2 = `анализ · ${formatClock(SUMMARY.generatedAt)} · локальная модель`;
    expect(all2, 'полная строка шапки без eventCount').toContain(header2);
    expect(all2, 'бейдж без числа — просто «устарело»').toContain('устарело');
    expect(all2, 'числа событий в шапке нет').not.toContain('событи');
  });
});
