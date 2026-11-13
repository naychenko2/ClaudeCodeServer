import { describe, expect, it } from 'vitest';
import { STAGE_ROLE_LABEL } from './WorkflowStageRow';
import { afterNames, keyboardTarget, presenceTag, stripColumns, stripGroups, whoLabel } from './workflowStripModel';
import type { WorkflowStage } from './types';

const parallel = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'design', presence: 'optional' },
  { role: 'plan', presence: 'on' },
  { role: 'qa', presence: 'optional', id: 'qa-plan', title: 'Проверка плана', after: ['design'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
  { role: 'review', presence: 'on', after: ['step-back', 'step-front'] },
];

describe('лента этапов', () => {
  it('номер = колонка, параллельные делят номер', () => {
    expect(stripColumns(parallel())).toEqual([1, 2, 3, 3, 4, 4, 5]);
  });

  it('выключенный этап — «—», остальные не сдвигаются', () => {
    const s = parallel();
    s[1] = { ...s[1], presence: 'off' };
    expect(stripColumns(s)).toEqual([1, null, 2, 2, 3, 3, 4]);
  });

  it('«после» именами, шаги различаются линией', () => {
    expect(afterNames(parallel(), 6, STAGE_ROLE_LABEL)).toEqual(['Шаг · бэк', 'Шаг · фронт']);
    expect(afterNames(parallel(), 3, STAGE_ROLE_LABEL)).toEqual(['Дизайн']);
  });

  it('рамки только у подряд идущих этапов одной колонки', () => {
    const g = stripGroups(parallel()).filter(x => x.parallel);
    expect(g.map(x => [x.column, x.from, x.to])).toEqual([[3, 2, 3], [4, 4, 5]]);
  });

  it('выключенный этап в рамку не попадает', () => {
    const s = parallel();
    s[3] = { ...s[3], presence: 'off' };
    expect(stripGroups(s).filter(x => x.parallel).map(x => x.column)).toEqual([4]);
  });

  it('«кто»: персона, специальность, шаг без выбора, прочее без выбора', () => {
    const persons = [{ id: 'p1', name: 'Александр', role: 'Архитектор' }];
    expect(whoLabel({ role: 'cut', presence: 'on', personaId: 'p1' }, persons, {})).toBe('Архитектор · Александр');
    expect(whoLabel({ role: 'qa', presence: 'on', specialty: 'qa' as never }, persons, { qa: 'QA-тестер' })).toBe('QA-тестер');
    expect(whoLabel({ role: 'step', presence: 'on' }, persons, {})).toBe('исполнитель из плана');
    expect(whoLabel({ role: 'review', presence: 'on' }, persons, {})).toBeNull();
  });

  it('тег присутствия', () => {
    expect(presenceTag('on').text).toBe('всегда');
    expect(presenceTag('optional').text).toBe('по решению');
    expect(presenceTag('off').text).toBe('нет');
  });

  it('подписи ролей: qa — «Проверка», cut — «Разрез»', () => {
    expect(STAGE_ROLE_LABEL.qa).toBe('Проверка');
    expect(STAGE_ROLE_LABEL.cut).toBe('Разрез');
  });

  it('клавиатурный шаг не выходит за список', () => {
    expect(keyboardTarget(0, -1, 3)).toBeNull();
    expect(keyboardTarget(2, 1, 3)).toBeNull();
    expect(keyboardTarget(1, 1, 3)).toBe(2);
  });
});
