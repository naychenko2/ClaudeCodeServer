// Пункты контекстного меню «Файлов» для контекста хода (ADR-023, 2к-2): «Работать с этой» (вертикаль
// превращает путь в объект — слот context-opener) и «В контекст» с ролью от вида основного объекта.
// Живёт при флаге composer-context-row и открытом чате; без них пунктов нет, меню прежнее.
import type { ReactNode } from 'react';
import { Check, Target } from 'lucide-react';
import { MenuItem } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { useActiveChatForContext } from '../../lib/chatContext';
import { refOf, rolesFor } from '../../lib/chatContext/fill';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import { attachRef, detachRef, setPrimary, useChatContext } from '../../lib/chatContext/store';
import type { ChatContextDto } from '../../lib/chatContext/types';
import { revealContextPanel, type ContextOpenerApi } from '../../lib/subsystems/registryCore';
import { useSlotItem } from '../../lib/subsystems/registry';
import { showToast } from '../../lib/toast';
import { NO_PRIMARY_REASON, NO_ROLE_REASON } from '../../components/generation/ContextAddButton';

const ic = (I: typeof Check) => <I size={15} strokeWidth={ICON_STROKE} />;

export const PROJECT_FILE_KIND = 'project-file';

export function useFileContextMenu(projectId: string, path: string | null, online: boolean, close: () => void): { on: boolean; items: ReactNode[] } {
  const flag = useFeature(FLAGS.composerContextRow);
  const chat = useActiveChatForContext();
  const opener = useSlotItem<never, ContextOpenerApi>('context-opener', 'image')?.action;
  const state = useChatContext(flag && chat ? chat.sessionId : null);
  const on = flag && !!chat && online;
  if (!on || !chat || !path) return { on, items: [] };
  return { on, items: fileContextItems({ projectId, sessionId: chat.sessionId, path, state, opener, close }) };
}

// Пункты по состоянию контекста: чистая сборка, чтобы тест не зависел от подписок хуков
export function fileContextItems({ projectId, sessionId, path, state, opener, close }: {
  projectId: string; sessionId: string; path: string; state: ChatContextDto; opener: ContextOpenerApi | undefined; close: () => void;
}): ReactNode[] {
  const candidate = { kind: PROJECT_FILE_KIND, ref: { path } };
  const items: ReactNode[] = [];

  if (opener?.isOpenable(path)) {
    items.push(
      <MenuItem key="ctx-work" icon={ic(Target)} label="Работать с этой" hint="картинка станет основной в контексте хода"
        onClick={() => {
          close();
          void opener.toRef({ projectId, sessionId, path }).then(async r => {
            if (!r) return;
            if (await setPrimary(sessionId, r) !== 'failed') revealContextPanel(sessionId);
          }).catch((e: Error) => showToast('Не удалось взять картинку в работу', e.message, 'error'));
        }} />,
    );
  }

  const inCtx = refOf(state, candidate);
  if (inCtx) {
    const role = roleLabel(inCtx.role);
    items.push(
      <MenuItem key="ctx-in" icon={ic(Check)} label={`В контексте${role ? ` · ${role}` : ''}`} hint="нажмите, чтобы убрать"
        onClick={() => { close(); void detachRef(sessionId, inCtx.id); }} />,
    );
    return items;
  }

  const roles = rolesFor({ projectId, sessionId, isMobile: false }, state, PROJECT_FILE_KIND);
  if (!state.primary || roles.length === 0) {
    items.push(<MenuItem key="ctx-add" label="В контекст" disabled hint={state.primary ? NO_ROLE_REASON : NO_PRIMARY_REASON} hintWrap />);
  } else if (roles.length === 1) {
    items.push(<MenuItem key="ctx-add" label="В контекст" onClick={() => { close(); void attachRef(sessionId, { ...candidate, role: roles[0].role }); }} />);
  } else {
    roles.forEach(r => items.push(
      <MenuItem key={`ctx-add-${r.role}`} label={`В контекст · ${r.label.toLowerCase()}`}
        onClick={() => { close(); void attachRef(sessionId, { ...candidate, role: r.role }); }} />,
    ));
  }
  return items;
}
