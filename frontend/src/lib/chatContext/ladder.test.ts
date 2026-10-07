import { describe, expect, it } from 'vitest';
import { contextRowLadder, gitChipBox, ladderNominal, ladderRungs, NOM, type LadderFacts } from './ladder';

// Типовое состояние макета: проект, объект, «Чем», два референса
const typical: LadderFacts = { project: true, hasPrimary: true, hasExec: true, refs: 2 };
// Тот же состав в «Чате»: «Чем» в строке нет (Р2)
const inChat: LadderFacts = { ...typical, hasExec: false };

describe('contextRowLadder', () => {
  it('номиналы типового состояния совпадают с таблицей макета', () => {
    const noms = ladderRungs(typical).map(f => ladderNominal(typical, f));
    expect(noms).toEqual([1053, 929, 817, 707, 597, 523, 485, 401]);
  });

  it('порог каждой ступени: на пороге берётся она, на пиксель уже — следующая', () => {
    const idx = (w: number) => contextRowLadder(w, typical).index;
    expect(idx(926)).toBe(2);
    expect(idx(817)).toBe(2);
    expect(idx(816)).toBe(3);
    expect(idx(707)).toBe(3);
    expect(idx(706)).toBe(4);
    expect(idx(597)).toBe(4);
    expect(idx(596)).toBe(5);
    expect(idx(523)).toBe(5);
    expect(idx(522)).toBe(6);
    expect(idx(485)).toBe(6);
    expect(idx(484)).toBe(7);
    expect(idx(401)).toBe(7);
  });

  it('ниже последней ступени строка прокручивается, объект и «Чем» не пропадают', () => {
    const p = contextRowLadder(400, typical);
    expect(p.scroll).toBe(true);
    expect(p.form).toMatchObject({ o: 1, e: 1, g: 1, k: 0 });
    expect(contextRowLadder(401, typical).scroll).toBe(false);
  });

  it('ступени из макета: референсы уходят в «+N» с конца, слова «Чем:» и ветки сжимаются раньше', () => {
    expect(contextRowLadder(930, typical).form).toMatchObject({ e: 2, g: 3, k: 2 });
    expect(contextRowLadder(926, typical).form).toMatchObject({ e: 2, g: 2, k: 2 });
    expect(contextRowLadder(820, typical).form).toMatchObject({ e: 2, g: 2, k: 2 });
    expect(contextRowLadder(710, typical).form).toMatchObject({ g: 2, k: 1 });
    expect(contextRowLadder(600, typical).form).toMatchObject({ g: 2, k: 0 });
    expect(contextRowLadder(550, typical).form).toMatchObject({ g: 1, o: 2, k: 0 });
  });

  it('«Чем» в «Чате» занимает 0 px и его ступеней нет: лестница короче на две', () => {
    expect(ladderRungs(inChat)).toHaveLength(ladderRungs(typical).length - 2);
    const noms = ladderRungs(inChat).map(f => ladderNominal(inChat, f));
    expect(noms).toEqual([727, 615, 505, 395, 321, 283]);
    expect(contextRowLadder(727, inChat).index).toBe(0);
    expect(contextRowLadder(283, inChat).scroll).toBe(false);
    expect(contextRowLadder(282, inChat).scroll).toBe(true);
  });

  it('«Чем» без объекта не считается: действие есть только у объекта', () => {
    const noObj: LadderFacts = { project: true, hasPrimary: false, hasExec: true, refs: 0 };
    expect(ladderRungs(noObj).map(f => ladderNominal(noObj, f))).toEqual([ladderNominal(noObj, { g: 3, o: 2, e: 3, k: 0 }), ladderNominal(noObj, { g: 2, o: 2, e: 3, k: 0 }), ladderNominal(noObj, { g: 1, o: 2, e: 3, k: 0 })]);
    // чип ветки один: 10 + 262
    expect(contextRowLadder(272, noObj).index).toBe(0);
  });

  it('личный чат: ветки нет, строка начинается с объекта', () => {
    const personal: LadderFacts = { project: false, hasPrimary: true, hasExec: false, refs: 0 };
    expect(ladderRungs(personal).map(f => f.g)).toEqual([3, 3]);
    expect(contextRowLadder(10 + 150, personal).index).toBe(0);
  });

  it('строка не зависит от панели: открытость панели во вход не входит', () => {
    // Вход — только ширина и факты. Лишнее поле, подсунутое снаружи, на результат не влияет
    const withPanel = { ...typical, panelOpen: true } as LadderFacts;
    for (const w of [300, 401, 523, 707, 817, 926]) {
      expect(contextRowLadder(w, withPanel)).toEqual(contextRowLadder(w, typical));
    }
  });

  it('телефон: ветка иконкой 34 px', () => {
    const mob: LadderFacts = { ...typical, mobile: true };
    expect(ladderNominal(mob, { g: 3, o: 2, e: 3, k: 0 })).toBe(ladderNominal(typical, { g: 3, o: 2, e: 3, k: 0 }) - 262 + 34);
  });
});

describe('gitChipBox', () => {
  it('на полной форме ветка по содержимому, на тесных ступенях режется', () => {
    expect(gitChipBox(3).label).toBeNull();
    expect(gitChipBox(3).chip).toBeGreaterThan(300);
    expect(gitChipBox(2)).toEqual({ chip: 150, label: 76 });
  });
});

describe('чип «Формат» в лестнице (ADR-025)', () => {
  const withFormat: LadderFacts = { ...typical, hasFormat: true };

  it('без чипа ступени прежние: лестница не знает про поле f', () => {
    expect(ladderRungs(typical).every(f => f.f === undefined)).toBe(true);
    expect(ladderRungs(typical)).toHaveLength(8);
  });

  it('с чипом на одну ступень длиннее: после «Чем» сжимается и сам «Формат»', () => {
    const r = ladderRungs(withFormat);
    expect(r).toHaveLength(10);
    expect(r[0]).toMatchObject({ e: 3, f: 2 });
    expect(r[1]).toMatchObject({ e: 2, f: 2 });
    expect(r[2]).toMatchObject({ e: 2, f: 1 });
    // До голых пропорций «16:9» чип сжимается после ветки g1, но раньше объекта и «Чем»
    const bare = r.findIndex(x => x.f === 0);
    expect(bare).toBeGreaterThan(r.findIndex(x => x.g === 1) - 1);
    expect(r[bare]).toMatchObject({ o: 2, e: 2, g: 1 });
    expect(r[bare + 1]).toMatchObject({ o: 1, f: 0 });
  });

  it('номинал растёт ровно на ширину чипа и зазор', () => {
    const first = ladderRungs(typical)[0];
    expect(ladderNominal(withFormat, ladderRungs(withFormat)[0]) - ladderNominal(typical, first)).toBe(NOM.f2 + NOM.gap);
  });
});
