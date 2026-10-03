// Связка панели «Контекст» с живыми данными (ADR-023 §Д1): стор контекста чата, вид из слота
// context-kind, git-чип, ссылка «назад». Саму отрисовку держит ContextPanel (по готовой модели),
// чтобы витрина кормила её фикстурами без стора и сети. Хост зовут обе страницы — проект и «Чаты».
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { File as FileIcon } from 'lucide-react';
import type { Project, Session } from '../../types';
import { useGitChip } from '../../hooks/useGitChip';
import { stubActionRun } from '../../lib/chatContext/actionRunStub';
import { clearContextReturn, useContextReturn } from '../../lib/chatContext/contextReturn';
import { getGitActions } from '../../lib/chatContext/gitActions';
import { getKindApi } from '../../lib/chatContext/registry';
import { selectRowAction } from '../../lib/chatContext/rowExec';
import {
  clearContext, detachRef, ensureChatContext, releasePrimary, setPrimary, useChatContext,
} from '../../lib/chatContext/store';
import type { ContextKindCtx, LaunchParam } from '../../lib/chatContext/types';
import { REVEAL_PANEL_EVENT, revealWorkspacePanel, type RevealPanelDetail } from '../../lib/subsystems/registryCore';
import { wsPanels } from '../../pages/workspace/panelStackState';
import { PublishDialog } from '../PublishDialog';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import type { RowGit } from '../chat/ContextRowView';
import { ContextPanel, type AddFromItem } from './ContextPanel';

interface Props {
  session: Session;
  // null — личный чат вне проекта: секции «Где» нет
  project: Project | null;
  onClose?: () => void;
  isMobile?: boolean;
  contained?: boolean;
  layout?: 'auto' | 'column' | 'sheet';
}

const noop = () => {};

export function ContextPanelHost(props: Props) {
  return props.project
    ? <WithGit {...props} project={props.project} />
    : <Core {...props} git={null} />;
}

function WithGit(props: Props & { project: Project }) {
  const { project, session } = props;
  const chip = useGitChip(project, session);
  const actions = getGitActions(session.id);
  const { reveal } = wsPanels.use();
  const [publish, setPublish] = useState(false);
  const git: RowGit | null = chip.isRepo ? {
    label: chip.label, changes: chip.diff.files, ahead: chip.ahead, publishN: chip.publishN,
    onCommitOwn: actions?.commitOwn ?? noop,
    onCommitAll: actions?.commitAll ?? noop,
    onPublish: () => setPublish(true),
    onShowChanges: () => {
      if (reveal('changes')) window.dispatchEvent(new CustomEvent('cc-panel-flash', { detail: { key: 'changes' } }));
      window.dispatchEvent(new CustomEvent('cc-git-open-working'));
    },
  } : null;
  return (
    <>
      <Core {...props} git={git} />
      {publish && <PublishDialog projectId={project.id} onClose={() => setPublish(false)} />}
    </>
  );
}

function Core({ session, project, onClose, isMobile = false, contained, layout, git }: Props & { git: RowGit | null }) {
  const sessionId = session.id;
  const ctx = useChatContext(sessionId);
  const ret = useContextReturn(sessionId);
  useEffect(() => { void ensureChatContext(sessionId); }, [sessionId]);

  // Повторный показ уже открытой панели: панель смонтирована, значит открыта — мигаем «С чем»
  const [flash, setFlash] = useState(0);
  useEffect(() => {
    const on = (e: Event) => {
      const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
      if (d?.key === 'chatContext' && (!d.sessionId || d.sessionId === sessionId)) setFlash(n => n + 1);
    };
    window.addEventListener(REVEAL_PANEL_EVENT, on);
    return () => window.removeEventListener(REVEAL_PANEL_EVENT, on);
  }, [sessionId]);

  const { primary, refs } = ctx;
  const kindCtx: ContextKindCtx = { projectId: project?.id ?? null, sessionId, isMobile };
  const api = primary ? getKindApi(primary.kind) : null;
  const sel = useMemo(
    () => (primary ? selectRowAction(api, kindCtx, primary, refs) : { action: null, executors: null }),
    // kindCtx собирается из примитивов: сравниваем их, а не новый объект на каждый рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [api, primary, refs, sessionId, project?.id, isMobile],
  );
  const action = sel.action;

  // Значения параметров запуска до живого useActionRun (1ф-4) держим здесь: сброс при смене действия
  const [paramValues, setParamValues] = useState<Record<string, number | string>>({});
  useEffect(() => { setParamValues({}); }, [action?.id, primary?.id]);
  const params: readonly LaunchParam[] = useMemo(
    () => (primary && action ? api?.params?.(kindCtx, action.id) ?? [] : []).map(p => {
      const v = paramValues[p.kind];
      return v === undefined || p.kind === 'fromQuestion' ? p : { ...p, value: v } as LaunchParam;
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [api, primary, action, paramValues, sessionId, project?.id, isMobile],
  );

  const iconOf = (kind: string): ReactNode =>
    getKindApi(kind)?.icon(kind) ?? <FileIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

  const addFrom: AddFromItem[] = [
    ...(project ? [{ id: 'files', label: 'Из файлов проекта', hint: 'выберите файл в «Файлах» и нажмите «В контекст»', run: () => { revealWorkspacePanel('files'); } }] : []),
    ...(project ? [{ id: 'characters', label: 'Из «Персонажей»', hint: 'ролью «персонаж»', run: () => { revealWorkspacePanel('characters'); } }] : []),
  ];

  return (
    <ContextPanel
      isMobile={isMobile}
      git={git}
      primary={primary}
      refs={refs}
      iconOf={iconOf}
      preview={primary && api ? api.preview(kindCtx, primary) : null}
      editor={primary ? api?.editor?.(kindCtx, primary) ?? null : null}
      step={primary ? api?.step?.(kindCtx, primary) ?? null : null}
      ret={ret}
      onReturn={() => {
        if (!ret) return;
        const { kind, ref } = ret.prev;
        clearContextReturn(sessionId);
        void setPrimary(sessionId, { kind, ref });
      }}
      action={action}
      exec={sel.executors}
      params={params}
      onParam={(p, v) => setParamValues(cur => ({ ...cur, [p.kind]: v }))}
      addFrom={addFrom}
      run={stubActionRun(action)}
      flash={flash}
      onRelease={() => { void releasePrimary(sessionId, true); }}
      onDetach={id => { void detachRef(sessionId, id); }}
      onClear={() => { void clearContext(sessionId); }}
      onClose={onClose}
      contained={contained}
      layout={layout}
    />
  );
}
