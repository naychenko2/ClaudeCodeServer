import { describe, expect, it } from 'vitest';
import {
  TIER_KEYS,
  type AccessGroup,
  type TierCell,
  type TierKey,
  accessMode,
  toInherited,
  toOwn,
  tierCellView,
} from './personaInheritModel';

// Данные — те же формы, что приносит сервер: персона «как у специальности» уже
// содержит подставленный Hydrate'ом шаблон в полях (A1a), шаблон специальности —
// AccessGroup из каталога (features/specialties/api.ts).

describe('accessMode — режим группы по флагу', () => {
  it('inheritsAccess=true → «как у специальности»', () => {
    expect(accessMode({ inheritsAccess: true })).toBe('inherited');
  });
  it('inheritsAccess=false → «своё»', () => {
    expect(accessMode({ inheritsAccess: false })).toBe('own');
  });
  // Старый бэк флага не шлёт; его поведение — «права — поля самой персоны».
  it('поля нет (старый бэк) → «своё»', () => {
    expect(accessMode({})).toBe('own');
  });
});

describe('toOwn — «своё» из текущих эффективных значений', () => {
  // Шаблон темы из каталога специальностей (SpecialtyCatalogEntry.template).
  const TOPIC: AccessGroup = { access: 'custom', tools: null, disallowedTools: ['WebSearch'] };

  it('копирует effective группы: access/tools/disallowedTools и флаг false', () => {
    const persona: AccessGroup = { access: 'readOnly', tools: ['tasks'], disallowedTools: null };
    expect(toOwn(persona, TOPIC)).toEqual({
      access: 'custom', tools: null, disallowedTools: ['WebSearch'], inheritsAccess: false,
    });
  });

  // Персона «как у специальности»: сервер уже подставил шаблон в её поля —
  // она сама и есть источник эффективных значений, отдельный шаблон не нужен.
  it('без отдельного шаблона копирует поля персоны (уже эффективные с сервера)', () => {
    const hydrated: AccessGroup = { access: 'readOnly', tools: ['tasks'], disallowedTools: null };
    expect(toOwn(hydrated)).toEqual({
      access: 'readOnly', tools: ['tasks'], disallowedTools: null, inheritsAccess: false,
    });
  });

  // Та же читка «отсутствующего», что у подстановки бэка: нет access → full,
  // отсутствие списков → без ограничений. Иначе «своё» записало бы null туда,
  // где человеку показан full.
  it('отсутствующие поля материализуются дефолтами бэка', () => {
    expect(toOwn({}, {})).toEqual({
      access: 'full', tools: null, disallowedTools: null, inheritsAccess: false,
    });
  });

  it('массивы — копии: запись не делит один массив с прочитанной персоной', () => {
    const source: AccessGroup = { access: 'custom', tools: ['tasks'], disallowedTools: ['WebSearch'] };
    const own = toOwn(source);
    own.tools!.push('Notes');
    own.disallowedTools!.push('Bash');
    expect(source.tools).toEqual(['tasks']);
    expect(source.disallowedTools).toEqual(['WebSearch']);
  });
});

describe('toInherited — возврат «как у специальности»', () => {
  it('фрагмент записи — только флаг true, override не кладётся', () => {
    expect(toInherited()).toEqual({ inheritsAccess: true });
  });

  // Пара обратных: то, что поставил toOwn, тоInherited снимает — режим читается
  // по фрагменту записи так, как его прочитает бэк на следующей выдаче.
  it('после возврата режим читается «как у специальности»', () => {
    const own = toOwn({ access: 'full' }, { access: 'readOnly', tools: null, disallowedTools: null });
    expect(accessMode(own)).toBe('own');
    expect(accessMode(toInherited())).toBe('inherited');
  });
});

describe('tierCellView — ячейка матрицы: своё / унаследованное', () => {
  const TOPIC_MATRIX: TierCell = {
    tierStrong: 'preset:topic-strong', tierMedium: 'model-m', tierWeak: 'local',
  };

  it('своя ячейка персоны — своё (inherited=false)', () => {
    expect(tierCellView({ tierStrong: 'my-model' }, TOPIC_MATRIX, 'tierStrong'))
      .toEqual({ value: 'my-model', inherited: false });
  });

  // Пустая ячейка — null и '' и отсутствие поля: все три форма — «своего нет».
  it('пустая ячейка показывает значение специальности как унаследованное', () => {
    expect(tierCellView({ tierStrong: null }, TOPIC_MATRIX, 'tierStrong'))
      .toEqual({ value: 'preset:topic-strong', inherited: true });
    expect(tierCellView({ tierMedium: '' }, TOPIC_MATRIX, 'tierMedium'))
      .toEqual({ value: 'model-m', inherited: true });
    expect(tierCellView({}, TOPIC_MATRIX, 'tierWeak'))
      .toEqual({ value: 'local', inherited: true });
  });

  it('у специальности значения нет — пусто и не «унаследованное»', () => {
    expect(tierCellView({ tierStrong: null }, null, 'tierStrong'))
      .toEqual({ value: null, inherited: false });
  });

  // Каждый уровень независим (как резолв бэка по узкой матрице): унаследованный
  // strong не делает остальные ячейки своими.
  it('каждый уровень резолвится независимо', () => {
    const expected: Record<TierKey, string> = {
      tierStrong: 'preset:topic-strong', tierMedium: 'model-m', tierWeak: 'local',
    };
    for (const tier of TIER_KEYS) {
      expect(tierCellView({}, TOPIC_MATRIX, tier)).toEqual({ value: expected[tier], inherited: true });
    }
  });
});
