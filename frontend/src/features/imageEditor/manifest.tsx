// Манифест MF-модуля «Редактор картинок» (ADR-018 §10.3). Ключ совпадает с
// ImageEditorSubsystem.Key бэкенда: гейт слотов сверяется с активными подсистемами
// из /api/auth/me. Фич-флаг владельца (image-editor) проверяют сами входы.

import type {
  SubsystemManifest, AppOverlayCtx, FileViewerToolbarCtx,
} from '../../lib/subsystems/registryCore';
import { ImageEditorEntryButton, ImageEditorHost, openImageEditor } from './ImageEditorEntry';
import { isEditableImage } from './format';

export const manifest: SubsystemManifest = {
  key: 'imageeditor',
  title: 'Редактор картинок',
  order: 95,
  noPill: true,
  slots: {
    // Чат картинки из контекста (ctx.ImageChat) редактор начнёт рисовать в шаге 14
    'app-overlay': [
      { name: 'image-editor', render: (_ctx: AppOverlayCtx) => <ImageEditorHost /> },
    ],
    // ImageEditorOpenerApi: вход из дерева файлов
    'image-editor': [
      { name: 'opener', action: { isEditable: isEditableImage, open: openImageEditor } },
    ],
    'file-viewer-toolbar': [
      {
        name: 'image-editor', order: 10,
        render: (ctx: FileViewerToolbarCtx) => (
          <ImageEditorEntryButton projectId={ctx.projectId} projectName={ctx.projectName}
            target={{ kind: 'edit', path: ctx.filePath }}
            onShowInFiles={ctx.onOpenFile} />
        ),
      },
    ],
  },
};
