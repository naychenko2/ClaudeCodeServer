// Карточка фичи (режим «Исполнение фичи»). Рендер — renderToStaticMarkup
// (паттерн соседних тестов, @testing-library нет; окружение node — window undefined,
// useIsMobile() = false, effects не идут). Проверяем:
//   • шапка (имя + ветка + прогресс);
//   • плашку-тревогу: «ждёт вас» (мерж/явное) и «исполнитель упал» (с/без чата);
//   • блок «Сейчас» (по карточке на now[]; у закрытой нет);
//   • итог смерженной фичи (из summary);
//   • пустое состояние (шагов 0) и легенду (когда шаги есть);
//   • что УШЛО: «Принять», «Изменить ход», «Финал зелёный», «Ревью не было».
//   • гейт фич-флага (M2) и мобилу (M7).
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureNow, FeatureAttention, FeatureSummary } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { FeatureRunView } from './FeatureRunView';
import { TurnPlanPill } from '../TurnPlanPill';
import { __test } from '../../../lib/featureRuns';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from '../__tests__/fakeDom';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя (как в PipelineFlow.test.ts).
// importOriginal — сохранить остальные экспорты (personaInitials и др., их читает PersonaAvatar).
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});
// useIsMobile — управляемый флаг (SSR по умолчанию false; M3 проверяет мобилу = true).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
beforeEach(() => { personaMap.clear(); mobileFlag.value = false; });

// Задача-заглушка (подмножество TaskItem, как несёт GET /api/tasks/features).
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Базовый агрегат фичи: эпик + шаг + ревью + финал; готовность/прогресс/вердикты.
// Новые поля (now/attention/closed/summary) — по умолчанию «тихие», тесты их покрывают.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator-console' }),
    steps: [task({ id: 's1', title: 'Шаг 1', stepRef: 'Ш1', lane: 'фронт', status: 'done' })],
    reviews: [task({ id: 'r1', title: 'Ревью', status: 'done', verification: { verdict: 'accepted' } })],
    qa: [],
    final: task({ id: 'f1', title: 'Финал', status: 'done', verification: { verdict: 'accepted' } }),
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 1,
    ready: true,
    now: [],
    attention: null,
    closed: false,
    summary: null,
    ...over,
  };
}

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(FeatureRunView, { run: r }));

// Ожидаемая дата-время итога: теми же локальными геттерами от той же Date, что и
// dateTimeLabel (featureTime) — тест не зависит от пояса машины (CI в UTC, разработка в МСК).
function localDateTime(iso: string): string {
  const d = new Date(iso);
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const mi = String(d.getMinutes()).padStart(2, '0');
  return `${dd}.${mm} ${hh}:${mi}`;
}

// now[]-элемент для блока «Сейчас».
function nowItem(over: Partial<FeatureNow> = {}): FeatureNow {
  return { taskId: 'n', stage: 'prod', title: 'Задача', ...over };
}

describe('FeatureRunView — шапка', () => {
  it('имя, ветка и прогресс на месте', () => {
    const html = render(run());
    expect(html).toContain('Пульт куратора');
    expect(html).toContain('feat/curator-console');
    expect(html).toContain('100%');
  });
});

describe('FeatureRunView — плашка «ждёт вас»', () => {
  it('мерж: «все шаги приняты, мержа нет» + действия [Попросить Марка] [Итог финала]', () => {
    // taskId у мержа — это финал: «Итог финала» рендерится только когда taskId есть (Майя M4).
    const att: FeatureAttention = { kind: 'wait', reason: 'мерж', since: '2026-10-17T10:00:00Z', taskId: 'f1' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Ждёт вас');
    expect(html).toContain('Все шаги приняты, мержа ещё нет');
    expect(html).toContain('Отдать фичу Марку на мерж?');
    expect(html).toContain('Попросить Марка');
    expect(html).toContain('Итог финала');
  });

  it('мерж без taskId: [Итог финала] не рендерится, [Попросить Марка] остаётся (Майя M4)', () => {
    const att: FeatureAttention = { kind: 'wait', reason: 'мерж', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Попросить Марка');
    expect(html).not.toContain('Итог финала');
  });

  it('явное ожидание (reason ≠ мерж): одна кнопка [Открыть задачу]; без taskId кнопок нет (Майя M4)', () => {
    const withTask: FeatureAttention = { kind: 'wait', reason: 'нужно решение о ревью', since: '2026-10-17T10:00:00Z', taskId: 't1' };
    const html1 = render(run({ ready: false, attention: withTask }));
    expect(html1).toContain('Открыть задачу');
    expect(html1).not.toContain('Попросить Марка');
    expect(html1).not.toContain('Итог финала');
    const noTask: FeatureAttention = { kind: 'wait', reason: 'нужно решение о ревью', since: '2026-10-17T10:00:00Z' };
    const html2 = render(run({ ready: false, attention: noTask }));
    expect(html2).not.toContain('Открыть задачу');
  });

  it('явное ожидание: причина из attention, без текста про мерж', () => {
    const att: FeatureAttention = { kind: 'wait', reason: 'нужно решение о мерже', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Ждёт вас');
    expect(html).toContain('нужно решение о мерже');
    expect(html).not.toContain('Отдать фичу Марку на мерж?');
  });
});

describe('FeatureRunView — плашка «исполнитель упал»', () => {
  it('с linkedSessionId: [Перезапустить] и [Чат исполнителя]', () => {
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z', linkedSessionId: 'sess-1' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Исполнитель упал');
    expect(html).toContain('Перезапустить');
    expect(html).toContain('Чат исполнителя');
  });

  it('без linkedSessionId: [Чат исполнителя] не рендерится', () => {
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Исполнитель упал');
    expect(html).toContain('Перезапустить');
    expect(html).not.toContain('Чат исполнителя');
  });
});

describe('FeatureRunView — блок «Сейчас»', () => {
  it('карточка на каждый элемент now[] (заголовок задачи + шаг)', () => {
    const now: FeatureNow[] = [
      nowItem({ taskId: 'n1', stepRef: 'Ш7', title: 'Блокеры финала', startedAt: '2026-10-17T10:00:00Z' }),
      nowItem({ taskId: 'n2', stepRef: 'Ш8', title: 'Доработка экрана', startedAt: '2026-10-17T10:05:00Z' }),
    ];
    const html = render(run({ ready: false, now }));
    expect(html).toContain('Сейчас');
    expect(html).toContain('Блокеры финала');
    expect(html).toContain('Доработка экрана');
    expect(html).toContain('Ш7');
    expect(html).toContain('Ш8');
  });

  it('у закрытой фичи «Сейчас» нет (даже если now[] не пуст)', () => {
    const now: FeatureNow[] = [nowItem({ taskId: 'n1', title: 'не должно быть' })];
    const html = render(run({ closed: true, now, summary: { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 } }));
    expect(html).not.toContain('>Сейчас<');
  });
});

describe('FeatureRunView — итог смерженной фичи', () => {
  it('«Фича смержена» + строка из summary (дата · длительность · задачи · шаги · ↺)', () => {
    const summary: FeatureSummary = { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 };
    const html = render(run({ closed: true, summary }));
    expect(html).toContain('Фича смержена');
    expect(html).toContain(localDateTime('2026-10-15T21:09:00Z'));
    expect(html).toContain('6 ч 10 мин');
    expect(html).toContain('14');
    expect(html).toContain('9');
    expect(html).toContain('↺3');
  });
});

describe('FeatureRunView — пустое состояние и легенда', () => {
  it('шагов 0: пустое состояние, легенды нет', () => {
    const html = render(run({ steps: [] }));
    expect(html).toContain('Шагов пока нет');
    expect(html).toContain('План появится');
    // Легенда (кольца + «нет в трекере») при пустом состоянии не рисуется.
    expect(html).not.toContain('нет в трекере');
  });

  it('когда шаги есть: легенда (вернул / принял с правками / принял / нет в трекере)', () => {
    const html = render(run()); // run() несёт один шаг
    expect(html).toContain('вернул');
    expect(html).toContain('принял с правками');
    expect(html).toContain('принял');
    expect(html).toContain('нет в трекере');
  });
});

describe('FeatureRunView — что ушло (заглушки и лишние плашки)', () => {
  it('нет «Принять», «Изменить ход», «Финал зелёный», «Ревью не было»', () => {
    const html = render(run());
    expect(html).not.toContain('Принять');
    expect(html).not.toContain('Изменить ход');
    expect(html).not.toContain('Финал зелёный');
    expect(html).not.toContain('Ревью не было');
  });
});

describe('FeatureRunView — конвейер (PipelineFlow) на месте', () => {
  it('одна секция «Конвейер», стадии рендерятся', () => {
    const html = render(run());
    expect(html).toContain('Конвейер');
    expect(html).toContain('Производство');
    expect(html).toContain('Ревью');
  });
});

describe('FeatureRunView — мобила (M7)', () => {
  it('useIsMobile не рвёт SSR-рендер', () => {
    expect(() => render(run())).not.toThrow();
  });
});

describe('Гейт фич-флага (M2)', () => {
  const todo: TodoItem = { content: 'Шаг плана', status: 'pending' };
  const sid = 's1';

  it('runs=[] → пилюля в режиме плана (нет «исполнение фичи»)', () => {
    __test.reset();
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [todo], runs: [], sessionId: sid }));
    expect(html).toContain('Показать план хода');
    expect(html).not.toContain('Показать исполнение фичи');
  });

  it('runs=[run] → пилюля в режиме фичи', () => {
    __test.reset();
    __test.setRuns(sid, [run()]);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [run()], sessionId: sid }));
    expect(html).toContain('Показать исполнение фичи');
  });
});

// === F6.1: доработка по ревью Ф4–Ф6 (Майя M3/M8/M9/M10, Глеб) ===
// Мок тоста (lib/toast) и api (lib/api): hoisted-состояние, сбрасывается в beforeEach.
const { toastMock, apiMock } = vi.hoisted(() => ({
  toastMock: { calls: [] as string[] },
  apiMock: { executeImpl: async (_id: string) => {} },
}));
vi.mock('../../../lib/toast', () => ({
  showToast: (title: string) => { toastMock.calls.push(title); },
}));
vi.mock('../../../lib/api', () => ({
  api: { tasks: { execute: (id: string) => apiMock.executeImpl(id) } },
}));

// <button> по тексту и fullWidth (триггер плашки не fullWidth, кнопка диалога — fullWidth).
type AnyNode = { nodeType: number; nodeName: string; childNodes: AnyNode[]; style: Record<string, string> };
function collectText(n: AnyNode): string {
  let out = '';
  const walk = (x: AnyNode) => { if (x.nodeType === 3) out += x.nodeValue ?? ''; for (const c of x.childNodes) walk(c); };
  walk(n);
  return out;
}
function findButton(root: AnyNode, text: string, fullWidth: boolean): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && (x.style?.width === '100%') === fullWidth && collectText(x).includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return hit;
}

describe('FeatureRunView: F6.1 (плашка)', () => {
  it('M3: кнопки плашки — md (minHeight 40) на мобиле, xs (minHeight 24) на десктопе', () => {
    const att: FeatureAttention = { kind: 'wait', reason: 'мерж', since: '2026-10-17T10:00:00Z', taskId: 'f1' };
    // Мобила: size md → min-height:40px.
    mobileFlag.value = true;
    const htmlMobile = render(run({ ready: false, attention: att }));
    const atM = htmlMobile.indexOf('Попросить Марка');
    expect(htmlMobile.slice(atM - 400, atM), 'мобила — md (40px)').toContain('min-height:40px');
    // Десктоп: size xs → min-height:24px.
    mobileFlag.value = false;
    const htmlDesk = render(run({ ready: false, attention: att }));
    const atD = htmlDesk.indexOf('Попросить Марка');
    expect(htmlDesk.slice(atD - 400, atD), 'десктоп — xs (24px)').toContain('min-height:24px');
  });

  it('M8: плашка «упал» — «{Имя} · {stepRef} {название} — {reason}»; пустой reason не даёт «— .»', () => {
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z', taskId: 's1', personaId: 'denis' };
    // run() по умолчанию несёт шаг s1 (stepRef Ш1, title «Шаг 1»).
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Денис · Ш1 Шаг 1 — auth_failed');
    // Пустой reason: «{Имя} · {stepRef} {название}» без висящего «— .».
    const att2: FeatureAttention = { kind: 'stopped', reason: '', since: '2026-10-17T10:00:00Z', taskId: 's1', personaId: 'denis' };
    const html2 = render(run({ ready: false, attention: att2 }));
    expect(html2).toContain('Денис · Ш1 Шаг 1');
    expect(html2).not.toContain('— .');
  });

  it('M9: нулевые/отсутствующие минуты — «Ждёт вас.» / «только что», без «0 мин» и висящего пробела', () => {
    // wait, since = now → 0 мин → «Ждёт вас.» (без «0 мин»).
    const attWait: FeatureAttention = { kind: 'wait', reason: 'мерж', since: new Date().toISOString(), taskId: 'f1' };
    const htmlWait = render(run({ ready: false, attention: attWait }));
    expect(htmlWait).toContain('Ждёт вас.');
    expect(htmlWait).not.toContain('0 мин');
    // stopped, since = now → 0 мин → «только что».
    const attStop: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: new Date().toISOString() };
    const htmlStop = render(run({ ready: false, attention: attStop }));
    expect(htmlStop).toContain('Исполнитель упал только что.');
    expect(htmlStop).not.toContain('0 мин');
    // wait без since → «Ждёт вас.» (без числа и висящего пробела).
    const attNoSince: FeatureAttention = { kind: 'wait', reason: 'мерж', taskId: 'f1' };
    const htmlNoSince = render(run({ ready: false, attention: attNoSince }));
    expect(htmlNoSince).toContain('>Ждёт вас.<');
  });

  it('Глеб: run.progress=undefined → нет «NaN%»', () => {
    const html = render(run({ progress: undefined as unknown as number }));
    expect(html).not.toContain('NaN%');
  });

  it('Глеб: «Сейчас» — пустая строка 1 (нет имени и stepRef) не рисуется', () => {
    personaMap.set('denis', 'Денис');
    const withLine1: FeatureNow[] = [nowItem({ taskId: 'n1', stepRef: 'Ш7', title: 'Блокеры финала', personaId: 'denis' })];
    const noLine1: FeatureNow[] = [nowItem({ taskId: 'n2', title: 'Блокеры финала' })]; // без personaId и stepRef
    const htmlA = render(run({ ready: false, now: withLine1 }));
    const htmlB = render(run({ ready: false, now: noLine1 }));
    // line1 («Денис · Ш7») — строка 1 (font-weight:600 + ellipsis); без имени/stepRef её нет.
    // font-weight:600 встречается и в шапке, поэтому сравниваем количество, а не наличие.
    const c600 = (h: string) => (h.match(/font-weight:600/g) ?? []).length;
    expect(c600(htmlA), 'с line1 — строка 1 на месте').toBeGreaterThan(c600(htmlB));
    // Заголовок задачи (строка 2) остаётся в обоих случаях.
    expect(htmlB).toContain('Блокеры финала');
  });

  it('M10: тихий сбой «Перезапустить» → тост «Не удалось перезапустить исполнителя»', async () => {
    const { installFakeDom, mount, reactProps, uninstallFakeDom } = await import('../__tests__/fakeDom');
    installFakeDom();
    personaMap.set('denis', 'Денис');
    apiMock.executeImpl = async () => { throw new Error('boom'); };
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z', taskId: 's1', personaId: 'denis' };
    const { root, container } = mount(createElement(FeatureRunView, { run: run({ ready: false, attention: att }) }));
    // 1) Клик по триггеру «Перезапустить» (не fullWidth) → ConfirmDialog (портал в document.body).
    const trigger = findButton(container as AnyNode, 'Перезапустить', false);
    expect(trigger, 'триггер «Перезапустить» не найден').not.toBeNull();
    act(() => (reactProps(trigger!)!.onClick as () => void)());
    // 2) Кнопка подтверждения в диалоге (fullWidth) → onConfirm → api.tasks.execute (бросает) → тост.
    const body = (globalThis as { document: AnyNode }).document.body;
    const confirm = findButton(body, 'Перезапустить', true);
    expect(confirm, 'кнопка подтверждения в диалоге не найдена').not.toBeNull();
    act(() => { (reactProps(confirm!)!.onClick as () => void)(); });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(toastMock.calls, 'тост при сбое перезапуска').toContain('Не удалось перезапустить исполнителя');
    root.unmount();
    uninstallFakeDom();
  });
});

// === K-3: меню пилюли в режиме фичи — во всю ширину ТОЛЬКО на мобиле ===
// На десктопе меню режима фичи — карточкой 280–380 (то же правило якоря, что у режима
// «План хода»): fullWidth не включается. На мобиле — во всю ширину (left/right 8).
// Карточка меню — портал в document.body; fullWidth отличим по `right:8px` в стиле.
describe('FeatureRunView: K-3 (меню пилюли: fullWidth только на мобиле)', () => {
  const sid = 'k3-sess';

  // Кнопка-пилюля: единственный <button> с aria-label, начинающимся с «Показать».
  function findPill(container: AnyNode): AnyNode | null {
    let hit: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.nodeName === 'BUTTON') {
        const al = x.getAttribute('aria-label') ?? '';
        if (al.startsWith('Показать')) { hit = x; return; }
      }
      for (const c of x.childNodes) walk(c);
    };
    walk(container);
    return hit;
  }

  // Карточка меню (портал в body): единственный элемент дерева body с non-empty `background`.
  // Подложка (backdrop) `background` не имеет, поэтому карточка недвусмысленна.
  function findMenuCard(): AnyNode | null {
    const body = (globalThis as { document: AnyNode }).document.body;
    let hit: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.style && typeof x.style.background === 'string' && x.style.background) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(body);
    return hit;
  }

  // Открывает меню пилюли (клик по кнопке) и возвращает карточку меню.
  function openPillMenu(mobile: boolean): AnyNode | null {
    mobileFlag.value = mobile;
    __test.reset();
    __test.setRuns(sid, [run()]);
    installFakeDom();
    const { root, container } = mount(createElement(TurnPlanPill, { todos: [], runs: [run()], sessionId: sid }));
    const pill = findPill(container);
    if (pill) {
      act(() => {
        (reactProps(pill)!.onClick as (e: unknown) => void)({
          currentTarget: { getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }) },
        });
      });
    }
    const card = findMenuCard();
    root.unmount();
    uninstallFakeDom();
    return card;
  }

  it('десктоп: fullWidth не включается — у карточки нет right (как у режима «План»)', () => {
    const card = openPillMenu(false);
    expect(card, 'карточка меню (десктоп) не найдена').not.toBeNull();
    expect(card!.style.right, 'десктоп: right не задан (fullWidth off)').toBeUndefined();
  });

  it('мобила: fullWidth включён — у карточки right:8px (во всю ширину)', () => {
    const card = openPillMenu(true);
    expect(card, 'карточка меню (мобила) не найдена').not.toBeNull();
    expect(card!.style.right, 'мобила: right:8px (fullWidth on)').toBe('8px');
  });
});
