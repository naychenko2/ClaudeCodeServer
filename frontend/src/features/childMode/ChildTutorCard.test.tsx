// Т4e: карточка репетитора — сохранение через putTutor (тело с voice из формы), списки
// «строка = пункт», аватар только через админские URL (ни одной строки /api/personas/
// в запросах и в src), галерея кандидатов → select, гонка смены personaId и отказ ручки.
// Х9: в пикере голоса карточки нет мёртвой опции «Голос по умолчанию» (бэк игнорирует
// null голоса), поле правил сверх 12 пунктов — в состоянии ошибки контрола.
// Окружение — node + linkedom, как в соседних .test.tsx волны. childApi мокается;
// URL-хелперы — РЕАЛЬНЫЕ (importOriginal): src кандидатов проверяется настоящими строками.
// lib/api мокается рекордером: единственный потребитель в графе — пикер голоса (tts.voices);
// любой вызов api.personas.* записывается и краснит тест «нет /api/personas/».

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile читает window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
  // Размеры окна для расчёта позиции Menu (в linkedom их нет)
  innerWidth: 1280, innerHeight: 800,
});
// Попап пикера голоса в linkedom: getBoundingClientRect (прямоугольник триггера — якорь
// Menu) и scrollIntoView (протятка к выбранной строке) здесь нет. Тест проверяет состав
// списка, а не геометрию — оба подменены no-op. Оба шима — про окружение, не про компонент.
(win as unknown as { HTMLElement: { prototype: { getBoundingClientRect?: unknown } } })
  .HTMLElement.prototype.getBoundingClientRect = () =>
  ({ x: 0, y: 0, top: 0, left: 0, right: 270, bottom: 36, width: 270, height: 36 });
(win as unknown as { Element: { prototype: { scrollIntoView?: unknown } } })
  .Element.prototype.scrollIntoView = () => {};
// Среда доводится до браузерной семантики textarea — иначе React в linkedom не может ни
// показать controlled value (initTextarea пишет через defaultValue, которого у linkedom
// нет), ни выдать onChange (isEventSupported('input') проверяет 'oninput' in document).
// Без этого поля карточки нельзя ни предзаполнить, ни набрать — а это ровно то, что
// проверяет тест. Оба шима — про окружение, не про компонент.
Object.defineProperty(win.HTMLTextAreaElement.prototype, 'defaultValue', {
  configurable: true,
  get() { return this.textContent; },
  set(v: string) { this.textContent = v == null ? '' : String(v); },
});
(doc as unknown as { oninput?: unknown }).oninput = null;
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// react-dom импортируется после стабов: canUseDOM и isInputEventSupported вычисляются на
// его загрузке — при статическом импорте они были бы false (window ещё не.stubbed).
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');

// childApi-рекордер: ответ по индексу вызова; нет записи — promise держится in-flight
// (на этом проверяются гонка смены репетитора и блокировка повторного клика).
let calls: { path: string; args: unknown[] }[] = [];
let answers: (Error | object)[] = [];
let pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let callIdx = 0;

function record(path: string, ...args: unknown[]): Promise<unknown> {
  const i = callIdx++;
  calls.push({ path, args });
  const a = answers[i];
  if (a instanceof Error) return Promise.reject(a);
  if (a) return Promise.resolve(a);
  return new Promise((res, rej) => pending.set(i, { resolve: res, reject: rej }));
}

const fakeChildApi = {
  putTutor: (userId: string, personaId: string, body: unknown) => record('putTutor', userId, personaId, body),
  tutorAvatar: {
    generate: (userId: string, personaId: string) => record('generate', userId, personaId),
    upload: (userId: string, personaId: string) => record('upload', userId, personaId),
    select: (userId: string, personaId: string, file: string) => record('select', userId, personaId, file),
  },
};

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, childApi: fakeChildApi };
});

// lib/api: пикер голоса зовёт только tts.voices; любое обращение к api.personas.* —
// в рекордер (тест «никаких /api/personas/» краснеет на первой же записи).
const apiCalls: string[] = [];
// Реальный голос в каталоге: при выбранном триггер пикера показывает его именем, а не
// «Голосом по умолчанию», и список открывается непустым — на этом проверяется отсутствие
// мёртвой опции (пустой каталог сделал бы проверку пустой)
const VOICES = [{ voice: 'alena', label: 'Алена · спокойная', gender: 'female', roles: ['good'], speedSupported: true }];
vi.mock('../../lib/api', () => ({
  api: {
    tts: {
      voices: () => { apiCalls.push('tts.voices'); return Promise.resolve({ voices: VOICES, configured: true }); },
    },
    personas: new Proxy({}, {
      get: () => () => { apiCalls.push('personas'); return Promise.resolve(null); },
    }),
  },
}));

const { ChildTutorCard } = await import('./ChildTutorCard');
import type { ChildTutorInitial } from './ChildTutorCard';

const httpErr = (status: number, code?: string, message?: string) =>
  Object.assign(new Error('отказ'), { status, body: { ...(code ? { code } : {}), ...(message ? { message } : {}) } });

let savedNames: string[] = [];

interface MountProps { userId: string; personaId: string; initial?: ChildTutorInitial | null }

async function mount(p: MountProps) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const render = (q: MountProps) => act(async () => {
    root.render(createElement(ChildTutorCard, { ...q, onSaved: (n: string) => { savedNames.push(n); } }));
  });
  await render(p);
  return {
    host: host as HTMLElement,
    // Тот же смонтированный экземпляр с другим репетитором: на этом проверяется сброс
    // черновика и отбрасывание ответа прежнего personaId, а не перемонтирование.
    async rerender(next: Partial<MountProps>) { await render({ ...p, ...next }); },
  };
}

const card = (host: HTMLElement) => host.querySelector('[data-testid="child-tutor-card"]') as HTMLElement;
const ta = (host: HTMLElement, label: string) =>
  card(host).querySelector(`textarea[aria-label="${label}"]`) as HTMLTextAreaElement;
const inputByLabel = (host: HTMLElement, label: string) => {
  const lbl = Array.from(card(host).querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('input') ?? null) as HTMLInputElement | null;
};
const buttonByText = (host: HTMLElement, text: string) =>
  Array.from(card(host).querySelectorAll('button')).find((b) => (b.textContent ?? '').includes(text)) as HTMLButtonElement;
const noticeText = (host: HTMLElement) =>
  (card(host).querySelector('[role="status"]') as HTMLElement | null)?.textContent ?? '';
const imgSrcs = (host: HTMLElement) =>
  Array.from(card(host).querySelectorAll('img')).map((i) => i.getAttribute('src') ?? '');
// Открытый Menu/Modal рендерится порталом в body — список проверяется по всему body,
// карточка (с триггером пикера) тоже внутри него
const bodyText = () => doc.body.textContent ?? '';

// Ввод в textarea: родной сеттер prototype обходит value-обёртку React (трекер значения
// остаётся старым), затем input-событие — React видит расхождение и зовёт onChange.
async function type(el: HTMLTextAreaElement, value: string) {
  const proto = (win as unknown as { HTMLTextAreaElement?: { prototype: object } }).HTMLTextAreaElement?.prototype;
  const desc = proto ? Object.getOwnPropertyDescriptor(proto, 'value') : undefined;
  if (desc?.set) desc.set.call(el, value);
  else el.value = value;
  await act(async () => { el.dispatchEvent(new win.Event('input', { bubbles: true })); });
}

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
  await act(async () => {});
}

async function settle() { await act(async () => { await Promise.resolve(); }); }

const bodyOf = (i: number) => calls[i].args[2] as Record<string, unknown>;
const initialFull = (): ChildTutorInitial => ({
  name: 'Сова Ульяна',
  character: 'Спокойная сова-учительница.',
  mustDo: ['Хвали за попытку'],
  mustNot: [],
  voice: { voice: 'alena', role: 'good', speed: 1 },
});

beforeEach(() => {
  calls = [];
  answers = [];
  pending = new Map();
  callIdx = 0;
  apiCalls.length = 0;
  savedNames = [];
  mobile = false;
  doc.getElementById('root')!.innerHTML = '';
});

describe('ChildTutorCard', () => {
  it('Save_CallsPutTutor_NameCharacterVoice', async () => {
    // Ответ ручки — снимок персоны ДО применения голоса (ревью Т4a): у намеренно другого
    // голоса в ответе проверяется, что пикер его не берёт (второй сохран ниже).
    answers = [{ name: 'Сова Ульяна', voice: { voice: 'other', role: null, speed: null } }];
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: initialFull() });
    await click(buttonByText(m.host, 'Сохранить'));
    const put = calls.find((c) => c.path === 'putTutor');
    expect(put).toBeTruthy();
    expect(put!.args[0]).toBe('u1');
    expect(put!.args[1]).toBe('p1');
    const body = bodyOf(calls.indexOf(put!));
    expect(body.name).toBe('Сова Ульяна');
    expect(body.character).toBe('Спокойная сова-учительница.');
    expect(body.voice).toEqual({ voice: 'alena', role: 'good', speed: 1 });
    expect(savedNames).toEqual(['Сова Ульяна']);
    // Голос после сохранения — значение формы, не из ответа: второе сохранение шлёт
    // alena (из формы), а не other (из ответа первой ручки)
    await click(buttonByText(m.host, 'Сохранить'));
    const second = calls.filter((c) => c.path === 'putTutor')[1];
    expect((second.args[2] as Record<string, unknown>).voice).toEqual({ voice: 'alena', role: 'good', speed: 1 });
  });

  it('Lists_OneLinePerItem_EmptyFieldSendsEmptyArray', async () => {
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Кот Буквоед' } });
    await type(ta(m.host, 'Обязательно'), 'А\n\nБ');
    // «Никогда» остаётся пустым полем
    await click(buttonByText(m.host, 'Сохранить'));
    const body = bodyOf(calls.findIndex((c) => c.path === 'putTutor'));
    expect(body.mustDo).toEqual(['А', 'Б']);
    expect(body.mustNot).toEqual([]);
  });

  it('Initial_TwoMustDoShownAsTwoLines', async () => {
    const m = await mount({
      userId: 'u1', personaId: 'p1',
      initial: { name: 'Мисс Пиппа', mustDo: ['Правило одно', 'Правило два'] },
    });
    expect(ta(m.host, 'Обязательно').value).toBe('Правило одно\nПравило два');
    expect(ta(m.host, 'Никогда').value).toBe('');
  });

  it('Generate_ShowCandidates_ClickSelects', async () => {
    answers = [{ candidates: ['cand-1.png', 'cand-2.png'] }];
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Лесовичок Тим' } });
    await click(buttonByText(m.host, 'Сгенерировать'));
    expect(calls.some((c) => c.path === 'generate' && c.args[0] === 'u1' && c.args[1] === 'p1')).toBe(true);
    const srcs = imgSrcs(m.host);
    expect(srcs.some((s) => s.startsWith('/api/users/u1/child-tutors/p1/avatar/candidate/cand-1.png'))).toBe(true);
    expect(srcs.some((s) => s.startsWith('/api/users/u1/child-tutors/p1/avatar/candidate/cand-2.png'))).toBe(true);
    const first = card(m.host).querySelector('[role="group"] button') as HTMLButtonElement;
    await click(first);
    const sel = calls.find((c) => c.path === 'select');
    expect(sel).toBeTruthy();
    expect(sel!.args).toEqual(['u1', 'p1', 'cand-1.png']);
  });

  it('NoPersonaEndpoints_InRequestsOrImgSrc', async () => {
    answers = [{ candidates: ['cand-1.png'] }];
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Гроссмейстер Ёж' } });
    await click(buttonByText(m.host, 'Сгенерировать'));
    // главный аватар и кандидат — только админские пути
    const srcs = imgSrcs(m.host);
    expect(srcs.some((s) => s.startsWith('/api/users/u1/child-tutors/p1/avatar'))).toBe(true);
    expect(srcs.some((s) => s.includes('/api/personas/'))).toBe(false);
    // ни одного обращения к api.personas.* (единственный разрешённый потребитель — tts.voices)
    expect(apiCalls.filter((a) => a.startsWith('personas'))).toEqual([]);
  });

  it('ChildTutorCard_StaleSaveAfterSwitch_Ignored', async () => {
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Первый' } });
    await act(async () => { buttonByText(m.host, 'Сохранить').click(); }); // putTutor для p1 — in-flight
    // Смена репетитора до ответа: черновик — новый, форма — для p2
    await m.rerender({ personaId: 'p2', initial: { name: 'Второй' } });
    pending.get(0)!.resolve({ name: 'Первый' }); // запоздавший ответ прежнего p1
    await settle();
    // Ответ прежнего personaId не зовёт onSaved и не трогает форму нового
    expect(savedNames).toEqual([]);
    expect(inputByLabel(m.host, 'Имя')!.value).toBe('Второй');
  });

  it('EmptyPersonaId_ShowsPlaceholderText', async () => {
    const m = await mount({ userId: 'u1', personaId: '' });
    expect(card(m.host).textContent).toContain('Репетитор появится после сохранения профиля');
    expect(card(m.host).querySelector('textarea')).toBeNull();
    expect(calls.length).toBe(0);
  });

  it('Rejection_ShowsNoticeWithServerMessage', async () => {
    answers = [httpErr(400, 'rules_too_long', 'Правил «Обязательно / Никогда» — не больше 12, каждое — не длиннее 300 символов.')];
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Сказочница Агата' } });
    await click(buttonByText(m.host, 'Сохранить'));
    expect(noticeText(m.host)).toContain('не больше 12');
    // после отказа карточка снова готова к правке: кнопка не в загрузке
    expect(buttonByText(m.host, 'Сохранить').disabled).toBe(false);
  });

  it('SecondSaveClickDuringRequest_Ignored', async () => {
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Кот Буквоед' } });
    const save = buttonByText(m.host, 'Сохранить');
    await act(async () => { save.click(); save.click(); }); // два клика до перерисовки: один запрос
    expect(calls.filter((c) => c.path === 'putTutor').length).toBe(1);
  });

  it('SecondGenerateClickDuringRequest_Ignored', async () => {
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Кот Буквоед' } });
    const gen = buttonByText(m.host, 'Сгенерировать');
    await act(async () => { gen.click(); gen.click(); }); // два клика до перерисовки: один запрос
    expect(calls.filter((c) => c.path === 'generate').length).toBe(1);
  });

  it('VoicePicker_NoDefaultOption', async () => {
    // Бэк игнорирует null голоса (ChildTutorsController.cs:170 — «не трогать»): выбор
    // «Голос по умолчанию» в этой карточке ничего не меняет — мёртвая опция, её нет
    const m = await mount({ userId: 'u1', personaId: 'p1', initial: initialFull() });
    // триггер с выбранным голосом показывает его именем, а не «по умолчанию»
    const trigger = buttonByText(m.host, 'Алена');
    expect(trigger).toBeTruthy();
    await click(trigger);
    // список открылся и отрисован: иначе «отсутствия опции» было бы пустой проверкой
    expect(bodyText()).toContain('Обычные');
    expect(bodyText()).toContain('спокойная');
    expect(bodyText()).not.toContain('Голос по умолчанию');
  });

  it('RulesField_ErrorStateOverLimit', async () => {
    // 13 пунктов — поле в состоянии ошибки контрола (aria-invalid + красный бордер,
    // как у остальных полей), 12 — нет; состояние следует текущему счёту, не предзаполнению
    const many = Array.from({ length: 13 }, (_, i) => `правило ${i + 1}`);
    const over = await mount({ userId: 'u1', personaId: 'p1', initial: { name: 'Кот Буквоед', mustDo: many, mustNot: many } });
    expect(ta(over.host, 'Обязательно').getAttribute('aria-invalid')).toBe('true');
    expect(ta(over.host, 'Никогда').getAttribute('aria-invalid')).toBe('true');
    const at = await mount({ userId: 'u1', personaId: 'p2', initial: { name: 'Кот Буквоед', mustDo: many.slice(0, 12) } });
    expect(ta(at.host, 'Обязательно').getAttribute('aria-invalid')).toBeNull();
    await type(ta(at.host, 'Обязательно'), many.join('\n')); // 12 → 13: состояние появилось
    expect(ta(at.host, 'Обязательно').getAttribute('aria-invalid')).toBe('true');
    await type(ta(at.host, 'Обязательно'), many.slice(0, 12).join('\n')); // убрано лишнее — поле чистое
    expect(ta(at.host, 'Обязательно').getAttribute('aria-invalid')).toBeNull();
  });
});
