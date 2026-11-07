// ПОТОЛОК РОСТА (frontend-api-split-2026-11.md): файл только худеет. Лимит строк — в
// lib/__tests__/apiFileBudget.test.ts; он равен фактической длине файла: снижается после
// каждого выноса домена в features/<домен>/api.ts, поднимать нельзя. Новые методы — сразу в features/<домен>/.
import type { WorkflowAgentInfo, WorkflowAgentBlock } from '../types';
import { localModelApi } from '../features/localModel/api';
import { providerAccountsApi } from '../features/providerAccounts/api';
import { accountApi } from '../features/account/api';
import { deployApi } from '../features/deploy/api';
import { hostControlApi } from '../features/hostControl/api';
import { modelsApi } from '../features/models/api';
import { spendApi } from '../features/spend/api';
import { modulesApi } from '../features/modules/api';
import { homeApi } from '../features/home/api';
import { chatAuxApi, chatsApi } from '../features/chat/api';
import { videoApi } from '../features/video/api';
import { devicesApi } from '../features/desktop/api';
import { spheresApi } from '../features/spheres/api';
import { specialtiesApi } from '../features/specialties/api';
import { knowledgeApi } from '../features/knowledge/api';
import { dossiersApi } from '../features/dossiers/api';
import { telemetryApi } from '../features/telemetry/api';
import { skillsApi } from '../features/skills/api';
import { plansApi } from '../features/plan/api';
import { imageGenerationApi } from '../features/imageGeneration/api';
import { mcpApi } from '../features/mcp/api';
import { readerApi } from '../features/reader/api';
import { pluginsApi } from '../features/plugins/api';
// eslint-disable-next-line module/no-cross-feature-import -- lib/api собирает объект api из api-модулей фич spread'ом; публичного index.ts у notes нет (баррел заведётся отдельно)
import { notesApi } from '../features/notes/api';
import { docsApi } from '../features/docs/api';
import { tasksApi } from '../features/tasks/api';
import { personasApi } from '../features/personas/api';
import { gitApi } from '../features/git/api';
import { filesApi } from '../features/files/api';
import { projectsApi } from '../features/projects/api';
import { sessionsApi } from '../features/sessions/api'; // eslint-disable-line module/no-cross-feature-import -- lib/api собирает объект api из api-модулей фич spread'ом; публичного index.ts у sessions нет (баррел заведётся отдельно)
import { writeStoredToken } from './offline';

// Типы «Проектов» (кандидат значка, «Архитектура») живут в features/projects/types.ts
export type * from '../features/projects/types';
// Типы «Локальных моделей» живут в features/localModel/types.ts, методы — в features/localModel/api.ts
export type * from '../features/localModel/types';
// Типы «Аккаунтов провайдеров» (Higgsfield) живут в features/providerAccounts/types.ts
export type * from '../features/providerAccounts/types';
// Типы «Аккаунта» (слоты моделей) живут в features/account/types.ts
export type * from '../features/account/types';
// Тип внешнего модуля (ModuleInfo) живёт в features/modules/types.ts
export type * from '../features/modules/types';
// Типы питания и пульта удалённых команд живут в features/hostControl/types.ts
export type * from '../features/hostControl/types';
// Типы выкатки (трей-раннер и журнал из чата) живут в features/deploy/types.ts
export type * from '../features/deploy/types';

export type { WorkflowAgentInfo, WorkflowAgentBlock };

// Типы «Знаний» (DifyDocument) живут в features/knowledge/types.ts
export type * from '../features/knowledge/types';
// Типы ридера (ReaderReadResult) живут в features/reader/types.ts
export type * from '../features/reader/types';
// Типы «Плагинов владельца» живут в features/plugins/types.ts; panelEntryPath — публичный
// экспорт и раньше (снаружи сейчас не импортируется), набор экспортов не меняем
export type * from '../features/plugins/types';
export { panelEntryPath } from '../features/plugins/api';
// Контекст worktree-чата для git-запросов: состояние живёт в features/git/api.ts,
// здесь только переэкспорт — адреса потребителей (ChatPanel, lib/git.ts) не меняются
export { setGitSessionContext, getGitSessionContext } from '../features/git/api';

// Перезапись cc_token после смены пароля: старый токен сервер отозвал, а хранилище
// выбирает не вызывающий — токен пишется туда, откуда его читает readStoredToken.
export function setStoredToken(token: string) {
  if (typeof window === 'undefined') return;
  writeStoredToken(token);
}

// Резюме фичи (W5-F2, «Точка 6 · резюме»): результат модельного анализа хронологии.
// text — markdown с разделами «Сделано», «Где застряло», «Дальше» (или голые пункты
// без заголовков — пока так просит промпт feature-summary); model=null — локальная
// модель; cached — результат взят из кэша бэка, без нового прогона. Ошибки: 502 —
// модель не ответила (причина в error), 503 — подсистема выключена, 404 — фичи нет.
export interface FeatureSummaryResult {
  text: string;
  generatedAt: string;
  model: string | null;
  cached: boolean;
  usage?: unknown;
  // Число событий хронологии, на которых построено резюме (W4-ANB): для шапки
  // «по N событиям» (бейдж устаревания — без числа). Бэк отдаёт всегда; optional —
  // защита от ответов старой версии сервера.
  eventCount?: number;
}

// Projects
export const api = {
  // Методы «Аккаунта» (auth/push/users/settings/слоты моделей) — в features/account/api.ts
  ...accountApi,

  // Расход токенов и лимиты подписок — в features/spend/api.ts
  ...spendApi,

  // Провайдеры, модели и исполнитель фоновых действий — в features/models/api.ts
  ...modelsApi,

  ...localModelApi,


  // Питание, пульт команд, бэкапы и файлы хоста — в features/hostControl/api.ts
  ...hostControlApi,

  // Выкатка трей-раннером и журнал выкатки из чата — в features/deploy/api.ts
  ...deployApi,

  ...providerAccountsApi,

  // Генератор картинок по местам — в features/imageGeneration/api.ts
  ...imageGenerationApi,

  // Модули, MF-remote подсистем и фича-флаги — в features/modules/api.ts
  ...modulesApi,

  // Личный реестр MCP-серверов — в features/mcp/api.ts
  ...mcpApi,

  // Устройства владельца и сопряжение — в features/desktop/api.ts
  ...devicesApi,

  // Плагины владельца (ADR-031 §2.3, §4) — в features/plugins/api.ts
  ...pluginsApi,

  // Раздел «Видео» — в features/video/api.ts
  ...videoApi,

  // Проекты и ссылки внешнего доступа — в features/projects/api.ts
  ...projectsApi,

  // Специальности персон и настройки к ним — в features/specialties/api.ts
  ...specialtiesApi,


  // Мелкие домены «Дома» (сводка, бриф, поиск, онбординг, «стена», быстрые фразы,
  // продуктовая история) — в features/home/api.ts
  ...homeApi,

  // Группы проектов и сферы — в features/spheres/api.ts
  ...spheresApi,

  // Задачи и доска агентов (диспетчерская) — в features/tasks/api.ts
  ...tasksApi,

  // Заметки (Obsidian-совместимая база знаний) — в features/notes/api.ts
  ...notesApi,

  // Персоны (олицетворённые ИИ-собеседники) — в features/personas/api.ts
  ...personasApi,

  // Сессии проекта — в features/sessions/api.ts
  ...sessionsApi,

  // Чаты вне проекта — в features/chat/api.ts
  ...chatsApi,

  // Сторожа чатов, workflow-агенты и голоса TTS — в features/chat/api.ts
  ...chatAuxApi,

  // Документация проекта (README.md + docs/**) — в features/docs/api.ts
  ...docsApi,

  // «История решений» — в features/dossiers/api.ts; «Телеметрия» — в features/telemetry/api.ts
  ...dossiersApi,
  ...telemetryApi,

  // Файлы проекта (включая агент устройства) и «Синхронизация» — в features/files/api.ts
  ...filesApi,

  // Git проекта с контекстом worktree-чата — в features/git/api.ts
  ...gitApi,

  // «Знания»: проекльный индекс Dify и менеджер баз знаний — в features/knowledge/api.ts
  ...knowledgeApi,

  // «Навыки» и реестр skills.sh — в features/skills/api.ts
  ...skillsApi,


  // Карта плана — в features/plan/api.ts
  ...plansApi,

  // «Ридер ссылок» с хелперами — в features/reader/api.ts
  ...readerApi,
};
