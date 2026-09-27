// Манифест MF-модуля «Редактор картинок» (ADR-018 §10.3, ADR-019). Ключ совпадает с
// ImageEditorSubsystem.Key бэкенда: гейт слотов сверяется с активными подсистемами
// из /api/auth/me. Фич-флаг владельца (image-editor) проверяют сами входы.

import { Contact, Image as ImageIcon } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  SubsystemManifest, FileViewerToolbarCtx, ChatItemToolCtx, ComposerChipApi, ComposerChipCtx, ComposerStripCtx,
  WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { isEditableImage } from './format';
import { ImageFileMovedRow, ImageLaunchCard, ImageLaunchRow, ImagePromptCard } from './chat/cards';
import { CharactersPanel } from './characters/CharactersPanel';
import { CHARACTERS_PANEL } from './characters/panel';
import { EditImageButton } from './entry/EditImageButton';
import { openFromTree } from './entry/openFromTree';
import { ImageComposerChip } from './composer/ComposerChip';
import { imageMode } from './composer/imageMode';
import { takeMarksAttachment } from './composer/marksAttachment';
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
    // ImageEditorOpenerApi: вход из дерева файлов — последний активный чат проекта
    'image-editor': [
      { name: 'opener', action: { isEditable: isEditableImage, open: openFromTree } },
    ],
    // Карточки ленты: ключ — имя инструмента или kind записи (image_launch и
    // image_file_moved — история архивных чатов картинки v2)
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
      {
        name: 'image-marks',
        render: (ctx: ComposerChipCtx) => <ImageComposerChip ctx={ctx} />,
        // Режим «Чат»: пометки уходят агенту снимком-вложением со следующим сообщением
        action: { beforeSend: takeMarksAttachment } satisfies ComposerChipApi as unknown as Record<string, unknown>,
      },
    ],
    // Панель «Персонажи» рабочей области проекта
    'workspace-panel-def': [
      {
        name: CHARACTERS_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <CharactersPanel projectId={ctx.projectId} />,
        action: {
          title: 'Персонажи',
          icon: <Contact size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: () => getFlag(FLAGS.imageEditor),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
    'file-viewer-toolbar': [
      {
        name: 'image-editor', order: 10,
        render: (ctx: FileViewerToolbarCtx) => (
          <EditImageButton projectId={ctx.projectId} projectName={ctx.projectName} path={ctx.filePath} />
        ),
      },
    ],
  },
};
