// «Git проекта» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// тонкая обёртка над живым REST; типы — types/index.ts (ре-экспорт features/git/types).
//
// Состояние контекста worktree-чата живёт ровно в одном экземпляре — здесь. lib/api.ts
// переэкспортирует setGitSessionContext/getGitSessionContext: адреса потребителей
// (api.git.* и импорты из lib/api) не меняются.
import { projectRequest } from '../../lib/deviceAgent';
import type {
  GitStatus, GitBranchInfo, GitLogEntry, GitCommitDetail, GitStashEntry,
  GitFileChange, GitBlameLine, GitRemoteInfo, GitCommitPromptInfo,
} from '../../types';

// Контекст worktree-чата для git-запросов: пока активен чат в отдельном git worktree,
// ВСЕ git-вызовы его проекта несут ?sessionId= — бэкенд переводит операции в дерево чата.
// Без контекста (или для другого проекта) запросы идут в корень проекта, как раньше.
// Выставляет ChatPanel по активной сессии; частичная передача сломала бы инвариант
// «коммит/дискард — в том же дереве, что и статус».
let gitSessionCtx: { projectId: string; sessionId: string } | null = null;
export function setGitSessionContext(projectId: string, sessionId: string | null) {
  gitSessionCtx = sessionId ? { projectId, sessionId } : null;
}
export function getGitSessionContext(): { projectId: string; sessionId: string } | null {
  return gitSessionCtx;
}

// Суффикс query для git-URL; sep — '?' для URL без параметров, '&' для URL с ними
function gq(projectId: string, sep: '?' | '&' = '?'): string {
  return gitSessionCtx?.projectId === projectId ? `${sep}sessionId=${gitSessionCtx.sessionId}` : '';
}

// По-чатовой класс кэша для git-запросов проекта: ответ с ?sessionId= — это ответ про
// дерево конкретного чата, без sessionId — про корень проекта (общий класс).
// Ключ без чата в URL не должен множить по-чатовые записи (панель «Изменения»
// открывается в дереве чата, и там ключ иначе был бы новым на каждый чат).
function gitChatScope(projectId: string): string | undefined {
  return gitSessionCtx?.projectId === projectId ? gitSessionCtx.sessionId : undefined;
}

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const gitApi = {
  // Git проекта (раздел «Файлы» → «Изменения»/«История»); ошибки операций — 409 { error }
  git: {
    // ?sessionId= (gq): активный worktree-чат переводит запросы в своё дерево — суффикс
    // добавляется ко всем операциям, достижимым из git-бара и панели «Изменения»
    status: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/status${gq(projectId)}`, { chatScope: gitChatScope(projectId) }),
    diff: (projectId: string, path: string, staged = false) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/git/diff?path=${encodeURIComponent(path)}&staged=${staged}${gq(projectId, '&')}`),
    log: (projectId: string, limit = 100, branch?: string) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/log?limit=${limit}${branch ? `&branch=${encodeURIComponent(branch)}` : ''}${gq(projectId, '&')}`, { chatScope: gitChatScope(projectId) }),
    // Незапушенные коммиты (впереди upstream) — стек скоупов панели «Изменения».
    // live: ответ не кэшируется вовсе: это самый тяжёлый по-чатовой класс (до 29 КБ на
    // чат, ключ с sessionId), а в офлайне он бесполезен — пушить без сети нельзя.
    unpushed: (projectId: string, limit = 100) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/unpushed?limit=${limit}${gq(projectId, '&')}`, { live: true }),
    // Настройка промпта AI-описания коммита: чтение (global/projectOverride/effective/default)
    getCommitPrompt: (projectId: string) =>
      projectRequest<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`),
    // Сохранить оба промпта: global (per-user, всегда) + project (override при useProject)
    setCommitPrompt: (projectId: string, global: string, project: string, useProject: boolean) =>
      projectRequest<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`, {
        method: 'PUT', body: JSON.stringify({ global, project, useProject }),
      }),
    // Определить стиль коммитов по истории репы → инструкция для поля (не сохраняет)
    detectCommitStyle: (projectId: string) =>
      projectRequest<{ prompt: string }>(`/projects/${projectId}/git/ai/detect-commit-style`, { method: 'POST', timeoutMs: 60_000 }),
    branches: (projectId: string) =>
      projectRequest<GitBranchInfo[]>(`/projects/${projectId}/git/branches${gq(projectId)}`),
    commitDetail: (projectId: string, sha: string) =>
      projectRequest<GitCommitDetail>(`/projects/${projectId}/git/commits/${sha}${gq(projectId)}`),
    commitFileDiff: (projectId: string, sha: string, path: string) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/git/commits/${sha}/diff?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    stage: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    unstage: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/unstage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    stageAll: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage-all${gq(projectId)}`, { method: 'POST' }),
    // Откат правок файла к HEAD — необратимо (подтверждение на фронте)
    discard: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/discard${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    discardAll: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/discard-all${gq(projectId)}`, { method: 'POST' }),
    commit: (projectId: string, message: string, amend = false) =>
      projectRequest<{ sha: string }>(`/projects/${projectId}/git/commit${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ message, amend }),
      }),
    checkout: (projectId: string, branch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/checkout${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ branch }) }),
    createBranch: (projectId: string, name: string, from?: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/branches${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ name, from: from ?? null }),
      }),
    fetch: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/fetch${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    pull: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/pull${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    push: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/push${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // «Подтянуть и опубликовать»: rebase на origin + push (ветка разошлась с origin).
    // Дольше push: внутри две сетевые операции подряд
    sync: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/sync${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // Частичный stage: patch — unified diff одного хунка/строк (сервер применяет с --recount)
    stageHunk: (projectId: string, patch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    unstageHunk: (projectId: string, patch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/unstage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    stashList: (projectId: string) =>
      projectRequest<GitStashEntry[]>(`/projects/${projectId}/git/stash${gq(projectId)}`),
    // Файлы отложенного (для просмотра в верхней зоне панели «Изменения», как у коммита)
    stashShow: (projectId: string, index: number) =>
      projectRequest<{ files: GitFileChange[] }>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`),
    stashPush: (projectId: string, message?: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ message: message ?? null }) }),
    stashPop: (projectId: string, index: number) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash/${index}/pop${gq(projectId)}`, { method: 'POST' }),
    // Удаление стэша — необратимо (подтверждение на фронте)
    stashDrop: (projectId: string, index: number) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`, { method: 'DELETE' }),
    // Безопасная отмена коммита: новый обратный коммит; конфликт → 409 { error }
    revertCommit: (projectId: string, sha: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/commits/${sha}/revert${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    blame: (projectId: string, path: string) =>
      projectRequest<GitBlameLine[]>(`/projects/${projectId}/git/blame?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Данные входа в веб-UI Forgejo (пароль нужен: приватные репо анониму отдают 404)
    forgejoCredentials: (projectId: string) =>
      projectRequest<{ login: string; password: string | null }>(`/projects/${projectId}/git/forgejo-credentials`),
    resetForgejoPassword: (projectId: string) =>
      projectRequest<{ login: string; password: string }>(`/projects/${projectId}/git/forgejo-credentials/reset`, { method: 'POST', timeoutMs: 30_000 }),
    // История одного файла (--follow) — вкладка «История» просмотра файла
    fileLog: (projectId: string, path: string, limit = 100) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/file-log?path=${encodeURIComponent(path)}&limit=${limit}${gq(projectId, '&')}`),
    // Содержимое файла в конкретной версии («открыть, как было»); null — бинарь/нет файла
    fileAtCommit: (projectId: string, sha: string, path: string) =>
      projectRequest<{ content: string | null }>(`/projects/${projectId}/git/commits/${sha}/file?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Документный режим: вернуть файл к версии из коммита (в авто-режиме сразу коммитится)
    restoreFile: (projectId: string, sha: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/commits/${sha}/restore-file${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ path }), timeoutMs: 60_000,
      }),
    // Документный режим: «Сохранить сейчас» (✨-сообщение + push при авто-пуше)
    saveNow: (projectId: string) =>
      projectRequest<{ committed: boolean; sha?: string }>(`/projects/${projectId}/git/save-now${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // LLM-помощь: описание коммита по staged-диффу / название стэша (генерация небыстрая — старт CLI)
    aiCommitMessage: (projectId: string) =>
      projectRequest<{ summary: string; description: string }>(`/projects/${projectId}/git/ai/commit-message${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    aiStashName: (projectId: string) =>
      projectRequest<{ name: string }>(`/projects/${projectId}/git/ai/stash-name${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // git init + при настроенном Forgejo создание удалённого репозитория
    init: (projectId: string) =>
      projectRequest<{ status: GitStatus; htmlUrl: string | null }>(`/projects/${projectId}/git/init`, { method: 'POST', timeoutMs: 60_000 }),
    remote: (projectId: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote`),
    // Подключить/обновить origin введённым адресом
    setRemote: (projectId: string, url: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote`, {
        method: 'POST', body: JSON.stringify({ url }), timeoutMs: 30_000,
      }),
    // Завести репозиторий на встроенном Forgejo и подключить его как origin
    createServerRepo: (projectId: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote/server`, { method: 'POST', timeoutMs: 60_000 }),
    setAutoCommit: (projectId: string, enabled: boolean, push: boolean) =>
      projectRequest<{ autoCommit: boolean; autoPush: boolean }>(`/projects/${projectId}/git/auto-commit`, {
        method: 'PUT', body: JSON.stringify({ enabled, push }),
      }),
  },
};
