// Тест «грязной» части карточки роли (срез «Кто работает по этой роли»,
// план командной реализации, этап 4): гейт «слой → что рисуется» и формат
// строки персоны как чистые функции из specialRules/model.ts.
//
// Контракт (см. model.ts):
//   • owner     → рисуется срез: список персон владельца с резолвом по уровням
//   • global    → строка-объяснение T8 (ROLE_SLICE_EXPLANATION) — список персон
//                 показан только в личном слое, иначе это были бы «чужие персоны»
//   • user      → та же строка-объяснение
//   • строка персоны: «Денис — Sonnet 5, сильная · в чате»
//       имя — из buildRolePersonaLine(persona).name
//       модель — modelsByTier[tier]
//       уровень — TIER_TITLE[tier] («Сильная»)
//       место — PERSONA_WORKPLACE_LABEL («в чате»)
//   • manual = true, если на любом уровне source ∈ {'persona-model','persona-cell'}
//     (правило специальности НЕ применяется — модель задана вручную)

import { describe, it, expect } from 'vitest';
import {
  ROLE_SLICE_EXPLANATION,
  PERSONA_WORKPLACE_LABEL,
  PERSONA_MANUAL_NOTE,
  buildRolePersonaLine,
  getRoleSliceKind,
  sortRolePersonaLines,
  type RolePersonaLine,
} from '../model';
import { TIER_TITLE } from '../../../../lib/modelTiers';
import type { TierKey } from '../../../../lib/modelProvidersShared';

describe('getRoleSliceKind — гейт «слой → что рисуется»', () => {
  it('owner → «owners» (рисуется срез персон владельца)', () => {
    expect(getRoleSliceKind('owner')).toBe('owners');
  });

  it('global → «explanation» (строка-объяснение, чужой список персон неуместен)', () => {
    expect(getRoleSliceKind('global')).toBe('explanation');
  });

  it('user → «explanation» (аналогично: список персон был бы про чужих людей)', () => {
    expect(getRoleSliceKind('user')).toBe('explanation');
  });
});

describe('ROLE_SLICE_EXPLANATION — текст для не-owner слоёв', () => {
  it('содержит причину, по которой срез не рисуется на global/user', () => {
    // Защита от случайного «обрубания» текста до пустой строки или замены
    // смысла — пустой текст на месте среза выглядел бы как сломанный UI.
    expect(ROLE_SLICE_EXPLANATION.length).toBeGreaterThan(20);
    expect(ROLE_SLICE_EXPLANATION).toMatch(/другого пользователя|для всех|общий/i);
  });
});

describe('PERSONA_WORKPLACE_LABEL / PERSONA_MANUAL_NOTE — слова для UI', () => {
  it('PERSONA_WORKPLACE_LABEL = «в чате» (T4)', () => {
    expect(PERSONA_WORKPLACE_LABEL).toBe('в чате');
  });

  it('PERSONA_MANUAL_NOTE упоминает ручную модель (T5)', () => {
    expect(PERSONA_MANUAL_NOTE.toLowerCase()).toContain('вручн');
  });
});

describe('buildRolePersonaLine — формат строки персоны для среза', () => {
  // Сборка строки в UI делается по компонентам, которые отдаёт
  // buildRolePersonaLine: name + modelsByTier[tier] + TIER_TITLE[tier] +
  // PERSONA_WORKPLACE_LABEL. Тест проверяет, что КОМПОНЕНТЫ доступны и
  // корректны — итоговая строка «Денис — Sonnet 5, сильная · в чате»
  // собирается в рендере по этим кусочкам.

  const persona = { id: 'p1', name: 'Денис' };

  it('для каждого уровня с резолвом — модель попадает в modelsByTier', () => {
    const line = buildRolePersonaLine(persona, {
      modelByTier: { strong: 'Sonnet 5', medium: null, weak: null },
      sourceByTier: { strong: 'specialty-cell' },
      presetNameByTier: { strong: null, medium: null, weak: null },
    });
    expect(line.name).toBe('Денис');
    expect(line.modelsByTier.strong).toBe('Sonnet 5');
    expect(line.modelsByTier.medium).toBeUndefined();
  });

  it('компоненты для сборки строки доступны (name, model, tier title, workplace)', () => {
    // Полная цепочка сборки «Денис — Sonnet 5, сильная · в чате»:
    const line = buildRolePersonaLine(persona, {
      modelByTier: { strong: 'Sonnet 5' },
      sourceByTier: { strong: 'specialty-cell' },
      presetNameByTier: {},
    });
    const model = line.modelsByTier.strong;            // 'Sonnet 5'
    const tierTitle = TIER_TITLE['strong'];             // «Сильная»
    const workplace = PERSONA_WORKPLACE_LABEL;          // «в чате»

    expect(model).toBe('Sonnet 5');
    expect(tierTitle).toBeTruthy();
    expect(workplace).toBe('в чате');

    // Сборка как в UI: «{name} — {model}, {tierTitle, нижний регистр} · {workplace}»
    // (заголовок уровня приводим к нижнему регистру по правилу UI).
    const composed = `${line.name} — ${model}, ${tierTitle.toLowerCase()} · ${workplace}`;
    expect(composed).toBe('Денис — Sonnet 5, сильная · в чате');
  });

  it('manual=true, если хоть один уровень имеет source «persona-model» или «persona-cell»', () => {
    // T5: модель задана вручную — правило специальности НЕ применяется.
    // Список источников MANUAL_MODEL_SOURCES приватный, поведение проверяем
    // через явные кейсы — иначе регрессия (забыли добавить новый источник
    // в MANUAL_MODEL_SOURCES) останется незамеченной.
    const cases: Array<{ source: string; expectManual: boolean }> = [
      { source: 'persona-model', expectManual: true },
      { source: 'persona-cell', expectManual: true },
      { source: 'specialty-cell', expectManual: false },
      { source: 'owner-slot', expectManual: false },
      { source: 'instance-slot', expectManual: false },
      { source: 'place-assignment', expectManual: false },
      { source: 'explicit', expectManual: false },
      // Пустой/мусорный source — тоже не manual.
      { source: '', expectManual: false },
      { source: 'unknown-source', expectManual: false },
    ];
    for (const { source, expectManual } of cases) {
      const line = buildRolePersonaLine(persona, {
        modelByTier: { strong: 'Sonnet 5' },
        sourceByTier: { strong: source },
        presetNameByTier: {},
      });
      expect(line.manual, `source=${source}`).toBe(expectManual);
    }
  });

  it('manual=true, если источник «persona-model» на СЛАБОМ уровне (не только на сильном)', () => {
    // Гейт «хотя бы один уровень» — даже если сильная модель пришла из слота,
    // а слабая задана руками, правило роли всё равно не применяется: ручная
    // модель на любом уровне «отменяет» наследование для этой персоны.
    const line = buildRolePersonaLine(persona, {
      modelByTier: { strong: 'Opus 5', weak: 'Haiku 4' },
      sourceByTier: { strong: 'owner-slot', weak: 'persona-cell' },
      presetNameByTier: {},
    });
    expect(line.manual).toBe(true);
    expect(line.modelsByTier.weak).toBe('Haiku 4');
  });

  it('fallbackLine — первый preset:{name} с самого «громкого» уровня', () => {
    // T10: берём с самого «громкого» уровня, где задано имя пресета
    // (от сильной к слабой). Тест проверяет приоритет strong → medium → weak.
    const line = buildRolePersonaLine(persona, {
      modelByTier: {},
      sourceByTier: {},
      presetNameByTier: {
        weak: 'Слабая цепочка',
        medium: 'Средняя цепочка',
        strong: 'Сильная цепочка',
      },
    });
    expect(line.fallbackLine).toBe('Фолбэк: цепочка «Сильная цепочка»');
  });

  it('fallbackLine: если strong пуст, берётся medium', () => {
    const line = buildRolePersonaLine(persona, {
      modelByTier: {},
      sourceByTier: {},
      presetNameByTier: {
        weak: 'Слабая цепочка',
        medium: 'Средняя цепочка',
        strong: null,
      },
    });
    expect(line.fallbackLine).toBe('Фолбэк: цепочка «Средняя цепочка»');
  });

  it('fallbackLine: null, если ни на одном уровне нет имени пресета', () => {
    const line = buildRolePersonaLine(persona, {
      modelByTier: { strong: 'Sonnet 5' },
      sourceByTier: { strong: 'specialty-cell' },
      presetNameByTier: {},
    });
    expect(line.fallbackLine).toBeNull();
  });

  it('id и name проходят как есть (identity-чек для UI-рендера)', () => {
    const line = buildRolePersonaLine(persona, {
      modelByTier: {},
      sourceByTier: {},
      presetNameByTier: {},
    });
    expect(line.id).toBe('p1');
    expect(line.name).toBe('Денис');
  });

  it('modelsByTier исключает пустые модели (falsy filter)', () => {
    // Тонкий момент: модель может быть пустой строкой ('') или undefined/null.
    // UI использует modelsByTier как «чип модели на этом уровне», и пустой
    // чип рендерить не надо.
    const line = buildRolePersonaLine(persona, {
      modelByTier: { strong: '', medium: null, weak: 'Haiku 4' },
      sourceByTier: { strong: 'specialty-cell', medium: 'specialty-cell', weak: 'specialty-cell' },
      presetNameByTier: {},
    });
    expect(line.modelsByTier.strong).toBeUndefined();
    expect(line.modelsByTier.medium).toBeUndefined();
    expect(line.modelsByTier.weak).toBe('Haiku 4');
  });
});

describe('sortRolePersonaLines — порядок строк персон', () => {
  // Сортировка стабильная: сначала с резолвом (есть хоть один чип), потом без,
  // внутри — по имени. Без сортировки порядок был бы «как пришли с сервера»,
  // а это может прыгать при обновлениях.

  function mkLine(over: Partial<RolePersonaLine> & { id: string; name: string }): RolePersonaLine {
    return {
      id: over.id,
      name: over.name,
      modelsByTier: over.modelsByTier ?? {},
      manual: over.manual ?? false,
      fallbackLine: over.fallbackLine ?? null,
    };
  }

  it('с резолвом идут раньше без, внутри — по имени (локале ru)', () => {
    // Внутри группы «с резолвом» имена 'Виктор' (В) и 'Денис' (Д) сортируются
    // по алфавиту: В идёт раньше Д. Аналогично «без резолва»: 'Анна' (А) раньше
    // 'Борис' (Б). Итого ['c', 'd', 'a', 'b'].
    const input: RolePersonaLine[] = [
      mkLine({ id: 'a', name: 'Анна' }),
      mkLine({ id: 'd', name: 'Денис', modelsByTier: { strong: 'Sonnet 5' } }),
      mkLine({ id: 'b', name: 'Борис' }),
      mkLine({ id: 'c', name: 'Виктор', modelsByTier: { weak: 'Haiku 4' } }),
    ];
    const sorted = sortRolePersonaLines(input);
    expect(sorted.map(l => l.id)).toEqual(['c', 'd', 'a', 'b']);
  });

  it('внутри групп — по имени (локале ru)', () => {
    const input: RolePersonaLine[] = [
      mkLine({ id: '1', name: 'Денис' }),
      mkLine({ id: '2', name: 'Анна' }),
      mkLine({ id: '3', name: 'Борис' }),
    ];
    expect(sortRolePersonaLines(input).map(l => l.id)).toEqual(['2', '3', '1']);
  });

  it('мутации исходного массива не происходит (иммутабельность)', () => {
    const input: RolePersonaLine[] = [
      mkLine({ id: 'b', name: 'Борис' }),
      mkLine({ id: 'a', name: 'Анна' }),
    ];
    const before = [...input];
    sortRolePersonaLines(input);
    expect(input).toEqual(before);
  });
});

// Подсказка будущим поколениям: список источников MANUAL_MODEL_SOURCES приватный.
// Если в нём добавят новый источник («persona-override», «persona-default»)
// — обновите кейсы в тесте «manual=true, если…», чтобы покрытие не отстало.
const _TIER_KEYS_FOR_TYPECHECK: readonly TierKey[] = ['strong', 'medium', 'weak'];
void _TIER_KEYS_FOR_TYPECHECK;