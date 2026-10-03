// Строка контекста хода: связка стора контекста (lib/chatContext) и git-чипа с отрисовкой
// ContextRowView. Рисуется вместо хоста полос над композером, когда включён флаг
// composer-context-row. Открытость правой панели сюда не приходит: строка от неё не зависит.
import { useEffect, useState, type ReactNode } from 'react';
import { File as FileIcon } from 'lucide-react';
import type { Project, Session } from '../../types';
import { useContainerWidth } from '../../hooks/useContainerWidth';
import { useGitChip } from '../../hooks/useGitChip';
import { registerGitActions } from '../../lib/chatContext/gitActions';
import { execMenuTitle, selectRowAction } from '../../lib/chatContext/rowExec';
import { getKindApi } from '../../lib/chatContext/registry';
import {
  clearContext, detachRef, ensureChatContext, releasePrimary, undoReleasePrimary, useChatContext, useReleaseOffer,
} from '../../lib/chatContext/store';
import type { ContextAction } from '../../lib/chatContext/types';
import type { TurnTree } from '../../lib/turnWorktree';
import { useActionMemoryVersion } from '../../lib/chatContext/actionMemory';
import { revealContextPanel } from '../../lib/subsystems/registryCore';
import { wsPanels } from '../../pages/workspace/panelStackState';
import { PublishDialog } from '../PublishDialog';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ContextRowView, type RowExec, type RowGit } from './ContextRowView';
import { ContextSheet } from './ContextSheet';

// Рамка и отступы строки: измеряем внешний блок, лестница считает от внутренней ширины
const ROW_BORDER = 2;

interface Props {
  session: Session;
  project: Project | null;
  turnTree?: TurnTree | null;
  isMobile: boolean;
  onCommitOwn: () => void;
  onCommitAll: () => void;
  // Открыть объект в правой панели «Контекст»; по умолчанию — revealContextPanel
  onOpenPrimary?: () => void;
}

const iconOf = (kind: string): ReactNode =>
  getKindApi(kind)?.icon(kind) ?? <FileIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

export function ContextRow(props: Props) {
  return (
    <>
      <ContextRowBody {...props} />
      <ContextSheet session={props.session} project={props.project} />
    </>
  );
}

function ContextRowBody(props: Props) {
  const { session, onCommitOwn, onCommitAll } = props;
  // Панель «Контекст» живёт на странице выше и сама поручений чату отправить не может — берёт их здесь
  useEffect(
    () => registerGitActions(session.id, { commitOwn: onCommitOwn, commitAll: onCommitAll }),
    [session.id, onCommitOwn, onCommitAll],
  );
  return props.project
    ? <WithGit {...props} project={props.project} />
    : <RowCore {...props} git={null} />;
}

function WithGit(props: Props & { project: Project }) {
  const { project, session, turnTree = null } = props;
  const chip = useGitChip(project, session, turnTree);
  const { reveal } = wsPanels.use();
  const [publish, setPublish] = useState(false);
  const git: RowGit | null = chip.isRepo ? {
    label: chip.label,
    changes: chip.diff.files,
    ahead: chip.ahead,
    publishN: chip.publishN,
    onCommitOwn: props.onCommitOwn,
    onCommitAll: props.onCommitAll,
    onPublish: () => setPublish(true),
    onShowChanges: () => {
      // Панель уже открыта — просим её мигнуть: иначе клик выглядит как «ничего не произошло»
      if (reveal('changes')) window.dispatchEvent(new CustomEvent('cc-panel-flash', { detail: { key: 'changes' } }));
      window.dispatchEvent(new CustomEvent('cc-git-open-working'));
    },
  } : null;
  return (
    <>
      <RowCore {...props} git={git} />
      {publish && <PublishDialog projectId={project.id} onClose={() => setPublish(false)} />}
    </>
  );
}

function RowCore({ session, project, isMobile, onOpenPrimary: onOpenPrimaryProp, git }: Props & { git: RowGit | null }) {
  const sessionId = session.id;
  // Клик по чипу объекта открывает панель «Контекст», а открытую мигает карточкой «С чем»
  const onOpenPrimary = onOpenPrimaryProp ?? (() => { revealContextPanel(sessionId); });
  const ctx = useChatContext(sessionId);
  const offer = useReleaseOffer(sessionId);
  useActionMemoryVersion();
  const [ref, width] = useContainerWidth<HTMLDivElement>();
  useEffect(() => { void ensureChatContext(sessionId); }, [sessionId]);

  const { primary, refs } = ctx;
  const kindCtx = { projectId: project?.id ?? null, sessionId, isMobile };

  // Выбранное действие объекта: предвыбор → память → умолчание (Р1). «Чем» есть только у run-действия
  // executors() вида зовётся на каждый рендер намеренно: выбранный исполнитель живёт в сторе вертикали,
  // а в зависимости мемо он не входит. Цену вызова держит контракт (types.ts): вид отдаёт стабильную модель
  const sel = primary ? selectRowAction(getKindApi(primary.kind), kindCtx, primary, refs) : null;
  const action: ContextAction | null = sel?.action ?? null;
  const exec: RowExec | null = sel?.executors
    ? {
        rows: sel.executors.rows, value: sel.executors.value, onChange: sel.executors.onChange,
        title: execMenuTitle(action),
      }
    : null;

  return (
    <div ref={ref}>
      <ContextRowView
        width={width === null ? null : width - ROW_BORDER}
        isMobile={isMobile}
        git={git}
        primary={primary}
        refs={refs}
        exec={exec}
        actionLabel={action?.label ?? null}
        actionOp={action?.op ?? null}
        iconOf={iconOf}
        offer={offer}
        onUndo={() => { void undoReleasePrimary(sessionId); }}
        onRelease={() => { void releasePrimary(sessionId, true); }}
        onOpenPrimary={onOpenPrimary}
        onDetach={id => { void detachRef(sessionId, id); }}
        onClear={() => { void clearContext(sessionId); }}
      />
    </div>
  );
}
