// Тесты чистых функций состояния панелей воркспейса: раскладка по колонкам
// внутри зоны, перенос панелей МЕЖДУ зонами, миграция со старых раздельных
// ключей localStorage, валидация мусора и нормализация весов.
import { describe, it, expect } from 'vitest';
import {
  sanitizeLayout, parseLayout, addPanel, placeByRail, removePanel, swapPanels, movePanelToNewColumn, movePanelAt,
  parseWeights, parseWidth, normalizeWeights, parseColFlex, normalizeColFlex,
  sanitizeZones, emptyZones, zoneOf, openPanelIn, togglePanelIn, closePanel,
  swapAcross, moveAcrossAt, moveAcrossToNewColumn, isZoneCollapsed, migrateZones, revealPanel,
  enforceZoneInvariant, homeOf, trackHome, parseHome, closePanelTo, evictForeign, replacePanelWith,
  isTucked, tuckPanel, untuckPanel, parseKeyList, sortRail, reorderRail, mergeTuckDefaults,
  railSequence, COL_DEFAULT, COL_MIN, COL_MAX,
  type PanelZones,
} from '../../pages/workspace/panelStackState';
import { RAIL_GROUPS } from '../../pages/workspace/panelCatalog';

// Компактный конструктор пары зон: остальные поля берутся дефолтные
function zones(left: string[][], right: string[][]): PanelZones {
  return sanitizeZones({ left: { layout: left }, right: { layout: right } });
}

// Первая группа рельсы (содержимое проекта) целиком — reorderRail переставляет
// кнопку только внутри своей группы и ждёт её ПОЛНЫЙ состав
const PROJECT_GROUP = RAIL_GROUPS[0];

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

  it('левая зона: новая колонка рождается у рельсы (в начало), прежние отъезжают к центру', () => {
    let l = addPanel([], 'plan', 'left');
    expect(l).toEqual([['plan']]);
    l = addPanel(l, 'files', 'left');
    expect(l).toEqual([['plan', 'files']]);
    // третья не в конец (это был бы центр), а новой колонкой у рельсы — в начало
    l = addPanel(l, 'tasks', 'left');
    expect(l).toEqual([['tasks'], ['plan', 'files']]);
  });

});

describe('placeByRail — панель встаёт по порядку кнопок рельсы', () => {
  // Порядок кнопок столбца сверху вниз
  const seq = ['plan', 'files', 'tasks', 'team'] as const;
  const place = (layout: string[][], k: string, side: 'left' | 'right' = 'right', cap = 4) =>
    placeByRail(layout as never, k as never, side, cap, seq as never);

  it('встаёт в СЕРЕДИНУ колонки — между кнопками, что выше и ниже её в рельсе', () => {
    // plan(0) и tasks(2) открыты, открываем files(1) — его место между ними
    const r = place([['plan', 'tasks']], 'files');
    expect(r.layout).toEqual([['plan', 'files', 'tasks']]);
    expect(r).toMatchObject({ ci: 0, ri: 1, newColumn: false });
  });

  it('самая верхняя кнопка встаёт первой, самая нижняя — последней', () => {
    expect(place([['files', 'tasks']], 'plan').layout).toEqual([['plan', 'files', 'tasks']]);
    expect(place([['plan', 'files']], 'team').layout).toEqual([['plan', 'files', 'team']]);
  });

  it('колонка не отсортирована (перетащили руками) — место по числу панелей выше в рельсе', () => {
    // В колонке порядок team(3), plan(0). У files(1) выше по рельсе только plan —
    // значит место одно: после первой панели, а не «перед первой, что ниже».
    const r = place([['team', 'plan']], 'files');
    expect(r.layout).toEqual([['team', 'files', 'plan']]);
    expect(r.ri).toBe(1);
  });

  it('пустая зона — первая колонка', () => {
    expect(place([], 'files')).toMatchObject({ layout: [['files']], ci: 0, ri: 0, newColumn: true });
  });

  it('колонка полна — новая панель заводит колонку У РЕЛЬСЫ, прежние отъезжают к центру', () => {
    // cap 2, колонка у рельсы забита; открываем files — он НЕ втискивается между
    // plan и tasks, а встаёт своей колонкой у рельсы (у правой зоны — конец массива)
    const r = placeByRail([['plan', 'tasks']] as never, 'files' as never, 'right', 2, seq as never);
    expect(r.layout).toEqual([['plan', 'tasks'], ['files']]);
    expect(r).toMatchObject({ ci: 1, ri: 0, newColumn: true });
  });

  it('у левой зоны новая колонка зеркальна — тоже у рельсы, в начало', () => {
    const r = placeByRail([['plan', 'tasks']] as never, 'files' as never, 'left', 2, seq as never);
    expect(r.layout).toEqual([['files'], ['plan', 'tasks']]);
    expect(r).toMatchObject({ ci: 0, ri: 0, newColumn: true });
  });

  it('уже открытые панели не перетасовываются: полная колонка остаётся как была', () => {
    // Ни одна панель не покидает свою колонку — даже та, что ниже новой по рельсе
    const before = [['team'], ['plan', 'tasks']];
    const r = placeByRail(before as never, 'files' as never, 'right', 2, seq as never);
    expect(r.layout).toEqual([['team'], ['plan', 'tasks'], ['files']]);
    // исходная раскладка не тронута (чистая функция)
    expect(before).toEqual([['team'], ['plan', 'tasks']]);
  });

  it('по одной панели на колонку — каждая следующая заводит колонку у рельсы', () => {
    const r = placeByRail([['tasks'], ['plan']] as never, 'files' as never, 'right', 1, seq as never);
    expect(r.layout).toEqual([['tasks'], ['plan'], ['files']]);
    expect(r).toMatchObject({ ci: 2, ri: 0, newColumn: true });
  });

  it('панель уже открыта — addPanel не дублирует её даже с порядком кнопок', () => {
    expect(addPanel([['plan']] as never, 'plan' as never, 'right', 4, seq as never)).toEqual([['plan']]);
  });

  it('addPanel без порядка кнопок кладёт в конец колонки у рельсы', () => {
    // Без railSeq сравнивать ранги не с чем — панель просто дописывается
    expect(addPanel([['tasks']] as never, 'plan' as never)).toEqual([['tasks', 'plan']]);
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

describe('parseColFlex / normalizeColFlex — доли ширины колонок', () => {
  it('parseColFlex: мусор и неположительные заменяются на 1, не-массив пуст', () => {
    expect(parseColFlex([1.5, 'x', -2, 0.5])).toEqual([1.5, 1, 1, 0.5]);
    expect(parseColFlex(null)).toEqual([]);
    expect(parseColFlex({})).toEqual([]);
  });

  it('normalizeColFlex: одна колонка — пусто (делить нечего)', () => {
    expect(normalizeColFlex(1, [2])).toEqual([]);
    expect(normalizeColFlex(0, [])).toEqual([]);
  });

  it('normalizeColFlex: длину приводит к числу колонок, сумму — к нему же', () => {
    // две колонки, доли 1 и 3 → сумма 4, нормируем к 2: [0.5, 1.5]
    expect(normalizeColFlex(2, [1, 3])).toEqual([0.5, 1.5]);
    // недостающие добираются 1, лишние отрезаются
    expect(normalizeColFlex(2, [3])).toEqual([1.5, 0.5]);
    expect(normalizeColFlex(2, [1, 1, 5])).toEqual([1, 1]);
  });

  it('переживает сериализацию: sanitizeZones приводит colFlex к числу колонок', () => {
    const saved = { ...zones([['chats'], ['files']], []), left: { layout: [['chats'], ['files']], colFlex: [1, 3] } };
    const restored = sanitizeZones(JSON.parse(JSON.stringify(saved)));
    expect(restored.left.colFlex).toEqual([0.5, 1.5]);
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

describe('replacePanelWith — дроп кнопки из рельсы на открытую панель', () => {
  it('гость встаёт в слот хозяина, хозяин закрывается и оставляет кнопку в своей зоне', () => {
    const z = trackHome(replacePanelWith(zones([['chats']], [['files', 'tasks']]), 'changes', 'files'));
    expect(z.right.layout).toEqual([['changes', 'tasks']]);
    expect(zoneOf(z, 'files')).toBeNull();
    expect(homeOf(z, 'files')).toBe('right');
    // соседей замена не касается
    expect(z.left.layout).toEqual([['chats']]);
  });

  it('замещение в solo-зоне не плодит вторую панель', () => {
    const base = sanitizeZones({ left: { layout: [['chats']] }, right: { layout: [['files']], mode: 'solo' } });
    const z = replacePanelWith(base, 'tasks', 'files');
    expect(z.right.layout).toEqual([['tasks']]);
  });

  it('гость из соседней зоны уходит из неё (панель ровно в одной зоне)', () => {
    const z = replacePanelWith(zones([['chats']], [['files']]), 'chats', 'files');
    expect(z.left.layout).toEqual([]);
    expect(z.right.layout).toEqual([['chats']]);
  });

  it('закрытый хозяин и guest===host — без изменений', () => {
    const base = zones([['chats']], [['files']]);
    expect(replacePanelWith(base, 'tasks', 'graph')).toEqual(base);
    expect(replacePanelWith(base, 'files', 'files')).toEqual(base);
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

  it('закрытая панель открывается ровно там, куда её бросили', () => {
    // Иконку тащат из рельсы: панель не лежит ни в одной зоне, и дроп в
    // направляющую — это её открытие в выбранном месте, а не перенос
    const z = moveAcrossAt(zones([['chats']], [['files', 'tasks']]), 'changes', 'right', 0, 1);
    expect(z.right.layout).toEqual([['files', 'changes', 'tasks']]);
    expect(z.left.layout).toEqual([['chats']]);
  });

  it('дроп закрытой панели в разделитель открывает её новой колонкой', () => {
    const z = moveAcrossToNewColumn(zones([], [['files']]), 'tasks', 'right', 0);
    expect(z.right.layout).toEqual([['tasks'], ['files']]);
  });

  it('пустая зона принимает закрытую панель первой колонкой', () => {
    const z = moveAcrossAt(zones([['chats']], []), 'files', 'right', 0, 0);
    expect(z.right.layout).toEqual([['files']]);
  });

  it('зона в режиме одной панели меняет свою панель на гостя', () => {
    const base = sanitizeZones({ left: { layout: [['chats']] }, right: { layout: [['files']], mode: 'solo' } });
    const z = moveAcrossAt(base, 'chats', 'right', 0, 0);
    expect(z.right.layout).toEqual([['chats']]);
    expect(z.left.layout).toEqual([]);
  });

  it('дроп в разделитель solo-зоны тоже не плодит вторую панель', () => {
    const base = sanitizeZones({ left: { layout: [['chats']] }, right: { layout: [['files']], mode: 'solo' } });
    const z = moveAcrossToNewColumn(base, 'chats', 'right', 0);
    expect(z.right.layout).toEqual([['chats']]);
  });
});

describe('homeOf / trackHome — иконка закрытой панели', () => {
  it('до первого открытия берётся домашняя зона из реестра', () => {
    expect(homeOf(emptyZones(), 'tasks')).toBe('right');
    expect(homeOf(emptyZones(), 'chats')).toBe('left');
  });

  it('панель, переехавшая в другую зону, остаётся её иконкой после закрытия', () => {
    const moved = trackHome(moveAcrossAt(zones([['chats']], [['tasks']]), 'tasks', 'left', 0, 1));
    expect(homeOf(moved, 'tasks')).toBe('left');
    // закрытие привязку не сбрасывает — иконка ждёт там, где панель лежала
    const closed = trackHome(closePanel(moved, 'tasks'));
    expect(zoneOf(closed, 'tasks')).toBeNull();
    expect(homeOf(closed, 'tasks')).toBe('left');
  });

  it('спрятанные «свернуть все» панели тоже держат свою зону', () => {
    const z = trackHome(sanitizeZones({ left: { layout: [], stash: [['tasks']] }, right: { layout: [] } }));
    expect(homeOf(z, 'tasks')).toBe('left');
  });

  it('внешний запрос открывает панель там, где её закрыли', () => {
    const moved = trackHome(moveAcrossAt(zones([['chats']], [['changes']]), 'changes', 'left', 0, 1));
    const r = revealPanel(trackHome(closePanel(moved, 'changes')), 'changes');
    expect(r.wasOpen).toBe(false);
    expect(zoneOf(r.zones, 'changes')).toBe('left');
  });

  it('дроп на рельсу закрывает панель и кладёт её иконку в эту зону', () => {
    // «Задачи» открыты справа, бросили на ЛЕВУЮ рельсу: панель закрылась, а её
    // кнопка ждёт слева — там, куда бросили
    const z = closePanelTo(zones([['chats']], [['tasks']]), 'left', 'tasks');
    expect(zoneOf(z, 'tasks')).toBeNull();
    expect(homeOf(z, 'tasks')).toBe('left');
    expect(z.left.layout).toEqual([['chats']]);
  });

  it('дроп КНОПКИ на чужую рельсу переносит только иконку — панель остаётся закрытой', () => {
    // «Изменения» нигде не открыты, их кнопку перетащили с правой рельсы на левую:
    // открывать панель не нужно, меняется только сторона, где живёт кнопка
    const base = zones([['chats']], [['tasks']]);
    expect(homeOf(base, 'changes')).toBe('right');
    const z = closePanelTo(base, 'left', 'changes');
    expect(zoneOf(z, 'changes')).toBeNull();
    expect(homeOf(z, 'changes')).toBe('left');
    // раскладки обеих зон нетронуты
    expect(z.left.layout).toEqual([['chats']]);
    expect(z.right.layout).toEqual([['tasks']]);
  });

  it('дроп кнопки на чужую рельсу вынимает панель из свёрнутого набора', () => {
    // Левая зона свёрнута кнопкой «Свернуть все», «Оглавление» осталось в её stash.
    // Кнопку бросили на ПРАВУЮ рельсу: без вычистки stash завершающий trackHome
    // перечитывал бы его и возвращал дом обратно налево — снаружи это выглядело
    // как «кнопка не переезжает вовсе».
    const base = sanitizeZones({ left: { layout: [], stash: [['chats', 'toc']] }, right: { layout: [] } });
    const z = trackHome(closePanelTo(base, 'right', 'toc'));
    expect(homeOf(z, 'toc')).toBe('right');
    // разворачивание левой зоны вернёт только то, что в ней осталось
    expect(z.left.stash).toEqual([['chats']]);
  });

  it('возврат кнопки из ящика тоже вынимает панель из свёрнутого набора', () => {
    const base = sanitizeZones({
      left: { layout: [], stash: [['toc']] }, right: { layout: [] }, tucked: ['toc'],
    });
    const z = trackHome(untuckPanel(base, 'right', 'toc'));
    expect(isTucked(z, 'toc')).toBe(false);
    expect(homeOf(z, 'toc')).toBe('right');
    expect(z.left.stash).toEqual([]);
  });

  it('parseHome отбрасывает мусор и переводит упразднённые ключи', () => {
    expect(parseHome({ tasks: 'left', personas: 'right', files: 'up', junk: 'left' }))
      .toEqual({ tasks: 'left', team: 'right' });
    expect(parseHome(null)).toEqual({});
    expect(parseHome(['tasks'])).toEqual({});
  });

  it('переживает сериализацию состояния', () => {
    const saved = trackHome(moveAcrossAt(zones([['chats']], [['tasks']]), 'tasks', 'left', 0, 1));
    const restored = sanitizeZones(JSON.parse(JSON.stringify(saved)));
    expect(homeOf(restored, 'tasks')).toBe('left');
  });
});

describe('tuckPanel / untuckPanel — ящик рельсы («…»)', () => {
  it('прячет кнопку и ЗАКРЫВАЕТ панель, приписывая её к зоне ящика', () => {
    // «Задачи» открыты справа, их кнопку бросили в ящик ЛЕВОЙ рельсы
    const z = tuckPanel(zones([['chats']], [['tasks']]), 'left', 'tasks');
    expect(isTucked(z, 'tasks')).toBe(true);
    expect(zoneOf(z, 'tasks')).toBeNull();
    expect(homeOf(z, 'tasks')).toBe('left');
    expect(z.right.layout).toEqual([]);
  });

  it('повторное прятанье не плодит дубль в списке', () => {
    const once = tuckPanel(zones([], [['tasks']]), 'right', 'tasks');
    const twice = tuckPanel(once, 'right', 'tasks');
    expect(twice.tucked).toEqual(['tasks']);
  });

  it('возврат убирает кнопку из ящика и кладёт её в ту рельсу, куда бросили', () => {
    const tucked = tuckPanel(zones([['chats']], []), 'right', 'changes');
    const back = untuckPanel(tucked, 'left', 'changes');
    expect(isTucked(back, 'changes')).toBe(false);
    expect(homeOf(back, 'changes')).toBe('left');
    // возвращают КНОПКУ, а не панель — раскладка не трогается
    expect(zoneOf(back, 'changes')).toBeNull();
    expect(back.left.layout).toEqual([['chats']]);
  });

  it('parseKeyList отбрасывает мусор, дубли и переводит упразднённые ключи', () => {
    expect(parseKeyList(['tasks', 'personas', 'мусор', 'tasks', 7])).toEqual(['tasks', 'team']);
    expect(parseKeyList(null)).toEqual([]);
    expect(parseKeyList({ tasks: true })).toEqual([]);
  });

  it('переживает сериализацию состояния', () => {
    const saved = tuckPanel(zones([['chats']], [['tasks']]), 'right', 'tasks');
    const restored = sanitizeZones(JSON.parse(JSON.stringify(saved)));
    expect(isTucked(restored, 'tasks')).toBe(true);
    expect(homeOf(restored, 'tasks')).toBe('right');
  });
});

describe('mergeTuckDefaults — разовая укладка редких кнопок в ящик', () => {
  const WANT = ['graph', 'knowledge', 'skills', 'terminal', 'preview'] as const;

  it('первый запуск: все кнопки уезжают в ящик и отмечаются в applied', () => {
    const r = mergeTuckDefaults([], WANT, []);
    expect(r.changed).toBe(true);
    expect(r.tucked).toEqual([...WANT]);
    expect(r.applied).toEqual([...WANT]);
  });

  it('существующий пользователь: ящик пополняется, свои спрятанные кнопки целы', () => {
    const r = mergeTuckDefaults(['tasks'], WANT, []);
    expect(r.tucked).toEqual(['tasks', ...WANT]);
  });

  it('повторный запуск ничего не трогает', () => {
    const r = mergeTuckDefaults([...WANT], WANT, [...WANT]);
    expect(r.changed).toBe(false);
    expect(r.tucked).toEqual([...WANT]);
  });

  // Главная защита: человек достал кнопку из ящика — она обязана там и остаться.
  // Без applied миграция утаскивала бы её обратно на каждом запуске.
  it('кнопку, возвращённую в столбец, обратно в ящик не уводит', () => {
    const afterUntuck = ['graph', 'knowledge', 'skills', 'preview'] as const; // terminal достали
    const r = mergeTuckDefaults(afterUntuck, WANT, [...WANT]);
    expect(r.changed).toBe(false);
    expect(r.tucked).not.toContain('terminal');
  });

  // Набор defaultTucked со временем пополняется — новая кнопка обязана доехать до
  // тех, кто прошлую волну уже прошёл, не тронув разобранные ими старые
  it('новая кнопка в наборе доезжает до старожилов один раз', () => {
    const r = mergeTuckDefaults(['graph'], [...WANT, 'toc'], [...WANT]);
    expect(r.changed).toBe(true);
    expect(r.tucked).toEqual(['graph', 'toc']);
    expect(r.applied).toEqual([...WANT, 'toc']);
  });

  it('дубль в ящике не плодится', () => {
    const r = mergeTuckDefaults(['graph'], ['graph'], []);
    expect(r.tucked).toEqual(['graph']);
  });
});

describe('sortRail / reorderRail — порядок кнопок рельсы', () => {
  // Группа рельсы — набор ключей каталога; здесь берём её укороченный вид,
  // чтобы тесты не зависели от состава PROJECT_KEYS
  const GROUP = ['files', 'docs', 'changes', 'tasks'] as const;

  it('без сохранённого порядка группа идёт как есть (каталожная очерёдность)', () => {
    expect(sortRail([], GROUP)).toEqual(['files', 'docs', 'changes', 'tasks']);
  });

  it('ключи вне сохранённого порядка уходят в ХВОСТ, сохраняя каталожную очерёдность', () => {
    // Порядок задавали, когда «Документации» ещё не было: она встаёт последней,
    // а не туда, где стоит в реестре
    expect(sortRail(['tasks', 'files', 'changes'], GROUP)).toEqual(['tasks', 'files', 'changes', 'docs']);
  });

  it('перестановка материализует ВСЮ группу, а не один сдвинутый ключ', () => {
    const z = reorderRail(emptyZones(), GROUP, 'tasks', 'files');
    expect(z.railOrder).toEqual(['tasks', 'files', 'docs', 'changes']);
    expect(sortRail(z.railOrder, GROUP)).toEqual(['tasks', 'files', 'docs', 'changes']);
  });

  it('before=null отправляет кнопку в конец группы', () => {
    const z = reorderRail(emptyZones(), GROUP, 'files', null);
    expect(sortRail(z.railOrder, GROUP)).toEqual(['docs', 'changes', 'tasks', 'files']);
  });

  it('порядок, дающий ту же очерёдность, состояние не меняет', () => {
    const base = emptyZones();
    // «Файлы» и так стоят перед «Документацией» — писать нечего
    expect(reorderRail(base, GROUP, 'files', 'docs')).toBe(base);
    // Ключ не из этой группы и дроп на себя — тоже
    expect(reorderRail(base, GROUP, 'plan', 'files')).toBe(base);
    expect(reorderRail(base, GROUP, 'files', 'files')).toBe(base);
  });

  it('перестановка в одной группе не трогает порядок другой', () => {
    const SESSION = ['plan', 'agents', 'context'] as const;
    const withSession = reorderRail(emptyZones(), SESSION, 'context', 'plan');
    const both = reorderRail(withSession, GROUP, 'tasks', 'files');
    expect(sortRail(both.railOrder, SESSION)).toEqual(['context', 'plan', 'agents']);
    expect(sortRail(both.railOrder, GROUP)).toEqual(['tasks', 'files', 'docs', 'changes']);
  });

  it('кнопка, закрытая в другой зоне или спрятанная в ящик, места в группе не теряет', () => {
    // Место задаётся СОСЕДОМ, а не индексом: «Изменения» лежат в ящике и в столбце
    // их не видно, но в порядке группы они остаются между «Документацией» и «Задачами»
    const base = sanitizeZones({ left: { layout: [] }, right: { layout: [] }, tucked: ['changes'] });
    const z = reorderRail(base, GROUP, 'tasks', 'docs');
    expect(sortRail(z.railOrder, GROUP)).toEqual(['files', 'tasks', 'docs', 'changes']);
  });

  it('переживает сериализацию состояния', () => {
    const saved = reorderRail(zones([['chats']], [['tasks']]), GROUP, 'tasks', 'files');
    const restored = sanitizeZones(JSON.parse(JSON.stringify(saved)));
    expect(sortRail(restored.railOrder, GROUP)).toEqual(['tasks', 'files', 'docs', 'changes']);
  });

  it('порядок и переезд кнопки на чужую рельсу живут независимо', () => {
    const moved = closePanelTo(reorderRail(zones([['chats']], [['tasks']]), GROUP, 'tasks', 'files'), 'left', 'tasks');
    expect(homeOf(moved, 'tasks')).toBe('left');
    expect(sortRail(moved.railOrder, GROUP)).toEqual(['tasks', 'files', 'docs', 'changes']);
  });
});

describe('evictForeign — ремонт раскладки под набор экрана', () => {
  const SESSION = ['plan', 'agents', 'context'] as const;

  it('возвращает панель домой из зоны, где её некому нарисовать', () => {
    // «Чаты» уехали в правую зону раздела, где доступны только панели сессии:
    // там они невидимы, а слева считаются «лежащими в соседней зоне»
    const broken = trackHome(zones([], [['chats', 'plan']]));
    const fixed = evictForeign(broken, 'right', SESSION);
    expect(fixed).not.toBeNull();
    expect(fixed!.right.layout).toEqual([['plan']]);
    expect(zoneOf(fixed!, 'chats')).toBeNull();
    expect(homeOf(fixed!, 'chats')).toBe('left');
  });

  it('чистит и спрятанный «свернуть все» набор', () => {
    const broken = sanitizeZones({ left: { layout: [] }, right: { layout: [], stash: [['chats']] } });
    const fixed = evictForeign(trackHome(broken), 'right', SESSION);
    expect(fixed!.right.stash).toEqual([]);
    expect(homeOf(fixed!, 'chats')).toBe('left');
  });

  it('нечего выселять — null, чтобы не будить подписчиков', () => {
    expect(evictForeign(zones([['chats']], [['plan']]), 'right', SESSION)).toBeNull();
    expect(evictForeign(zones([['chats']], []), 'right', SESSION)).toBeNull();
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

describe('enforceZoneInvariant — панель ровно в одном месте', () => {
  it('спрятанный набор не хранит панель, уехавшую в другую зону', () => {
    // Сценарий бага: свернули левую (chats ушёл в её stash), потом открыли chats
    // и перетащили направо. Разворачивание stash вернуло бы вторую копию.
    const z = enforceZoneInvariant(sanitizeZones({
      left: { layout: [], stash: [['chats']] },
      right: { layout: [['files', 'chats']] },
    }));
    expect(z.right.layout).toEqual([['files', 'chats']]);
    expect(z.left.stash).toEqual([]);
  });

  it('раскладка на экране сильнее спрятанного набора своей же зоны', () => {
    const z = enforceZoneInvariant(sanitizeZones({
      left: { layout: [['chats']], stash: [['chats', 'tasks']] },
      right: { layout: [] },
    }));
    expect(z.left.layout).toEqual([['chats']]);
    expect(z.left.stash).toEqual([['tasks']]);
  });

  it('дубль между раскладками зон остаётся справа', () => {
    const z = enforceZoneInvariant(sanitizeZones({
      left: { layout: [['chats', 'files']] },
      right: { layout: [['files']] },
    }));
    expect(z.right.layout).toEqual([['files']]);
    expect(z.left.layout).toEqual([['chats']]);
  });

  it('чистое состояние не меняется', () => {
    const z = sanitizeZones({ left: { layout: [['chats']] }, right: { layout: [['files']], stash: [['tasks']] } });
    expect(enforceZoneInvariant(z)).toEqual(z);
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

  // Ниже — ФОЛБЭК-путь (домашняя зона не смонтирована): он обязан размещать по тому
  // же правилу рельсы, что и клик по кнопке. Обычно показ исполняет сама зона —
  // registerOpener, живая вместимость колонки.
  it('встаёт на своё место в колонке по порядку кнопок, а не в конец', () => {
    // Порядок рельсы: chats, files, changes, tasks… — «Изменения» выше «Задач»,
    // значит встают ПЕРЕД ними. Прежнее правило пушило панель в конец колонки.
    const r = revealPanel(zones([], [['tasks']]), 'changes');
    expect(r.zones.right.layout).toEqual([['changes', 'tasks']]);
  });

  it('колонка полна — новая растёт У РЕЛЬСЫ, как и по клику', () => {
    // Правая зона, вместимость фолбэка COL_CAP=2: колонка забита, значит changes
    // заводит свою колонку у рельсы — конец массива. Прежние панели не двигаются.
    const r = revealPanel(zones([], [['files', 'tasks']]), 'changes');
    expect(r.zones.right.layout).toEqual([['files', 'tasks'], ['changes']]);
  });

  it('порядок кнопок пользователя учитывается', () => {
    // Переставим changes ПОСЛЕ tasks в столбце — показ обязан положить панель уже
    // под задачами: правило читает railOrder, а не порядок реестра
    const base = reorderRail(zones([], [['tasks']]), PROJECT_GROUP, 'changes', null);
    expect(revealPanel(base, 'changes').zones.right.layout).toEqual([['tasks', 'changes']]);
  });
});

describe('railSequence — канонический порядок кнопок для фолбэка', () => {
  it('группы идут подряд: проектные, инструменты, контекст', () => {
    const seq = railSequence([]);
    expect(seq.indexOf('files')).toBeLessThan(seq.indexOf('terminal'));
    expect(seq.indexOf('terminal')).toBeLessThan(seq.indexOf('plan'));
    expect(seq.indexOf('plan')).toBeLessThan(seq.indexOf('toc'));
  });

  it('внутри группы действует пользовательский порядок', () => {
    const z = reorderRail(emptyZones(), PROJECT_GROUP, 'tasks', 'files');
    const seq = railSequence(z.railOrder);
    expect(seq.indexOf('tasks')).toBeLessThan(seq.indexOf('files'));
    // граница групп при этом на месте: перестановка не выносит ключ из своей группы
    expect(seq.indexOf('tasks')).toBeLessThan(seq.indexOf('terminal'));
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
