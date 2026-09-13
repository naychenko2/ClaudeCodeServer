import { useEffect, useState } from 'react';
import { NotebookPen } from 'lucide-react';
import { api, bumpNotes, beginAiBusy, endAiBusy, showToast, useSubsystem, MenuItem } from 'aihome_shell/kit';
import { openNoteById } from './saveToNote';
import type { ChatHeaderSummaryCtx, ChatHeaderMenuItemCtx } from '../../lib/subsystems/registryCore';

// «Итог сессии в заметку» запускается ТОЛЬКО через AI-палитру (действие chat.summary).
// Компонент невидим, но остаётся смонтированным ради слушателя cc-ai-run; при успехе
// открывает созданную заметку. Вынесен из ChatHeaderBar: каркас шапки рисует вклад
// слота chat-header-action, а не импортирует фичу.

export function SessionSummaryAction({ session, hasMessages, online }: ChatHeaderSummaryCtx) {
  const notesOn = useSubsystem('notes');
  const [busy, setBusy] = useState(false);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс busy при смене чата
  useEffect(() => { setBusy(false); }, [session.id]);
  const run = () => {
    if (busy) return;
    setBusy(true);
    beginAiBusy();
    api.sessions.summary(session.id)
      .then(n => { bumpNotes(); openNoteById(n.id); })
      .catch(() => showToast('Итог сессии', 'Не удалось составить итог (claude не залогинен?)', 'info'))
      .finally(() => { setBusy(false); endAiBusy(); });
  };
  useEffect(() => {
    if (!notesOn || !online || !hasMessages) return;
    const onRun = (e: Event) => { if ((e as CustomEvent<{ action?: string }>).detail?.action === 'chat.summary') run(); };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notesOn, online, session.id, hasMessages, busy]);
  return null;
}

// Пункт контекстного меню шапки «Итог сессии в заметку». ctx.run выполняет запуск
// (каркас заворачивает в закрытие меню).
export function ChatSummaryMenuItem({ run }: ChatHeaderMenuItemCtx) {
  return (
    <MenuItem
      icon={<NotebookPen size={15} strokeWidth={2} />}
      label="Итог сессии в заметку"
      onClick={run}
    />
  );
}
