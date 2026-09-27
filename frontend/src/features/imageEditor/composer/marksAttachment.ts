// Пометки уходят агенту и в режиме «Чат» (записка v3, «Композер»): перед отправкой
// сообщения чип «hero.png · N пометок» превращается во вложение — снимок картинки с
// пометками. ✕ на чипе снимает пометки, и тогда прикладывать нечего.

import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { exportChatSnapshot } from '../marks';
import { threadName } from '../thread/model';
import { getFocusedThread, getThreadMarks, setThreadMarks } from '../thread/threadStore';
import { imageSrc, loadImage } from '../thread/useThreadLaunch';

export async function takeMarksAttachment(ctx: ComposerChipCtx): Promise<File[]> {
  const t = getFocusedThread(ctx.sessionId);
  if (!t || !ctx.projectId) return [];
  const { marks, size } = getThreadMarks(t.id);
  const src = imageSrc(ctx.projectId, t, t.currentStepId);
  if (!marks.length || !size || !src) return [];
  const img = await loadImage(src);
  const blob = await exportChatSnapshot(img, marks, size.w, size.h);
  if (!blob) return [];
  // Пометки ушли с сообщением — чип гаснет, как после запуска в режиме «Картинка»
  setThreadMarks(t.id, [], null);
  const stem = threadName(t).replace(/\.\w+$/, '');
  return [new File([blob], `${stem}-пометки.png`, { type: 'image/png' })];
}
