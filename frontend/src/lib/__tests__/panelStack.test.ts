// Тесты чистых функций состояния панелей воркспейса: раскладка по колонкам
// внутри зоны, перенос панелей МЕЖДУ зонами, миграция со старых раздельных
// ключей localStorage, валидация мусора и нормализация весов.
import { describe, it, expect } from 'vitest';
import {
  sanitizeLayout, parseLayout, addPanel, removePanel, swapPanels, movePanelToNewColumn, movePanelAt,
  parseWeights, parseWidth, normalizeWeights,
  sanitizeZones, emptyZones, zoneOf, openPanelIn, togglePanelIn, closePanel,
  swapAcross, moveAcrossAt, moveAcrossToNewColumn, isZoneCollapsed, migrateZones, revealPanel,
  COL_DEFAULT, COL_MIN, COL_MAX,
  type PanelZones,
} from '../../pages/workspace/panelStackState';

// Компактный конструктор пары зон: остальные поля берутся дефолтные
function zones(left: string[][], right: string[][]): PanelZones {
  return sanitizeZones({ left: { layout: left }, right: { layout: right } });
}

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

describe('swapPanels — дроп панели на панель', () => {
  it('меняет местами панели из разных колонок, раскладку не трогает', () => {
    // [plan, files] [tasks] → тащим files на tasks → [plan, tasks] [files]
    expect(swapPanels([['plan', 'files'], ['tasks']], 'files', 'tasks'))
      .toEqual([['plan', 'tasks'], ['files']]);
  });

  it('форма колонок сохраняется (2+2 остаётся 2+2)', () => {
    // [plan, files] [tasks, team] → тащим files на team → [plan, team] [tasks, files]
    expect(swapPanels([['plan', 'files'], ['tasks', 'team']], 'files', 'team'))
      .toEqual([['plan', 'team'], ['tasks', 'files']]);
  });

  it('обмен внутри одной колонки меняет порядок', () => {
    expect(swapPanels([['plan', 'files']], 'files', 'plan')).toEqual([['files', 'plan']]);
  });

  it('обмен симметричен (порядок аргументов не важен)', () => {
    const l = [['plan', 'files'], ['tasks']] as Parameters<typeof swapPanels>[0];
    expect(swapPanels(l, 'files', 'tasks')).toEqual(swapPanels(l, 'tasks', 'files'));
  });

  it('неизвестные ключи и a===b — без изменений', () => {
    const l = [['plan'], ['files']] as Parameters<typeof swapPanels>[0];
    expect(swapPanels(l, 'plan', 'plan')).toEqual(l);
    expect(swapPanels(l, 'tasks', 'plan')).toEqual(l);
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

describe('sanitizeZones — инвариант «панель в одной зоне»', () => {
  it('дубль между зонами остаётся справа (там настоящий контент)', () => {
    const z = zones([['chats', 'files']], [['files', 'tasks']]);
    expect(z.left.layout).toEqual([['chats']]);
    expect(z.right.layout).toEqual([['files', 'tasks']]);
  });

  it('мусор даёт пустые зоны с дефолтами', () => {
    const z = sanitizeZones('ерунда');
    expect(z).toEqual(emptyZones());
    expect(z.right.width).toBe(COL_DEFAULT);
    expect(z.right.mode).toBe('multi');
  });
});

describe('zoneOf / openPanelIn / togglePanelIn', () => {
  it('открытие панели в другой зоне — это перенос, а не копия', () => {
    const z = openPanelIn(zones([['chats']], [['files']]), 'left', 'files');
    expect(zoneOf(z, 'files')).toBe('left');
    expect(z.right.layout).toEqual([]);
    expect(z.left.layout).toEqual([['chats', 'files']]);
  });

  it('клик по иконке своей зоны закрывает, чужой — забирает к себе', () => {
    const base = zones([['chats']], [['files']]);
    expect(togglePanelIn(base, 'left', 'chats').left.layout).toEqual([]);
    expect(zoneOf(togglePanelIn(base, 'left', 'files'), 'files')).toBe('left');
  });

  it('в solo-зоне открытие вытесняет прежнюю панель', () => {
    const base = sanitizeZones({ left: { layout: [['chats']], mode: 'solo' }, right: { layout: [['files']] } });
    const z = openPanelIn(base, 'left', 'files');
    expect(z.left.layout).toEqual([['files']]);
    expect(z.right.layout).toEqual([]);
  });

  it('закрытие убирает панель из любой зоны', () => {
    expect(closePanel(zones([['chats']], [['files']]), 'files').right.layout).toEqual([]);
    expect(zoneOf(closePanel(zones([['chats']], []), 'chats'), 'chats')).toBeNull();
  });
});

describe('swapAcross — дроп панели на панель через границу зон', () => {
  it('панели меняются зонами, форма раскладок сохраняется', () => {
    const z = swapAcross(zones([['chats']], [['files', 'tasks']]), 'chats', 'tasks');
    expect(z.left.layout).toEqual([['tasks']]);
    expect(z.right.layout).toEqual([['files', 'chats']]);
  });

  it('внутри одной зоны работает как обычный swap', () => {
    const z = swapAcross(zones([], [['files', 'tasks']]), 'files', 'tasks');
    expect(z.right.layout).toEqual([['tasks', 'files']]);
  });

  it('закрытая панель и a===b — без изменений', () => {
    const base = zones([['chats']], [['files']]);
    expect(swapAcross(base, 'chats', 'graph')).toEqual(base);
    expect(swapAcross(base, 'chats', 'chats')).toEqual(base);
  });
});

describe('moveAcrossAt / moveAcrossToNewColumn — дроп в направляющие чужой зоны', () => {
  it('вставляет панель из другой зоны в указанную колонку и строку', () => {
    const z = moveAcrossAt(zones([['chats']], [['files', 'tasks']]), 'chats', 'right', 0, 1);
    expect(z.left.layout).toEqual([]);
    expect(z.right.layout).toEqual([['files', 'chats', 'tasks']]);
  });

  it('пустая зона принимает панель первой колонкой', () => {
    const z = moveAcrossAt(zones([], [['files']]), 'files', 'left', 0, 0);
    expect(z.left.layout).toEqual([['files']]);
    expect(z.right.layout).toEqual([]);
  });

  it('выносит панель из другой зоны в новую колонку на позицию разделителя', () => {
    const z = moveAcrossToNewColumn(zones([['chats']], [['files'], ['tasks']]), 'chats', 'right', 1);
    expect(z.right.layout).toEqual([['files'], ['chats'], ['tasks']]);
    expect(z.left.layout).toEqual([]);
  });

  it('внутри своей зоны остаётся обычной перестановкой', () => {
    const z = moveAcrossAt(zones([], [['files', 'tasks']]), 'tasks', 'right', 0, 0);
    expect(z.right.layout).toEqual([['tasks', 'files']]);
  });
});

describe('isZoneCollapsed', () => {
  it('свёрнута = своих панелей нет, но спрятанный набор есть', () => {
    const z = sanitizeZones({ left: { layout: [], stash: [['chats']] }, right: { layout: [['files']] } });
    expect(isZoneCollapsed(z.left)).toBe(true);
    expect(isZoneCollapsed(z.right)).toBe(false);
    expect(isZoneCollapsed(emptyZones().left)).toBe(false);
  });
});

describe('revealPanel — внешний запрос показать панель (git-бар)', () => {
  it('закрытая открывается в своей домашней зоне', () => {
    const r = revealPanel(emptyZones(), 'changes');
    expect(r.wasOpen).toBe(false);
    expect(zoneOf(r.zones, 'changes')).toBe('right');
    // «Чаты» дома слева — та же функция кладёт их в другую зону
    expect(zoneOf(revealPanel(emptyZones(), 'chats').zones, 'chats')).toBe('left');
  });

  it('уже открытую не двигает — ни в своей зоне, ни в чужой', () => {
    const дома = zones([], [['changes']]);
    expect(revealPanel(дома, 'changes')).toEqual({ zones: дома, wasOpen: true });
    // панель уехала в левую зону — её оставляют там, а не тащат обратно домой
    const переехала = zones([['changes']], []);
    expect(revealPanel(переехала, 'changes')).toEqual({ zones: переехала, wasOpen: true });
  });
});

describe('migrateZones — переезд со старых раздельных ключей', () => {
  const store = (data: Record<string, string>) => (k: string) => data[k] ?? null;

  it('переводит упразднённые ключи: personas → team, tools отбрасывается', () => {
    const z = migrateZones(store({
      'cc_ws_left_panels_layout': '[["chats","personas"],["tools"]]',
    }), 'ws');
    expect(z?.left.layout).toEqual([['chats', 'team']]);
  });

  it('дубль в обеих зонах остаётся справа', () => {
    const z = migrateZones(store({
      'cc_ws_panels_layout': '[["files","tasks"]]',
      'cc_ws_left_panels_layout': '[["chats","files"]]',
    }), 'ws');
    expect(z?.right.layout).toEqual([['files', 'tasks']]);
    expect(z?.left.layout).toEqual([['chats']]);
  });

  it('переносит режим, ширину и спрятанный набор каждой зоны отдельно', () => {
    const z = migrateZones(store({
      'cc_ws_panels_layout': '[["files"]]',
      'cc_ws_panels_width': '420',
      'cc_ws_panels_mode': 'solo',
      'cc_ws_left_panels_layout': '[]',
      'cc_ws_left_panels_width': '300',
      'cc_ws_left_panels_stash': '[["chats"]]',
    }), 'ws');
    expect(z?.right.width).toBe(420);
    expect(z?.right.mode).toBe('solo');
    expect(z?.left.width).toBe(300);
    expect(z?.left.stash).toEqual([['chats']]);
    expect(z?.left.mode).toBe('multi');
  });

  it('мигрирует совсем старый плоский список правой зоны', () => {
    const z = migrateZones(store({ 'cc_ws_panels_open': '["files","tasks","team"]' }), 'ws', 'cc_ws_panels_open');
    expect(z?.right.layout).toEqual([['files', 'tasks'], ['team']]);
  });

  it('без старых ключей возвращает null (значит применяется дефолт)', () => {
    expect(migrateZones(store({}), 'ws')).toBeNull();
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
