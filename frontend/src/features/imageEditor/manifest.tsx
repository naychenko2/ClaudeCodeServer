// Манифест MF-модуля «Редактор картинок» (ADR-018 §10.3). Ключ совпадает с
// ImageEditorSubsystem.Key бэкенда: гейт слотов сверяется с активными подсистемами
// из /api/auth/me. Фич-флаг владельца (image-editor) проверяют сами входы.

import { Image as ImageIcon } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  SubsystemManifest, AppOverlayCtx, FileViewerToolbarCtx, ChatItemToolCtx, ChatCardBadgeCtx,
  ComposerChipCtx, ComposerStripCtx,
} from '../../lib/subsystems/registryCore';
import { ImageEditorEntryButton, ImageEditorHost, openImageEditor } from './ImageEditorEntry';
import { isEditableImage } from './format';
import { ImageFileMovedRow, ImageLaunchCard, ImageLaunchRow, ImagePromptCard } from './chat/cards';
import { ImageChatCardBadge } from './chat/ChatCardBadge';
import { openImageChat } from './chat/openFromChat';
import { ImageComposerChip } from './composer/ComposerChip';
import { imageMode } from './composer/imageMode';
import { ImagesStrip, imagesStripStatus } from './strip/ImagesStrip';
import { ThreadAnchor } from './thread/ThreadCard';
import { recordKey, ThreadSysLine } from './thread/records';
import { IMAGES_STRIP } from './thread/threadStore';

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
      // Нить основного чата (ADR-019 §3): якорь карточки-стопки и тихие строки module_record
      { name: recordKey('image_thread'), render: (ctx: ChatItemToolCtx) => <ThreadAnchor ctx={ctx} /> },
      ...['image_launch', 'image_saved', 'image_stack_forked', 'image_focus'].map(t => (
        { name: recordKey(t), render: (ctx: ChatItemToolCtx) => <ThreadSysLine ctx={ctx} /> }
      )),
    ],
    // Полоса «Картинки» над композером: выбор картинки открывает её сам (стор нитей)
    'composer-strip': [
      {
        name: IMAGES_STRIP, order: 20,
        render: (ctx: ComposerStripCtx) => <ImagesStrip ctx={ctx} />,
        action: {
          title: 'Картинки',
          icon: <ImageIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          isAvailable: () => getFlag(FLAGS.imageEditor),
          status: ({ sessionId }: { sessionId: string | null }) => imagesStripStatus(sessionId),
        },
      },
    ],
    // Режим поля ввода «Картинка» — только при выбранной картинке
    'composer-mode': [{ name: 'image', order: 10, action: imageMode as unknown as Record<string, unknown> }],
    // Чип пометок и попап «Редактор»
    'composer-chip': [
      { name: 'image-marks', render: (ctx: ComposerChipCtx) => <ImageComposerChip ctx={ctx} /> },
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
