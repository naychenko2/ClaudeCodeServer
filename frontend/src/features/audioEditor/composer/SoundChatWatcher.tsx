// Невидимый вклад composer-chip: рисуется в каждом чате, пока полоса «Звук» может быть и не
// показана, — грузит нити чата и при входе заново просит полосу по серверному фокусу (как
// чип картинок) и держит попап «Редактор». Без него выбранный звук после перезагрузки не вернул бы свою полосу.

import { useEffect } from 'react';
import { FLAGS, useFeature } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { audioScope } from '../scope';
import { AudioEditorModal } from '../editor/EditorModal';
import { enterChat, getEditor, useAudioThreads } from '../thread/threadStore';

function Watch({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useAudioThreads(audioScope(projectId), sessionId);
  useEffect(() => { enterChat(sessionId); }, [sessionId]);
  // Редактор звука живёт здесь же: композер смонтирован, пока открыт чат (как у попапа картинок)
  const editor = getEditor();
  return editor?.sessionId === sessionId
    ? <AudioEditorModal projectId={projectId} sessionId={sessionId} threadId={editor.threadId} versionId={editor.versionId} />
    : null;
}

export function SoundChatWatcher({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.audioEditor);
  return on && ctx.sessionId ? <Watch projectId={ctx.projectId} sessionId={ctx.sessionId} /> : null;
}
