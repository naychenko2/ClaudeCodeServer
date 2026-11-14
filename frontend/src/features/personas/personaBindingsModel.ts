// Группировка вкладки «Умения» (A3b, макет persona-inheritance.html P29–P37,
// сверка reconciliation-report.md §2.1). ЧИСТЫЙ модуль: ни DOM, ни запросов —
// читает эффективный список привязок (GET {id}/bindings,
// PersonaBindingsService.EffectiveBindings) и skill-профиль специальности
// (defaultBindings каталога prompt-sections, эффективный вызывающему) — отдаёт
// строки трёх секций вкладки. Ничего не пишет и не отправляет.

import type { PersonaBinding, PersonaBindingType, SpecialtyDefaultBinding } from '../../types';

// Строка секции «От специальности» и её состояние — по нему кнопки строки
// (сверка §2.4 п.6): 'inherited' — «Отключить», 'disabledByYou' — «Вернуть»,
// 'disabledBySpecialty' — никаких: это выбор админа для всех, не этой персоны.
export type SkillRowKind = 'inherited' | 'disabledBySpecialty' | 'disabledByYou';

export interface SkillRow {
  b: PersonaBinding;
  kind: SkillRowKind;
}

export interface SkillGroups {
  // Секция «От специальности «X»»: унаследованные строки (в порядке бэка), затем
  // «отключено вами» — свои off-записи к тем же именам лежат в конце стора, но
  // относятся к этой секции.
  fromSpecialty: SkillRow[];
  // Секция «Свои»: свои skill-привязки, не совпавшие с профилем (в т. ч. навыки
  // плагинов), плюс свои tool-привязки — возможности самой персоны, не данные проекта.
  own: PersonaBinding[];
  // Секция «Подобраны ИИ под проект»: данные-копии проекта (разрез 12.11 §2:
  // «умения, подбираемые ИИ (Knowledge/Project/…), остаются копией: они проектные»).
  projectCopies: PersonaBinding[];
}

// Имена skill-умений профиля специальности (нижний регистр) — ключи сверки.
export function specialtySkillNames(defaultBindings: SpecialtyDefaultBinding[] | null | undefined): Set<string> {
  const names = new Set<string>();
  for (const b of defaultBindings ?? []) {
    if (b.type !== 'skill') continue;
    const n = b.skillName?.trim().toLowerCase();
    if (n) names.add(n);
  }
  return names;
}

// Та же сверка, что на бэке (PersonaBindingsService.cs:696): по имени, без учёта
// регистра. Плагин-навыки (target 'plugin:…') не совпадают никогда: профиль
// специальности их не хранит (ADR-031).
export function matchesSpecialtySkill(target: string, names: ReadonlySet<string>): boolean {
  const t = target.trim().toLowerCase();
  return t.length > 0 && !t.startsWith('plugin:') && names.has(t);
}

// «Копия данных проекта» — типы-источники, а не навыки и не инструменты:
// tool — возможность самой персоны, plugin — личный навык владельца (И-4).
const COPY_TYPES: readonly PersonaBindingType[] = ['project', 'projectPath', 'knowledge', 'notes'];

export function isProjectCopyType(type: PersonaBindingType): boolean {
  return COPY_TYPES.includes(type);
}

export function groupSkillBindings(
  bindings: PersonaBinding[],
  skillNames: ReadonlySet<string>,
): SkillGroups {
  const inherited: SkillRow[] = [];
  const disabledByYou: SkillRow[] = [];
  const own: PersonaBinding[] = [];
  const projectCopies: PersonaBinding[] = [];
  for (const b of bindings) {
    if (b.inherited === true) {
      inherited.push({ b, kind: b.mode === 'off' ? 'disabledBySpecialty' : 'inherited' });
      continue;
    }
    if (b.type === 'skill') {
      // Своя off-запись к умению специальности = «отключено вами» (A2: она гасит
      // унаследованную). Любая другая своя запись этого имени — своё решение персоны
      // об умении, унаследованной копии уже нет — это своя строка.
      if (b.mode === 'off' && matchesSpecialtySkill(b.target, skillNames)) {
        disabledByYou.push({ b, kind: 'disabledByYou' });
      } else {
        own.push(b);
      }
      continue;
    }
    if (isProjectCopyType(b.type)) projectCopies.push(b);
    else own.push(b);
  }
  return { fromSpecialty: [...inherited, ...disabledByYou], own, projectCopies };
}

// Счётчик шапки — фактическое число строк трёх секций (сверка §2.4 п.2: «Умения 7»
// при шести строках — считать по факту). Кросс-проектные привязки живут в своей
// секции «Сотрудничество» и сюда не входят.
export function skillsRowCount(g: SkillGroups): number {
  return g.fromSpecialty.length + g.own.length + g.projectCopies.length;
}
