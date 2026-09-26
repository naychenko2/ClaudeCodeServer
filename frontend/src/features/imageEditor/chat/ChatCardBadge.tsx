// Чат картинки в списке чатов (макет image-editor-v2, «Экран проекта»): миниатюра текущего
// файла и метка «картинка» в строке названия. Клик по карточке открывает редактор с этим
// чатом. Флаг выключен — карточка обычная.

import { api as appApi, C, FLAGS, FS, R, useFeature } from 'aihome_shell/kit';
import type { ChatCardBadgeCtx } from '../../../lib/subsystems/registryCore';

export function ImageChatCardBadge({ ctx }: { ctx: ChatCardBadgeCtx }) {
  const enabled = useFeature(FLAGS.imageEditor);
  const { session } = ctx;
  const path = session.imageChat?.currentPath;
  // Черновик «Нарисовать картинку»: миниатюры ещё нет, метка — есть
  const draft = !path && session.imageChat?.draftFolder != null;
  if (!enabled || (!path && !draft) || !session.projectId) return null;
  return (
    <>
      {path && (
        <img data-image-chat-thumb="" src={appApi.files.fileUrl(session.projectId, path)} alt="" loading="lazy"
          style={{ width: 18, height: 18, borderRadius: R.sm, objectFit: 'cover', flexShrink: 0, background: C.bgInset }} />
      )}
      <span data-image-chat-tag="" title={path ? `Чат картинки ${path}: откроется в редакторе` : 'Новая картинка: откроется в редакторе'} style={{
        flexShrink: 0, fontSize: FS.xs, lineHeight: 1, padding: '2px 6px', borderRadius: R.sm,
        background: C.accentLight, color: C.accent, whiteSpace: 'nowrap',
      }}>
        картинка
      </span>
    </>
  );
}
