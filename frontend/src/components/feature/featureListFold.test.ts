// Свёртка длинных списков карточек (GC-F4): чистые функции foldProd и foldLast.
// Проверяем: незакрытые видны всегда, из готовых — только последние, раскрытая
// карточка не прячется никогда, showAll показывает всё, порядок видимых исходный,
// вход не мутируется, шаг, попавший и в хвост после мержа, и в шаги стадии,
// не считается дважды.

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

// 48 шагов: 40 готовых (done) и 8 незакрытых (по четыре run/wait/queue/withdrawn).
function mixed48(): Card[] {
  const out: Card[] = [];
  for (let i = 1; i <= 40; i++) out.push(card(`Ш${i}`, 'done'));
  out.push(card('Ш41', 'run'), card('Ш42', 'wait'), card('Ш43', 'queue'), card('Ш44', 'withdrawn'));
  out.push(card('Ш45', 'run'), card('Ш46', 'wait'), card('Ш47', 'queue'), card('Ш48', 'run'));
  return out;
}

describe('foldProd: свёртка ленты шагов', () => {
  it('48 шагов, 40 готовых: видны 8 незакрытых и последние 3 готовых, hiddenDone=37', () => {
    const cards = mixed48();
    const r = foldProd(cards, null, false);
    expect(r.visible.length).toBe(8 + 3);
    expect(r.hiddenDone.length).toBe(37);
    // Готовые в хвосте — последние по порядку входа: Ш38, Ш39, Ш40.
    expect(r.visible.filter(c => c.state === 'done').map(c => c.stepRef)).toEqual(['Ш38', 'Ш39', 'Ш40']);
  });

  it('раскрытая старая готовая видна и уходит из hiddenDone', () => {
    const cards = mixed48();
    const r = foldProd(cards, 'Ш5', false);
    expect(r.visible.some(c => c.stepRef === 'Ш5')).toBe(true);
    expect(r.hiddenDone.some(c => c.stepRef === 'Ш5')).toBe(false);
    expect(r.visible.length).toBe(8 + 3 + 1);
    expect(r.hiddenDone.length).toBe(36);
  });

  it('showAll: видно всё, hiddenDone пуст', () => {
    const cards = mixed48();
    const r = foldProd(cards, null, true);
    expect(r.visible.length).toBe(48);
    expect(r.hiddenDone.length).toBe(0);
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

  it('готовость карточки — по состоянию, а не по корзине бэка', () => {
    // 'accepted' тоже готовый; 'withdrawn' — нет (не входит в хвост, но виден как незакрытый).
    const cards = [card('a', 'accepted'), card('b', 'withdrawn'), card('c', 'run')];
    const r = foldProd(cards, null, false);
    expect(r.hiddenDone).toEqual([]);
    expect(r.visible.length).toBe(3);
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
