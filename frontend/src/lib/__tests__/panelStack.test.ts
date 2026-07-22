// Тесты чистых функций состояния правых панелей (workspace-cc-panels):
// раскладка по колонкам, миграция со старого плоского списка, валидация
// мусора из localStorage и нормализация весов.
import { describe, it, expect } from 'vitest';
import {
  sanitizeLayout, parseLayout, addPanel, removePanel, movePanel, movePanelToNewColumn, movePanelAt,
  parseWeights, parseWidth, normalizeWeights,
  COL_DEFAULT, COL_MIN, COL_MAX,
} from '../../pages/workspace/panelStackState';

describe('sanitizeLayout', () => {
  it('мусор даёт пустую раскладку', () => {
    expect(sanitizeLayout(null)).toEqual([]);
    expect(sanitizeLayout('строка')).toEqual([]);
    expect(sanitizeLayout([1, 'x'])).toEqual([]);
  });

  it('фильтрует неизвестные ключи, дубли и пустые колонки', () => {
    expect(sanitizeLayout([['plan', 'мусор'], [], ['files', 'plan']])).toEqual([['plan'], ['files']]);
  });
});

describe('parseLayout', () => {
  it('без сохранённого — пусто', () => {
    expect(parseLayout(null, null)).toEqual([]);
  });

  it('читает явную раскладку колонок (в т.ч. несимметричную 1+2)', () => {
    expect(parseLayout('[["plan"],["files","tasks"]]', null)).toEqual([['plan'], ['files', 'tasks']]);
  });

  it('мигрирует старый плоский список по две на колонку', () => {
    expect(parseLayout(null, '["files","tasks","team"]')).toEqual([['files', 'tasks'], ['team']]);
  });

  it('битый layout при живом legacy — миграция', () => {
    expect(parseLayout('оборванный{', '["plan"]')).toEqual([['plan']]);
  });
});

describe('addPanel — дефолтная расстановка', () => {
  it('1-я во всю высоту, 2-я вниз, 3-я вправо, 4-я вниз третьей', () => {
    let l = addPanel([], 'plan');
    expect(l).toEqual([['plan']]);
    l = addPanel(l, 'files');
    expect(l).toEqual([['plan', 'files']]);
    l = addPanel(l, 'tasks');
    expect(l).toEqual([['plan', 'files'], ['tasks']]);
    l = addPanel(l, 'team');
    expect(l).toEqual([['plan', 'files'], ['tasks', 'team']]);
  });

  it('уже открытая панель не дублируется', () => {
    expect(addPanel([['plan']], 'plan')).toEqual([['plan']]);
  });
});

describe('removePanel', () => {
  it('удаляет панель и схлопывает опустевшую колонку', () => {
    expect(removePanel([['plan', 'files'], ['tasks']], 'tasks')).toEqual([['plan', 'files']]);
    expect(removePanel([['plan'], ['files']], 'plan')).toEqual([['files']]);
  });
});

describe('movePanel — drag-and-drop', () => {
  it('переносит в колонку цели, вставляя перед ней', () => {
    // [plan, files] [tasks] → тащим files на tasks → [plan] [files, tasks]
    expect(movePanel([['plan', 'files'], ['tasks']], 'files', 'tasks'))
      .toEqual([['plan'], ['files', 'tasks']]);
  });

  it('даёт несимметричную раскладку: одна панель слева, две справа', () => {
    // [plan, files] [tasks, team] → тащим files на team → [plan] [tasks, files, team]
    expect(movePanel([['plan', 'files'], ['tasks', 'team']], 'files', 'team'))
      .toEqual([['plan'], ['tasks', 'files', 'team']]);
  });

  it('перенос внутри колонки меняет порядок', () => {
    expect(movePanel([['plan', 'files']], 'files', 'plan')).toEqual([['files', 'plan']]);
  });

  it('неизвестные ключи и from===to — без изменений', () => {
    const l = [['plan'], ['files']] as Parameters<typeof movePanel>[0];
    expect(movePanel(l, 'plan', 'plan')).toEqual(l);
    expect(movePanel(l, 'tasks', 'plan')).toEqual(l);
  });
});

describe('movePanelToNewColumn — дроп в разделитель', () => {
  it('выносит панель в новую колонку на позицию разделителя', () => {
    // [plan, files] [tasks] → files в разделитель перед первой колонкой
    expect(movePanelToNewColumn([['plan', 'files'], ['tasks']], 'files', 0))
      .toEqual([['files'], ['plan'], ['tasks']]);
    // → files в разделитель между колонками
    expect(movePanelToNewColumn([['plan', 'files'], ['tasks']], 'files', 1))
      .toEqual([['plan'], ['files'], ['tasks']]);
    // → files правее последней
    expect(movePanelToNewColumn([['plan', 'files'], ['tasks']], 'files', 2))
      .toEqual([['plan'], ['tasks'], ['files']]);
  });

  it('единственная панель колонки: опустевшая колонка схлопывается без сдвига цели', () => {
    // [plan] [files] → plan правее последней → [files] [plan]
    expect(movePanelToNewColumn([['plan'], ['files']], 'plan', 2))
      .toEqual([['files'], ['plan']]);
  });

  it('индекс клампится, неизвестный ключ — без изменений', () => {
    expect(movePanelToNewColumn([['plan']], 'plan', 99)).toEqual([['plan']]);
    const l = [['plan']] as Parameters<typeof movePanelToNewColumn>[0];
    expect(movePanelToNewColumn(l, 'files', 0)).toEqual(l);
  });
});

describe('movePanelAt — горизонтальный плейсхолдер', () => {
  it('вставляет над первой панелью и под последней панелью колонки', () => {
    // [plan, files] [tasks] → tasks над plan → [tasks, plan, files]
    expect(movePanelAt([['plan', 'files'], ['tasks']], 'tasks', 0, 0))
      .toEqual([['tasks', 'plan', 'files']]);
    // → tasks под files → [plan, files, tasks]
    expect(movePanelAt([['plan', 'files'], ['tasks']], 'tasks', 0, 2))
      .toEqual([['plan', 'files', 'tasks']]);
  });

  it('перенос внутри колонки вниз учитывает сдвиг после удаления', () => {
    // [plan, files, tasks] → plan в позицию под files (rowIdx=2 до удаления)
    expect(movePanelAt([['plan', 'files', 'tasks']], 'plan', 0, 2))
      .toEqual([['files', 'plan', 'tasks']]);
  });

  it('невалидная колонка или неизвестный ключ — без изменений', () => {
    const l = [['plan']] as Parameters<typeof movePanelAt>[0];
    expect(movePanelAt(l, 'plan', 5, 0)).toEqual(l);
    expect(movePanelAt(l, 'files', 0, 0)).toEqual(l);
  });
});

describe('parseWeights', () => {
  it('null и мусор дают пустой объект', () => {
    expect(parseWeights(null)).toEqual({});
    expect(parseWeights('оборванный{')).toEqual({});
    expect(parseWeights('[1,2]')).toEqual({});
  });

  it('отбрасывает неизвестные ключи, NaN/Infinity и вырожденно малые веса', () => {
    const raw = JSON.stringify({ plan: 1.5, tasks: 0.01, files: 'x', team: null, мусор: 2 });
    expect(parseWeights(raw)).toEqual({ plan: 1.5 });
  });
});

describe('parseWidth', () => {
  it('мусор → дефолт, значения клампятся в COL_MIN..COL_MAX', () => {
    expect(parseWidth(null)).toBe(COL_DEFAULT);
    expect(parseWidth('abc')).toBe(COL_DEFAULT);
    expect(parseWidth('100')).toBe(COL_MIN);
    expect(parseWidth('9000')).toBe(COL_MAX);
    expect(parseWidth('400')).toBe(400);
  });
});

describe('normalizeWeights', () => {
  it('сумма весов открытых панелей = числу открытых', () => {
    const w = normalizeWeights(['plan', 'files'], { plan: 4, files: 4 });
    expect(w.plan).toBeCloseTo(1);
    expect(w.files).toBeCloseTo(1);
  });

  it('пропорции сохраняются, панели без веса получают 1 до нормировки', () => {
    const w = normalizeWeights(['plan', 'files'], { plan: 3 });
    // до нормировки: plan=3, files=1 → сумма 4, фактор 2/4
    expect(w.plan).toBeCloseTo(1.5);
    expect(w.files).toBeCloseTo(0.5);
  });

  it('веса закрытых панелей не трогает (вернутся с прежней долей)', () => {
    const w = normalizeWeights(['plan'], { plan: 2, terminal: 0.7 });
    expect(w.terminal).toBeCloseTo(0.7);
    expect(w.plan).toBeCloseTo(1);
  });
});
