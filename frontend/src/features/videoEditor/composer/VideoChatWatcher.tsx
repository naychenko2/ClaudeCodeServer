// Невидимый вклад composer-chip: рисуется в каждом чате, пока полоса «Видео» может быть и не показана, —
// грузит сцены чата и при входе заново просит полосу по серверному фокусу.

import { useEffect } from 'react';
import { FLAGS, useFeature } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { videoScope } from '../scope';
import { enterChat, useVideoThreads } from '../store/videoStore';

function Watch({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useVideoThreads(videoScope(projectId), sessionId);
  useEffect(() => { enterChat(sessionId); }, [sessionId]);
  return null;
}

export function VideoChatWatcher({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  return on && ctx.sessionId ? <Watch projectId={ctx.projectId} sessionId={ctx.sessionId} /> : null;
}
