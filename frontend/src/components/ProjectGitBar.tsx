// Git-бар над композером чата (клод-стиль): слева ветка/имя worktree, справа
// суммарный diff +N/−M, кнопки «Зафиксировать N» и «Опубликовать N».
// Витрина поверх готовой механики: данные/мутации — из стора lib/git.ts, форма
// фиксации живёт в правой панели «Изменения» (сюда только ведём). Виден только в
// проектном чате на десктопе; прячется, когда фиксировать и публиковать нечего.
import { useEffect, useState } from 'react';
import { GitBranch, FolderGit2, GitCommit, CloudUpload } from 'lucide-react';
import type { Project, Session } from '../types';
import { C, FONT, R } from '../lib/design';
import { basename } from '../lib/paths';
import { ensureGit, useGitState, loadUnpushedLog, gitPush, workingDiffStat } from '../lib/git';
import { usePanelStack } from '../pages/workspace/panelStackState';
import { Modal, ModalActions } from './ui';
import { ICON_STROKE } from './ui/icons';

export function ProjectGitBar({ project, session, onCommitOwn }: { project: Project; session?: Session; onCommitOwn: () => void }) {
  const st = useGitState(project.id);
  const status = st.status;
  const { layout, toggle } = usePanelStack();
  const [publishConfirm, setPublishConfirm] = useState(false);
  // Чат в отдельном worktree: запросы стора уже идут в его дерево (gitSessionContext),
  // перечитываем статус при переключении дерева у активной сессии
  const worktreeBranch = session?.worktreeBranch ?? null;

  // Статус + стек незапушенных (для кнопки «Опубликовать»); realtime держит их свежими
  useEffect(() => {
    ensureGit(project.id, true);
    void loadUnpushedLog(project.id);
  }, [project.id, worktreeBranch]);

  const diff = workingDiffStat(status);
  const ahead = status?.ahead ?? 0;
  const publishN = ahead > 0 ? ahead : st.unpushed.length;
  const canPublish = publishN > 0;

  // Нечего ни фиксировать, ни публиковать — бар не показываем
  if (!status?.isRepo || (diff.files === 0 && !canPublish)) return null;

  // Метка: ветка worktree чата > имя папки (проект сам открыт как worktree) > ветка
  const label = worktreeBranch ?? (status.isWorktree ? basename(project.rootPath) : (status.branch ?? '—'));

  // Открыть правую панель «Изменения» на скоупе «Не зафиксировано» (working).
  // toggle не закрывает уже открытую панель — гейтим по наличию в раскладке.
  const openChanges = () => {
    if (!layout.flat().includes('changes')) toggle('changes');
    window.dispatchEvent(new CustomEvent('cc-git-open-working'));
  };

  return (
    // Отдельная плашка над композером: ширина — от общего контейнера ChatPanel
    // (CHAT_MAX_W, ровно как у композера), высота — в размер карточки поля ввода
    <div style={{
      display: 'flex', alignItems: 'center', gap: 12, margin: '10px 0 8px',
      height: 51, padding: '0 8px 0 12px',
      background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
    }}>
      {/* Ветка / имя worktree; папка-иконка — чат в отдельном дереве */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
        {worktreeBranch
          ? <FolderGit2 size={15} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />
          : <GitBranch size={15} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />}
        <span title={worktreeBranch ? `Отдельное дерево чата: ${label}` : label} style={{
          fontFamily: FONT.mono, fontSize: 12.5, color: C.textSecondary,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{label}</span>
      </div>

      <div style={{ flex: 1 }} />

      {/* diff-пилюля +N/−M — кликом открывает панель «Изменения» */}
      {diff.files > 0 && (
        <button
          type="button"
          onClick={openChanges}
          title="Открыть изменения"
          style={{
            display: 'flex', alignItems: 'center', gap: 8, height: 28, padding: '0 11px',
            border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite,
            cursor: 'pointer', fontFamily: FONT.mono, fontSize: 12.5, flexShrink: 0,
          }}
        >
          {diff.added > 0 && <span style={{ color: C.diffAddText }}>+{diff.added}</span>}
          {diff.deleted > 0 && <span style={{ color: C.diffRemText }}>−{diff.deleted}</span>}
          {diff.added === 0 && diff.deleted === 0 && <span style={{ color: C.textMuted }}>±0</span>}
        </button>
      )}

      {/* Зафиксировать своё — делегирует чату коммит ТОЛЬКО изменений этого диалога */}
      {diff.files > 0 && (
        <button
          type="button"
          onClick={onCommitOwn}
          title="Зафиксировать в чате только изменения этого диалога"
          style={{
            display: 'flex', alignItems: 'center', gap: 6, height: 28, padding: '0 12px',
            border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgInset,
            cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading, flexShrink: 0,
          }}
        >
          <GitCommit size={15} strokeWidth={ICON_STROKE} color={C.accent} />
          Зафиксировать своё
        </button>
      )}

      {/* Опубликовать N — git push с подтверждением */}
      {canPublish && (
        <button
          type="button"
          onClick={() => setPublishConfirm(true)}
          disabled={st.busy}
          title="Опубликовать (git push)"
          style={{
            display: 'flex', alignItems: 'center', gap: 6, height: 28, padding: '0 12px',
            border: 'none', borderRadius: R.md, background: C.accent, color: C.onAccent,
            cursor: st.busy ? 'default' : 'pointer', fontFamily: FONT.sans, fontSize: 12.5,
            fontWeight: 600, flexShrink: 0, opacity: st.busy ? 0.6 : 1,
          }}
        >
          <CloudUpload size={15} strokeWidth={ICON_STROKE} />
          Опубликовать <span style={{ opacity: 0.85 }}>{publishN}</span>
        </button>
      )}

      {/* Подтверждение публикации (аналог publishConfirm в панели «Изменения») */}
      {publishConfirm && (
        <Modal
          width={440}
          onClose={() => setPublishConfirm(false)}
          title="Опубликовать изменения"
          subtitle={<span>Отправить {publishN} коммит(ов) на сервер</span>}
          footer={
            <ModalActions
              confirmLabel="Опубликовать"
              onConfirm={() => { setPublishConfirm(false); void gitPush(project.id); }}
              onCancel={() => setPublishConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Локальные коммиты ветки <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{status.branch}</span> будут отправлены в удалённый репозиторий (git push).
          </div>
        </Modal>
      )}
    </div>
  );
}
