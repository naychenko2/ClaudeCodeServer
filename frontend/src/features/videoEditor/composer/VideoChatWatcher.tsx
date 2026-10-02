// Невидимый вклад composer-chip: рисуется в каждом чате, пока полоса «Видео» может быть и не показана, —
// грузит сцены чата, при входе заново просит полосу по серверному фокусу и ловит клик по .film в дереве.

import { useEffect } from 'react';
import { FLAGS, useFeature } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { watchFilmFiles } from '../film/filmFileWatch';
import { videoScope } from '../scope';
import { enterChat, useVideoThreads } from '../store/videoStore';

function Watch({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useVideoThreads(videoScope(projectId), sessionId);
  useEffect(() => { enterChat(sessionId); }, [sessionId]);
  // Клик по .film в дереве «Файлов» открывает вкладку «Фильм»; у личного чата дерева нет
  useEffect(() => (projectId ? watchFilmFiles(sessionId) : undefined), [projectId, sessionId]);
  return null;
}

export function VideoChatWatcher({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  return on && ctx.sessionId ? <Watch projectId={ctx.projectId} sessionId={ctx.sessionId} /> : null;
}
