// Пункты контекстного меню «Файлов» для контекста хода (ADR-023, 2к-2): «Работать с этой» (вертикаль
// превращает путь в объект — слот context-opener) и «В контекст» с ролью от вида основного объекта.
// Живёт при открытом чате и связи с сервером; без них пунктов нет, меню прежнее.
import type { ReactNode } from 'react';
import { Check, Target } from 'lucide-react';
import { MenuItem } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { useActiveChatForContext } from '../../lib/chatContext';
import { refOf, rolesFor } from '../../lib/chatContext/fill';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import { attachRef, detachRef, setPrimary, useChatContext } from '../../lib/chatContext/store';
import type { ChatContextDto } from '../../lib/chatContext/types';
import { revealContextPanel, type ContextOpenerApi } from '../../lib/subsystems/registryCore';
import { useSlot } from '../../lib/subsystems/registryCore';
import { showToast } from '../../lib/toast';
import { NO_PRIMARY_REASON, NO_ROLE_REASON } from '../../components/generation/ContextAddButton';

const ic = (I: typeof Check) => <I size={15} strokeWidth={ICON_STROKE} />;

export const PROJECT_FILE_KIND = 'project-file';

const AUDIO_FILE = /\.(wav|mp3|flac|ogg|m4a)$/i;

// Звуковой файл берёт только основной звук, остальные файлы — не звук: референс другого вида бэкенд принял бы
// (kind у обоих «project-file»), но смысла в нём нет — «образец стиля» из .wav
const takesFile = (primaryKind: string, path: string) => AUDIO_FILE.test(path) === (primaryKind === 'audio');

export function useFileContextMenu(projectId: string, path: string | null, online: boolean, close: () => void): { on: boolean; items: ReactNode[] } {
  const chat = useActiveChatForContext();
  // Вход в контекст у каждой вертикали свой (картинка, звук): берём того, кто принимает этот файл
  const openers = useSlot<never, ContextOpenerApi>('context-opener');
  const opener = path ? openers.map(o => o.action).find(a => a?.isOpenable(path)) : undefined;
  const state = useChatContext(chat ? chat.sessionId : null);
  const on = !!chat && online;
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
      <MenuItem key="ctx-work" icon={ic(Target)} label="Работать с этой" hint="станет основным объектом в контексте хода"
        onClick={() => {
          close();
          void opener.toRef({ projectId, sessionId, path }).then(async r => {
            if (!r) return;
            if (await setPrimary(sessionId, r) !== 'failed') revealContextPanel(sessionId);
          }).catch((e: Error) => showToast('Не удалось взять файл в работу', e.message, 'error'));
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

  const roles = state.primary && !takesFile(state.primary.kind, path) ? [] : rolesFor({ projectId, sessionId, isMobile: false }, state, PROJECT_FILE_KIND);
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
