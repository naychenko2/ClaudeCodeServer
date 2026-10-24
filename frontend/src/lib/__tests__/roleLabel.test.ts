import { describe, it, expect } from 'vitest';
import { roleLabel } from '../roleLabel';

// Подпись роли из сырых значений (taskRole/stepRef/lane): словарь по роли +
// для step «{stepRef} · {бэк|фронт}» (линия нормализуется, как в laneOfChain).

describe('roleLabel: словарь по роли', () => {
  it('rework → «доработка {stepRef}»', () => {
    expect(roleLabel('rework', 'Ш2', 'back')).toBe('доработка Ш2');
  });
  it('review → «ревью»', () => expect(roleLabel('review', null, null)).toBe('ревью'));
  it('qa → «проверка»', () => expect(roleLabel('qa', null, null)).toBe('проверка'));
  it('final → «финал»', () => expect(roleLabel('final', null, null)).toBe('финал'));
  it('merge → «мерж»', () => expect(roleLabel('merge', null, null)).toBe('мерж'));
  it('cut → «разрез»', () => expect(roleLabel('cut', null, null)).toBe('разрез'));
  it('design → «дизайн»', () => expect(roleLabel('design', null, null)).toBe('дизайн'));
  it('plan → «план»', () => expect(roleLabel('plan', null, null)).toBe('план'));
  it('consult → «консультация»', () => expect(roleLabel('consult', null, null)).toBe('консультация'));
});

describe('roleLabel: step → «{stepRef} · {линия}»', () => {
  it('back → «{stepRef} · бэк»', () => expect(roleLabel('step', 'Б1a', 'back')).toBe('Б1a · бэк'));
  it('front → «{stepRef} · фронт»', () => expect(roleLabel('step', 'Ф0', 'front')).toBe('Ф0 · фронт'));
  it('линия как есть (бэк) → «{stepRef} · бэк»', () => expect(roleLabel('step', 'Ш2', 'бэк')).toBe('Ш2 · бэк'));
  it('линия как есть (фронт) → «{stepRef} · фронт»', () => expect(roleLabel('step', 'Ш2', 'фронт')).toBe('Ш2 · фронт'));
  it('линия backend → бэк', () => expect(roleLabel('step', 'D1', 'backend')).toBe('D1 · бэк'));
  it('линия frontend → фронт', () => expect(roleLabel('step', 'D1', 'frontend')).toBe('D1 · фронт'));
  it('линия null → только stepRef', () => expect(roleLabel('step', 'Ш2', null)).toBe('Ш2'));
  it('линия пустая → только stepRef', () => expect(roleLabel('step', 'Ш2', '')).toBe('Ш2'));
  it('линия «тест» (иная) → только stepRef', () => expect(roleLabel('step', 'Ш2', 'тест')).toBe('Ш2'));
  it('stepRef null + линия → только линия', () => expect(roleLabel('step', null, 'back')).toBe('бэк'));
  it('stepRef null + линия null → пусто', () => expect(roleLabel('step', null, null)).toBe(''));
});

describe('roleLabel: null/прочее → пусто', () => {
  it('taskRole null → «»', () => expect(roleLabel(null, 'Ш2', 'back')).toBe(''));
  it('taskRole «feature» (эпик, не в словаре) → «»', () => expect(roleLabel('feature', null, null)).toBe(''));
  it('taskRole «unknown» → «»', () => expect(roleLabel('unknown', null, null)).toBe(''));
});
