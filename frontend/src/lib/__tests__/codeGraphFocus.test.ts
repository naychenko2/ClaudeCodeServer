// Единая цепочка навигации (`navPath`) в сторе графа: узел-шаг ведёт в «Фокус» (свежий
// select пересчитывает цепочку с нуля, refocus — перефокус — дописывает её в конец),
// группа-шаг — в «Обзор» с соответствующим раскрытием. «Назад» и клик по ступени
// («toStep») — единый способ вернуться, работающий одинаково для обоих видов шагов.
import { describe, it, expect, beforeEach } from 'vitest';
import {
  selectGraphNode, refocusGraphNode, navGraphBack, navGraphToStep, setGraphFocusTail,
  toggleGraphFocusDepth2, expandOverviewGroup, drillOverviewTypes, getCodeGraphState,
} from '../codeGraph';

describe('навигация графа: узел-шаги («Фокус»)', () => {
  beforeEach(() => { selectGraphNode(null); });

  it('свежий select без загруженного графа строит цепочку из одного узла', () => {
    selectGraphNode('a');
    const s = getCodeGraphState();
    expect(s.selectedId).toBe('a');
    expect(s.viewMode).toBe('focus');
    expect(s.focusHistory).toEqual([]);
  });

  it('перефокус (refocus) дописывает шаг в конец, история копится', () => {
    selectGraphNode('a');
    refocusGraphNode('b');
    refocusGraphNode('c');
    const s = getCodeGraphState();
    expect(s.selectedId).toBe('c');
    expect(s.focusHistory).toEqual(['a', 'b']);
  });

  it('«Назад» возвращает предыдущий центр, затем уводит в корень «Обзора»', () => {
    selectGraphNode('a');
    refocusGraphNode('b');
    navGraphBack();
    expect(getCodeGraphState().selectedId).toBe('a');
    expect(getCodeGraphState().focusHistory).toEqual([]);
    navGraphBack();                                     // единственный узел-шаг — назад к корню
    expect(getCodeGraphState().selectedId).toBeNull();
    expect(getCodeGraphState().viewMode).toBe('overview');
    navGraphBack();                                      // цепочка уже пуста — no-op
    expect(getCodeGraphState().selectedId).toBeNull();
  });

  it('клик по ступени (toStep) отбрасывает всё, что было после неё', () => {
    selectGraphNode('a');
    refocusGraphNode('b');
    refocusGraphNode('c');
    navGraphToStep(0);                                   // шаг 0 = узел 'a'
    const s = getCodeGraphState();
    expect(s.selectedId).toBe('a');
    expect(s.focusHistory).toEqual([]);
  });

  it('toStep(-1) — полный сброс к корню «Обзора»', () => {
    selectGraphNode('a');
    refocusGraphNode('b');
    navGraphToStep(-1);
    const s = getCodeGraphState();
    expect(s.selectedId).toBeNull();
    expect(s.viewMode).toBe('overview');
  });

  it('клик по пустому холсту (select(null)) снимает выбор, историю и раскрытый хвост', () => {
    selectGraphNode('a');
    refocusGraphNode('b');
    setGraphFocusTail('in');
    expect(getCodeGraphState().focusTail).toBe('in');
    selectGraphNode(null);
    const s = getCodeGraphState();
    expect(s.selectedId).toBeNull();
    expect(s.focusHistory).toEqual([]);
    expect(s.focusTail).toBeNull();
  });

  it('перефокус на тот же центр — no-op, смена центра сбрасывает хвост', () => {
    selectGraphNode('a');
    setGraphFocusTail('out');
    refocusGraphNode('a');                               // тот же узел — no-op, хвост не трогаем
    expect(getCodeGraphState().focusTail).toBe('out');
    refocusGraphNode('b');
    expect(getCodeGraphState().focusTail).toBeNull();
  });

  it('тумблер глубины переключается независимо от навигации', () => {
    selectGraphNode('a');
    toggleGraphFocusDepth2();
    expect(getCodeGraphState().focusDepth2).toBe(true);
    toggleGraphFocusDepth2();
    expect(getCodeGraphState().focusDepth2).toBe(false);
  });
});

describe('навигация графа: группа-шаги («Обзор»)', () => {
  beforeEach(() => { selectGraphNode(null); });

  it('раскрытие группы копит цепочку, «Обзор» остаётся текущим видом', () => {
    expandOverviewGroup('A');
    expandOverviewGroup('A.B');
    const s = getCodeGraphState();
    expect(s.viewMode).toBe('overview');
    expect(s.overviewExpanded).toEqual(['A', 'A.B']);
    expect(s.overviewTypesGroup).toBeNull();
  });

  it('drillOverviewTypes на последней раскрытой группе не дублирует ступень', () => {
    expandOverviewGroup('A');
    expandOverviewGroup('A.B');
    drillOverviewTypes('A.B');
    const s = getCodeGraphState();
    expect(s.overviewExpanded).toEqual(['A']);           // A.B теперь «раскрыта до типов», не в expanded
    expect(s.overviewTypesGroup).toBe('A.B');
  });

  it('toStep возвращает ровно к раскрытию на этом шаге', () => {
    expandOverviewGroup('A');
    expandOverviewGroup('A.B');
    drillOverviewTypes('A.B');
    navGraphToStep(0);                                   // шаг 0 = группа 'A' (expand, не drilled)
    const s = getCodeGraphState();
    expect(s.overviewExpanded).toEqual(['A']);
    expect(s.overviewTypesGroup).toBeNull();
  });

  it('свежий select со сброшенным графом отбрасывает группа-шаги', () => {
    expandOverviewGroup('A');
    selectGraphNode('x');
    const s = getCodeGraphState();
    expect(s.viewMode).toBe('focus');
    expect(s.selectedId).toBe('x');
    expect(s.overviewExpanded).toEqual([]);
  });
});
