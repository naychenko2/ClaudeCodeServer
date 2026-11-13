// Точка прав по персоне общего проекта (W3-F1a): чистая логика «что можно этому человеку
// с этой персоной» по матрице §2 разреза docs/research/shared-projects-personas-cut-2026-11.md.
// Без React — проверяется юнит-тестами. Право решает бэк (ResolvePersona и [ProjectScope]
// с правом); фронт лишь не обещает того, что бэк откажет. Все компоненты F1b–F1i
// проверяют здесь, в одном месте, а не по своему контролу.

import type { Persona, Project, ProjectRoleView, Session } from '../../types';

// Роль текущего человека в проекте, к которому относится персона. Источник — только
// project.myRole (контракт соседей: своей логики «владелец ли» по ownerId не заводим).
// Нет проекта, нет projectId или нет myRole (обычный проект и старый бэк) → null,
// что читается как «как владелец»: старый бэк разрешал вызывающему всё.
export function personaRole(
  persona: Pick<Persona, 'projectId'>,
  projects: readonly Pick<Project, 'id' | 'myRole'>[],
): ProjectRoleView | null {
  return projects.find((p) => p.id === persona.projectId)?.myRole ?? null;
}

// Действия над персоной. Чат с персоной — RunTurn (открыт редактору); всё остальное —
// карточка, её память, привязки, ИИ-сборка команды, создание проектной персоны, поля
// зоны/денег, удаление, руководитель проекта, автоматика, MCP/плагин-привязки, навыки,
// память↔заметка и правка привязки к цели вне проекта — Configure, то есть владелец
// (решение Андрея 12.11). Новое действие над персоной — сюда, а не в вызывающий компонент.
export type PersonaAction =
  | 'chat' | 'editCard' | 'createPersona' | 'editZoneModel' | 'delete' | 'makeLead' | 'automation'
  | 'memoryWrite' | 'memoryNote' | 'bindingWrite' | 'bindingMcpPlugin' | 'skills' | 'aiTeam'
  | 'bindingForeignTarget';

// Действия, открытые редактору: только чат с персоной (решение Андрея 12.11); правка
// карточки, памяти, привязок, ИИ-сборка команды и создание проектной персоны — владелец.
const EDITOR_ACTIONS: readonly PersonaAction[] = ['chat'];

// Можно ли это действие над проектной персоной: нет роли (обычный проект и старый бэк)
// и владелец — всё; читатель — ничего (у него нет RunTurn); редактор — только чат.
export function canPersona(role: ProjectRoleView | null, action: PersonaAction): boolean {
  if (!role || role === 'owner') return true;
  if (role === 'reader') return false;
  return EDITOR_ACTIONS.includes(action);
}

// Смена собеседника в чате (матрица §3.2): владелец — в любом чате проекта; редактор —
// только в чате, который создал сам (автор — session.createdByUserId); читатель — ни в каком.
// Нет роли (обычный проект и старый бэк) — разрешено, как было.
export function canChangeCompanion(
  project: Pick<Project, 'myRole'> | null | undefined,
  session: Pick<Session, 'createdByUserId'> | null | undefined,
  meId: string,
): boolean {
  const role = project?.myRole;
  if (!role || role === 'owner') return true;
  if (role === 'reader') return false;
  return session?.createdByUserId === meId;
}
