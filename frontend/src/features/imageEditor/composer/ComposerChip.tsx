// Вклад composer-chip: монтируется один раз на чат, пока он открыт, и держит попап «Редактор»
// (карточка в ленте может уехать из виду) и подгружает нити чата в стор. Пометки показывает строка контекста («С чем» и чип
// «Изменить отмеченное»), поэтому отдельного чипа пометок здесь нет; их отправку агенту
// держит beforeSend вклада (composer/marksAttachment).

import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { EditorModal } from '../editor/EditorModal';
import { enterScope } from '../scope';
import { getEditor, useThreads } from '../thread/threadStore';

export function ImageComposerChip({ ctx }: { ctx: ComposerChipCtx }) {
  const { sessionId } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  // Подписка на нити чата: монтирование вклада заодно загружает их в стор
  useThreads(projectId, sessionId);
  const editor = getEditor();
  if (!sessionId || editor?.sessionId !== sessionId) return null;
  return <EditorModal projectId={projectId} sessionId={sessionId} threadId={editor.threadId} versionId={editor.versionId} startTool={editor.tool} />;
}
