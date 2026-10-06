// Невидимый вклад composer-chip: рисуется в каждом чате — грузит нити чата и держит попап «Редактор»
// (композер смонтирован, пока открыт чат).

import { FLAGS, useFeature } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { audioScope } from '../scope';
import { AudioEditorModal } from '../editor/EditorModal';
import { getEditor, useAudioThreads } from '../thread/threadStore';

function Watch({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useAudioThreads(audioScope(projectId), sessionId);
  // Редактор звука живёт здесь же (как попап картинок)
  const editor = getEditor();
  return editor?.sessionId === sessionId
    ? <AudioEditorModal projectId={projectId} sessionId={sessionId} threadId={editor.threadId} versionId={editor.versionId} />
    : null;
}

export function SoundChatWatcher({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.audioEditor);
  return on && ctx.sessionId ? <Watch projectId={ctx.projectId} sessionId={ctx.sessionId} /> : null;
}
