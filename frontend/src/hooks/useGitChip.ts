// Данные git-чипа чата: ветка/дерево, правки, коммиты к публикации. Один источник для полосы
// ProjectGitBar и чипа «Где» в строке контекста — оба показывают одно и то же состояние.
import { useEffect } from 'react';
import type { Project, Session } from '../types';
import { basename } from '../lib/paths';
import { ensureGit, gitStripStatus, loadUnpushedLog, useGitState, workingDiffStat } from '../lib/git';
import type { TurnTree } from '../lib/turnWorktree';

export function useGitChip(project: Project, session?: Session, turnTree: TurnTree | null = null) {
  const st = useGitState(project.id);
  const status = st.status;
  // Чат в отдельном worktree: запросы стора уже идут в его дерево (gitSessionContext),
  // перечитываем статус при переключении дерева у активной сессии
  const worktreeBranch = session?.worktreeBranch ?? null;

  // Статус + стек незапушенных (для «Опубликовать»); realtime держит их свежими
  useEffect(() => {
    ensureGit(project.id, true);
    void loadUnpushedLog(project.id);
  }, [project.id, worktreeBranch]);

  const diff = workingDiffStat(status);
  const ahead = status?.ahead ?? 0;
  const behind = status?.behind ?? 0;
  const publishN = ahead > 0 ? ahead : st.unpushed.length;
  const canPublish = publishN > 0;
  // Активное дерево (чата или хода) держит полосу даже при пустом диффе
  const treeActive = !!worktreeBranch || !!turnTree;
  const isEmpty = diff.files === 0 && !canPublish;
  const strip = gitStripStatus(status, st.unpushed.length);
  // Метка: ветка worktree чата > имя папки (проект сам открыт как worktree) > ветка
  const label = status
    ? (worktreeBranch ?? (status.isWorktree ? basename(project.rootPath) : (status.branch ?? '—')))
    : '—';

  return { st, status, isRepo: !!status?.isRepo, worktreeBranch, diff, ahead, behind, publishN, canPublish, treeActive, isEmpty, strip, label };
}
