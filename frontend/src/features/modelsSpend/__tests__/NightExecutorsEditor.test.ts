// Рендер-тест компонента «Исполнители ночи» (W7b):
// — рендер: заголовок, подписи движков ровно как пришли в title (в т. ч. «Основная»),
//   порядок строк — по позиции в engines, подсказка у параллельности и «Общий потолок»;
// — ревью W2c: при mainLocalEnabled=false «local» нет ни строкой, ни подписью;
// — отметка двух движков → ровно один onSave, items в порядке engines (не в порядке
//   отметки); Stepper не даёт parallel больше потолка движка;
// — отказ onSave (400 с body.errors): тексты валидатора в Notice, черновик цел,
//   необработанного rejection нет;
// — doubleSave_singleCall: пока onSave не завершился, кнопка выключена — ровно один
//   вызов, черновик заморожен (мутации (1)/(2)/(8) — снять блокировку кнопки / снять
//   страж в обработчике / снять заморозку черновика — красные);
// — устаревший ответ: новая value во время сохранения сбрасывает черновик, и ответ
//   сохранения её не затирает; новая value вне сохранения тоже сбрасывает черновик;
// — смена основной локали без смены value перестраивает черновик по новому составу
//   видимых движков (ревью W7b, minor 1): локаль возвращается ОТМЕЧЕННОЙ, а не
//   неотмеченной. Мутация: ключ синхронизации только value → тест красный;
// — пустой выбор = сброс к одному маршруту: тело { maxTotal: 0, executors: [] };
//   потолок общего Stepper — 4.
//
// Мутации этого прогона (вывод — в отчёт задачи):
// (1) снять блокровку кнопки сохранения на время onSave → doubleSave краснеет
//     (проверка disabled);
// (2) снять страж `if (saving) return` в обработчике → doubleSave краснеет
//     (второй клик дошёл бы до onSave);
// (3) не фильтровать «local» по mainLocalEnabled → тест W2c краснеет;
// (4) не сбрасывать черновик при новой value (убрать переход value→черновик) →
//     «устаревший ответ» и «сброс черновика» краснеют;
// (5) при отказе сохранения сбрасывать черновик на value → «черновик цел» краснеет;
// (6) при пустом выборе слать текущий maxTotal вместо 0 → «пустой выбор» краснеет;
// (7) строить items не из порядка engines (развернуть список) → «порядок items»
//     краснеет (в fixture порядок отметки обратный engines — порядок тела проверяется
//     и против порядка отметки, и против любой сортировки);
// (8) снять заморозку черновика на время сохранения (страж в patch) → doubleSave
//     краснеет («+» посреди сохранения сдвинул бы параллельность уже отправленного
//     тела).

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import { NightExecutorsEditor, type NightExecutorsBody } from '../NightExecutorsEditor';

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const HINT = 'больше 1 на одном движке ускоряет, только если памяти хватает на два окна';

// Движки — форма ollama.localEngines (W2c): «local» первой (как строит бэк), затем
// именованные; maxConcurrency — потолок движка. Порядок заведомо не алфавитный
const ENGINES = [
  { name: 'local', title: 'Qwen3.8-27B', maxConcurrency: 2 },
  { name: 'gemma', title: 'Gemma 4', maxConcurrency: 2 },
];
// Значение места — форма actions[].executors (W7a): items в порядке стора
const VALUE = { maxTotal: 2, items: [{ engine: 'local', parallel: 1 }, { engine: 'gemma', parallel: 2 }] };

const roots: Root[] = [];

type Props = {
  engines?: typeof ENGINES;
  value?: { maxTotal: number; items: { engine: string; parallel: number }[] } | null;
  mainLocalEnabled?: boolean;
  onSave?: (b: NightExecutorsBody) => Promise<void>;
};

function render(props: Props = {}): { c: AnyNode; root: Root } {
  installFakeDom();
  const { root, container } = mount(createElement(NightExecutorsEditor, {
    engines: props.engines ?? ENGINES,
    value: props.value ?? null,
    mainLocalEnabled: props.mainLocalEnabled ?? true,
    onSave: props.onSave ?? (async () => {}),
  }));
  roots.push(root);
  return { c: container, root };
}

function rerender(root: Root, props: Props): void {
  act(() => {
    root.render(createElement(NightExecutorsEditor, {
      engines: props.engines ?? ENGINES,
      value: props.value ?? null,
      mainLocalEnabled: props.mainLocalEnabled ?? true,
      onSave: props.onSave ?? (async () => {}),
    }));
  });
}

async function settle(times = 20): Promise<void> {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(c: AnyNode): string[] {
  return textNodes(c).map((t) => t.trim()).filter(Boolean);
}
function joined(c: AnyNode): string {
  return texts(c).join(' ');
}
function has(c: AnyNode, needle: string): boolean {
  return joined(c).includes(needle);
}
function row(c: AnyNode, name: string): AnyNode | null {
  return findNodeByAttr(c, 'data-testid', `night-exec-row-${name}`);
}
function checked(c: AnyNode, name: string): boolean {
  const cb = row(c, name) && findNodeByAttr(row(c, name)!, 'role', 'checkbox');
  return cb?.getAttribute('aria-checked') === 'true';
}
// Отметка — кликом по чекбоксу: его DOM-проп — onClick (сам onChange компонента
// на DOM-узле не висит), а он переключает текущее состояние
function setChecked(c: AnyNode, name: string): void {
  const cb = findNodeByAttr(row(c, name)!, 'role', 'checkbox')!;
  const p = reactProps(cb) as { onClick?: (e: unknown) => void };
  act(() => { p.onClick!(undefined); });
}
// «+» внутри конкретного Stepper (по группе с её aria-label — иначе схватится «+»
// первой строки движка)
function plusIn(scope: AnyNode): void {
  const group = findNodeByAttr(scope, 'role', 'group')!;
  const b = findNodeByAttr(group, 'aria-label', 'Больше')!;
  const p = reactProps(b) as { onClick?: (e: unknown) => void };
  act(() => { p.onClick!(undefined); });
}
const plusRow = (c: AnyNode, name: string): void => plusIn(row(c, name)!);
const totalGroup = (c: AnyNode): AnyNode =>
  findNodeByAttr(c, 'aria-label', 'Общий потолок одновременных ночных задач')!;
const saveBtn = (c: AnyNode): AnyNode => findNodeByAttr(c, 'data-testid', 'night-executors-save')!;
function clickSave(c: AnyNode): void {
  const p = reactProps(saveBtn(c)) as { onClick?: (e: unknown) => void };
  act(() => { p.onClick!(undefined); });
}
const isDisabled = (n: AnyNode): boolean => (reactProps(n) as { disabled?: boolean }).disabled === true;

const httpError = (status: number, body: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
});

describe('«Исполнители ночи» (W7b)', () => {
  it('рендер: заголовок, подписи как пришли, порядок по engines, подсказка и общий потолок', () => {
    const { c } = render({ value: VALUE });
    expect(has(c, 'Исполнители ночи')).toBe(true);
    expect(has(c, 'Qwen3.8-27B')).toBe(true);
    expect(has(c, 'Gemma 4')).toBe(true);
    // Подсказка у параллельности — точная строка постановки
    expect(has(c, HINT)).toBe(true);
    expect(has(c, 'Общий потолок')).toBe(true);
    // Порядок строк — по позиции в engines (порядок в тексте, не по алфавиту)
    const all = joined(c);
    expect(all.indexOf('Qwen3.8-27B')).toBeLessThan(all.indexOf('Gemma 4'));
    // Состояние — из value: оба движка отмечены
    expect(checked(c, 'local')).toBe(true);
    expect(checked(c, 'gemma')).toBe(true);
  });

  it('ревью W2c: при выключенной основной локали «local» нет; подпись «Основная» — как пришла в title', () => {
    // local у бэка есть всегда (BuildLocalEngineOptions), при выключенной основной
    // локали компонент её не предлагает: ни строки, ни подписи
    const off = render({ mainLocalEnabled: false, value: null });
    expect(row(off.c, 'local')).toBeNull();
    expect(has(off.c, 'Qwen3.8-27B')).toBe(false);
    expect(row(off.c, 'gemma')).not.toBeNull();

    // Без настроенной модели бэк присылает title «Основная» — показываем как пришло
    const MAIN = [{ name: 'local', title: 'Основная', maxConcurrency: 1 }, { name: 'gemma', title: 'Gemma 4', maxConcurrency: 2 }];
    const on = render({ engines: MAIN as typeof ENGINES, mainLocalEnabled: true });
    expect(row(on.c, 'local')).not.toBeNull();
    expect(has(on.c, 'Основная')).toBe(true);
  });

  it('нет списка (value=null): ничего не отмечено, состояние объяснено, черновик 1', () => {
    const { c } = render({ value: null });
    expect(checked(c, 'local')).toBe(false);
    expect(checked(c, 'gemma')).toBe(false);
    expect(has(c, 'Ничего не отмечено — ночной разбор пойдёт по своему одному маршруту, как сейчас.')).toBe(true);
  });

  it('отметка двух движков: ровно один onSave, items в порядке engines (не в порядке отметки)', async () => {
    const calls: NightExecutorsBody[] = [];
    const onSave = vi.fn(async (b: NightExecutorsBody) => { calls.push(b); });
    const { c } = render({ value: null, onSave });
    // мутация (7): порядок отметки обратный engines — если тело строится по отметке,
    // порядок в теле красный
    setChecked(c, 'gemma');
    setChecked(c, 'local');
    clickSave(c);
    await settle();
    expect(onSave).toHaveBeenCalledTimes(1);
    expect(calls[0]).toEqual({
      maxTotal: 1,
      executors: [{ engine: 'local', parallel: 1 }, { engine: 'gemma', parallel: 1 }],
    });
  });

  it('Stepper не даёт parallel больше потолка движка: три «+» при потолке 2 → parallel 2', async () => {
    const calls: NightExecutorsBody[] = [];
    const onSave = vi.fn(async (b: NightExecutorsBody) => { calls.push(b); });
    const { c } = render({ value: { maxTotal: 2, items: [{ engine: 'gemma', parallel: 1 }] }, onSave });
    plusRow(c, 'gemma');
    plusRow(c, 'gemma'); // упёрся в 2 — третья «+» не даёт 3
    plusRow(c, 'gemma');
    clickSave(c);
    await settle();
    expect(calls[0]).toEqual({ maxTotal: 2, executors: [{ engine: 'gemma', parallel: 2 }] });
  });

  it('общий потолок: пять «+» при 1 → 4 (потолок стора), не 6', async () => {
    const calls: NightExecutorsBody[] = [];
    const onSave = vi.fn(async (b: NightExecutorsBody) => { calls.push(b); });
    const { c } = render({ value: { maxTotal: 1, items: [{ engine: 'local', parallel: 1 }] }, onSave });
    for (let i = 0; i < 5; i++) plusIn(totalGroup(c));
    clickSave(c);
    await settle();
    expect(calls[0].maxTotal).toBe(4);
  });

  it('пустой выбор = сброс к одному маршруту: тело { maxTotal: 0, executors: [] }', async () => {
    const calls: NightExecutorsBody[] = [];
    const onSave = vi.fn(async (b: NightExecutorsBody) => { calls.push(b); });
    const { c } = render({ value: VALUE, onSave });
    setChecked(c, 'local'); // снятие: value их отмечала — клик снимает
    setChecked(c, 'gemma');
    expect(has(c, 'Ничего не отмечено')).toBe(true);
    clickSave(c);
    await settle();
    // мутация (6): вместо 0 ушёл бы текущий maxTotal черновика — проверка красная
    expect(calls[0]).toEqual({ maxTotal: 0, executors: [] });
  });

  it('двойной клик «Сохранить»: кнопка выключена до конца onSave — ровно один вызов', async () => {
    let resolveSave: (() => void) | null = null;
    const onSave = vi.fn(() => new Promise<void>((resolve) => { resolveSave = resolve; }));
    const { c } = render({ value: VALUE, onSave });
    clickSave(c);
    expect(onSave).toHaveBeenCalledTimes(1);
    // мутация (1): без блокировки кнопки эта проверка краснеет
    expect(isDisabled(saveBtn(c))).toBe(true);
    // черновик на время сохранения заморожен: «+» на строке с parallel=1 не делает её 2 —
    // запрос уже построен по прежнему значению (мутация (8): без стража в patch — красное)
    plusIn(row(c, 'local')!);
    expect(texts(findNodeByAttr(row(c, 'local')!, 'role', 'group')!).join('')).toBe('1');
    // второй клик дошёл бы до обработчика мимо disabled (в тесте handler зовётся прямо) —
    // мутация (2): без стража в обработчике здесь стало бы 2 вызова
    clickSave(c);
    expect(onSave).toHaveBeenCalledTimes(1);
    resolveSave!();
    await settle();
    expect(isDisabled(saveBtn(c))).toBe(false);
  });

  it('отказ 400: тексты валидатора в Notice, черновик цел, необработанного rejection нет', async () => {
    const unhandled: unknown[] = [];
    const onUnhandled = (e: unknown): void => { unhandled.push(e); };
    process.on('unhandledRejection', onUnhandled);
    try {
      const errors = ['parallel 3 у движка «gemma» — потолок движка 2', 'maxTotal 5 — допустимо 1..4'];
      const onSave = vi.fn(() => Promise.reject(httpError(400, { errors })));
      const { c } = render({ value: null, onSave });
      setChecked(c, 'gemma');
      clickSave(c);
      await settle();
      expect(onSave).toHaveBeenCalledTimes(1);
      // админ видит, ЧТО именно не принято (контракт W2b/W7a), а не «HTTP 400»
      expect(has(c, errors[0])).toBe(true);
      expect(has(c, errors[1])).toBe(true);
      // мутация (5): если при отказе сбросить черновик на value, отметка пропала бы
      expect(checked(c, 'gemma')).toBe(true);
      expect(isDisabled(saveBtn(c))).toBe(false);
      await settle();
      expect(unhandled).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });

  it('устаревший ответ: новая value во время сохранения не затирается ответом onSave', async () => {
    let resolveSave: (() => void) | null = null;
    const onSave = vi.fn(() => new Promise<void>((resolve) => { resolveSave = resolve; }));
    const { c, root } = render({ value: VALUE, onSave });
    clickSave(c);
    // пока сохранение идёт, значение места сменилось (перечитка/чужая правка) —
    // черновик обязан перейти на новое, и ответ сохранения не имеет права его затирать
    rerender(root, { value: { maxTotal: 3, items: [{ engine: 'gemma', parallel: 2 }] }, onSave });
    resolveSave!();
    await settle();
    expect(checked(c, 'local')).toBe(false);
    expect(checked(c, 'gemma')).toBe(true);
    expect(texts(totalGroup(c)).join('')).toContain('3');

    // подтверждение нового состояния: тело сохранения — из нового значения
    const calls: NightExecutorsBody[] = [];
    rerender(root, { value: { maxTotal: 3, items: [{ engine: 'gemma', parallel: 2 }] }, onSave: async (b) => { calls.push(b); } });
    clickSave(c);
    await settle();
    expect(calls[0]).toEqual({ maxTotal: 3, executors: [{ engine: 'gemma', parallel: 2 }] });
  });

  it('новая value вне сохранения сбрасывает черновик: что было намечено — уходит к значению', () => {
    const { c, root } = render({ value: null });
    setChecked(c, 'local');
    expect(checked(c, 'local')).toBe(true);
    rerender(root, { value: { maxTotal: 1, items: [{ engine: 'gemma', parallel: 1 }] } });
    expect(checked(c, 'local')).toBe(false);
    expect(checked(c, 'gemma')).toBe(true);
  });

  it('смена основной локали без смены value: черновик по новому составу движков, локаль — отмечена', () => {
    // Значение места отмечало «local»; при выключенной основной локали его нет ни в
    // списке, ни в черновике. Когда локаль вернулась, она обязана вернуться ОТМЕЧЕННОЙ:
    // неотмеченную её «Сохранить» снял бы с места (ревью W7b, minor 1)
    const { c, root } = render({ mainLocalEnabled: false, value: VALUE });
    expect(row(c, 'local')).toBeNull();
    expect(row(c, 'gemma')).not.toBeNull();
    expect(checked(c, 'gemma')).toBe(true);

    rerender(root, { mainLocalEnabled: true, value: VALUE });
    expect(row(c, 'local')).not.toBeNull();
    expect(checked(c, 'local')).toBe(true);
    expect(checked(c, 'gemma')).toBe(true);
  });
});
