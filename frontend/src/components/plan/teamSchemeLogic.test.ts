// Юнит-тесты чистой логики схемы командного плана. Покрывают:
//  • waveHint — подписи волн («параллельно» / «после первой» / «после N-й»);
//  • groupByWave — группировка и сортировка, несмежные номера волн, порядок внутри волны;
//  • countsOf — производные счётчики: уникальные исполнители, под-задачи без
//    исполнителя, файлы в работе;
//  • attentionOf — детерминированный блок внимания: конфликты файлов и под-задачи
//    без исполнителя, порядок пунктов;
//  • buildTeamScheme — сборка согласована с отдельными функциями;
//  • countNumbers — ряд чисел «Сути» со склонениями на границах (1/2/5/11/21).

import { describe, expect, it } from 'vitest';
import type { TeamPlanSubtask } from '../../types';
import {
  attentionOf, buildTeamScheme, countNumbers, countsOf, groupByWave, waveHint,
  type TeamSchemeCounts,
} from './teamSchemeLogic';

function subtask(over: Partial<TeamPlanSubtask> = {}): TeamPlanSubtask {
  return {
    id: 'st1', title: 'Под-задача', goal: '', executorPersonaId: null,
    executorRationale: '', files: [], wave: 1, doneCriteria: '',
    ...over,
  };
}

describe('waveHint', () => {
  it('первая волна — параллельно', () => {
    expect(waveHint(1)).toBe('параллельно');
  });

  it('номера <= 1 защищаются тем же правилом (нулевая волна не бывает, но не падаем)', () => {
    expect(waveHint(0)).toBe('параллельно');
    expect(waveHint(-1)).toBe('параллельно');
  });

  it('вторая — после первой', () => {
    expect(waveHint(2)).toBe('после первой');
  });

  it('третья и далее — «после N-й» по предыдущему номеру', () => {
    expect(waveHint(3)).toBe('после 2-й');
    expect(waveHint(5)).toBe('после 4-й');
  });
});

describe('groupByWave', () => {
  it('пустые subtasks — пустой результат', () => {
    expect(groupByWave([])).toEqual([]);
  });

  it('одна волна — одна группа с подписью «параллельно»', () => {
    const groups = groupByWave([subtask({ id: 'a' }), subtask({ id: 'b' })]);
    expect(groups).toHaveLength(1);
    expect(groups[0].wave).toBe(1);
    expect(groups[0].hint).toBe('параллельно');
  });

  it('несмежные номера волн сортируются по возрастанию, пустых волн не выдумывает', () => {
    const groups = groupByWave([
      subtask({ id: 'late', wave: 7 }),
      subtask({ id: 'first', wave: 1 }),
      subtask({ id: 'mid', wave: 3 }),
    ]);
    expect(groups.map(g => g.wave)).toEqual([1, 3, 7]);
    // Подпись считается по НОМЕРУ волны, а не по порядку группы — семантика waveHint
    expect(groups[1].hint).toBe('после 2-й');
    expect(groups[2].hint).toBe('после 6-й');
  });

  it('внутри волны порядок под-задач — исходный, не переупорядочиваем', () => {
    const groups = groupByWave([
      subtask({ id: 'b', wave: 2 }),
      subtask({ id: 'a', wave: 1 }),
      subtask({ id: 'c', wave: 2 }),
      subtask({ id: 'd', wave: 1 }),
    ]);
    expect(groups.map(g => g.items.map(s => s.id))).toEqual([['a', 'd'], ['b', 'c']]);
  });
});

describe('countsOf', () => {
  it('пустые subtasks — все счётчики нулевые', () => {
    expect(countsOf([])).toEqual<TeamSchemeCounts>({
      subtasks: 0, waves: 0, executors: 0, unassigned: 0, files: 0,
    });
  });

  it('уникальные исполнители: повтор personaId считается один раз', () => {
    const counts = countsOf([
      subtask({ executorPersonaId: 'p1' }),
      subtask({ executorPersonaId: 'p2' }),
      subtask({ executorPersonaId: 'p1' }),
    ]);
    expect(counts.executors).toBe(2);
    expect(counts.unassigned).toBe(0);
  });

  it('null/undefined/пустая строка — исполнитель не назначен', () => {
    const counts = countsOf([
      subtask({ executorPersonaId: null }),
      subtask({ executorPersonaId: undefined }),
      subtask({ executorPersonaId: '' }),
    ]);
    expect(counts.unassigned).toBe(3);
    expect(counts.executors).toBe(0);
  });

  it('файлы в работе — уникальные по всем под-задачам', () => {
    const counts = countsOf([
      subtask({ files: ['a.ts', 'b.ts'] }),
      subtask({ files: ['a.ts'] }),
      subtask({ files: [] }),
    ]);
    expect(counts.files).toBe(2);
  });

  it('волны и под-задачи считаются по фактической структуре', () => {
    const counts = countsOf([
      subtask({ wave: 1, executorPersonaId: 'p1' }),
      subtask({ wave: 2, executorPersonaId: 'p2' }),
      subtask({ wave: 1 }),
    ]);
    expect(counts.subtasks).toBe(3);
    expect(counts.waves).toBe(2);
    expect(counts.unassigned).toBe(1);
  });
});

describe('attentionOf', () => {
  it('пустые subtasks — внимания нет', () => {
    expect(attentionOf([])).toEqual([]);
  });

  // Фикстуры конфликтов: исполнители назначены, чтобы no-executor пункты не
  // примешивались к проверяемым file-conflict (изолируем одну причину внимания).
  it('файл в двух под-задачах — конфликт с id обеих в порядке под-задач', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1', files: ['a.ts'] }),
      subtask({ id: 'st2', executorPersonaId: 'p2', files: ['a.ts'] }),
    ]);
    expect(attention).toEqual([
      { kind: 'file-conflict', file: 'a.ts', subtaskIds: ['st1', 'st2'] },
    ]);
  });

  it('файл дважды ВНУТРИ одной под-задачи — не конфликт (одна рука)', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1', files: ['a.ts', 'a.ts'] }),
    ]);
    expect(attention).toEqual([]);
  });

  it('файл в трёх под-задачах — все три id без дублей', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1', files: ['a.ts'] }),
      subtask({ id: 'st2', executorPersonaId: 'p2', files: ['a.ts'] }),
      subtask({ id: 'st3', executorPersonaId: 'p3', files: ['a.ts'] }),
    ]);
    expect(attention).toEqual([
      { kind: 'file-conflict', file: 'a.ts', subtaskIds: ['st1', 'st2', 'st3'] },
    ]);
  });

  it('несколько конфликтов — в порядке первого появления файла', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1', files: ['x.ts'] }),
      subtask({ id: 'st2', executorPersonaId: 'p2', files: ['y.ts', 'x.ts'] }),
      subtask({ id: 'st3', executorPersonaId: 'p3', files: ['y.ts'] }),
    ]);
    expect(attention).toEqual([
      { kind: 'file-conflict', file: 'x.ts', subtaskIds: ['st1', 'st2'] },
      { kind: 'file-conflict', file: 'y.ts', subtaskIds: ['st2', 'st3'] },
    ]);
  });

  it('под-задачи без исполнителя — пункты no-executor в порядке массива', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1' }),
      subtask({ id: 'st2' }),
      subtask({ id: 'st3' }),
    ]);
    expect(attention).toEqual([
      { kind: 'no-executor', subtaskId: 'st2' },
      { kind: 'no-executor', subtaskId: 'st3' },
    ]);
  });

  it('смешанный случай: конфликты файлов идут раньше под-задач без исполнителя', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', files: ['a.ts'] }),      // и конфликт, и без исполнителя
      subtask({ id: 'st2', files: ['a.ts'], executorPersonaId: 'p1' }),
    ]);
    expect(attention).toEqual([
      { kind: 'file-conflict', file: 'a.ts', subtaskIds: ['st1', 'st2'] },
      { kind: 'no-executor', subtaskId: 'st1' },
    ]);
  });

  it('всем назначены исполнители и файлы не пересекаются — блок пуст', () => {
    const attention = attentionOf([
      subtask({ id: 'st1', executorPersonaId: 'p1', files: ['a.ts'] }),
      subtask({ id: 'st2', executorPersonaId: 'p2', files: ['b.ts'] }),
    ]);
    expect(attention).toEqual([]);
  });

  it('детерминированность: тот же вход — тот же выход', () => {
    const input = [
      subtask({ id: 'st1', files: ['a.ts'] }),
      subtask({ id: 'st2', files: ['a.ts'] }),
      subtask({ id: 'st3' }),
    ];
    expect(attentionOf(input)).toEqual(attentionOf([...input]));
  });
});

describe('buildTeamScheme', () => {
  it('сборка согласована с отдельными функциями', () => {
    const subtasks = [
      subtask({ id: 'st1', wave: 1, executorPersonaId: 'p1', files: ['a.ts'] }),
      subtask({ id: 'st2', wave: 2, files: ['a.ts', 'b.ts'] }),
    ];
    const scheme = buildTeamScheme(subtasks);
    expect(scheme.waves).toEqual(groupByWave(subtasks));
    expect(scheme.counts).toEqual(countsOf(subtasks));
    expect(scheme.attention).toEqual(attentionOf(subtasks));
    expect(scheme.waves).toHaveLength(scheme.counts.waves);
  });

  it('пустой план — пустая схема без падения', () => {
    const scheme = buildTeamScheme([]);
    expect(scheme.waves).toEqual([]);
    expect(scheme.attention).toEqual([]);
    expect(scheme.counts.subtasks).toBe(0);
  });
});

describe('countNumbers', () => {
  it('пустой план — ряд не строится', () => {
    expect(countNumbers(countsOf([]))).toEqual([]);
  });

  it('типичный план: под-задачи, волны, исполнители, файлы; нулевой unassigned не показываем', () => {
    const subtasks = [
      subtask({ wave: 1, executorPersonaId: 'p1', files: ['a.ts'] }),
      subtask({ wave: 1, executorPersonaId: 'p2', files: ['b.ts'] }),
      subtask({ wave: 2, executorPersonaId: 'p1', files: ['c.ts'] }),
      subtask({ wave: 2, executorPersonaId: 'p3', files: ['d.ts'] }),
      subtask({ wave: 2, executorPersonaId: 'p2', files: ['e.ts'] }),
    ];
    const numbers = countNumbers(countsOf(subtasks));
    expect(numbers).toEqual([
      { value: '5', label: 'под-задач' },
      { value: '2', label: 'волны' },
      { value: '3', label: 'исполнителя' },
      { value: '5', label: 'файлов в работе' },
    ]);
  });

  it('единственное число склоняется правильно (1/21)', () => {
    const counts = countsOf([subtask({ executorPersonaId: 'p1', files: ['a.ts'] })]);
    expect(countNumbers(counts)).toEqual([
      { value: '1', label: 'под-задача' },
      { value: '1', label: 'волна' },
      { value: '1', label: 'исполнитель' },
      { value: '1', label: 'файл в работе' },
    ]);
  });

  it('под-задачи без исполнителя показываются пилюлей, исполнители при нуле — нет', () => {
    const counts = countsOf([
      subtask(),
      subtask(),
    ]);
    const numbers = countNumbers(counts);
    expect(numbers).toContainEqual({ value: '2', label: 'под-задачи без исполнителя' });
    expect(numbers.some(n => n.label.startsWith('исполнитель'))).toBe(false);
  });

  it('склонение на границах: 11 — «под-задач», 21 — «под-задача», 5 — без исполнителя', () => {
    const eleven = countsOf(Array.from({ length: 11 }, (_, i) => subtask({ id: `st${i}` })));
    expect(countNumbers(eleven)).toContainEqual({ value: '11', label: 'под-задач' });
    expect(countNumbers(eleven)).toContainEqual({ value: '11', label: 'под-задач без исполнителя' });

    const twentyOne = countsOf(Array.from({ length: 21 }, (_, i) => subtask({ id: `st${i}` })));
    expect(countNumbers(twentyOne)).toContainEqual({ value: '21', label: 'под-задача' });
    expect(countNumbers(twentyOne)).toContainEqual({ value: '21', label: 'под-задача без исполнителя' });
  });
});
