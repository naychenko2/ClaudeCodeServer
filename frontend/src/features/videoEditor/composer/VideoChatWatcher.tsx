// Невидимый вклад composer-chip: рисуется в каждом чате — грузит сцены чата и ловит клик по .film в дереве.

import { useEffect } from 'react';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { watchFilmFiles } from '../film/filmFileWatch';
import { videoScope } from '../scope';
import { useVideoThreads } from '../store/videoStore';

function Watch({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useVideoThreads(videoScope(projectId), sessionId);
  // Клик по .film в дереве «Файлов» показывает фильм в панели «Контекст»; у личного чата дерева нет
  useEffect(() => (projectId ? watchFilmFiles(sessionId) : undefined), [projectId, sessionId]);
  return null;
}

export function VideoChatWatcher({ ctx }: { ctx: ComposerChipCtx }) {
  return ctx.sessionId ? <Watch projectId={ctx.projectId} sessionId={ctx.sessionId} /> : null;
}
