// Свёртка длинных списков карточек (GC-F4 + GC-F4R): чистые функции foldProd и foldLast.
// Проверяем: незакрытые видны всегда, снятый ('withdrawn') не считается незакрытым
// и не показывается, готовым считается только 'done' (не бэковая корзина 'accepted'),
// из готовых — только последние, раскрытая карточка не прячется никогда, showAll
// показывает ровно то, что пришло, foldLast не схлопывает (длина входа сохраняется),
// порядок видимых исходный, вход не мутируется, шаг, попавший и в хвост после мержа,
// и в шаги стадии, не считается дважды (только в foldProd).

import { describe, it, expect } from 'vitest';
import { foldProd, foldLast, PROD_DONE_TAIL } from './featureListFold';

interface Card {
  stepRef: string;
  state: string;
  planned?: boolean;
}

function card(stepRef: string, state: string, planned = false): Card {
  return { stepRef, state, planned };
}

// 48 шагов: 40 готовых (done) и 8 неготовых (по четыре run/wait/queue/withdrawn).
// Снятый Ш44 не считается незакрытым и в свёртке не показывается (GC-R-F4).
function mixed48(): Card[] {
  const out: Card[] = [];
  for (let i = 1; i <= 40; i++) out.push(card(`Ш${i}`, 'done'));
  out.push(card('Ш41', 'run'), card('Ш42', 'wait'), card('Ш43', 'queue'), card('Ш44', 'withdrawn'));
  out.push(card('Ш45', 'run'), card('Ш46', 'wait'), card('Ш47', 'queue'), card('Ш48', 'run'));
  return out;
}

describe('foldProd: свёртка ленты шагов', () => {
  it('48 шагов, 40 готовых: видны 7 незакрытых (снятый не среди них) и последние 3 готовых, hiddenDone=37', () => {
    const cards = mixed48();
    const r = foldProd(cards, null, false);
    expect(r.visible.length).toBe(7 + 3);
    expect(r.hiddenDone.length).toBe(37);
    // Готовые в хвосте — последние по порядку входа: Ш38, Ш39, Ш40.
    expect(r.visible.filter(c => c.state === 'done').map(c => c.stepRef)).toEqual(['Ш38', 'Ш39', 'Ш40']);
    // Снятый шаг не показывается: ни как незакрытый, ни в хвосте готовых (GC-R-F4).
    expect(r.visible.some(c => c.stepRef === 'Ш44')).toBe(false);
    expect(r.hiddenDone.some(c => c.stepRef === 'Ш44')).toBe(false);
  });

  it('раскрытая старая готовая видна и уходит из hiddenDone', () => {
    const cards = mixed48();
    const r = foldProd(cards, 'Ш5', false);
    expect(r.visible.some(c => c.stepRef === 'Ш5')).toBe(true);
    expect(r.hiddenDone.some(c => c.stepRef === 'Ш5')).toBe(false);
    expect(r.visible.length).toBe(7 + 3 + 1);
    expect(r.hiddenDone.length).toBe(36);
  });

  it('showAll: видно всё, hiddenDone пуст', () => {
    const cards = mixed48();
    const r = foldProd(cards, null, true);
    expect(r.visible.length).toBe(48);
    expect(r.hiddenDone.length).toBe(0);
  });

  it('showAll показывает ровно то, что пришло: с дублем шага (GC-R-F4)', () => {
    // Без свёртки схлопывания нет: «Показать все 3» не может показать 2.
    const cards = [card('Ш1', 'done'), card('Ш1', 'done'), card('Ш2', 'run')];
    const r = foldProd(cards, null, true);
    expect(r.visible.length).toBe(3);
    expect(r.visible.filter(c => c.stepRef === 'Ш1').length).toBe(2);
    expect(r.hiddenDone).toEqual([]);
  });

  it('порядок видимых — исходный (не по датам и не по типу)', () => {
    // Готовых четыре — в хвост (три) попадают только последние три.
    const cards = [
      card('a', 'run'), card('b', 'done'), card('c', 'wait'), card('d', 'done'),
      card('e', 'done'), card('f', 'done'),
    ];
    const r = foldProd(cards, null, false);
    expect(r.visible.map(c => c.stepRef)).toEqual(['a', 'c', 'd', 'e', 'f']);
    expect(r.hiddenDone.map(c => c.stepRef)).toEqual(['b']);
  });

  it('вход не мутируется: элементы и их порядок те же', () => {
    const cards = mixed48();
    const before = cards.map(c => ({ ...c }));
    const snapshot = [...cards];
    foldProd(cards, 'Ш5', false);
    expect(cards).toEqual(before);
    expect(cards).toEqual(snapshot);
  });

  it('шаг, попавший и в хвост после мержа, и в шаги стадии, не считается дважды', () => {
    // Хвост «Ш1» присутствует и в списке стадии, и в отдельной строке хвостов.
    const stage = [card('Ш1', 'done'), card('Ш2', 'run')];
    const tail = [card('Ш1', 'done')];
    const merged = [...stage, ...tail];
    const r = foldProd(merged, null, false);
    expect(r.visible.filter(c => c.stepRef === 'Ш1').length).toBe(1);
    expect(r.visible.length).toBe(2);
  });

  it('ключ шага сравнивается без краёв и регистра', () => {
    const cards = [card('Б1', 'done'), card(' б1 ', 'done'), card('Б2', 'run')];
    const r = foldProd(cards, 'б1', false);
    expect(r.visible.filter(c => c.stepRef === 'Б1').length).toBe(1);
    expect(r.visible.length).toBe(2);
  });

  it('пустой список — пустой результат', () => {
    const r = foldProd([], null, false);
    expect(r.visible).toEqual([]);
    expect(r.hiddenDone).toEqual([]);
  });

  it('пространства состояний не смешиваются: accepted — не готовый, withdrawn — не незакрытый, queue — незакрытый', () => {
    // 'accepted' — имя бэковой корзины, состояния карточки с ним нет (ProdCardState):
    // готовым не считается (GC-R-F4). Сценарий с четырьмя 'done'-подобными нужен,
    // чтобы различие было измеримым: при ≤3 готовых любой попал бы в хвост.
    const cards = [
      card('a', 'accepted'),
      card('b', 'done'), card('c', 'done'), card('d', 'done'),
      card('e', 'withdrawn'),
      card('f', 'queue'),
    ];
    const r = foldProd(cards, null, false);
    // 'a' не готовый → виден как незакрытый, в hiddenDone не попадает;
    // 'e' снятый → не показывается нигде; 'f' queue — незакрытый, виден.
    expect(r.visible.map(c => c.stepRef)).toEqual(['a', 'b', 'c', 'd', 'f']);
    expect(r.hiddenDone).toEqual([]);
  });
});

describe('foldLast: свёртка общего списка', () => {
  it('74 элемента: видны последние 10, hiddenEarly=64', () => {
    const items = Array.from({ length: 74 }, (_, i) => card(`s${i}`, 'run'));
    const r = foldLast(items, null, false);
    expect(r.visible.length).toBe(10);
    expect(r.hiddenEarly.length).toBe(64);
    expect(r.visible[0]!.stepRef).toBe('s64');
    expect(r.visible[9]!.stepRef).toBe('s73');
  });

  it('foldLast не схлопывает: шаг и его доработка с одинаковым stepRef видны оба (GC-R-F4)', () => {
    // У задачи шага и задачи-доработки один stepRef (FeatureStepAccounting.cs:144-146) —
    // это разные задачи, обе должны остаться в ленте; длина входа сохраняется.
    const items = [card('GC-F1', 'done'), card('GC-F1', 'done'), card('GC-F2', 'run')];
    const r = foldLast(items, null, false, 10);
    expect(r.visible.length).toBe(3);
    expect(r.visible.filter(i => i.stepRef === 'GC-F1').length).toBe(2);
    expect(r.hiddenEarly).toEqual([]);
  });

  it('showAll: видно всё, hiddenEarly пуст', () => {
    const items = Array.from({ length: 74 }, (_, i) => card(`s${i}`, 'run'));
    const r = foldLast(items, null, true);
    expect(r.visible.length).toBe(74);
    expect(r.hiddenEarly.length).toBe(0);
  });

  it('раскрытый ранний элемент виден и уходит из hiddenEarly', () => {
    const items = Array.from({ length: 74 }, (_, i) => card(`s${i}`, 'run'));
    const r = foldLast(items, 's3', false);
    expect(r.visible.some(i => i.stepRef === 's3')).toBe(true);
    expect(r.hiddenEarly.some(i => i.stepRef === 's3')).toBe(false);
    expect(r.visible.length).toBe(11);
    expect(r.hiddenEarly.length).toBe(63);
  });

  it('порядок видимых — исходный; вход не мутируется', () => {
    const items = Array.from({ length: 12 }, (_, i) => card(`s${i}`, 'run'));
    const before = items.map(i => ({ ...i }));
    const r = foldLast(items, null, false);
    expect(r.visible.map(i => i.stepRef)).toEqual(['s2', 's3', 's4', 's5', 's6', 's7', 's8', 's9', 's10', 's11']);
    expect(items).toEqual(before);
  });

  it('свой n: столько последних элементов видно', () => {
    const items = Array.from({ length: 30 }, (_, i) => card(`s${i}`, 'run'));
    const r = foldLast(items, null, false, 5);
    expect(r.visible.length).toBe(5);
    expect(r.hiddenEarly.length).toBe(25);
  });

  it('список короче n — виден целиком', () => {
    const items = [card('a', 'run'), card('b', 'run')];
    const r = foldLast(items, null, false);
    expect(r.visible.length).toBe(2);
    expect(r.hiddenEarly.length).toBe(0);
  });
});

describe('константа хвоста', () => {
  it('PROD_DONE_TAIL = 3', () => {
    expect(PROD_DONE_TAIL).toBe(3);
  });
});
