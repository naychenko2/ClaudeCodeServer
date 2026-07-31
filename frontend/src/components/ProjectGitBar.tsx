// Git-бар над композером чата (клод-стиль): слева «где мы работаем» — ветка/дерево
// чата и дерево текущего хода (turnWorktree), справа суммарный diff +N/−M, кнопки
// «Зафиксировать» и «Опубликовать N». Витрина поверх готовой механики: данные/мутации —
// из стора lib/git.ts, форма фиксации живёт в правой панели «Изменения» (сюда только
// ведём). Виден только в проектном чате на десктопе. Прячется, когда фиксировать и
// публиковать нечего И нет активного дерева: с активным деревом (чата или хода) бар
// показываем ВСЕГДА, даже с пустым диффом — иначе после переключения в свежее дерево
// узнать «где мы работаем» было бы неоткуда (композер значение дерева не показывает,
// там только кнопка-тумблер).
import { useEffect, useState } from 'react';
import { GitBranch, FolderGit2, GitCommit, CloudUpload, ChevronDown } from 'lucide-react';
import type { Project, Session } from '../types';
import { C, FONT, FS, R, SP } from '../lib/design';
import { basename } from '../lib/paths';
import { ensureGit, useGitState, loadUnpushedLog, gitPush, workingDiffStat } from '../lib/git';
import type { TurnTree } from '../lib/turnWorktree';
import { wsPanels } from '../pages/workspace/panelStackState';
import { Modal, ModalActions, Menu } from './ui';
import { ICON_STROKE } from './ui/icons';

// Строка меню коммита в стиле оглавления DocsPanel (TocRow): плотный текстовый ряд
// без иконок, hover-подложка bgInset, приглушённый текст до наведения. Роль та же —
// «выбрать вариант из короткого списка», поэтому и вид общий.
function CommitMenuRow({ label, onClick }: { label: string; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', width: '100%', minHeight: 22,
        padding: `1px ${SP.sm}px`, border: 'none', borderRadius: R.md, cursor: 'pointer',
        textAlign: 'left', fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.35, minWidth: 0,
        background: hover ? C.bgInset : 'transparent',
        color: hover ? C.textHeading : C.textSecondary,
      }}
    >
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
    </button>
  );
}

export function ProjectGitBar({ project, session, turnTree = null, turnTreeLive = false, onCommitOwn, onCommitAll }: {
  project: Project;
  session?: Session;
  // Дерево ХОДА: агент внутри хода ушёл в свой git worktree (EnterWorktree), минуя
  // Session.worktreePath (см. lib/turnWorktree). Показываем вторым сегментом
  // нейтральным тоном; turnTreeLive — идёт ли ход сейчас (пульс-точка и формулировка title)
  turnTree?: TurnTree | null;
  turnTreeLive?: boolean;
  // Коммит только файлов этого диалога / всех изменений рабочего дерева
  onCommitOwn: () => void;
  onCommitAll: () => void;
}) {
  const st = useGitState(project.id);
  const status = st.status;
  const { reveal } = wsPanels.use();
  const [publishConfirm, setPublishConfirm] = useState(false);
  // rect кнопки «Зафиксировать» — открытое меню выбора области коммита (null = закрыто)
  const [commitMenu, setCommitMenu] = useState<DOMRect | null>(null);
  // Чат в отдельном worktree: запросы стора уже идут в его дерево (gitSessionContext),
  // перечитываем статус при переключении дерева у активной сессии
  const worktreeBranch = session?.worktreeBranch ?? null;

  // Статус + стек незапушенных (для кнопки «Опубликовать»); realtime держит их свежими
  useEffect(() => {
    ensureGit(project.id, true);
    void loadUnpushedLog(project.id);
  }, [project.id, worktreeBranch]);

  // Меню коммита в anchor-режиме само не ловит Esc — закрываем на вызывающей стороне
  useEffect(() => {
    if (!commitMenu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setCommitMenu(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [commitMenu]);

  const diff = workingDiffStat(status);
  const ahead = status?.ahead ?? 0;
  const publishN = ahead > 0 ? ahead : st.unpushed.length;
  const canPublish = publishN > 0;

  // Нечего ни фиксировать, ни публиковать — бар не показываем, ЕСЛИ нет активного
  // дерева. Активное дерево (чата или хода) держит бар даже при пустом диффе.
  // Вне git-репозитория бару по-прежнему делать нечего
  const treeActive = !!worktreeBranch || !!turnTree;
  if (!status?.isRepo || (!treeActive && diff.files === 0 && !canPublish)) return null;

  // Метка: ветка worktree чата > имя папки (проект сам открыт как worktree) > ветка
  const label = worktreeBranch ?? (status.isWorktree ? basename(project.rootPath) : (status.branch ?? '—'));

  // Открыть панель «Изменения» на скоупе «Не зафиксировано» (working). Панель
  // живёт в любой из зон, поэтому просим стор её показать: reveal открывает
  // закрытую в её домашней рельсе и возвращает true, если она УЖЕ открыта.
  // В этом случае просим панель мигнуть: иначе клик выглядит как «ничего не
  // произошло», хотя скоуп в панели переключился.
  const openChanges = () => {
    if (reveal('changes')) {
      window.dispatchEvent(new CustomEvent('cc-panel-flash', { detail: { key: 'changes' } }));
    }
    window.dispatchEvent(new CustomEvent('cc-git-open-working'));
  };

  return (
    // Отдельная плашка над композером: ширина — от общего контейнера ChatPanel
    // (CHAT_MAX_W, ровно как у композера), высота — в размер карточки поля ввода
    <div style={{
      display: 'flex', alignItems: 'center', gap: 12, margin: '10px 0 8px',
      height: 51, padding: '0 8px 0 12px',
      // Ни рамки, ни тени: плашку держит один полупрозрачный утопленный тон
      // (bgInsetSoft) — сквозь неё виден дудл-паттерн холста. Скругление общее
      // с карточкой поля ввода под ней
      background: C.bgInsetSoft, borderRadius: R.xxl,
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

      {/* Дерево ХОДА (агент ушёл в свой worktree внутри хода): нейтральный сегмент
          рядом с меткой чата — читаются все сочетания: только ветка, только дерево
          чата, только дерево хода, оба дерева сразу. Полный путь — в title */}
      {turnTree && (
        <span
          title={`${turnTreeLive ? 'Ход выполняется' : 'Последний ход выполнялся'} в дереве агента: ${turnTree.path}`}
          style={{
            display: 'flex', alignItems: 'center', gap: 6, height: 28, maxWidth: 220,
            // Тон приподнятых элементов панели (bgPanel): на утопленной подложке
            // bgSelected почти сливался с ней в светлой теме
            padding: '0 11px', borderRadius: R.md, background: C.bgPanel,
            border: `1px solid ${C.border}`, color: C.textSecondary, flexShrink: 0, minWidth: 0,
          }}
        >
          <span style={{
            fontFamily: FONT.mono, fontSize: 12.5, whiteSpace: 'nowrap',
            overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
          }}>ход: {turnTree.name}</span>
          <span style={{
            width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0,
            opacity: turnTreeLive ? 1 : 0.4,
            animation: turnTreeLive ? 'pulsedot 1.2s ease-in-out infinite' : 'none',
          }} />
        </span>
      )}

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

      {/* Зафиксировать — делегирует коммит чату; меню выбирает область: только
          изменения этого диалога («своё») или всё рабочее дерево. */}
      {diff.files > 0 && (
        <div style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
          <button
            type="button"
            onClick={e => setCommitMenu(e.currentTarget.getBoundingClientRect())}
            title="Зафиксировать изменения (git commit)"
            style={{
              display: 'flex', alignItems: 'center', gap: 6, height: 28, padding: '0 10px 0 12px',
              // Фон светлее плашки (bgPanel, как у соседней diff-пилюли): подложка
              // панели утоплена, и кнопка на её же тоне держалась бы только рамкой
              border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgPanel,
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading,
            }}
          >
            <GitCommit size={15} strokeWidth={ICON_STROKE} color={C.accent} />
            Зафиксировать
            <ChevronDown size={14} strokeWidth={ICON_STROKE} color={C.textMuted} />
          </button>
          {commitMenu && (
            <Menu anchor={commitMenu} minWidth={190} maxHeight={90} gap={2} onClose={() => setCommitMenu(null)}>
              <CommitMenuRow label="Только этот чат" onClick={() => { setCommitMenu(null); onCommitOwn(); }} />
              <CommitMenuRow label="Всё дерево" onClick={() => { setCommitMenu(null); onCommitAll(); }} />
            </Menu>
          )}
        </div>
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
              // Панель «Изменения» (если открыта) после публикации возвращаем на
              // «Не зафиксировано»: опубликованные коммиты ушли из её селектора
              onConfirm={() => {
                setPublishConfirm(false);
                void gitPush(project.id).then(ok => {
                  if (ok) window.dispatchEvent(new CustomEvent('cc-git-open-working'));
                });
              }}
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
