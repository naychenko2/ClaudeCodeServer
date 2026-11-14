// Домен «Шаблоны процесса фичи» (feature-workflows, C1a): тонкая обёртка над живым REST.
// Контракт — WorkflowTemplatesController.cs (B1c): ключ шаблона — (scope, projectId, id);
// id для PUT/DELETE — из маршрута, id тела игнорируется; PUT требует expectedVersion
// (версию редактор получил из последнего чтения), устаревшую версию сервер отвечает 409.
// Область определяется наличием projectId: параметра нет — глобальная, есть — проектная.
// Отказ возвращается как есть: промис с ошибкой request() (Error с .status/.body) —
// 409 различим по status; обёртку «перечитать и править снова» делает экран (C1b), не здесь.
// recommendWorkflow/confirmWorkflow — ручки B4b (разрез §5), вызывает их блок подтверждения
// маршрута (C2); recommend разворачивает серверный ответ { proposal } здесь (см. ниже).
import { request } from '../../lib/offline';
// ConfirmedRoute — тип подтверждённого маршрута, он же поле epic.workflow агрегата
// (types/featureRun.ts импортирует его оттуда же): ответ confirm даёт тот же набор полей,
// поэтому переводить их некуда.
import type { ConfirmedRoute } from '../../components/feature/FeatureWorkflowConfirmModel';
import type { ConfirmWorkflowBody, WorkflowProposal, WorkflowTemplate, WorkflowTemplateEntry } from './types';

// Строка запроса области: без параметра — глобальная, с параметром — проектная (ScopeOf на бэке).
function scopeQs(projectId?: string): string {
  return projectId ? `?projectId=${encodeURIComponent(projectId)}` : '';
}

export const workflowTemplatesApi = {
  // Список: глобальные; с projectId — глобальные плюс проектные этого проекта.
  // Сервер отдаёт обёртки { scope, projectId, template } — раскрываем их здесь,
  // область каждой строки берёт вызывающий из entry.scope.
  listTemplates: (projectId?: string) =>
    request<WorkflowTemplateEntry[]>(`/workflow-templates${scopeQs(projectId)}`),

  // Сохранение. Новый шаблон (version 0 — ещё не лежал в сторе) → POST с expectedVersion: 0:
  // существующий id сервер отказывает 409, а не тихой перезаписью.
  // Правка (version > 0) → PUT по id с expectedVersion = последней прочитанной версии:
  // параллельная правка становится 409, а не затиранием чужого.
  // Ответ — сохранённый шаблон с новой version: вызывающий продолжает с ним.
  saveTemplate: (tpl: WorkflowTemplate, projectId?: string) => {
    const body = JSON.stringify({ ...tpl, expectedVersion: tpl.version });
    return tpl.version > 0
      ? request<WorkflowTemplate>(
          `/workflow-templates/${encodeURIComponent(tpl.id)}${scopeQs(projectId)}`,
          { method: 'PUT', body },
        )
      : request<WorkflowTemplate>(`/workflow-templates${scopeQs(projectId)}`, { method: 'POST', body });
  },

  // Удаление по id из маршрута. Встроенный стор не отдаёт (403): его можно только сбросить.
  deleteTemplate: (id: string, projectId?: string) =>
    request<void>(`/workflow-templates/${encodeURIComponent(id)}${scopeQs(projectId)}`, { method: 'DELETE' }),

  // Сброс встроенного пресета к коду (Version+1) — только admin, глобальная область:
  // ключ пресета сервер берёт из builtInKey шаблона, id — id шаблона (builtin-*).
  resetBuiltIn: (id: string) =>
    request<WorkflowTemplate>(`/workflow-templates/${encodeURIComponent(id)}/reset`, { method: 'POST' }),

  // Подбор маршрута (B4b): запускает рекомендатель. Сервер отвечает обёрткой
  // { proposal: WorkflowProposal | null } (TaskWorkflowController.Recommend) — разворачиваем
  // её здесь, на границе: вызывающий видит только WorkflowProposal | null, обёртку выше
  // никто не знает.
  // Модель может ждать десятки секунд — сигнал вызывающего (отмена при уходе со страницы, C2)
  // уходит в request() как есть, он объединяет его со своим таймаутом.
  recommendWorkflow: async (epicId: string, signal?: AbortSignal): Promise<WorkflowProposal | null> => {
    const resp = await request<{ proposal?: WorkflowProposal | null }>(
      `/tasks/${encodeURIComponent(epicId)}/workflow/recommend`, { method: 'POST', signal });
    const proposal = resp?.proposal ?? null;
    // «Предложения нет» сервер отдаёт объектом {state:'none', reason}: он truthy, но
    // предложением не является. Ответ без state — по-прежнему предложение.
    if (proposal?.state && proposal.state !== 'proposed') return null;
    return proposal;
  },

  // Подтверждение маршрута (B4b): единственный писатель epic.Workflow, только человек (И7).
  // Ответ — подтверждённый маршрут таким, каким его записал сервер: обёртка { confirmed }
  // (ConfirmedDto, TaskWorkflowController.cs:136). Её разворачиваем на границе, как обёртку
  // recommend: вызывающий применяет записанную версию сразу (Ф-2.15), а не ждёт штатный
  // task_changed. Старый сервер тела не даёт — null: вызывающий остаётся на прежнем виде.
  confirmWorkflow: async (epicId: string, body: ConfirmWorkflowBody): Promise<ConfirmedRoute | null> => {
    const resp = await request<{ confirmed?: ConfirmedRoute | null }>(
      `/tasks/${encodeURIComponent(epicId)}/workflow/confirm`, { method: 'POST', body: JSON.stringify(body) });
    return resp?.confirmed ?? null;
  },

  // «Оставить vN» (B03): отказ от обновления маршрута до версии templateVersion; маршрут не меняется,
  // предупреждение не возвращается до следующей версии. Состояние придёт штатным task_changed.
  keepVersion: (epicId: string, templateVersion: number) =>
    request<void>(`/tasks/${encodeURIComponent(epicId)}/workflow/keep-version`, { method: 'POST', body: JSON.stringify({ templateVersion }) }),
};
