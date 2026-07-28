// История режима «Фокус» в сторе графа: выбор узла копит путь переходов,
// «Назад» и клик по крошке возвращают центр, снятие выбора очищает историю.
import { describe, it, expect, beforeEach } from 'vitest';
import {
  selectGraphNode, focusGraphBack, focusGraphCrumb, setGraphFocusTail,
  toggleGraphFocusDepth2, getCodeGraphState,
} from '../codeGraph';

describe('фокус графа: история переходов', () => {
  beforeEach(() => { selectGraphNode(null); });

  it('переход от узла к узлу копит историю', () => {
    selectGraphNode('a');
    selectGraphNode('b');
    selectGraphNode('c');
    const s = getCodeGraphState();
    expect(s.selectedId).toBe('c');
    expect(s.focusHistory).toEqual(['a', 'b']);
  });

  it('«Назад» возвращает предыдущий центр', () => {
    selectGraphNode('a');
    selectGraphNode('b');
    focusGraphBack();
    expect(getCodeGraphState().selectedId).toBe('a');
    expect(getCodeGraphState().focusHistory).toEqual([]);
    focusGraphBack();                                   // истории больше нет — no-op
    expect(getCodeGraphState().selectedId).toBe('a');
  });

  it('клик по крошке отбрасывает всё, что было после неё', () => {
    selectGraphNode('a');
    selectGraphNode('b');
    selectGraphNode('c');
    focusGraphCrumb('a');
    const s = getCodeGraphState();
    expect(s.selectedId).toBe('a');
    expect(s.focusHistory).toEqual([]);
  });

  it('клик по пустому холсту снимает выбор, историю и раскрытый хвост', () => {
    selectGraphNode('a');
    selectGraphNode('b');
    setGraphFocusTail('in');
    expect(getCodeGraphState().focusTail).toBe('in');
    selectGraphNode(null);
    const s = getCodeGraphState();
    expect(s.selectedId).toBeNull();
    expect(s.focusHistory).toEqual([]);
    expect(s.focusTail).toBeNull();
  });

  it('смена центра сбрасывает хвост, тумблер глубины переключается', () => {
    selectGraphNode('a');
    setGraphFocusTail('out');
    selectGraphNode('b');
    expect(getCodeGraphState().focusTail).toBeNull();
    toggleGraphFocusDepth2();
    expect(getCodeGraphState().focusDepth2).toBe(true);
    toggleGraphFocusDepth2();
    expect(getCodeGraphState().focusDepth2).toBe(false);
  });
});
