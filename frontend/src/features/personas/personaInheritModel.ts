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

// Права в теле создания/сохранения мастера. Со специальностью и без ручных правок — только
// флаг true (права не копируются, правки специальности дойдут до персоны); без специальности
// либо после ручной правки — своё: флаг false вместе с копией значений формы.
export interface WizardAccessFields {
  inheritsAccess: boolean;
  tools?: string[];
  access?: PersonaAccess;
  disallowedTools?: string[];
}

export function wizardAccessFields(
  specialty: string,
  rightsTouched: boolean,
  own: { tools: string[]; access: PersonaAccess; disallowedTools: string[] },
): WizardAccessFields {
  if (specialty !== 'none' && !rightsTouched) return { inheritsAccess: true };
  return { inheritsAccess: false, ...own };
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

// Сколько из трёх ячеек уровней заданы самой персоной («своё · N из 3»)
export function ownTierCount(cells: TierCell): number {
  return TIER_KEYS.filter(k => !!cells[k]).length;
}

// Захват права на отправку сброса уровней: пока флаг поднят, вторая отправка не уходит.
export function claimTierReset(flag: { current: boolean }): boolean {
  if (flag.current) return false;
  flag.current = true;
  return true;
}

// Что делать с переключением группы «Модели»: ignore — идёт запрос или режим тот же;
// open — «Своё» открывает ячейки; confirm — возврат стирает заданные ячейки, нужен диалог;
// close — возврат без своих ячеек, стирать нечего.
export function tiersModeAction(target: 'own' | 'inherited', ownNow: boolean, ownCount: number, busy: boolean): 'ignore' | 'open' | 'confirm' | 'close' {
  if (busy || (target === 'own') === ownNow) return 'ignore';
  if (target === 'own') return 'open';
  return ownCount > 0 ? 'confirm' : 'close';
}

// Ячейки после сброса: все три пусты (данные для PUT и для формы).
export function clearedTiers(): Required<TierCell> {
  return { tierStrong: '', tierMedium: '', tierWeak: '' };
}

// ── Исход сброса уровней (F28) ────────────────────────────────────────────────────────

// Что вернул PUT сброса уровней: успех с сохранённой персоной или отказ с текстом.
export type TierResetOutcome =
  | { ok: true; saved: unknown }
  | { ok: false; error: string };

// Решение по исходу PUT: применять ли ответ форме и что показать.
// isAlive — жив ли ещё экземпляр формы. Родители перемонтируют форму по смене персоны
// (key={persona.id}), поэтому «ответ мой» определяется не сравнением id (у размонтированного
// экземпляра ref не обновляется и сравнение никогда не станет истинным), а живостью.
// Ответ умершего экземпляра не трогает ни форму, ни родителя: иначе правка чужой персоны
// закрылась бы и несохранённое пропало.
export function settleTierReset(
  outcome: TierResetOutcome,
  isAlive: boolean,
): { apply: boolean; error: string | null; saved?: unknown } {
  if (!outcome.ok) return { apply: false, error: outcome.error };
  if (!isAlive) return { apply: false, error: null };
  return { apply: true, error: null, saved: outcome.saved };
}
