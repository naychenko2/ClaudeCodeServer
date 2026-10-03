// Связка панели «Контекст» с живыми данными (ADR-023 §Д1): стор контекста чата, вид из слота
// context-kind, git-чип, ссылка «назад». Саму отрисовку держит ContextPanel (по готовой модели),
// чтобы витрина кормила её фикстурами без стора и сети. Хост зовут обе страницы — проект и «Чаты».
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { File as FileIcon } from 'lucide-react';
import type { Project, Session } from '../../types';
import { useGitChip } from '../../hooks/useGitChip';
import { useActionMemoryVersion } from '../../lib/chatContext/actionMemory';
import { useActionRun } from '../../lib/chatContext/useActionRun';
import { clearContextReturn, useContextReturn } from '../../lib/chatContext/contextReturn';
import { getGitActions } from '../../lib/chatContext/gitActions';
import { getKindApi } from '../../lib/chatContext/registry';
import { selectRowAction } from '../../lib/chatContext/rowExec';
import {
  attachRef, clearContext, detachRef, ensureChatContext, releasePrimary, setPrimary, useChatContext,
} from '../../lib/chatContext/store';
import type { ContextKindCtx } from '../../lib/chatContext/types';
import { REVEAL_PANEL_EVENT, revealWorkspacePanel, type RevealPanelDetail } from '../../lib/subsystems/registryCore';
import { wsPanels } from '../../pages/workspace/panelStackState';
import { showToast } from '../../lib/toast';
import { PublishDialog } from '../PublishDialog';
import { Menu, MenuItem } from '../ui';
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
  useActionMemoryVersion();
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
  // executors() вида зовётся на каждый рендер намеренно (контракт types.ts): выбранный исполнитель живёт
  // в сторе вертикали и в зависимости мемо не входит
  const sel = primary ? selectRowAction(api, kindCtx, primary, refs) : { action: null, executors: null };
  const action = sel.action;
  // Подпись, цена, состояние и запуск — те же, что у кнопки поля ввода: один хук на обоих
  const run = useActionRun(sessionId, kindCtx);

  const iconOf = (kind: string): ReactNode =>
    getKindApi(kind)?.icon(kind) ?? <FileIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

  // «С компьютера»: файл → вид кладёт его в свою рабочую папку → референс; роль спрашиваем, если их несколько
  const upload = primary && api?.upload ? api.upload(kindCtx, primary) : null;
  const fileInput = useRef<HTMLInputElement>(null);
  const menuAt = useRef<DOMRect | null>(null);
  const [rolePick, setRolePick] = useState<{ files: File[]; at: DOMRect } | null>(null);
  const addFiles = async (files: File[], role: string) => {
    if (!upload) return;
    for (const f of files) {
      try {
        await attachRef(sessionId, { kind: upload.kind, ref: await upload.send(f), role });
      } catch (e) {
        showToast((e as Error).message || 'Не удалось загрузить файл', '', 'error');
      }
    }
  };

  const addFrom: AddFromItem[] = [
    ...(upload ? [{ id: 'computer', label: 'С компьютера', hint: upload.hint, run: (at: DOMRect | null) => { menuAt.current = at; fileInput.current?.click(); } }] : []),
    ...(project ? [{ id: 'files', label: 'Из файлов проекта', hint: 'выберите файл в «Файлах» и нажмите «В контекст»', run: () => { revealWorkspacePanel('files'); } }] : []),
    ...(project ? [{ id: 'characters', label: 'Из «Персонажей»', hint: 'ролью «персонаж»', run: () => { revealWorkspacePanel('characters'); } }] : []),
  ];

  return (
    <>
    {upload && (
      <input ref={fileInput} type="file" accept={upload.accept} multiple hidden data-ctx-upload=""
        onChange={e => {
          const files = [...(e.target.files ?? [])];
          e.target.value = '';
          if (!files.length) return;
          if (upload.roles.length === 1) void addFiles(files, upload.roles[0].role);
          else setRolePick({ files, at: menuAt.current ?? new DOMRect(window.innerWidth / 2, window.innerHeight / 2, 0, 0) });
        }} />
    )}
    {rolePick && upload && (
      <Menu anchor={rolePick.at} onClose={() => setRolePick(null)} minWidth={220}>
        {upload.roles.map(r => (
          <MenuItem key={r.role} label={r.label} onClick={() => { const { files } = rolePick; setRolePick(null); void addFiles(files, r.role); }} />
        ))}
      </Menu>
    )}
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
      params={run.params}
      onParam={(p, v) => run.setParam(p.kind, v)}
      addFrom={addFrom}
      run={run}
      flash={flash}
      onRelease={() => { void releasePrimary(sessionId, true); }}
      onDetach={id => { void detachRef(sessionId, id); }}
      onClear={() => { void clearContext(sessionId); }}
      onClose={onClose}
      contained={contained}
      layout={layout}
    />
    </>
  );
}
