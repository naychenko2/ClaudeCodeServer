import { describe, expect, it } from 'vitest';
import {
  allRoleRows, buildGroups, buildLevelBars, configuredRoleRows, countFilledFields,
  coverageOf, pickStartScope, totalFields, tripleOf, unruledRoleRows,
} from '../model';
import type {
  SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtySettingsResponse,
} from '../../../../types';

// Логика вкладки «Особые правила»: группировка ролей по совпадающим тройкам, картина
// по уровням и счётчики. Числа в тестах — с реального прода (14 ролей, 42 правила,
// 9 различных наборов): именно на них рисовался макет v4, и если группировка поедет,
// экран молча выродится либо в 14 одиночек, либо в одну ложную группу.

const role = (key: string, label: string): SpecialtyCatalogEntry =>
  ({ key, label, executorFamily: false, template: null }) as unknown as SpecialtyCatalogEntry;

const CATALOG: SpecialtyCatalogEntry[] = [
  role('none', 'Без специальности'),
  role('librarian', 'Библиотекарь'),
  role('mentor', 'Наставник'),
  role('secretary', 'Секретарь'),
  role('analyst', 'Аналитик'),
  role('designer', 'Дизайнер'),
];

const layer = (specialties: SpecialtySettingsLayer['specialties'],
  defaultSpecialty?: SpecialtySettingsLayer['defaultSpecialty']): SpecialtySettingsLayer =>
  ({ specialties, defaultSpecialty: defaultSpecialty ?? null, presets: [] });

const triple = (s: string, m: string, w: string) =>
  ({ access: 'full', tierStrong: s || null, tierMedium: m || null, tierWeak: w || null }) as
    SpecialtySettingsLayer['specialties'][string];

describe('роли и тройки', () => {
  it('служебная «нет специальности» в список ролей не попадает', () => {
    const rows = allRoleRows(CATALOG, layer({}));
    expect(rows.map(r => r.key)).toEqual(['librarian', 'mentor', 'secretary', 'analyst', 'designer']);
  });

  it('карточками рисуются только роли с хотя бы одним заданным полем', () => {
    const rows = allRoleRows(CATALOG, layer({ analyst: triple('', 'preset:m', '') }));
    expect(configuredRoleRows(rows).map(r => r.key)).toEqual(['analyst']);
  });

  it('пустая запись слоя даёт пустую тройку', () => {
    expect(tripleOf(undefined)).toEqual(['', '', '']);
    expect(tripleOf(triple('a', '', 'c'))).toEqual(['a', '', 'c']);
  });
});

describe('группы одинаковых наборов', () => {
  const rows = configuredRoleRows(allRoleRows(CATALOG, layer({
    librarian: triple('preset:glm', 'preset:eco', 'preset:txt'),
    mentor: triple('preset:glm', 'preset:eco', 'preset:txt'),
    secretary: triple('preset:glm', 'preset:eco', 'preset:txt'),
    analyst: triple('preset:main', 'preset:eco', 'preset:txt'),
    designer: triple('preset:dsg', 'preset:dsg', 'preset:txt'),
  })));

  it('роли с совпадающей тройкой собираются в одну группу, остальные — одиночки', () => {
    const { groups, singles } = buildGroups(rows);
    expect(groups).toHaveLength(1);
    expect(groups[0].roles.map(r => r.key)).toEqual(['librarian', 'mentor', 'secretary']);
    expect(singles.map(r => r.key)).toEqual(['analyst', 'designer']);
  });

  it('различие хотя бы в одном поле разводит роли по разным группам', () => {
    const { groups } = buildGroups(configuredRoleRows(allRoleRows(CATALOG, layer({
      librarian: triple('preset:glm', 'preset:eco', ''),
      mentor: triple('preset:glm', 'preset:eco', 'preset:txt'),
    }))));
    expect(groups).toHaveLength(0);
  });

  it('«выделить» уводит роль в одиночки, не трогая остальную группу', () => {
    const { groups, singles } = buildGroups(rows, new Set(['secretary']));
    expect(groups[0].roles.map(r => r.key)).toEqual(['librarian', 'mentor']);
    expect(singles.map(r => r.key)).toContain('secretary');
  });

  it('после «выделить» последней пары группа исчезает целиком', () => {
    const { groups, singles } = buildGroups(rows, new Set(['secretary', 'mentor']));
    expect(groups).toHaveLength(0);
    expect(singles).toHaveLength(5);
  });
});

describe('картина по уровням', () => {
  const rows = allRoleRows(CATALOG, layer({
    librarian: triple('preset:glm', 'preset:eco', 'preset:txt'),
    mentor: triple('preset:glm', 'preset:eco', 'preset:txt'),
    analyst: triple('preset:main', 'preset:eco', ''),
  }));

  it('сегменты идут по убыванию числа ролей', () => {
    const [strong] = buildLevelBars(rows);
    expect(strong.segments.map(s => [s.route, s.count])).toEqual([
      ['preset:glm', 2], ['preset:main', 1],
    ]);
  });

  it('знаменатель полосы — весь каталог, незаданное уходит в хвост «не задано»', () => {
    const [strong, , weak] = buildLevelBars(rows);
    expect(strong.total).toBe(5);
    expect(strong.unset).toBe(2);
    // У «Аналитика» слабое поле пусто — в хвосте три роли, а не две
    expect(weak.unset).toBe(3);
  });

  it('в сегменте перечислены роли — по ним идёт подсветка карточек', () => {
    const [strong] = buildLevelBars(rows);
    expect(strong.segments[0].roleKeys).toEqual(['librarian', 'mentor']);
  });
});

describe('счётчики и стартовый слой', () => {
  const filledLayer = layer(
    { librarian: triple('a', 'b', 'c'), analyst: triple('a', '', '') },
    triple('x', 'y', ''),
  );

  it('считаются именно ПОЛЯ, вместе с полями «Любой специальности»', () => {
    expect(countFilledFields(filledLayer, CATALOG)).toBe(3 + 1 + 2);
    expect(totalFields(CATALOG)).toBe((5 + 1) * 3);
  });

  it('бейдж показывает охват специальностями, а не число полей', () => {
    expect(coverageOf(filledLayer, CATALOG)).toEqual({ configured: 2, total: 5 });
  });

  it('пустой общий слой уводит админа на личный', () => {
    const settings = {
      version: 2, global: layer({}), owner: filledLayer, presets: [],
    } as unknown as SpecialtySettingsResponse;
    expect(pickStartScope(settings, CATALOG, true)).toBe('owner');
  });

  it('непустой общий слой оставляет админа на нём, не-админ всегда на личном', () => {
    const settings = {
      version: 2, global: filledLayer, owner: layer({}), presets: [],
    } as unknown as SpecialtySettingsResponse;
    expect(pickStartScope(settings, CATALOG, true)).toBe('global');
    expect(pickStartScope(settings, CATALOG, false)).toBe('owner');
  });
});

describe('роли без правил (этап 5)', () => {
  // Тот же слой, что в группах: librarian/mentor/secretary — конфигурированные,
  // analyst/designer — одиночки. На этом фоне проверяем, что пустые роли
  // (mentor вынесли в unruled через удаление правила) НЕ образуют «ложную группу» —
  // buildGroups склеил бы их по пустому ключу '', а unruledRoleRows отдаёт их
  // списком для отдельной секции.
  const rows = allRoleRows(CATALOG, layer({
    librarian: triple('preset:glm', 'preset:eco', 'preset:txt'),
    mentor: triple('', '', ''),         // без правила
    secretary: triple('preset:glm', 'preset:eco', 'preset:txt'),
    analyst: triple('', '', ''),         // без правила
    designer: triple('', '', ''),        // без правила
  }));

  it('возвращает только роли с пустой тройкой И хотя бы одной персоной', () => {
    const counts = new Map<string, number>([
      ['mentor', 3], ['analyst', 1], // designer без персон — не попадает
    ]);
    expect(unruledRoleRows(rows, counts).map(r => r.key)).toEqual(['mentor', 'analyst']);
  });

  it('роль без правил и без персон в список не попадает', () => {
    expect(unruledRoleRows(rows, new Map()).map(r => r.key)).toEqual([]);
  });

  it('пустые тройки не образуют группу — buildGroups не считает их одинаковыми ролями', () => {
    // Проверяем инвариант unruled: buildGroups слил бы mentor+analyst+designer в одну
    // «группу из пустых троек», а singles оказались бы пустыми. unruledRoleRows
    // разводит их по списку ролей без правил — одиночки/группы configuredRoleRows
    // остаются нетронутыми.
    const configured = configuredRoleRows(rows);
    const { groups, singles } = buildGroups(configured);
    expect(groups.map(g => g.roles.map(r => r.key))).toEqual([['librarian', 'secretary']]);
    expect(singles.map(r => r.key)).toEqual([]);
  });
});
