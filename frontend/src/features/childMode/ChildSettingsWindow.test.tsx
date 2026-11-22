// Ф2b: каркас окна «Настройки детского режима» и раздел «Обзор».
// Проверяется: четыре раздела с подписями макета и переход по ним; слот раздела replaces
// заглушку и получает живой черновик через общий контекст окна; обзор читает профиль
// (число предметов, настроенное время, согласие и его автор, опекунов и ожидающие флага);
// опекун без флага — его ИМЯ (не id) и текст про shared-projects; пустой профиль — пустое
// состояние «с чего начать»; отказ PUT — серверный текст в открытом окне, черновик цел;
// отказ GET — «Повторить» и второй запрос; устаревший ответ (смена ребёнка, закрытое окно)
// в состояние не входит.
// Окружение — node + linkedom, как в соседних .test.tsx волны; createPortal — pass-through
// (паттерн Modal.test.ts и UserManagementModal.test.tsx: содержимое окна становится inline);
// childApi — mock по индексу вызова: без записи в answers запрос висит in-flight — на нём и
// проверяются гонка и закрытие окна. Черновик правится тестовым слотом (d) => … — тем же
// путём, каким его правят разделы Ф2c–Ф2e.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) снять проверку поколения в getChildProfile (if (genRef.current !== gen) return) →
//     «ответ прежнего ребёнка…» краснеет;
// (2) закрыть окно при отказе PUT (onClose() вместо текста в тело) → «Не сохранено…» краснеет;
// (3) не брать имена из userNames (показывать id) → «Опекун без общих проектов…» краснеет.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildSettingsProfile, ChildSettingsResponse } from './types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: Modal и Overview читают window.matchMedia при mount.
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

// Окно — портал в document.body: в тесте его содержимое становится inline.
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

// Ответ по индексу вызова каждой ручки: Error — отказ с status/body, как его выносит request()
// из lib/offline; объект — немедленный ответ; нет записи — promise держится in-flight.
type Pending = { resolve: (v: ChildSettingsResponse) => void; reject: (e: unknown) => void };
let getCalls: string[] = [];
let putCalls: { userId: string; body: unknown }[] = [];
let getAnswers: (Error | ChildSettingsResponse)[] = [];
let putAnswers: (Error | ChildSettingsResponse)[] = [];
let getPending = new Map<number, Pending>();
let putPending = new Map<number, Pending>();
let idx = { get: 0, put: 0 };

function deferred(map: Map<number, Pending>, answers: (Error | ChildSettingsResponse)[], i: number): Promise<ChildSettingsResponse> {
  const a = answers[i];
  if (a instanceof Error) return Promise.reject(a);
  if (a !== undefined) return Promise.resolve(a);
  return new Promise<ChildSettingsResponse>((res, rej) => { map.set(i, { resolve: res, reject: rej }); });
}

vi.mock('./api', () => ({
  childApi: {
    getChildProfile: (userId: string) => {
      const i = idx.get++;
      getCalls.push(userId);
      return deferred(getPending, getAnswers, i);
    },
    putChildProfile: (userId: string, body: unknown) => {
      const i = idx.put++;
      putCalls.push({ userId, body });
      return deferred(putPending, putAnswers, i);
    },
  },
}));

const { ChildSettingsWindow } = await import('./ChildSettingsWindow');
type Draft = import('./ChildSettingsWindow').ChildSettingsDraftState;

const resp = (profile: ChildSettingsProfile | null): ChildSettingsResponse => ({ profile, tutorPersonaId: 'p-tutor' });

const profile = (over: Partial<ChildSettingsProfile> = {}): ChildSettingsProfile => ({
  enabled: true,
  guardianUserIds: ['u-mom'],
  providerKey: 'anthropic',
  model: 'sonnet',
  spaces: { lessons: { windows: [{ from: '15:00', to: '19:00', weekdays: [1, 2, 3, 4, 5] }], dailyMinutes: 45 } },
  subjects: [
    { id: 'math', label: 'Математика', glyph: '2+2', color: 'info' },
    { id: 'russian', label: 'Русский язык', glyph: 'Аа', color: 'rose' },
    { id: 'english', label: 'Английский', glyph: 'Ab', color: 'warning', enabled: false },
  ],
  acknowledgeRouteRisk: true,
  routeRiskAcknowledgedAt: '2026-11-18T10:00:00',
  routeRiskAcknowledgedBy: 'Папа',
  guardians: [{ userId: 'u-mom', sharedProjectsFlag: true }],
  sharedProjectsInstanceEnabled: true,
  ...over,
});

// Профиль «другого ребёнка» — с другими числами и без опекунов: обзор называет предметы
// числом, а не именами, поэтому различаем ответ по плиткам (лимит, счёт) и по имени опекуна.
const otherProfile = profile({
  subjects: [{ id: 'geo', label: 'География', glyph: 'Г', color: 'accent' }],
  spaces: { lessons: { windows: [], dailyMinutes: 30 } },
  guardianUserIds: [], guardians: [],
});

const httpErr = (status: number, code: string, message: string) =>
  Object.assign(new Error('отказ'), { status, body: { code, message } });

// Слот-заполнитель раздела «Предметы»: получает живой черновик окна (тот же контракт, что у
// разделов Ф2c–Ф2e) и правит его; «сменилось» видно полным циклом (патч → черновик → рендер
// с ним).
function TestEditor({ draft }: { draft: Draft }) {
  return (
    <button type="button" data-testid="test-patch" onClick={() => draft.patch({ model: 'haiku' })}>
      model: {draft.profile.model} {draft.dirty ? 'изменён' : 'без правок'}
    </button>
  );
}
const editorSlot = (d: Draft) => createElement(TestEditor, { draft: d });

async function settle(fn = () => {}) {
  await act(async () => {
    fn();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

let closeCalls = 0;

async function mount(userId = 'u-kid', props: Record<string, unknown> = {}) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const el = (over: Record<string, unknown>) =>
    createElement(ChildSettingsWindow, {
      userId, userLabel: 'Маша', userNames: { 'u-mom': 'Ольга', 'u-dad': 'Папа' },
      onClose: () => { closeCalls++; }, ...over,
    });
  await act(async () => { root.render(el(props)); });
  return {
    host: host as HTMLElement,
    // Тот же смонтированный экземпляр с другим ребёнком: на нём проверяются сброс состояния
    // и отбрасывание ответа прежнего id, а не перемонтирование.
    async setUserId(next: string) { await act(async () => { root.render(el({ ...props, userId: next })); }); },
    async unmount() { await act(async () => { root.unmount(); }); },
  };
}

const win_ = (host: HTMLElement) => host.querySelector('[data-testid="child-settings-window"]') as HTMLElement;
const tabs = (host: HTMLElement) => Array.from(host.querySelectorAll('[role="tab"]')) as HTMLElement[];
const selectedTab = (host: HTMLElement) => tabs(host).find((t) => t.getAttribute('aria-selected') === 'true')?.textContent ?? '';
const byTestId = (host: HTMLElement, testid: string) => host.querySelector(`[data-testid="${testid}"]`) as HTMLElement | null;
const btn = (host: HTMLElement, testid: string) => host.querySelector(`[data-testid="${testid}"]`) as HTMLButtonElement;

async function click(el: HTMLElement) {
  await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); });
}

beforeEach(() => {
  mobile = false;
  getCalls = []; putCalls = [];
  getAnswers = []; putAnswers = [];
  getPending = new Map(); putPending = new Map();
  idx = { get: 0, put: 0 };
  closeCalls = 0;
  doc.getElementById('root')!.innerHTML = '';
});

describe('ChildSettingsWindow: каркас и навигация', () => {
  it('четыре раздела с подписями макета; открыт «Обзор»; шапка называет ребёнка', async () => {
    getAnswers[0] = resp(profile());
    const { host } = await mount();
    expect(getCalls).toEqual(['u-kid']);
    expect(tabs(host).map((t) => t.textContent)).toEqual(['Обзор', 'Предметы', 'Время и лимиты', 'Безопасность и согласие']);
    expect(selectedTab(host)).toBe('Обзор');
    expect(byTestId(host, 'child-settings-overview')).not.toBeNull();
    expect(host.textContent).toContain('Маша');
    expect(host.textContent).toContain('детский режим включён');
  });

  it('переход по разделам: активный подчёркнут, прежний раздел не остаётся; пустой слот — заглушка', async () => {
    getAnswers[0] = resp(profile());
    const { host } = await mount();
    await click(tabs(host)[2]);
    expect(selectedTab(host)).toBe('Время и лимиты');
    expect(byTestId(host, 'child-settings-slot-time')?.textContent).toContain('наполнит следующий шаг');
    expect(byTestId(host, 'child-settings-overview')).toBeNull();
    await click(tabs(host)[1]);
    expect(selectedTab(host)).toBe('Предметы');
    expect(byTestId(host, 'child-settings-slot-subjects')).not.toBeNull();
  });

  it('слот раздела получает живой черновик окна: патч → черновик → отметка «несохранённое»', async () => {
    getAnswers[0] = resp(profile());
    const { host } = await mount('u-kid', { subjectsSection: editorSlot });
    await click(tabs(host)[1]); // слот живёт под своим разделом
    expect(byTestId(host, 'child-settings-slot-subjects')).toBeNull(); // заполнен — заглушки нет
    expect(byTestId(host, 'test-patch')!.textContent).toContain('model: sonnet');
    expect(byTestId(host, 'test-patch')!.textContent).toContain('без правок');
    await click(btn(host, 'test-patch'));
    expect(byTestId(host, 'test-patch')!.textContent).toContain('model: haiku');
    expect(byTestId(host, 'child-settings-dirty')?.textContent).toContain('Есть несохранённые изменения');
  });
});

describe('ChildSettingsOverview: что читается из профиля', () => {
  it('предметы, настроенное время и согласие с автором — в плитках обзора', async () => {
    getAnswers[0] = resp(profile());
    const { host } = await mount();
    expect(byTestId(host, 'tile-subjects')?.textContent).toContain('2 из 3');
    expect(byTestId(host, 'tile-subjects')?.textContent).toContain('1 выключен');
    // Время профиля: дневной лимит и окна. Фактический расход минут ребёнка — его per-owner
    // снимок /api/child/status, админской ручки у него нет (расхождение с макетом — в отчёте).
    expect(byTestId(host, 'tile-time')?.textContent).toContain('45 мин в день');
    expect(byTestId(host, 'tile-time')?.textContent).toContain('15:00–19:00');
    expect(byTestId(host, 'tile-consent')?.textContent).toContain('дано');
    expect(byTestId(host, 'tile-consent')?.textContent).toContain('18.11.2026 · Папа');
    expect(byTestId(host, 'overview-consent-missing')).toBeNull(); // согласие дано — плашки нет
    expect(byTestId(host, 'tile-guardians')?.textContent).toContain('1');
  });

  it('согласия нет — плашка с переходом в «Безопасность и согласие», плитка говорит то же', async () => {
    getAnswers[0] = resp(profile({ acknowledgeRouteRisk: false, routeRiskAcknowledgedAt: null, routeRiskAcknowledgedBy: null }));
    const { host } = await mount();
    expect(byTestId(host, 'tile-consent')?.textContent).toContain('не дано');
    expect(byTestId(host, 'overview-consent-missing')?.textContent).toContain('Согласие на риск маршрута не дано');
    await click(btn(host, 'overview-go-safety'));
    expect(selectedTab(host)).toBe('Безопасность и согласие');
  });

  it('опекун без флага — его имя и текст про shared-projects; строка опекуна — с тем же флагом', async () => {
    getAnswers[0] = resp(profile({
      guardianUserIds: ['u-mom', 'u-dad'],
      guardians: [{ userId: 'u-mom', sharedProjectsFlag: false }, { userId: 'u-dad', sharedProjectsFlag: true }],
    }));
    const { host } = await mount();
    const notice = byTestId(host, 'overview-guardians-flag')!;
    expect(notice.textContent).toContain('Ольга'); // имя из userNames, не id
    expect(notice.textContent).toContain('shared-projects');
    expect(notice.textContent).not.toContain('u-mom');
    expect(byTestId(host, 'tile-guardians')?.textContent).toContain('1 без флага');
    expect(byTestId(host, 'overview-guardian-u-mom')?.textContent).toContain('нужен флаг shared-projects');
    expect(byTestId(host, 'overview-guardian-u-dad')?.textContent).toContain('shared-projects включён');
    await click(btn(host, 'overview-go-guardians'));
    expect(selectedTab(host)).toBe('Безопасность и согласие');
  });

  it('рубильник инстанса выключен — об этом сказано прямо, хотя флаги опекунов включены', async () => {
    getAnswers[0] = resp(profile({ sharedProjectsInstanceEnabled: false }));
    const { host } = await mount();
    expect(byTestId(host, 'overview-guardians-flag')).toBeNull(); // флаги включены — плашки про флаг нет
    expect(host.textContent).toContain('Механизм общих проектов на сервере выключен');
  });

  it('пустой профиль — «Предметов пока нет» с переходом в «Предметы», нули плиток и плашка согласия', async () => {
    getAnswers[0] = resp(profile({ subjects: [], acknowledgeRouteRisk: false, guardians: [], guardianUserIds: [] }));
    const { host } = await mount();
    expect(host.textContent).toContain('Предметов пока нет');
    expect(byTestId(host, 'tile-subjects')?.textContent).toContain('нет');
    expect(byTestId(host, 'tile-time')?.textContent).toContain('45 мин в день');
    expect(byTestId(host, 'tile-guardians')?.textContent).toContain('0');
    expect(host.textContent).toContain('Опекунов нет');
    await click(btn(host, 'overview-go-subjects'));
    expect(selectedTab(host)).toBe('Предметы');
  });
});

describe('Запись и отказ', () => {
  it('правок нет — «Сохранить» недоступен; патч → PUT с изменённым телом → черновик чист', async () => {
    getAnswers[0] = resp(profile());
    putAnswers[0] = resp(profile({ model: 'haiku' })); // сервер возвращает то, что сохранил
    const { host } = await mount('u-kid', { subjectsSection: editorSlot });
    await click(tabs(host)[1]);
    expect(btn(host, 'child-settings-save').disabled).toBe(true);
    await click(btn(host, 'test-patch'));
    expect(btn(host, 'child-settings-save').disabled).toBe(false);
    await click(btn(host, 'child-settings-save'));
    await settle();
    expect(putCalls[0].userId).toBe('u-kid');
    expect((putCalls[0].body as ChildSettingsProfile).model).toBe('haiku');
    expect(byTestId(host, 'child-settings-save-error')).toBeNull();
    expect(byTestId(host, 'child-settings-dirty')?.textContent).toContain('Изменений нет');
    expect(byTestId(host, 'test-patch')!.textContent).toContain('без правок');
    expect(closeCalls).toBe(0);
  });

  it('отказ PUT — серверный текст в открытом окне, черновик и отметка «несохранённое» целы', async () => {
    getAnswers[0] = resp(profile());
    putAnswers[0] = httpErr(400, 'guardian_not_found', 'Среди опекунов есть id, которого нет среди пользователей.');
    const { host } = await mount('u-kid', { subjectsSection: editorSlot });
    await click(tabs(host)[1]);
    await click(btn(host, 'test-patch'));
    await click(btn(host, 'child-settings-save'));
    await settle();
    expect(putCalls).toHaveLength(1);
    const notice = byTestId(host, 'child-settings-save-error')!;
    expect(notice.textContent).toContain('Среди опекунов есть id, которого нет среди пользователей.');
    expect(win_(host)).not.toBeNull(); // окно НЕ закрыто
    expect(closeCalls).toBe(0);
    expect(byTestId(host, 'child-settings-dirty')?.textContent).toContain('Есть несохранённые изменения');
    expect(byTestId(host, 'test-patch')!.textContent).toContain('model: haiku'); // черновик не стёрт
  });

  it('отказ GET — «Настройки не загрузились» с серверным текстом и «Повторить»: повтор читает заново', async () => {
    getAnswers[0] = httpErr(404, 'user_not_found', 'Нет такого пользователя.');
    const { host } = await mount();
    expect(byTestId(host, 'child-settings-load-error')?.textContent).toContain('Нет такого пользователя.');
    expect(byTestId(host, 'child-settings-overview')).toBeNull();
    await click(btn(host, 'child-settings-retry'));
    expect(getCalls).toEqual(['u-kid', 'u-kid']);
    await settle(() => { getPending.get(1)?.resolve(resp(profile())); });
    expect(byTestId(host, 'child-settings-load-error')).toBeNull();
    expect(byTestId(host, 'child-settings-overview')).not.toBeNull();
  });

  it('ответ был, профиля нет: «Профиля настроек нет» — вместо вечного «Загружаю…»', async () => {
    getAnswers[0] = resp(null);
    const { host } = await mount();
    expect(byTestId(host, 'child-settings-no-profile')?.textContent).toContain('Профиля настроек нет');
    expect(byTestId(host, 'child-settings-loading')).toBeNull();
    expect(btn(host, 'child-settings-save').disabled).toBe(true);
  });
});

describe('Конкурентность', () => {
  it('ответ прежнего ребёнка, пришедший после смены, в состояние не входит', async () => {
    const { host, setUserId } = await mount('u-ann'); // его запрос висит (индекс 0)
    await setUserId('u-boris'); // его ответ — индекс 1
    expect(getCalls).toEqual(['u-ann', 'u-boris']);
    await settle(() => { getPending.get(1)?.resolve(resp(otherProfile)); });
    expect(byTestId(host, 'child-settings-overview')).not.toBeNull();
    expect(byTestId(host, 'tile-time')?.textContent).toContain('30 мин в день');
    expect(byTestId(host, 'tile-subjects')?.textContent).toContain('1 из 1');
    await settle(() => { getPending.get(0)?.resolve(resp(profile())); }); // устаревший ответ Анны
    expect(byTestId(host, 'tile-time')?.textContent).toContain('30 мин в день');
    expect(byTestId(host, 'tile-subjects')?.textContent).toContain('1 из 1');
    expect(host.textContent).not.toContain('45 мин в день');
    expect(host.textContent).not.toContain('2 из 3');
    expect(host.textContent).not.toContain('Ольга');
    expect(byTestId(host, 'child-settings-loading')).toBeNull();
  });

  it('окно закрыто, ответ висящий: он не поднимает состояния и не оставляет неохваченного отказа', async () => {
    const { host, unmount } = await mount('u-kid');
    await unmount();
    expect(host.textContent).toBe('');
    await settle(() => { getPending.get(0)?.resolve(resp(profile())); });
    expect(host.textContent).toBe('');
  });
});

// L4 аудита 21.11 (макет kids-settings-window.html:1066-1073): на телефоне разделы —
// список рядов-карточек с переходом в раздел и «←», не горизонтальный ряд вкладок,
// где четвёртая подпись уходит за кадр. Десктоп остаётся на вкладках (вариант A).
describe('Мобильная навигация (L4 аудита)', () => {
  beforeEach(() => {
    mobile = true;
    getAnswers = [resp(profile())];
  });

  it('разделы — список рядов-карточек, вкладок нет', async () => {
    const { host, unmount } = await mount();
    const navNodes = Array.from(host.querySelectorAll('[data-testid^="child-settings-nav-"]')) as unknown as HTMLElement[];
    const items = navNodes.filter((n) => n.getAttribute('data-testid') !== 'child-settings-nav-list');
    expect(byTestId(host, 'child-settings-nav-list'), 'списка разделов нет').not.toBeNull();
    expect(items.length, 'рядов списка').toBe(4);
    expect(items.map((n) => n.textContent)).toEqual(['Обзор', 'Предметы', 'Время и лимиты', 'Безопасность и согласие']);
    expect(host.querySelectorAll('[role="tab"]').length, 'вкладки на мобиле остались').toBe(0);
    await unmount();
  });

  it('тап по разделу — экран раздела с «←»; «←» — список; слот раздела живёт', async () => {
    const { host, unmount } = await mount();
    await click(byTestId(host, 'child-settings-nav-time')!);
    expect(byTestId(host, 'child-settings-nav-list'), 'список не скрылся').toBeNull();
    const bar = byTestId(host, 'child-settings-back-bar');
    expect(bar, 'бара «назад» нет').not.toBeNull();
    expect(bar?.textContent, 'имя раздела в баре').toContain('Время и лимиты');
    expect(byTestId(host, 'child-settings-slot-time'), 'содержимого раздела нет').not.toBeNull();
    expect(host.querySelectorAll('[role="tab"]').length).toBe(0);
    const back = bar!.querySelector('[aria-label="К разделам"]') as HTMLElement;
    await click(back);
    expect(byTestId(host, 'child-settings-nav-list'), 'список не вернулся').not.toBeNull();
    expect(byTestId(host, 'child-settings-back-bar')).toBeNull();
    await unmount();
  });
});
