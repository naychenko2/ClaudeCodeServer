// Чип пометок над полем ввода (слот composer-chip): «hero.png · 1 пометка ✕». Пометки
// из редактора уходят со следующим сообщением в любом режиме, ✕ — не прикладывать.
// Здесь же живёт попап «Редактор»: композер смонтирован всё время, пока открыт чат, а
// карточка в ленте может уехать из виду.

import { Brush } from 'lucide-react';
import { Chip, ICON_SIZE, ICON_STROKE, SP } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { EditorModal } from '../editor/EditorModal';
import { plural } from '../format';
import { threadName } from '../thread/model';
import { getEditor, getThreadMarks, openEditor, setThreadMarks, useThreads } from '../thread/threadStore';

export function ImageComposerChip({ ctx }: { ctx: ComposerChipCtx }) {
  const { projectId, sessionId } = ctx;
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const editor = getEditor();
  const n = thread ? getThreadMarks(thread.id).marks.length : 0;
  return (
    <>
      {thread && n > 0 && (
        <span style={{ display: 'inline-flex', paddingTop: SP.sm }}>
        <Chip leading={<Brush size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} maxW={260}
          title="Пометки уйдут со следующим сообщением. Нажмите, чтобы открыть редактор"
          onClick={() => { if (sessionId) openEditor(sessionId, thread.id); }}
          onRemove={() => setThreadMarks(thread.id, [], null)}>
          {threadName(thread)} · {n} {plural(n, 'пометка', 'пометки', 'пометок')}
        </Chip>
        </span>
      )}
      {projectId && sessionId && editor?.sessionId === sessionId && (
        <EditorModal projectId={projectId} sessionId={sessionId} threadId={editor.threadId} />
      )}
    </>
  );
}
