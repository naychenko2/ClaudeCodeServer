import { describe, expect, it } from 'vitest';
import { actionChips, chipSelectable, CHAT_CHIP_ID, MAX_VIEW_ACTIONS } from './actionChips';
import type { ContextAction } from './types';

const run = (id: string, over: Partial<ContextAction> = {}): ContextAction => ({ id, kind: 'run', label: id, hint: `хинт ${id}`, op: id, ...over });
const many = (n: number) => Array.from({ length: n }, (_, i) => run(`a${i}`));

describe('чипы действий: потолок и «Чат» первым', () => {
  it('«Чат» ставит хост первым; пять действий вида — итого шесть', () => {
    const chips = actionChips(many(5));
    expect(chips).toHaveLength(6);
    expect(chips[0]).toMatchObject({ id: CHAT_CHIP_ID, kind: 'chat', label: 'Чат' });
    expect(chips.slice(1).map(c => c.id)).toEqual(['a0', 'a1', 'a2', 'a3', 'a4']);
  });

  it('седьмое действие (шестое от вида) — исключение, а не молчаливая обрезка', () => {
    expect(MAX_VIEW_ACTIONS).toBe(5);
    expect(() => actionChips(many(6))).toThrow(/потолок/);
  });

  it('дубль id и занятый id «Чата» — исключение', () => {
    expect(() => actionChips([run('x'), run('x')])).toThrow();
    expect(() => actionChips([run(CHAT_CHIP_ID)])).toThrow();
  });

  it('радио только среди «Чата» и живых run; вход, меню и серое — не выбираются', () => {
    const chips = actionChips([
      run('edit'),
      run('stems', { disabledReason: 'Выделите кусок на волне' }),
      { id: 'mark', kind: 'editor', label: 'Отметить', hint: 'Открыть редактор', open: () => {} },
      { id: 'frame', kind: 'menu', label: 'Кадр A', hint: 'откуда взять', items: () => [] },
    ]);
    const byId = Object.fromEntries(chips.map(c => [c.id, chipSelectable(c)]));
    expect(byId).toEqual({ [CHAT_CHIP_ID]: true, edit: true, stems: false, mark: false, frame: false });
    // Тултип серого чипа несёт причину
    expect(chips.find(c => c.id === 'stems')!.hint).toBe('stems: Выделите кусок на волне');
  });
});
