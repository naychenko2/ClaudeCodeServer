// Типы домена «Проекты» (вынесены из шапки lib/api.ts, docs/research/frontend-api-split-2026-11.md).
// lib/api.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.
// Основные типы проекта (Project, ProjectService…) остались в types/index.ts.

// Кандидат значка проекта (ADR-009 §2.2): name — имя lucide-иконки из белого списка.
// Сервер валидирует на входе; на фронте достаточно хранить как есть.
export interface GlyphCandidate {
  name?: string | null;
}

// Итог «Собрать архитектуру» (POST /projects/{id}/architecture/generate).
// graphBuiltAt — время снимка графа кода, которым помечена карта (ISO); added/matched —
// новые и сохранённые элементы: повторная сборка ручные описания не трогает.
export interface ArchitectureGenerateResult {
  modelPath: string;
  graphBuiltAt: string | null;
  generatedAt: string;
  containers: number;
  components: number;
  added: number;
  matched: number;
  connectionsAdded: number;
  // Проход 1 «Собрать архитектуру»: внешние системы-кандидаты (тег «кандидат»), элементы из
  // кода, которых больше нет (тег «нет в коде»), снятые пометки и удалённые руками (не
  // пересоздаются); missing/candidatesSkipped — имена для сводки
  candidates?: number;
  markedMissing?: number;
  unmarked?: number;
  skippedDeleted?: number;
  missing?: string[] | null;
  candidatesSkipped?: string[] | null;
  // Проход 2 (withAgent): задача исполнителю, его персона (null — без персоны-архитектора)
  // и код отказа; launch_failed — задача создана, но исполнитель не стартовал
  agentTaskId?: string | null;
  agentPersonaId?: string | null;
  agentError?: string | null;
  warnings?: ArchitectureModelFinding[] | null;
}

// Несостыковка модели (ArchitectureModelValidator на бэке): висящая ссылка, точка связи,
// которой у карточки нет, дубль id. Предупреждение — запись она не блокирует
export interface ArchitectureModelFinding {
  kind: 'dangling_parent' | 'dangling_connection' | 'dangling_flow_step' | 'unknown_handle' | 'duplicate_id';
  elementId: string;
  text: string;
}

// Модель раздела «Архитектура» (GET /projects/{id}/architecture/model). version — SHA-256
// содержимого файла; updatedBy — имя человека или «Сборка из кода».
export interface ArchitectureModelDto {
  exists: boolean;
  content: string | null;
  version: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}

export interface ArchitectureSaveResult {
  version: string;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}

// --- Участники общего проекта (shared-projects, план docs/research/shared-projects-plan-2026-10.md §5) ---
//
// Роль участника: editor (читает и пишет) или reader (только читает) — матрица прав
// разреза §2.1. «Владелец» в списке участников не живёт никогда: он выводится из
// Project.OwnerId (ProjectAccess.Check), поэтому ни на бэке, ни здесь его в списке нет.
export type ProjectMemberRole = 'editor' | 'reader';

// Роль ТЕКУЩЕГО человека в проекте. Сервер пока не отдаёт её нигде (в DTO проекта её
// нет — ProjectsController.WithCount), поле резервируем под следующий шаг UI. Сегодня
// право действия определяют по ownerId, а не по этому полю.
export type ProjectRoleView = 'owner' | 'editor' | 'reader';

// Строка списка участников в ответах /api/projects/{id}/members. Владельца в списке нет:
// он приходит отдельным полем ответа (ProjectMembersView.owner). username/displayName/
// addedAt остаются необязательными: старые данные и тесты строят участника из одной пары
// userId + role, а UI в таком случае показывает то, что есть.
export interface ProjectMember {
  userId: string;
  role: ProjectMemberRole;
  username?: string;
  displayName?: string;
  addedAt?: string;
  addedBy?: string;
}

// Владелец проекта в ответе списка участников
export interface ProjectOwnerRef {
  userId: string;
  username: string;
  displayName?: string | null;
}

// Тело GET /api/projects/{id}/members и ответ каждой мутации членства: участники, владелец
// и роль того, кто спрашивает (по ней вид настроек — владелец или участник).
export interface ProjectMembersView {
  members: ProjectMember[];
  owner: ProjectOwnerRef;
  myRole: ProjectRoleView;
}

// Человек в сводке участников (ProjectMembersView.Summary): только опознавательные поля,
// роли нет — она есть в полном списке.
export interface ProjectMemberSummaryRef {
  userId: string;
  username?: string;
  displayName?: string | null;
}

// Сводка участников в DTO проекта (S1-2b): количество + первые трое. Не полный список:
// он только в GET /api/projects/{id}/members (ProjectMembersView).
export interface ProjectMembersSummary {
  count: number;
  first: ProjectMemberSummaryRef[];
}

// Машинные коды отказов операций членства. Текст отказа бэк отдаёт готовым для человека;
// по коду фронт выбирает действие («включить флаг», «это проект на устройстве»,
// «человек уже здесь»). Тело ошибки приносит lib/offline целиком (err.body).
export const MEMBER_REFUSAL = {
  // Рубильник «Общие проекты» выключен на сервере (503)
  SwitchDisabled: 'shared_projects_disabled',
  // Флаг выключен у владельца ИЛИ у приглашаемого (409)
  FlagDisabled: 'flag_disabled',
  // Проект на устройстве — его нельзя открыть другим людям (409)
  LocalProject: 'local_project',
  // Роль не editor/reader (400)
  InvalidRole: 'invalid_role',
  // Пользователя с таким username нет (404)
  UserNotFound: 'user_not_found',
  // Владелец приглашает сам себя (400)
  Self: 'self',
  // Человек уже в списке (409)
  AlreadyMember: 'already_member',
  // Меняют/убирают не участника (404)
  NotMember: 'not_member',
  // Проект не виден вызывающему (404, ответ сервиса членства)
  NotFound: 'not_found',
  // Проект виден, но управляет им только владелец (403)
  NotOwner: 'not_owner',
  // Владелец не может покинуть свой проект (400)
  OwnerCannotLeave: 'owner_cannot_leave',
  // Не удалось подготовить git-репозиторий проекта (500)
  GitInitFailed: 'git_init_failed',
  // Отказ точки доступа [ProjectScope] до тела действия: 404 «не найдено» или 403
  // «нет права на запись» — один код на оба (ProjectScopeAttribute.ErrorCode)
  ProjectScope: 'project_scope',
} as const;
export type MemberRefusalCode = (typeof MEMBER_REFUSAL)[keyof typeof MEMBER_REFUSAL];
