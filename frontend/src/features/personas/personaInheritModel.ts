// Группа прав формы персоны «как у специальности / своё» (разрез
// docs/research/ai-fabrika-workflow-cut-2026-11.md §2 «Форма персоны», шаг A3a).
//
// ЧИСТЫЙ модуль: ни DOM, ни запросов, ни состояния — читает флаг и ячейки, готовит
// фрагменты записи для формы (A3a→A3b). Источник правды — persona.inheritsAccess
// с сервера (A1a: PersonaManager.Hydrate подставляет эффективный шаблон специальности
// на каждой выдаче, PersonaManager.cs:94-115). Фронт эффективные значения НЕ вычисляет
// сам: получает их — из самой персоны (сервер уже подставил шаблон в поля) или из
// шаблона специальности (features/specialties/api.ts). Ни разрешения матриц, ни
// резолва уровней здесь нет — это бэк (ModelAssignmentResolver / ModelPreview).

import type { Persona, PersonaAccess } from './types';

// ── Режим группы прав ────────────────────────────────────────────────────────────────

export type AccessMode = 'inherited' | 'own';

// Та же читка флага, что у бэка (PersonaManager.cs:100: наследует только true):
// false и отсутствие поля (старый бэк, который флаг не шлёт) — «своё», т. е.
// прежнее поведение «права — поля самой персоны».
export function accessMode(persona: Pick<Persona, 'inheritsAccess'>): AccessMode {
  return persona.inheritsAccess === true ? 'inherited' : 'own';
}

// Снимок группы прав: три поля, которые бэк подставляет одной группой (чтобы группа
// не рвалась). Отсутствие поля здесь — не значение: «своего значения нет» — для
// ячейки значит наследовать, для группы значит дефолт бэка (см. materialize).
export interface AccessGroup {
  access?: PersonaAccess;
  tools?: string[] | null;
  disallowedTools?: string[] | null;
}

// Та же читка «отсутствующего», что у подстановки бэка (PersonaManager.cs:105-107):
// нет access → full; null/отсутствие списков → без ограничений. Без этого перехода
// переключение в «своё» записало бы null туда, где человеку показан full.
function materialize(source: AccessGroup): Required<AccessGroup> {
  return {
    access: source.access ?? 'full',
    tools: source.tools ?? null,
    disallowedTools: source.disallowedTools ?? null,
  };
}

// Копия снимка: массивы копируются — состояние формы и запись не должны делить один
// массив с прочитанной персоной.
function copyOf(values: Required<AccessGroup>): Required<AccessGroup> {
  return {
    ...values,
    tools: values.tools ? [...values.tools] : null,
    disallowedTools: values.disallowedTools ? [...values.disallowedTools] : null,
  };
}

export type OwnAccess = Required<AccessGroup> & { inheritsAccess: false };

// Переключение в «своё»: текущие эффективные значения становятся собственным override
// персоны. effective — то, что получил вызывающий: шаблон специальности из каталога
// либо persona «как у специальности» (её поля сервер уже сделал эффективными) — это
// допустимый источник, поэтому он же и опущенный fallback. Результат — фрагмент
// записи: группа плюс флаг false; саму персону функция не меняет.
export function toOwn(persona: AccessGroup, effective?: AccessGroup): OwnAccess {
  return { ...copyOf(materialize(effective ?? persona)), inheritsAccess: false };
}

// Возврат «как у специальности»: только флаг true. Ничего от персоны здесь не читается,
// а override в запись не кладётся: бэк сохраняет последние материализованные значения
// (они нужны для отката кода без флага, §2), а на чтении подставит шаблон специальности.
export function toInherited(): { inheritsAccess: true } {
  return { inheritsAccess: true };
}

// ── Ячейки матрицы моделей ───────────────────────────────────────────────────────────

// Ячейка матрицы (персона или шаблон специальности): значение — id модели или
// "preset:{id}"; пустая (null/''/нет поля) — своего значения нет.
export interface TierCell {
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
}

export type TierKey = 'tierStrong' | 'tierMedium' | 'tierWeak';
export const TIER_KEYS: readonly TierKey[] = ['tierStrong', 'tierMedium', 'tierWeak'];

export interface TierCellView {
  // Что показать; null — значения нет ни у персоны, ни у специальности: цепочка
  // идёт шире (defaultTier, слоты владельца) — их фронт не резолвит.
  value: string | null;
  // true — показанное не своё: серым, со ссылкой на роль (§2 «Форма персоны»).
  inherited: boolean;
}

// View одной ячейки: своё значение персоны — «своё» (inherited=false); пустая ячейка
// показывает значение специальности как унаследованное (inherited=true). Каждый
// уровень независим: унаследованный strong не делает medium своей ячейкой.
export function tierCellView(personaCell: TierCell, specialtyCell: TierCell | null | undefined, tier: TierKey): TierCellView {
  const own = personaCell[tier];
  if (own) return { value: own, inherited: false };
  const inheritedValue = specialtyCell?.[tier] ?? null;
  return { value: inheritedValue, inherited: inheritedValue !== null };
}

// ── Отличия своих прав от прав специальности ─────────────────────────────────────────

export interface AccessDiff {
  // Профиль доступа отличается от шаблона
  access: boolean;
  // Ключи возможностей (tasks/notes/web), у которых включённость иная, чем у шаблона
  tools: string[];
  // Запрещённые инструменты, которых у специальности нет
  extraDisallowed: string[];
  // Запрещённые у специальности, но снятые здесь
  missingDisallowed: string[];
  // Всего мест отличия (профиль + возможности + запреты)
  count: number;
}

const ALL_TOOLS = ['tasks', 'notes', 'web'];

// Сравнение текущих значений группы с шаблоном специальности. Отсутствующее у шаблона —
// как у бэка (materialize): full, все возможности, без запретов. Запреты сравниваются
// только при профиле «Свой» — у остальных профилей они не действуют.
export function accessDiff(own: AccessGroup, template: AccessGroup | null | undefined): AccessDiff {
  const base = materialize(template ?? {});
  const mine = materialize(own);
  const baseTools = new Set(base.tools ?? ALL_TOOLS);
  const myTools = new Set(mine.tools ?? ALL_TOOLS);
  const tools = ALL_TOOLS.filter(k => baseTools.has(k) !== myTools.has(k));
  const baseDis = base.access === 'custom' ? base.disallowedTools ?? [] : [];
  const myDis = mine.access === 'custom' ? mine.disallowedTools ?? [] : [];
  const extraDisallowed = myDis.filter(t => !baseDis.includes(t));
  const missingDisallowed = baseDis.filter(t => !myDis.includes(t));
  const access = mine.access !== base.access;
  return {
    access, tools, extraDisallowed, missingDisallowed,
    count: (access ? 1 : 0) + tools.length + extraDisallowed.length + missingDisallowed.length,
  };
}

// Переключение возможности чекбоксом: порядок и дубли не копятся.
export function toggleToolKey(tools: string[], key: string, on: boolean): string[] {
  const rest = tools.filter(t => t !== key);
  return on ? [...rest, key] : rest;
}

// ── Тексты диалогов перехода (F27) ───────────────────────────────────────────────────

const TOOL_NAMES: Record<string, string> = { tasks: 'задачи', notes: 'заметки', web: 'веб-поиск' };
const ACCESS_NAMES: Record<PersonaAccess, string> = { full: 'полный', readOnly: 'только чтение', custom: 'свой' };

const toolName = (key: string) => TOOL_NAMES[key] ?? key;

// Перечисление отличий своих прав от специальности для диалога возврата: каждое место —
// отдельная короткая фраза («выключен веб-поиск», «запрещён Bash»). own нужен, чтобы
// отличить выключенную возможность от включённой поверх шаблона.
export function describeAccessDiff(diff: AccessDiff, own: AccessGroup): string[] {
  const mine = new Set(materialize(own).tools ?? ALL_TOOLS);
  return [
    ...(diff.access ? [`профиль доступа: ${ACCESS_NAMES[materialize(own).access]}`] : []),
    ...diff.tools.map(k => `${mine.has(k) ? 'включён' : 'выключен'} ${toolName(k)}`),
    ...diff.extraDisallowed.map(t => `запрещён ${t}`),
    ...diff.missingDisallowed.map(t => `снят запрет ${t}`),
  ];
}

// Какими станут права по шаблону специальности: «профиль …; выключены …; запрещены …».
// Профиль «Полный» без ограничений — просто «полные права».
export function describeAccessGroup(group: AccessGroup | null | undefined): string {
  const g = materialize(group ?? {});
  const off = ALL_TOOLS.filter(k => !(g.tools ?? ALL_TOOLS).includes(k)).map(toolName);
  const dis = g.access === 'custom' ? g.disallowedTools ?? [] : [];
  const parts = [
    g.access === 'full' ? 'полные права' : `профиль «${ACCESS_NAMES[g.access]}»`,
    ...(off.length ? [`выключено: ${off.join(', ')}`] : []),
    ...(dis.length ? [`запрещены ${dis.join(' и ')}`] : []),
  ];
  return parts.join(', ');
}

// Применение диалога возврата: срабатывает один раз — повторный клик по «Вернуть», пока
// диалог не размонтирован, видит уже закрытое ожидание и ничего не меняет.
export function takeReturn(pending: boolean): { pending: false; apply: boolean } {
  return { pending: false, apply: pending };
}
