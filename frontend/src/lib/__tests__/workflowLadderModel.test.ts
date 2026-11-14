import { describe, expect, it } from 'vitest';
import { buildLadder, type LadderStageInput } from '../workflowLadderModel';

const st = (id: string, after: string[], over: Partial<LadderStageInput> = {}): LadderStageInput =>
  ({ id, after, presence: 'on', skippable: false, ...over });

describe('buildLadder', () => {
  it('линейный маршрут: по колонке на этап, без параллельных и дуг', () => {
    const l = buildLadder([st('a', []), st('b', ['a']), st('c', ['b'])], new Set());
    expect(l.columns).toEqual([
      { index: 0, stageIds: ['a'], parallel: false },
      { index: 1, stageIds: ['b'], parallel: false },
      { index: 2, stageIds: ['c'], parallel: false },
    ]);
    expect(l.edges).toEqual([{ from: 'a', to: 'b', far: false }, { from: 'b', to: 'c', far: false }]);
    expect(l.removed).toEqual([]);
  });

  it('параллельный цикл: две ветки в одной колонке и сходятся в ревью', () => {
    const l = buildLadder([
      st('plan', []), st('back', ['plan']), st('front', ['plan']), st('review', ['back', 'front']),
    ], new Set());
    expect(l.columns[1]).toEqual({ index: 1, stageIds: ['back', 'front'], parallel: true });
    expect(l.columns[2]).toEqual({ index: 2, stageIds: ['review'], parallel: false });
    expect(l.edges).toContainEqual({ from: 'back', to: 'review', far: false });
    expect(l.edges).toContainEqual({ from: 'front', to: 'review', far: false });
  });

  it('снятие qa-plan: потомок наследует предшественника, колонки сжимаются', () => {
    const stages = [
      st('plan', []), st('qa-plan', ['plan'], { presence: 'optional', skippable: true }), st('step', ['qa-plan']),
    ];
    const l = buildLadder(stages, new Set(['qa-plan']));
    expect(l.removed).toEqual(['qa-plan']);
    expect(l.columns.map(c => c.stageIds)).toEqual([['plan'], ['step']]);
    expect(l.edges).toEqual([{ from: 'plan', to: 'step', far: false }]);
  });

  it('снятие несъёмного и обязательного шаблоном игнорируется, locked называет причину', () => {
    const stages = [
      st('plan', []), st('step', ['plan']),
      st('qa', ['step'], { skippable: true }),
    ];
    const l = buildLadder(stages, new Set(['step', 'qa']));
    expect(l.removed).toEqual([]);
    expect(l.locked).toEqual({ plan: 'role', step: 'role', qa: 'template' });
  });

  it('ребро через колонку помечено far, транзитивное ребро убрано', () => {
    const l = buildLadder([
      st('a', []), st('b', ['a']), st('c', ['b']), st('d', ['a']), st('e', ['c', 'd']), st('f', ['a', 'c']),
    ], new Set());
    expect(l.edges).toContainEqual({ from: 'd', to: 'e', far: true });
    expect(l.edges).not.toContainEqual(expect.objectContaining({ from: 'a', to: 'f' }));
  });

  it('presence=off снимает этап сам', () => {
    const l = buildLadder([st('a', []), st('x', ['a'], { presence: 'off', skippable: true }), st('b', ['x'])], new Set());
    expect(l.removed).toEqual(['x']);
    expect(l.edges).toEqual([{ from: 'a', to: 'b', far: false }]);
  });
});
