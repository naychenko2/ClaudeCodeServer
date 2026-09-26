// Манифест MF-модуля «Редактор картинок» (ADR-018 §10.3). Ключ совпадает с
// ImageEditorSubsystem.Key бэкенда: гейт слотов сверяется с активными подсистемами
// из /api/auth/me. Фич-флаг владельца (image-editor) проверяют сами входы.

import type {
  SubsystemManifest, AppOverlayCtx, FileViewerToolbarCtx, ChatItemToolCtx, ChatCardBadgeCtx,
} from '../../lib/subsystems/registryCore';
import { ImageEditorEntryButton, ImageEditorHost, openImageEditor } from './ImageEditorEntry';
import { isEditableImage } from './format';
import { ImageFileMovedRow, ImageLaunchCard, ImageLaunchRow, ImagePromptCard } from './chat/cards';
import { ImageChatCardBadge } from './chat/ChatCardBadge';
import { openImageChat } from './chat/openFromChat';

// Имена инструментов MCP-сервера image-editor (ImageEditorToolset.Schemas.cs)
const TOOL = (name: string) => `mcp__image-editor__${name}`;

export const manifest: SubsystemManifest = {
  key: 'imageeditor',
  title: 'Редактор картинок',
  order: 95,
  noPill: true,
  slots: {
    // Чат картинки — компонент ядра из контекста: своей копии ChatPanel в модуле нет
    'app-overlay': [
      { name: 'image-editor', render: (ctx: AppOverlayCtx) => <ImageEditorHost ImageChat={ctx.ImageChat} /> },
    ],
    // ImageEditorOpenerApi: вход из дерева файлов
    'image-editor': [
      { name: 'opener', action: { isEditable: isEditableImage, open: openImageEditor } },
    ],
    // Карточки ленты чата картинки: ключ — имя инструмента или kind записи
    'chat-item-tool': [
      { name: TOOL('image_generate'), render: (ctx: ChatItemToolCtx) => <ImageLaunchCard ctx={ctx} /> },
      { name: TOOL('image_suggest_prompt'), render: (ctx: ChatItemToolCtx) => <ImagePromptCard ctx={ctx} /> },
      { name: 'image_launch', render: (ctx: ChatItemToolCtx) => <ImageLaunchRow ctx={ctx} /> },
      { name: 'image_file_moved', render: (ctx: ChatItemToolCtx) => <ImageFileMovedRow ctx={ctx} /> },
    ],
    // Значок и миниатюра в списке чатов; клик по чату картинки открывает редактор
    'chat-card-badge': [
      {
        name: 'image-chat',
        render: (ctx: ChatCardBadgeCtx) => <ImageChatCardBadge ctx={ctx} />,
        action: { open: openImageChat },
      },
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
