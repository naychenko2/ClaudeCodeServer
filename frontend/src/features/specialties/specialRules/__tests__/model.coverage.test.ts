// Сторож инварианта волны 5 «Персонализация специальностей»: заполнение Display
// (override имени/описания) НЕ должно превращать роль в «настроенную» ни в одной из
// метрик вкладки «Особые правила». Coverage и configuredRoleRows считают только
// факт наличия правил (ячеек матрицы по уровням); добавление Display — это просто
// переименование роли для отображения, оно не должно раздувать бейдж «14 из 14»
// на пустом слое и не должно заставлять карточку появиться в списке ролей с правилами.
//
// Регрессия была бы: реализация «настроенности» через hasAnyField, который бы
// считал и display — пустой слой с Display для всех 14 ролей показал бы «14 из 14»,
// и вкладка «Особые правила» молча выродилась бы в «всё настроено, идти некуда».
// Сторож стоит здесь, в чистой логике (model.ts), без зависимости от React.

import { describe, expect, it } from 'vitest';
import {
  allRoleRows, configuredRoleRows, coverageOf,
} from '../model';
import type {
  SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtyTemplateSettings,
} from '../../../../types';

const role = (key: string, label: string): SpecialtyCatalogEntry =>
  ({ key, label, executorFamily: false, template: null }) as unknown as SpecialtyCatalogEntry;

// 14 ролей каталога — на проде именно столько, проверка с реальным количеством
// защищает от регрессии «забыли один ключ → 13 из 14».
const CATALOG: SpecialtyCatalogEntry[] = [
  role('analyst', 'Аналитик'),
  role('planner', 'Планировщик'),
  role('reviewer', 'Ревьюер'),
  role('executor', 'Исполнитель (универсальный)'),
  role('secretary', 'Секретарь'),
  role('coordinator', 'Координатор'),
  role('mentor', 'Наставник'),
  role('designer', 'Дизайнер'),
  role('consultant', 'Консультант'),
  role('librarian', 'Библиотекарь'),
  role('tester', 'Тестировщик'),
  role('backendExecutor', 'Исполнитель (бэкенд)'),
  role('frontendExecutor', 'Исполнитель (фронтенд)'),
  role('devopsExecutor', 'Исполнитель (DevOps)'),
];

// Шаблон записи специальности в слое (минимум полей — у тестов нет доступа до
// TierStrong/TierMedium/TierWeak как не-null, обнуляем их явно).
const fullRecord = (): SpecialtyTemplateSettings => ({
  access: 'full', tierStrong: null, tierMedium: null, tierWeak: null,
  tools: null, disallowedTools: null,
});

// Запись Display для КАЖДОЙ роли каталога. Имитация сценария «владелец прошёл по
// всем 14 ролям и задал своё имя»: ничего правил не задано, но слой display
// заполнен.
type DisplayMap = Record<string, { name?: string | null; description?: string | null }>;

// Сборщик слоя, скрывающий display от TS excess-property-check: сначала собираем
// «легальный» слой, потом накладываем display через spread. Тип SpecialtySettingsLayer
// формально не объявляет display — это аугментация из lib/specialties.ts, и TS
// отказывается принимать литерал с display как инициализатор.
const layerWithDisplay = (specialties: SpecialtySettingsLayer['specialties'],
  display: DisplayMap, defaultSpecialty: SpecialtySettingsLayer['defaultSpecialty'] = null,
): SpecialtySettingsLayer => {
  const base: SpecialtySettingsLayer = {
    specialties, defaultSpecialty, presets: [],
  };
  return { ...base, display } as unknown as SpecialtySettingsLayer;
};

const allDisplayLayer = (): SpecialtySettingsLayer => {
  const display: DisplayMap = {};
  for (const e of CATALOG) {
    display[e.key] = { name: `Свой ${e.label}`, description: `Своё описание для ${e.label}` };
  }
  return layerWithDisplay({}, display);
};

describe('coverageOf и configuredRoleRows не реагируют на Display', () => {
  it('coverageOf для слоя «только display» — 0 из 14 (Display ≠ правило)', () => {
    const layer = allDisplayLayer();
    const cov = coverageOf(layer, CATALOG);
    expect(cov).toEqual({ configured: 0, total: 14 });
  });

  it('configuredRoleRows для слоя «только display» возвращает пустой массив', () => {
    const layer = allDisplayLayer();
    const rows = configuredRoleRows(allRoleRows(CATALOG, layer));
    expect(rows).toEqual([]);
  });

  it('coverageOf не увеличивается, если Display заполнен поверх реальных правил', () => {
    // Правило задано только у одной роли (analyst), display — у всех 14.
    // Метрика должна остаться «1 из 14», а не «14 из 14».
    const display: DisplayMap = Object.fromEntries(
      CATALOG.map(e => [e.key, { name: `Свой ${e.label}`, description: null }]),
    );
    const layer = layerWithDisplay(
      { analyst: { ...fullRecord(), tierStrong: 'preset:main' } },
      display,
    );
    expect(coverageOf(layer, CATALOG)).toEqual({ configured: 1, total: 14 });
  });

  it('configuredRoleRows не включает роли с одним только Display', () => {
    const display: DisplayMap = {
      // Дисплей без правил — НЕ конфигурированные
      mentor: { name: 'Свой Наставник', description: null },
      designer: { name: 'Свой Дизайнер', description: null },
    };
    const layer = layerWithDisplay(
      {
        analyst: { ...fullRecord(), tierMedium: 'preset:eco' },
        librarian: { ...fullRecord(), tierWeak: 'preset:txt' },
      },
      display,
    );
    const rows = configuredRoleRows(allRoleRows(CATALOG, layer));
    expect(rows.map(r => r.key).sort()).toEqual(['analyst', 'librarian']);
  });

  it('Display с пустой записью (name=null, description=null) не считается конфигурацией', () => {
    // Пустая display-запись (всё null) эквивалентна отсутствию — её быть не должно.
    const layer = layerWithDisplay({}, {
      analyst: { name: null, description: null },
      librarian: { name: '', description: '' },
    });
    expect(coverageOf(layer, CATALOG)).toEqual({ configured: 0, total: 14 });
    expect(configuredRoleRows(allRoleRows(CATALOG, layer))).toEqual([]);
  });

  it('coverageOf не меняется от Display, добавленного в defaultSpecialty-цепочку', () => {
    // defaultSpecialty = «Любая специальность», её дисплей тоже не конфигурация.
    const layer = layerWithDisplay(
      {},
      { any: { name: 'Своя Любая', description: null } },
      { ...fullRecord(), tierStrong: 'preset:main' },
    );
    // Покрытие ролей — 0 из 14, потому что defaultSpecialty это «any», а не роль.
    // (Сама defaultSpecialty учитывается в countFilledFields, но НЕ в coverageOf — это
    //  другая метрика, и её поведение зафиксировано отдельным тестом в model.test.ts.)
    expect(coverageOf(layer, CATALOG)).toEqual({ configured: 0, total: 14 });
  });
});