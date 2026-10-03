// Невидимый вклад composer-chip: окна редакторов «Сцена» и «Монтаж» живут здесь (композер смонтирован,
// пока открыт чат), как редактор звука. Окно принадлежит чату, в котором его открыли.

import { FLAGS, useFeature } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { getVideoEditor, useVideoStoreVersion } from '../store/videoStore';
import { MontageEditor } from './MontageEditor';
import { SceneEditor } from './SceneEditor';

export function VideoEditorHost({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  useVideoStoreVersion();
  const open = getVideoEditor();
  if (!on || !ctx.sessionId || open?.sessionId !== ctx.sessionId) return null;
  return open.kind === 'film'
    ? <MontageEditor key="film" projectId={ctx.projectId} sessionId={ctx.sessionId} />
    : <SceneEditor key="scene" projectId={ctx.projectId} sessionId={ctx.sessionId} />;
}
