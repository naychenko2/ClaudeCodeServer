// Модель группировки вкладки «Умения» (A3b) — чистые кейсы: без DOM и запросов.
// Проверяет то, на чём держатся секции панели: разрез по inherited/своим off-записям
// по профилю специальности (тот же ключ сверки, что на бэке, PersonaBindingsService.cs:696),
// отнесение копий данных проекта и счётчик строк.

import { describe, expect, it } from 'vitest';
import type { PersonaBinding } from '../../types';
import {
  groupSkillBindings,
  isProjectCopyType,
  matchesSpecialtySkill,
  skillsRowCount,
  specialtySkillNames,
} from './personaBindingsModel';

const b = (over: Partial<PersonaBinding> & { type: PersonaBinding['type']; target: string }): PersonaBinding =>
  ({ id: 'x', path: null, condition: '', mode: 'auto', createdAt: '', updatedAt: '', ...over }) as PersonaBinding;

// Skill-профиль роли: три умения (нижний регистр — ключ сверки)
const PROFILE = [
  { type: 'skill' as const, mode: 'auto' as const, condition: 'проверка UI', skillName: 'Playwright-E2E' },
  { type: 'skill' as const, mode: 'auto' as const, condition: 'матрица', skillName: 'regression-matrix' },
  { type: 'skill' as const, mode: 'off' as const, condition: 'профилирование', skillName: 'perf-profiling' },
  // Не skill — в профиль умений не входит (это данные/инструменты, не навыки)
  { type: 'knowledge' as const, mode: 'auto' as const, condition: 'база', skillName: null },
];

describe('specialtySkillNames / matchesSpecialtySkill', () => {
  it('имена — только skill, нижний регистр; несущая строка пустого профиля — пусто', () => {
    expect(specialtySkillNames(PROFILE)).toEqual(new Set(['playwright-e2e', 'regression-matrix', 'perf-profiling']));
    expect(specialtySkillNames(null)).toEqual(new Set());
    expect(specialtySkillNames([])).toEqual(new Set());
  });

  it('сверка по имени без регистра; plugin:-цели не совпадают никогда (ADR-031)', () => {
    const names = specialtySkillNames(PROFILE);
    expect(matchesSpecialtySkill('Playwright-E2E', names)).toBe(true);
    expect(matchesSpecialtySkill('  playwright-e2e ', names)).toBe(true); // trim + нижний регистр
    expect(matchesSpecialtySkill('plugin:my-plugin/playwright-e2e', names)).toBe(false);
    expect(matchesSpecialtySkill('', names)).toBe(false);
  });
});

describe('groupSkillBindings', () => {
  it('inherited auto → «inherited»; inherited off → «выключено специальностью»', () => {
    const names = specialtySkillNames(PROFILE);
    const g = groupSkillBindings([
      b({ type: 'skill', target: 'playwright-e2e', inherited: true }),
      b({ type: 'skill', target: 'perf-profiling', mode: 'off', inherited: true }),
    ], names);
    expect(g.fromSpecialty.map(r => r.kind)).toEqual(['inherited', 'disabledBySpecialty']);
    expect(g.own).toEqual([]);
    expect(g.projectCopies).toEqual([]);
  });

  it('своя off-запись имени профиля → «отключено вами», в конце секции специальности', () => {
    const names = specialtySkillNames(PROFILE);
    const g = groupSkillBindings([
      b({ type: 'skill', target: 'playwright-e2e', inherited: true }),
      b({ id: 'own-off', type: 'skill', target: 'Web-Design-Guidelines', mode: 'off' }), // вне профиля → «свои»
      b({ id: 'own-off2', type: 'skill', target: 'perf-profiling', mode: 'off' }),
      b({ id: 'own-off3', type: 'skill', target: 'REGRESSION-MATRIX', mode: 'off' }),
    ], names);
    // Унаследованные — в порядке бэка, «отключено вами» — после них
    expect(g.fromSpecialty.map(r => r.kind)).toEqual(['inherited', 'disabledByYou', 'disabledByYou']);
    expect(g.own.map(x => x.id)).toEqual(['own-off']);
  });

  it('своё НЕ off-запись имени профиля и запись вне профиля — «свои»; plugin:-навык — «свои»', () => {
    const names = specialtySkillNames(PROFILE);
    const ownAuto = b({ id: 'own-auto', type: 'skill', target: 'playwright-e2e' });
    const ownOther = b({ id: 'own-other', type: 'skill', target: 'my-skill' });
    const ownOffOther = b({ id: 'own-off-other', type: 'skill', target: 'my-skill', mode: 'off' });
    const plugin = b({ id: 'own-plugin', type: 'skill', target: 'plugin:p/s', mode: 'off' });
    const g = groupSkillBindings([ownAuto, ownOther, ownOffOther, plugin], names);
    expect(g.fromSpecialty).toEqual([]);
    expect(g.own.map(x => x.id)).toEqual(['own-auto', 'own-other', 'own-off-other', 'own-plugin']);
  });

  it('копии данных проекта — своя секция; tool — «свои» (возможность самой персоны)', () => {
    const g = groupSkillBindings([
      b({ id: 'c1', type: 'project', target: 'p1' }),
      b({ id: 'c2', type: 'projectPath', target: 'p1' }),
      b({ id: 'c3', type: 'knowledge', target: 'k1' }),
      b({ id: 'c4', type: 'notes', target: 'personal' }),
      b({ id: 't1', type: 'tool', target: 'tasks' }),
    ], new Set());
    expect(g.projectCopies.map(x => x.id)).toEqual(['c1', 'c2', 'c3', 'c4']);
    expect(g.own.map(x => x.id)).toEqual(['t1']);
    expect(isProjectCopyType('tool')).toBe(false);
    expect(isProjectCopyType('knowledge')).toBe(true);
  });

  it('счётчик — фактическое число строк трёх секций (сверка §2.4 п.2)', () => {
    const names = specialtySkillNames(PROFILE);
    const g = groupSkillBindings([
      b({ type: 'skill', target: 'playwright-e2e', inherited: true }),
      b({ id: 'own-off', type: 'skill', target: 'regression-matrix', mode: 'off' }),
      b({ id: 'own', type: 'skill', target: 'my-skill' }),
      b({ id: 'copy', type: 'project', target: 'p1' }),
    ], names);
    expect(skillsRowCount(g)).toBe(4);
    expect(skillsRowCount(groupSkillBindings([], names))).toBe(0);
  });
});
