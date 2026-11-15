// Чистая модель редактора шаблонов процесса (feature-workflows, C1a): ни DOM, ни запросов.
// Зеркало серверных WorkflowRules (backend/ClaudeHomeServer.Tasks/WorkflowRules.cs): те же
// белые списки, тот же кламп, те же тексты подсказок. Истина при этом остаётся серверным
// валидатором: stageErrors только показывает картину ДО отправки (редактор правит за один
// проход, а не по одной ошибке на сохранение), сервер проверяет сам и при несогласии
// отвечает 400 со своим списком.
// Порядок stages = порядок маршрута (по позиции) — модель порядок не меняет.

import type { WorkflowStage, WorkflowStagePresence, WorkflowStageRole, WorkflowTemplate } from './types';

// Роли этапов: ключи TaskRoles без feature/rework/consult (разрез §0.3) — шаблон не вводит
// новых видов работ; rework берёт этап step, эпик и consult вне маршрута.
export const STAGE_ROLES: readonly WorkflowStageRole[] =
  ['cut', 'design', 'plan', 'step', 'review', 'qa', 'final', 'merge'];

// Несъёмные роли (разрез §1): step — сама работа, merge — закрытие фичи, final — единственная
// проверка решения целиком, а его скрытый пропуск — уже названный риск эпика.
// final несъёмный ВРЕМЕННО — открытый вопрос 1 Андрею; его ответ правит эту строку,
// а не логику ниже (на бэке — та же строка в WorkflowRules.NonSkippable).
export const NON_SKIPPABLE: readonly WorkflowStageRole[] = ['step', 'merge', 'final'];

// Роли, которых в маршруте не больше одного (разрез 2 §1.6, на бэке — WorkflowRules.SingleRoles):
// cut, final и merge — по одному на фичу. Повторяться могут design, plan, step, review и qa —
// у каждого свой id (дублем на бэке считается id, не роль).
export const SINGLE_ROLES: readonly WorkflowStageRole[] = ['cut', 'final', 'merge'];

// Присутствие этапа (разрез §1 «Пропуск») и политика простоя (серверные WorkflowStagePresence.All /
// WorkflowStallPolicies.All).
export const STAGE_PRESENCES: readonly WorkflowStagePresence[] = ['on', 'optional', 'off'];
export const STALL_POLICIES: readonly string[] = ['nudge-alert', 'alert', 'stop-alert'];

// Лимит текста промпта этапа (серверный WorkflowRules.PromptLimit).
export const PROMPT_LIMIT = 4000;

// Лимит имени этапа (G7, решение контракта a5d0c76e; серверный WorkflowRules.TitleLimit).
export const TITLE_LIMIT = 40;

// Адресная метка этапа (Р-Ф-2.39, правило единое с бэком): Title этапа, если задан; иначе роль;
// если роль встречается в маршруте больше одного раза и Title нет — роль + id этапа.
// name — то, что стоит в кавычках, suffix — «(id)» после них; key — name + suffix без кавычек
// (ключ привязки ошибки к строке и ключ SaveFailure.stageErrors).
export function stageLabelParts(stages: WorkflowStage[], index: number): { name: string; suffix: string; key: string } {
  const st = stages[index];
  const title = (st?.title ?? '').trim();
  const role = (st?.role ?? '').trim();
  if (title) return { name: title, suffix: '', key: title };
  const repeated = stages.filter(s => s && (s.role ?? '').trim() === role).length > 1;
  if (!repeated) return { name: role, suffix: '', key: role };
  const id = (st?.id ?? '').trim() || role;
  return { name: role, suffix: ` (${id})`, key: `${role} (${id})` };
}

export function stageLabel(stages: WorkflowStage[], index: number): string {
  const { name, suffix } = stageLabelParts(stages, index);
  return `этап «${name}»${suffix}`;
}

export function isNonSkippable(role: string): boolean {
  return (NON_SKIPPABLE as readonly string[]).includes(role);
}

// Ключ группы имён — тот же, что ValidateTitleGroups на бэке (WorkflowRules.cs:199): роль + линия;
// линия есть только у step, поэтому группы прочих ролей — по одной роли. Роль вне белого списка
// группы не даёт (на бэке та же отсечка).
function titleGroupKey(stage: WorkflowStage): string {
  return `${(stage?.role ?? '').trim()} ${(stage?.lane ?? '').trim()}`;
}

// Нужен ли этому этапу Title (G7): он делит пару (Role, Lane) с другим этапом — без имени
// граф показал бы две неотличимые «Проверки» (qa-plan и qa). Это ПОДСКАЗКА для поля редактора
// до отправки; истина — за серверным валидатором (его тексты приходят с 400 с той же адресной
// меткой этапа: Title → роль → «роль (id)» при повторе роли, см. stageLabelParts).
export function titleRequired(stages: WorkflowStage[], index: number): boolean {
  const cur = stages[index];
  if (!cur || !(STAGE_ROLES as readonly string[]).includes((cur.role ?? '').trim())) return false;
  const key = titleGroupKey(cur);
  return stages.some((s, i) => i !== index && s
    && (STAGE_ROLES as readonly string[]).includes((s.role ?? '').trim())
    && titleGroupKey(s) === key);
}

// Присутствие этапа: возвращает КОПИЮ шаблона с новым presence этапа роли (несущность —
// состояние редактора в React). Несъёмную роль снять нельзя: 'off'/'optional' для неё —
// шаблон возвращается без изменений (тот же запрет, что у серверного валидатора).
// Роли нет в шаблоне — без изменений: ставить некуда.
export function setPresence(
  tpl: WorkflowTemplate,
  role: string,
  presence: WorkflowStagePresence,
): WorkflowTemplate {
  const idx = tpl.stages.findIndex(s => s.role === role);
  if (idx < 0) return tpl;
  if (isNonSkippable(role) && presence !== 'on') return tpl;
  const stages = [...tpl.stages];
  stages[idx] = { ...stages[idx], presence };
  return { ...tpl, stages };
}

// Порог молчания этапа (И3 «таймаут обойти нельзя»): null — дефолт конфига; число обязано
// лежать в [5..240] минут. Молча клампить нельзя — человек не увидит, что сохранилось другое
// число, поэтому вне диапазона (и нечисло) — ошибка в stageErrors, а «Сохранить» заблокирован.
export const STALL_MIN = 5;
export const STALL_MAX = 240;
export const STALL_RANGE_ERROR = 'От 5 до 240 минут';

export function stallInRange(n: number | null | undefined): boolean {
  if (n === null || n === undefined) return true;
  return typeof n === 'number' && Number.isFinite(n) && n >= STALL_MIN && n <= STALL_MAX;
}

// Ячейка матрицы — та же проверка, что серверной ModelCellRules.ValidateModelCell:
// пустая валидна; "tier:*" запрещён (ячейка уже адресована уровнем). Без trim: сервер
// тоже триммит не ячейку, а только пустоту — " tier:strong " для него валидная строка-id.
function modelCellError(cell: string | null | undefined, label: string): string | null {
  if (cell === null || cell === undefined || cell.trim() === '') return null;
  if (['tier:strong', 'tier:medium', 'tier:weak'].includes(cell.toLowerCase())) {
    return `${label}: в ячейке уровня не может быть tier:* — уровень уже выбран строкой`;
  }
  return null;
}

// Подсказки по шаблону до отправки — зеркало WorkflowRules.Validate, включая контракт
// «собрать ВСЕ ошибки, не первую» и поведение против пустых записей (null-этап — ошибка
// в списке, а не исключение). Метки те же: роль, если она известна, иначе порядковый номер.
export function stageErrors(tpl: WorkflowTemplate | null | undefined): string[] {
  const errors: string[] = [];
  if (!tpl) {
    errors.push('шаблон не задан.');
    return errors;
  }
  const stages = tpl.stages;
  if (!Array.isArray(stages)) {
    errors.push('шаблон: список этапов не задан.');
    return errors;
  }

  // Только одиночные роли (SINGLE_ROLES): повтор не-одиночной здесь молчит, как на бэке
  // (singleRoleSeen в WorkflowRules.Validate держит лишь их).
  const seenSingleRoles = new Set<string>();

  stages.forEach((stage, i) => {
    // Метка: роль, если она известна; иначе порядковый номер — «неизвестную роль»
    // именем этапа не назвать.
    const labelUnknown = `этап #${i + 1}`;
    if (!stage) {
      errors.push(`${labelUnknown}: пустая запись`);
      return;
    }

    const role = (stage.role ?? '').trim();
    // Presence и StallPolicy триммятся так же, как роль (R-B1a, п.3): строка из UI
    // с крающими пробелами — та же запись, а не «неизвестное значение».
    const presence = (stage.presence ?? '').trim();
    const stallPolicy = (stage.stallPolicy ?? '').trim();
    const known = (STAGE_ROLES as readonly string[]).includes(role);
    const label = known ? stageLabel(stages, i) : labelUnknown;

    if (!known) {
      errors.push(`${label}: неизвестная роль этапа «${role}». Допустимы: ${STAGE_ROLES.join(', ')}.`);
    } else if ((SINGLE_ROLES as readonly string[]).includes(role)) {
      // Та же граница, что у серверного `SingleRoles.Contains(role) && !singleRoleSeen.Add(role)`
      // (WorkflowRules.cs): проверка и запись — вместе. Повтор design/plan/step/review/qa
      // легален (у этапа свой id) — редактор не подсвечивает валидный шаблон красным.
      if (seenSingleRoles.has(role)) {
        errors.push(`${label}: роль «${role}» одиночная — в маршруте она встречается дважды.`);
      } else {
        seenSingleRoles.add(role);
      }
    }

    if (!(STAGE_PRESENCES as readonly string[]).includes(presence)) {
      errors.push(`${label}: неизвестное Presence «${presence}». Допустимы: ${STAGE_PRESENCES.join(', ')}.`);
    } else if (isNonSkippable(role) && (presence === 'off' || presence === 'optional')) {
      // Несъёмную роль нельзя ни снять, ни сделать опциональной: пропуск должен быть
      // виден, а не исчезать тихо (риск эпика «скрытый пропуск финала»).
      errors.push(`${label}: роль несъёмная, Presence «${presence}» запрещён.`);
    }

    // Кого звать — одно: специальность ИЛИ конкретная персона (разрез §1).
    // specialty='none' — «не задана», не выбор, с personaId не конфликтует.
    if (stage.specialty && stage.specialty !== 'none' && (stage.personaId ?? '').trim() !== '') {
      errors.push(`${label}: Specialty и PersonaId взаимоисключающи: укажите что-то одно.`);
    }

    if ((stage.prompt ?? '').length > PROMPT_LIMIT) {
      errors.push(`${label}: текст промпта этапа длиннее ${PROMPT_LIMIT} символов`);
    }

    // Имя этапа (G7): длина — по каждому этапу, как на бэке (WorkflowRules.cs:153, трим перед
    // сравнением). Требование группы (обязательность и различность) по одному этапу ответить
    // нельзя — его скажет сервер (ValidateTitleGroups), редактор покажет отказ у поля.
    if ((stage.title ?? '').trim().length > TITLE_LIMIT) {
      errors.push(`${label}: имя этапа длиннее ${TITLE_LIMIT} символов`);
    }

    for (const cell of [stage.tierStrong, stage.tierMedium, stage.tierWeak]) {
      const cellError = modelCellError(cell, label);
      if (cellError) errors.push(cellError);
    }

    if (!stallInRange(stage.stallMinutes)) {
      errors.push(`${label}: ${STALL_RANGE_ERROR}`);
    }

    if (stallPolicy !== '' && !(STALL_POLICIES as readonly string[]).includes(stallPolicy)) {
      errors.push(`${label}: неизвестная политика простоя «${stallPolicy}». Допустимы: ${STALL_POLICIES.join(', ')}.`);
    }
  });

  return errors;
}
