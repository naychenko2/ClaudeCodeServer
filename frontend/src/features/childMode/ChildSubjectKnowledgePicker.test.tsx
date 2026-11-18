// К5c: ChildSubjectKnowledgePicker — пикер «Базы знаний» предмета (макет Д2, §3).
// Окружение — node + linkedom, как в соседних .test.tsx волны; knowledgeBases.list подменён по
// индексу вызова: без записи запрос висит in-flight (на нём проверяется гонка размонтирования).
// Select монтируется настоящий: состояние выбранного пункта читается по option.selected, смена —
// прявым вызовом onChange узла (тот же путь, что у ChildRoutePicker.test). Каждое «сменилось»
// проверяется полным циклом: onChange → значение у родителя → повторный рендер с ним.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) снять флаг отмены в эффекте → KnowledgePicker_ListAfterUnmount_NoUpdate краснеет;
// (2) не фильтровать список (показывать всё, что вернул сервер) → «ровно две attachable…» краснеет;
// (3) значение вне списка рендерить обычным (не-disabled) пунктом → «База недоступна…» краснеет.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { KnowledgeBaseSummary } from '../knowledge/types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: общий стаб для контролов, читающих matchMedia при mount (Notice, Field).
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

// Ответ list() по индексу вызова: Error — отказ (как его выносит request()); объект — ответ;
// нет записи — promise держится in-flight.
type ListResp = { configured: boolean; items: KnowledgeBaseSummary[] };
type Pending = { resolve: (v: ListResp) => void; reject: (e: unknown) => void };
let listCalls: number[] = [];
let listAnswers: (Error | ListResp)[] = [];
let listPending = new Map<number, Pending>();
let idx = 0;

vi.mock('../knowledge/api', () => ({
  knowledgeApi: {
    knowledgeBases: {
      list: () => {
        const i = idx++;
        listCalls.push(i);
        const a = listAnswers[i];
        if (a instanceof Error) return Promise.reject(a);
        if (a !== undefined) return Promise.resolve(a);
        return new Promise<ListResp>((res, rej) => { listPending.set(i, { resolve: res, reject: rej }); });
      },
    },
  },
}));

const { ChildSubjectKnowledgePicker } = await import('./ChildSubjectKnowledgePicker');

const kb = (over: Partial<KnowledgeBaseSummary>): KnowledgeBaseSummary => ({
  id: 'id', title: 'т', type: 'т', visibility: 'personal', documentCount: 0, createdAt: null, deletable: false,
  ...over,
});

// Состав по именам из постановки, в форме, которую отдаёт GET /api/knowledge (Classify):
// «учебники» — общая; «admin:kb:мат» — своя самостоятельная; «admin:notes» — своя база заметок
// (deletable=false); «persona:x» — внутренняя память (дефолт mock: не deletable); «bob:kb:y» —
// чужая (бэк её в список не отдаёт вовсе; если бы просочилась — не deletable для текущего админа).
const listResponse = (): ListResp => ({
  configured: true,
  items: [
    kb({ id: 'ds-textbook', title: 'учебники', type: 'Публичная', visibility: 'public', deletable: true, documentCount: 3 }),
    kb({ id: 'ds-math', title: 'Математика (Моро)', type: 'Самостоятельная', documentCount: 5, deletable: true }),
    kb({ id: 'ds-notes', title: 'Заметки', type: 'Заметки', documentCount: 12 }),
    kb({ id: 'ds-persona', title: 'persona:x', type: 'Память персоны', documentCount: 4 }),
    kb({ id: 'ds-bob', title: 'база Боба', type: 'Самостоятельная', documentCount: 1 }),
  ],
});

let changes: (string | null)[] = [];

async function mount(value: string | null, disabled?: boolean) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  changes = [];
  const render = (v: string | null) =>
    createElement(ChildSubjectKnowledgePicker, { value: v, onChange: (d: string | null) => { changes.push(d); }, disabled });
  await act(async () => { root.render(render(value)); });
  return {
    host: host as HTMLElement,
    root,
    // Тот же экземпляр с новым значением: полный цикл «действие → значение у родителя → поле
    // показывает новое».
    async applyValue(next: string | null) {
      await act(async () => { root.render(render(next)); });
    },
  };
}

// Как в продукте (main.tsx): StrictMode над деревом — двойной прогон эффектов на маунте.
async function mountStrict(value: string | null) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  changes = [];
  await act(async () => {
    root.render(createElement(StrictMode, null,
      createElement(ChildSubjectKnowledgePicker, { value, onChange: (d: string | null) => { changes.push(d); } })));
  });
  return { host: host as HTMLElement, root };
}

const panel = (host: HTMLElement) => host.querySelector('[data-testid="child-knowledge-picker"]') as HTMLElement;
const select = (host: HTMLElement) => panel(host).querySelector('select') as Element;

interface Opt { value: string; text: string; selected: boolean; disabled: boolean; group: string | null; }
const options = (sel: Element): Opt[] =>
  Array.from(sel.querySelectorAll('option')).map((o) => ({
    value: o.getAttribute('value') ?? '',
    text: o.textContent ?? '',
    // .selected — свойство linkedom (атрибут selected); React ставит его при сверке значения
    selected: (o as unknown as { selected?: boolean }).selected === true,
    // disabled React мог записать и атрибутом, и свойством (у option в linkedom нет геттера
    // disabled) — проверяем обе формы
    disabled: (o as unknown as { disabled?: boolean }).disabled === true || o.getAttribute('disabled') !== null,
    group: o.parentElement?.tagName === 'OPTGROUP' ? o.parentElement?.getAttribute('label') ?? null : null,
  }));

const selectedOption = (sel: Element): Opt | undefined => options(sel).find((o) => o.selected);
// «Поле показывает значение» — проверяется как «пункт с этим значением selected», а не через
// selectedOption: сеттер option.selected в linkedom снимает прежний выбор только внутри своего
// parentElement (linkedom/esm/html/option-element.js:21-25), для пунктов в optgroup это своя
// группа — между группами старый выбор повисает артефактом окружения (в браузере исключение
// держит select). Там, где selected один (первый рендер, переход к «Без базы»), читается он.
const isSelected = (sel: Element, value: string) => options(sel).some((o) => o.value === value && o.selected);

// Сменить значение контрола: React держит props узла под __reactProps$…; зовём onChange напрямую.
const reactProps = (el: Element): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};
const changeSelect = async (el: Element, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};

const notice = (host: HTMLElement, testid: string) => panel(host).querySelector(`[data-testid="${testid}"]`) as HTMLElement | null;

beforeEach(() => {
  listCalls = []; listAnswers = []; listPending = new Map(); idx = 0;
});

describe('ChildSubjectKnowledgePicker', () => {
  it('KnowledgePicker_ListAfterUnmount_NoUpdate: ответ отменённого эффекта не применяется', async () => {
    // Полное размонтирование в React 19 — наблюдаемо нулём: fiber снятого компонента отвязывается
    // в коммите (react-dom-client.development.js:14197-14214, 16299-16310), и setState по нему
    // ничего не планировает и не печатает (проверено по исходникам, не по памяти). Поэтому флаг
    // отмены проверяется там, где отмонтированный эффект живёт в живом компоненте: StrictMode
    // (как в main.tsx) делает двойной прогон эффектов на маунте — первый эффект отменён своим
    // cleanup, компонент смонтирован. Ответ первого (отменённого) запроса с list приходит, второй
    // висит: применил бы компонент ответ отменённого эффекта — список встал бы в DOM до второго
    // ответа. С флагом — поле остаётся в состоянии загрузки.
    const m = await mountStrict(null);
    expect(listCalls.length).toBe(2); // двойной прогон эффектов — два read, первый отменён
    listPending.get(0)!.resolve(listResponse());
    await act(async () => { await Promise.resolve(); }); // флюш того, что было бы запланировано
    expect(options(select(m.host)).map((o) => o.text)).toEqual(['Загрузка…']);
    // второй (живой) ответ — список на месте: тест краснеет и если флаг отменяет оба ответа
    listPending.get(1)!.resolve(listResponse());
    await act(async () => { await Promise.resolve(); });
    expect(options(select(m.host)).map((o) => o.text)).toEqual([
      'Без базы', 'Математика (Моро) · 5 док.', 'учебники · 3 док.',
    ]);
  });

  it('из списка — ровно две attachable + «Без базы»; свои первыми, подпись с числом документов', async () => {
    listAnswers.push(listResponse());
    const { host } = await mount(null);
    expect(listCalls.length).toBe(1); // один read на монтировании, записей пикер не делает
    const opts = options(select(host));
    // «учебники» (общая) и «Математика (Моро)» (соя самостоятельная) — показаны; «Заметки»,
    // «persona:x» и чужая «база Боба» — скрыты
    expect(opts.map((o) => [o.value, o.text, o.group])).toEqual([
      ['', 'Без базы', null],
      ['ds-math', 'Математика (Моро) · 5 док.', 'Мои базы'],
      ['ds-textbook', 'учебники · 3 док.', 'Общие базы'],
    ]);
    // порядок внутри групп — серверный (свежие первыми), пикер его не меняет
    expect(opts.some((o) => o.value === 'ds-notes' || o.value === 'ds-persona' || o.value === 'ds-bob')).toBe(false);
    // пустое значение — выбран «Без базы», а не пустой контрол
    expect(selectedOption(select(host))?.value).toBe('');
  });

  it('выбор «Без базы» — один onChange(null); привязанная база — выбранный пункт', async () => {
    listAnswers.push(listResponse());
    const m = await mount('ds-math');
    expect(selectedOption(select(m.host))?.value).toBe('ds-math');

    await changeSelect(select(m.host), '');
    expect(changes).toEqual([null]); // ровно один вызов
    await m.applyValue(null);
    expect(selectedOption(select(m.host))?.value).toBe('');
  });

  it('смена базы — onChange её id; полный цикл: поле показывает новую', async () => {
    listAnswers.push(listResponse());
    const m = await mount('ds-math');
    await changeSelect(select(m.host), 'ds-textbook');
    expect(changes).toEqual(['ds-textbook']);
    await m.applyValue('ds-textbook');
    expect(isSelected(select(m.host), 'ds-textbook')).toBe(true);
  });

  it('value вне списка — «База недоступна» выбрана и неактивна, список живёт рядом', async () => {
    listAnswers.push(listResponse());
    const m = await mount('ds-gone');
    const opts = options(select(m.host));
    const bad = opts.find((o) => o.value === 'ds-gone');
    expect(bad?.text).toBe('База недоступна');
    expect(bad?.selected, 'привязанное значение видно в поле').toBe(true);
    expect(bad?.disabled, 'повторно выбрать его нельзя').toBe(true);
    const n = notice(m.host, 'knowledge-base-gone');
    expect(n, 'плашка о причине').not.toBeNull();
    expect(n!.textContent).toContain('Привязанную базу удалили');
    // починить можно, выбрав настоящую базу: список живой
    await changeSelect(select(m.host), 'ds-math');
    expect(changes).toEqual(['ds-math']);
    await m.applyValue('ds-math');
    expect(isSelected(select(m.host), 'ds-math')).toBe(true);
    expect(notice(m.host, 'knowledge-base-gone')).toBeNull();
  });

  it('отказ загрузки — Notice, «Без базы» остаётся доступным выбором', async () => {
    listAnswers.push(new Error('Dify недоступен'));
    const m = await mount(null);
    const n = notice(m.host, 'knowledge-load-error');
    expect(n, 'Notice об отказе').not.toBeNull();
    expect(n!.getAttribute('role')).toBe('status');
    // поле не погасло: «Без базы» можно выбрать и при отказе
    expect((select(m.host) as unknown as { disabled?: boolean }).disabled === true || select(m.host).getAttribute('disabled') !== null).toBe(false);
    await changeSelect(select(m.host), '');
    expect(changes).toEqual([null]);
    await m.applyValue(null);
    expect(selectedOption(select(m.host))?.value).toBe('');
  });

  it('«Знания» выключены (configured=false) — disabled-поле с пунктом «Знания недоступны»', async () => {
    listAnswers.push({ configured: false, items: [] });
    const { host } = await mount(null);
    const sel = select(host);
    expect((sel as unknown as { disabled?: boolean }).disabled === true || sel.getAttribute('disabled') !== null).toBe(true);
    expect(options(sel).map((o) => o.text)).toEqual(['Знания недоступны']);
  });

  it('disabled от родителя гасит поле (состояние «Сохранение»)', async () => {
    listAnswers.push(listResponse());
    const { host } = await mount('ds-math', true);
    const sel = select(host);
    expect((sel as unknown as { disabled?: boolean }).disabled === true || sel.getAttribute('disabled') !== null).toBe(true);
    // значение при этом остаётся видимым
    expect(selectedOption(sel)?.value).toBe('ds-math');
  });
});
