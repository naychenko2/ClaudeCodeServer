// Т4c/Т6: ChildRoutePicker — пикер «поставщик → модель» из списка сервера (макет Д1, пачка 1 +
// пачка 2). Окружение — node + linkedom, как в соседних .test.tsx волны: Select монтируется настоящий
// (linkedom даёт select.options и option.selected — состояние выбранного пункта читается по
// option.selected), смена значения — прямым вызовом onChange узла (тот же путь, что событие
// select, без событийной обвязки linkedom). Каждое «сменилось» проверяется полным циклом:
// onChange → значение у родителя → повторный рендер с ним → поле показывает новое.
// Пачка 2 (Т6): группы списков, предупреждения облачного/локального, галка согласия и её
// привязка к providerKey — тексты и порядок по макету Д1 (пикер (б)).
// Мутации этого прогона (копия дерева, вывод — в отчёте задачи):
// (1) при смене поставщика не отдавать acknowledgeRouteRisk: false → «смена поставщика …
//     acknowledgeRouteRisk=false» краснеет (ожидался объект без флага).

import { describe, it, expect, vi } from 'vitest';
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

import { ChildRoutePicker } from './ChildRoutePicker';
import type { ChildRouteValue } from './ChildRoutePicker';
import type { ChildRoute } from './types';

// Тот же состав, что отдаёт GET /api/admin/child-routes: записи пула первыми, затем провайдеры
// (ChildRoutesController.cs:27-28,64), модели — пары id/label, порядок конфига. Пустых models
// здесь нет: такой список не отдаёт сервер.
const routes = (): ChildRoute[] => [
  {
    providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
    priced: true, requiresAck: false,
    models: [{ id: 'sonnet', label: 'Sonnet' }, { id: 'haiku', label: 'Haiku' }],
  },
  {
    providerKey: 'glm', kind: 'cloud', label: 'GLM (Zhipu)',
    priced: true, requiresAck: true,
    models: [{ id: 'glm-5', label: 'GLM-5' }],
  },
  {
    providerKey: 'strata', kind: 'local', label: 'Strata (локально)',
    priced: false, requiresAck: true,
    models: [{ id: 'qwen-a', label: 'Qwen A' }, { id: 'qwen-b', label: 'Qwen B' }],
  },
];

const ACK_LABEL = 'Понимаю риск и разрешаю этот маршрут';

let changes: ChildRouteValue[] = [];

async function mount(props: { routes: ChildRoute[]; value: ChildRouteValue; disabled?: boolean }) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  changes = [];
  const render = (p: typeof props) =>
    createElement(ChildRoutePicker, { ...p, onChange: (v: ChildRouteValue) => { changes.push(v); } });
  await act(async () => { root.render(render(props)); });
  return {
    host: host as HTMLElement,
    // Тот же экземпляр с новым значением: так проверяется полный цикл «действие → значение
    // у родителя → поле показывает новое», а не только вызов onChange.
    async applyValue(next: ChildRouteValue) {
      await act(async () => { root.render(render({ ...props, value: next })); });
    },
  };
}

const panel = (host: HTMLElement) => host.querySelector('[data-testid="child-route-picker"]') as HTMLElement;

// Поле по подписи Field: обёртка Field — общий родитель label и select.
const selectByLabel = (host: HTMLElement, label: string) => {
  const lbl = Array.from(panel(host).querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('select') ?? null) as Element | null;
};

interface Opt {
  value: string;
  text: string;
  selected: boolean;
  disabled: boolean;
}
const options = (sel: Element): Opt[] =>
  Array.from(sel.querySelectorAll('option')).map((o) => ({
    value: o.getAttribute('value') ?? '',
    text: o.textContent ?? '',
    // .selected — свойство linkedom (атрибут selected); React ставит его при сверке значения
    selected: (o as unknown as { selected?: boolean }).selected === true,
    // disabled React мог записать и атрибутом, и свойством (у option в linkedom нет геттера
    // disabled) — проверяем обе формы
    disabled: (o as unknown as { disabled?: boolean }).disabled === true || o.getAttribute('disabled') !== null,
  }));

const selectedOption = (sel: Element): Opt | undefined => options(sel).find((o) => o.selected);

// Сменить значение контрола: React держит props узла под __reactProps$…; зовём onChange напрямую.
const reactProps = (el: Element): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};
const changeSelect = async (el: Element, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};

const ackBox = (host: HTMLElement) => panel(host).querySelector(`[role="checkbox"][aria-label="${ACK_LABEL}"]`) as HTMLElement | null;
const clickAck = async (host: HTMLElement) => {
  const b = ackBox(host);
  expect(b, 'галка согласия').not.toBeNull();
  await act(async () => { b!.click(); });
};
const hasText = (root: Element, text: string) => (root.textContent ?? '').includes(text);

describe('ChildRoutePicker', () => {
  it('группы — Anthropic, облачные, локальные; внутри — порядок и label сервера; выбранное подсвечено', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'haiku' } });
    const prov = selectByLabel(host, 'Поставщик');
    const model = selectByLabel(host, 'Модель');
    expect(prov, 'поле «Поставщик»').not.toBeNull();
    expect(model, 'поле «Модель»').not.toBeNull();

    expect(Array.from(prov!.querySelectorAll('optgroup')).map((g) => g.getAttribute('label'))).toEqual([
      'Anthropic · записи пула с API-ключом', 'Облачные поставщики', 'Локальные модели',
    ]);
    expect(options(prov!).map((o) => [o.value, o.text])).toEqual([
      ['anthropic-api-kids', 'Anthropic · ключ «kids»'],
      ['glm', 'GLM (Zhipu)'],
      ['strata', 'Strata (локально)'],
    ]);
    expect(selectedOption(prov!)?.value).toBe('anthropic-api-kids');
    // модели — только выбранного поставщика, в его порядке; текст — label от сервера
    expect(options(model!).map((o) => [o.value, o.text])).toEqual([
      ['sonnet', 'Sonnet'],
      ['haiku', 'Haiku'],
    ]);
    expect(selectedOption(model!)?.value).toBe('haiku');
    // свободного ввода нет: ноль <input> в пикере
    expect(panel(host).querySelectorAll('input').length).toBe(0);
  });

  it('смена поставщика — один onChange: первая модель его списка и acknowledgeRouteRisk=false', async () => {
    const m = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'haiku' } });
    await changeSelect(selectByLabel(m.host, 'Поставщик')!, 'glm');

    // c1 Т6: новый поставщик требует согласия — пикер отдаёт флаг false, галка не остаётся прежней
    expect(changes).toEqual([{ providerKey: 'glm', model: 'glm-5', acknowledgeRouteRisk: false }]); // ровно один вызов
    // полный цикл: родитель применил результат — список моделей стал его списком
    await m.applyValue({ providerKey: 'glm', model: 'glm-5', acknowledgeRouteRisk: false });
    const model = selectByLabel(m.host, 'Модель')!;
    expect(options(model).map((o) => o.value)).toEqual(['glm-5']);
    expect(selectedOption(model)?.value).toBe('glm-5');
    expect(ackBox(m.host)?.getAttribute('aria-checked'), 'галка сброшена').toBe('false');
  });

  it('смена модели — onChange той же пары, без второго вызова', async () => {
    const m = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'sonnet' } });
    await changeSelect(selectByLabel(m.host, 'Модель')!, 'haiku');
    expect(changes).toEqual([{ providerKey: 'anthropic-api-kids', model: 'haiku' }]);
    await m.applyValue({ providerKey: 'anthropic-api-kids', model: 'haiku' });
    expect(selectedOption(selectByLabel(m.host, 'Модель')!)?.value).toBe('haiku');
  });

  it('облачный маршрут — предупреждение «переписка уходит поставщику» и галка; согласия у пула нет', async () => {
    const cloud = await mount({ routes: routes(), value: { providerKey: 'glm', model: 'glm-5' } });
    expect(hasText(cloud.host, 'Переписка уходит поставщику GLM (Zhipu)'), 'Notice по label поставщика').toBe(true);
    expect(hasText(cloud.host, 'обрабатывает GLM (Zhipu) на своих серверах')).toBe(true);
    expect(ackBox(cloud.host), 'галка согласия').not.toBeNull();
    expect(ackBox(cloud.host)!.getAttribute('aria-checked'), 'без согласия — снята').toBe('false');
    expect(hasText(cloud.host, 'бюджет не тратится'), 'пометка локального не у облачного').toBe(false);

    // пул: ни предупреждения, ни галки — согласия у пула нет никогда (разрез §3)
    const { host } = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'sonnet' } });
    expect(ackBox(host), 'галки у пула нет').toBeNull();
    expect(panel(host).querySelectorAll('[role="status"]').length, 'предупреждений нет').toBe(0);
  });

  it('локальный маршрут — «слабее держит правила», «бюджет не тратится» и галка', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: 'strata', model: 'qwen-a' } });
    expect(hasText(host, 'Локальная модель слабее держит правила безопасности')).toBe(true);
    expect(hasText(host, 'бюджет не тратится'), 'пометка И3 для локального').toBe(true);
    expect(hasText(host, 'Ограничивают только окна и минуты в день')).toBe(true);
    expect(ackBox(host), 'галка согласия').not.toBeNull();
  });

  it('галка согласия — один onChange той же пары с acknowledgeRouteRisk=true; полный цикл — галка отмечена', async () => {
    const m = await mount({ routes: routes(), value: { providerKey: 'glm', model: 'glm-5', acknowledgeRouteRisk: false } });
    await clickAck(m.host);
    expect(changes).toEqual([{ providerKey: 'glm', model: 'glm-5', acknowledgeRouteRisk: true }]);
    await m.applyValue({ providerKey: 'glm', model: 'glm-5', acknowledgeRouteRisk: true });
    expect(ackBox(m.host)?.getAttribute('aria-checked'), 'галка отмечена').toBe('true');
  });

  it('смена модели согласие не трогает: тот же acknowledgeRouteRisk в onChange', async () => {
    const m = await mount({ routes: routes(), value: { providerKey: 'strata', model: 'qwen-a', acknowledgeRouteRisk: true } });
    await changeSelect(selectByLabel(m.host, 'Модель')!, 'qwen-b');
    expect(changes).toEqual([{ providerKey: 'strata', model: 'qwen-b', acknowledgeRouteRisk: true }]);
  });

  it('старое значение вне routes — «недоступно», выбрать повторно нельзя', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: 'old-key', model: 'old-model' } });
    const prov = selectByLabel(host, 'Поставщик')!;
    const model = selectByLabel(host, 'Модель')!;

    const bad = options(prov).find((o) => o.value === 'old-key');
    expect(bad?.text).toBe('Недоступно: old-key');
    expect(bad?.selected, 'значение показано в поле').toBe(true);
    expect(bad?.disabled, 'пункт недоступен — повторный выбор не вернёт старое').toBe(true);
    // список живёт рядом: починить можно, выбрав настоящего поставщика
    expect(options(prov).map((o) => o.value)).toEqual(['old-key', 'anthropic-api-kids', 'glm', 'strata']);

    const badModel = options(model).find((o) => o.value === 'old-model');
    expect(badModel?.text).toBe('Недоступно: old-model');
    expect(badModel?.selected).toBe(true);
    expect(badModel?.disabled).toBe(true);
  });

  it('модель вне списка выбранного поставщика — «недоступно» только у модели', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'old-model' } });
    const prov = selectByLabel(host, 'Поставщик')!;
    expect(selectedOption(prov)?.value).toBe('anthropic-api-kids');
    expect(options(prov).some((o) => o.text.startsWith('Недоступно'))).toBe(false);

    const model = selectByLabel(host, 'Модель')!;
    const bad = options(model).find((o) => o.value === 'old-model');
    expect(bad?.text).toBe('Недоступно: old-model');
    expect(bad?.disabled).toBe(true);
    // модели поставщика при этом доступны — поле чинится выбором из списка
    expect(options(model).filter((o) => !o.disabled).map((o) => o.value)).toEqual(['sonnet', 'haiku']);
  });

  it('пустые routes — Notice о причине, полей нет', async () => {
    const { host } = await mount({ routes: [], value: { providerKey: 'x', model: 'y' } });
    const note = panel(host); // data-testid стоит на самом Notice
    expect(note.getAttribute('role'), 'Notice').toBe('status');
    expect(note.textContent).toContain('Нет записей пула с API-ключом');
    expect(note.querySelectorAll('select').length).toBe(0);
  });

  it('пустое значение — placeholder, а не первый пункт списка', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: '', model: '' } });
    const prov = selectByLabel(host, 'Поставщик')!;
    const model = selectByLabel(host, 'Модель')!;
    expect(selectedOption(prov)?.text).toBe('Выбери поставщика');
    expect(selectedOption(model)?.text).toBe('Выбери модель');
    // и действия нет: placeholder не считается выбором
    await changeSelect(prov, '');
    expect(changes).toEqual([]);
  });

  it('на мобиле поля стоят столбиком (360), на широком — в ряд; галка ≥ 40px', async () => {
    mobile = true;
    try {
      const { host } = await mount({ routes: routes(), value: { providerKey: 'strata', model: 'qwen-a' } });
      expect(panel(host).querySelector('div')!.style.flexDirection, 'ряд полей на 360').toBe('column');
      const box = ackBox(host)!;
      expect(Number.parseFloat(box.style.width), 'тач-цель галки по ширине').toBeGreaterThanOrEqual(40);
      expect(Number.parseFloat(box.style.height), 'тач-цель галки по высоте').toBeGreaterThanOrEqual(40);
    } finally {
      mobile = false;
    }
    const { host } = await mount({ routes: routes(), value: { providerKey: 'anthropic-api-kids', model: 'sonnet' } });
    expect(panel(host).querySelector('div')!.style.flexDirection, 'ряд полей на 1280').toBe('row');
  });

  it('disabled гасит оба поля и галку согласия (состояние «Сохранение»)', async () => {
    const { host } = await mount({ routes: routes(), value: { providerKey: 'glm', model: 'glm-5' }, disabled: true });
    for (const label of ['Поставщик', 'Модель']) {
      const sel = selectByLabel(host, label)!;
      expect((sel as unknown as { disabled?: boolean }).disabled === true || sel.getAttribute('disabled') !== null, label).toBeTruthy();
    }
    const box = ackBox(host)!;
    expect((box as unknown as { disabled?: boolean }).disabled === true || box.getAttribute('disabled') !== null, 'галка на сохранении тоже гасится').toBeTruthy();
  });
});
