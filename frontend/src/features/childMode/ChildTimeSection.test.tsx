// Ф2d: раздел «Время и лимиты» — onChange-компонент окна настроек ребёнка. Кейсы перенесены
// из тестов прежнего редактора профиля ребёнка (файл уже удалён): «Минут в день» — 8 (некорректные 5 + пустое + «45» +
// выключенный режим), «Предупредить за» — 5 (некорректные 3 + пустое→5 + «15»), подсветка времени — 1 (окно «25:00»),
// ещё 1 перенесён с изменением формы (окно через полночь → в values), поля окна в одну колонку и тач-цели ≥ 40px —
// адаптирован под элементы раздела; перечень перенесённого здесь не полный. Добавлены и новые кейсы
// с Ф2d: «абв» в цикле, новое окно в values («Добавить окно», заполнение, галка дня, удаление),
// валидационные бюджеты («1,5», пустые → null), мобильные ряды «Бюджет на день» и «Бюджет на месяц»
// в одну колонку, эхо, внешние значения. Вместо «ни одного запроса»
// у образца здесь — «из раздела ничего не выходит»: раздел не делает запросов, он отдаёт
// родителю целые корректные значения, и на них же проверяется «сохранение заблокировано».
// Окружение — node + linkedom, как в соседних .test.tsx волны.
//
// Родитель в тесте честный: хранит то, что послал раздел, и после каждого действия,
// изменившего хранимые значения, перерисовывает экземпляр новым объектом values (как каркас,
// который выводит values из своего черновика на каждый рендер). Без этого само-рендеры
// раздела видели бы прежние values и перечитывали черновик по правилу эха — тест ловил бы
// не поведение раздела, а свою же лень. Эхо (те же значения, новый объект) на этом же и
// проверяется: черновик человека от него не сбрасывается.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile читает window.matchMedia при mount.
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

const { ChildTimeSection } = await import('./ChildTimeSection');
import type { ChildTimeValues } from './ChildTimeSection';

// Значения с заполненными полями — то, что держит черновик родителя после загрузки профиля
// (тот же состав, что fullProfile у образца).
const fullValues = (): ChildTimeValues => ({
  windows: [{ from: '09:00', to: '20:00', weekdays: [1, 3, 5] }],
  dailyMinutes: 40,
  warnMinutes: 5,
  dailyBudgetUsd: 2,
  monthlyBudgetUsd: 40,
});

// Что послал раздел: на длине этого списка проверяется «сохранение заблокировано»,
// на последнем элементе — ушедшие родителю значения.
let sent: ChildTimeValues[] = [];

async function mount(initial: ChildTimeValues, enabled = true) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  let values = initial;
  let sentSeen = 0;
  const render = () => root.render(createElement(ChildTimeSection, { enabled, values, onChange }));
  const onChange = (next: ChildTimeValues) => { sent.push(next); values = next; };
  // Ответ родителя: был новый ответ раздела — перерисовать экземпляр с хранимыми
  // значениями (новым объектом — на этом проверяется подавление эха); действия, ничего
  // не пославшие, родителя не будят: его состояние не менялось.
  const parentRerender = async () => {
    if (sent.length === sentSeen) return;
    sentSeen = sent.length;
    await act(async () => { render(); });
  };
  await act(async () => { render(); });
  return {
    host: host as HTMLElement,
    // Действие человека и ответ родителя на него.
    async type(el: HTMLInputElement, v: string) {
      await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
      await parentRerender();
    },
    async click(el: HTMLElement) {
      await act(async () => { el.click(); });
      await parentRerender();
    },
    // Внешняя смена значений (загрузка профиля, ответ сохранения): другие values.
    async setExternal(v: ChildTimeValues) { values = v; await act(async () => { render(); }); },
  };
}

const panel = (host: HTMLElement) => host.querySelector('[data-testid="child-time-section"]') as HTMLElement;
const inputByPlaceholder = (host: HTMLElement, ph: string) =>
  panel(host).querySelector(`input[placeholder="${ph}"]`) as HTMLInputElement;
// Поле, значение которого заполнено (placeholder не виден), ищется по подписи Field:
// обёртка Field — родитель label и содержит свой input (тот же путь, что у образца).
const inputByLabel = (host: HTMLElement, label: string) => {
  const lbl = Array.from(panel(host).querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('input') ?? null) as HTMLInputElement | null;
};
const windowInputs = (host: HTMLElement) =>
  Array.from(panel(host).querySelectorAll('[data-testid="time-window"] input')) as HTMLInputElement[];
const minutesInput = (host: HTMLElement) => inputByPlaceholder(host, '30');
const warnInput = (host: HTMLElement) => inputByLabel(host, 'Предупредить за') as HTMLInputElement;
const dailyUsdInput = (host: HTMLElement) => inputByPlaceholder(host, '2');
const lastSent = () => sent[sent.length - 1];

// Набрать в поле ввода: React держит props узла под __reactProps$…; зовём onChange напрямую
// (тот же приём, что у образца), без событийной обвязки linkedom.
const reactProps = (el: HTMLElement): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};

beforeEach(() => {
  sent = [];
  mobile = false;
});

describe('ChildTimeSection: «Минут в день»', () => {
  // Перенесено из тестов прежнего редактора профиля (5 кейсов), плюс «абв» по критерию Ф2d.
  for (const bad of ['3O', '0', '-5', '2000', '12.5', 'абв']) {
    it(`«${bad}» — поле подсвечено и названо, из раздела ничего не выходит (R-KM-F11)`, async () => {
      const A = await mount(fullValues());
      const host = A.host;
      await A.type(minutesInput(host), bad);
      const el = minutesInput(host);
      expect(el.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
      // текст ошибки поля — в обороте Field (input → обёртка TextField → div Field)
      expect(el.parentElement?.parentElement?.textContent ?? '').toContain('Введи число минут, например 30');
      expect(sent.length, 'некорректные минуты — сохранения заблокировано').toBe(0);
    });
  }

  it('пустое — «лимита нет»: dailyMinutes null в значениях, поле не подсвечено', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.type(minutesInput(host), '');
    expect(minutesInput(host).style.border).not.toContain('var(--c-danger-border)');
    expect(lastSent().dailyMinutes).toBeNull();
  });

  it('«45» — корректно: без подсветки, onChange с dailyMinutes 45', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.type(minutesInput(host), '45');
    expect(minutesInput(host).style.border).not.toContain('var(--c-danger-border)');
    expect(sent.length).toBe(1);
    expect(lastSent().dailyMinutes).toBe(45);
  });

  it('режим выключен: «9999» в «Минут в день» не подсвечено (R-KM-F11)', async () => {
    const A = await mount(fullValues(), false);
    const host = A.host;
    await A.type(minutesInput(host), '9999');
    expect(minutesInput(host).style.border, 'при выключенном режиме поле не краснеет').not.toContain('var(--c-danger-border)');
  });
});

describe('ChildTimeSection: «Предупредить за»', () => {
  // Перенесено из тестов прежнего редактора профиля: 5 кейсов как есть.
  for (const bad of ['abc', '0', '99']) {
    it(`«${bad}» — поле подсвечено и названо, из раздела ничего не выходит (R-KM-F11)`, async () => {
      const A = await mount(fullValues());
      const host = A.host;
      await A.type(warnInput(host), bad);
      const el = warnInput(host);
      expect(el.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
      expect(el.parentElement?.parentElement?.textContent ?? '').toContain('Введи число от 1 до 60, например 5');
      expect(sent.length, 'некорректное предупреждение — сохранения заблокировано').toBe(0);
    });
  }

  it('пустое — значение по умолчанию: без подсветки, warnMinutes 5', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.type(warnInput(host), '');
    expect(warnInput(host).style.border).not.toContain('var(--c-danger-border)');
    expect(lastSent().warnMinutes).toBe(5);
  });

  it('«15» — корректно: без подсветки, warnMinutes 15', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.type(warnInput(host), '15');
    expect(warnInput(host).style.border).not.toContain('var(--c-danger-border)');
    expect(lastSent().warnMinutes).toBe(15);
  });
});

describe('ChildTimeSection: окна времени', () => {
  it('окно «25:00» из значений — поле подсвечено и названо, из раздела ничего не выходит', async () => {
    const A = await mount({ ...fullValues(), windows: [{ from: '25:00', to: '20:00' }] });
    const from = windowInputs(A.host)[0];
    expect(from.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
    expect(from.parentElement?.parentElement?.textContent ?? '').toContain('Формат ЧЧ:ММ');
    expect(sent.length, 'некорректное время — сохранения заблокировано').toBe(0);
  });

  it('окно через полночь и без дней — в значениях: «23:00»–«06:00», поля weekdays нет', async () => {
    const A = await mount({ ...fullValues(), windows: [{ from: '22:00', to: '06:00' }] });
    await A.type(windowInputs(A.host)[0], '23:00');
    const w = lastSent().windows[0];
    expect([w.from, w.to]).toEqual(['23:00', '06:00']);
    expect(w.weekdays, 'дни не выбраны — поля weekdays нет').toBeUndefined();
  });

  it('«Добавить окно»: пустые поля не краснеют, но пока окно не заполнено — ничего не выходит', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.click(panel(host).querySelector('[data-testid="add-window"]') as HTMLElement);
    const inputs = windowInputs(host);
    expect(inputs.length).toBe(4);
    expect(inputs[2].style.border, 'пустое новое поле не краснеет сразу').not.toContain('var(--c-danger-border)');
    await A.type(minutesInput(host), '45');
    expect(sent.length, 'незаполненное окно блокирует весь раздел').toBe(0);
  });

  it('заполнил новое окно — оно уходит в значения вместе с правкой минут', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.click(panel(host).querySelector('[data-testid="add-window"]') as HTMLElement);
    const inputs = windowInputs(host);
    await A.type(inputs[2], '10:00');
    await A.type(inputs[3], '11:00');
    await A.type(minutesInput(host), '45');
    const v = lastSent();
    expect(v.dailyMinutes).toBe(45);
    expect([v.windows[1].from, v.windows[1].to]).toEqual(['10:00', '11:00']);
    expect(v.windows[1].weekdays).toBeUndefined();
  });

  it('галка дня — в значениях; снял все дни — поля weekdays нет', async () => {
    const A = await mount({ ...fullValues(), windows: [{ from: '09:00', to: '20:00' }] });
    const sat = panel(A.host).querySelector('[aria-label="День недели Сб"]') as HTMLElement;
    await A.click(sat);
    expect(lastSent().windows[0].weekdays).toEqual([6]);
    await A.click(sat);
    expect(lastSent().windows[0].weekdays).toBeUndefined();
  });

  it('удаление окна — в значениях пустой список', async () => {
    const A = await mount({ ...fullValues(), windows: [{ from: '09:00', to: '20:00' }] });
    await A.click(panel(A.host).querySelector('[data-testid="window-remove-0"]') as HTMLElement);
    expect(lastSent().windows).toEqual([]);
  });
});

describe('ChildTimeSection: бюджеты', () => {
  // «1,5» → 1.5 — по критерию Ф2d; «abc» и «-5» — ошибка поля (R-KM-F11: молчаливого
  // «бюджета нет» из неразбираемого текста быть не должно).
  for (const bad of ['abc', '-5']) {
    it(`«${bad}» в «Бюджет на день» — поле подсвечено и названо, из раздела ничего не выходит`, async () => {
      const A = await mount(fullValues());
      const host = A.host;
      await A.type(dailyUsdInput(host), bad);
      const el = dailyUsdInput(host);
      expect(el.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
      expect(el.parentElement?.parentElement?.textContent ?? '').toContain('Введи число долларов, например 2');
      expect(sent.length, 'неразбираемый бюджет — сохранения заблокировано').toBe(0);
    });
  }

  it('«1,5» — запятая как десятичная точка: dailyBudgetUsd 1.5', async () => {
    const A = await mount(fullValues());
    await A.type(dailyUsdInput(A.host), '1,5');
    expect(dailyUsdInput(A.host).style.border).not.toContain('var(--c-danger-border)');
    expect(lastSent().dailyBudgetUsd).toBe(1.5);
  });

  it('пустые бюджеты — null', async () => {
    const A = await mount(fullValues());
    const host = A.host;
    await A.type(dailyUsdInput(host), '');
    await A.type(inputByPlaceholder(host, '40'), '');
    const v = lastSent();
    expect(v.dailyBudgetUsd).toBeNull();
    expect(v.monthlyBudgetUsd).toBeNull();
  });
});

describe('ChildTimeSection: черновик и эхо родителя', () => {
  it('эхо родителя (те же значения на новом рендере) черновик не сбрасывает', async () => {
    const A = await mount(fullValues());
    // Родитель после каждого ответа раздела перерисовывает экземпляр новым объектом тех же
    // значений (см. mount): черновик человека не должен обнуляться его же эхом.
    await A.type(minutesInput(A.host), '45');
    await A.type(minutesInput(A.host), '4');
    expect(minutesInput(A.host).value).toBe('4');
    expect(sent.length, 'эхо — не новое изменение').toBe(2);
  });

  it('внешние значения (загрузка профиля) перечитывают черновик', async () => {
    const A = await mount({ windows: [], dailyMinutes: null, warnMinutes: 5, dailyBudgetUsd: null, monthlyBudgetUsd: null });
    expect(panel(A.host).querySelector('[data-testid="no-windows"]'), 'пустой черновик — empty-state').toBeTruthy();
    await A.setExternal(fullValues());
    expect(windowInputs(A.host).length, 'окно из загруженного профиля').toBe(2);
    expect(minutesInput(A.host).value).toBe('40');
  });
});

describe('ChildTimeSection: мобила (360)', () => {
  it('поля окна в одну колонку, тач-цели ≥ 40px', async () => {
    mobile = true;
    const A = await mount(fullValues());
    const row = panel(A.host).querySelector('[data-testid="window-row"]') as HTMLElement;
    expect(row.style.flexDirection).toBe('column');
    const add = panel(A.host).querySelector('[data-testid="add-window"]') as HTMLElement;
    expect(Number.parseFloat(add.style.minHeight), 'тач-цель «Добавить окно»').toBeGreaterThanOrEqual(40);
    const chip = panel(A.host).querySelector('[aria-pressed]') as HTMLElement;
    expect(Number.parseFloat(chip.style.minHeight), 'тач-цель дня недели').toBeGreaterThanOrEqual(40);
  });

  it('«Бюджет на день» и «Бюджет на месяц» — в одну колонку', async () => {
    mobile = true;
    const A = await mount(fullValues());
    const row = panel(A.host).querySelector('[data-testid="budget-row"]') as HTMLElement;
    expect(row, 'ряд бюджетов').toBeTruthy();
    expect(row.style.flexDirection, 'два поля бюджета — столбиком (360)').toBe('column');
  });
});
