// Манифест MF-модуля «Редактор картинок» (ADR-018 §10.3, ADR-019). Ключ совпадает с
// ImageEditorSubsystem.Key бэкенда: гейт слотов сверяется с активными подсистемами
// из /api/auth/me. Фич-флаг владельца (image-editor) проверяют сами входы.

import { Contact, Image as ImageIcon } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ContextOpenerApi, SubsystemManifest, FileViewerToolbarCtx, ChatItemToolCtx, ComposerChipApi, ComposerChipCtx, ComposerStripCtx,
  WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { isEditableImage } from './format';
import { ImageFileMovedRow, ImageLaunchCard, ImageLaunchRow, ImagePromptCard } from './chat/cards';
import { IMAGES_PANEL } from './characters/panel';
import { CHARACTERS_PANEL, CharactersContextPanel } from './characters/CharactersContextPanel';
import { EditImageButton } from './entry/EditImageButton';
import { openFromTree } from './entry/openFromTree';
import { ImageComposerChip } from './composer/ComposerChip';
import { ImagesPanel } from './panel/ImagesPanel';
import { IMAGE_COMPOSER_MODE, imageMode } from './composer/imageMode';
import { imageKindApi } from './context/kind';
import { imageRefOfPath } from './context/opener';
import { takeMarksAttachment } from './composer/marksAttachment';
import { ImagesStrip, imagesStripStatus } from './strip/ImagesStrip';
import { ThreadAnchor } from './thread/ThreadCard';
import { LaunchAnchor } from './thread/VersionCards';
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
    // Вход из «Файлов» в контекст хода (ADR-023): картинка проекта становится нитью-основным объектом
    'context-opener': [
      {
        name: 'image',
        action: {
          isOpenable: isEditableImage,
          toRef: ({ projectId, sessionId, path }) => imageRefOfPath(projectId, sessionId, path),
        } satisfies ContextOpenerApi as unknown as Record<string, unknown>,
      },
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
      // Запуск ИИ в нить (изменение 27.09): строка запуска и карточка на каждый вариант
      { name: recordKey('image_launch_versions'), render: (ctx: ChatItemToolCtx) => <LaunchAnchor ctx={ctx} /> },
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
          // При флаге composer-context-row вход «Картинка» — create вида контекста, полоса не нужна (без дублей)
          isAvailable: () => getFlag(FLAGS.imageEditor) && !getFlag(FLAGS.composerContextRow),
          status: ({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }) => imagesStripStatus(projectId, sessionId),
        },
      },
    ],
    // Режим поля ввода «Картинка» — только при выбранной картинке
    'composer-mode': [{ name: IMAGE_COMPOSER_MODE, order: 10, action: imageMode as unknown as Record<string, unknown> }],
    // Вид «картинка» контекста хода (ADR-023): чипы действий, превью, «Чем» и параметры панели «Контекст»
    'context-kind': [{ name: 'image', action: imageKindApi as unknown as Record<string, unknown> }],
    // Чип пометок и попап «Редактор»
    'composer-chip': [
      {
        name: 'image-marks',
        render: (ctx: ComposerChipCtx) => <ImageComposerChip ctx={ctx} />,
        // Режим «Чат»: пометки уходят агенту снимком-вложением со следующим сообщением
        action: { beforeSend: takeMarksAttachment } satisfies ComposerChipApi as unknown as Record<string, unknown>,
      },
    ],
    // Панель «Картинки»: настройки и персонажи вкладками, в проекте и в правой колонке личного чата
    'workspace-panel-def': [
      {
        name: IMAGES_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <ImagesPanel ctx={ctx} />,
        action: {
          title: 'Картинки',
          icon: <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: () => getFlag(FLAGS.imageEditor),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
      // «Персонажи» отдельной панелью (ADR-023 §Д1, 2к-2): только при флаге composer-context-row — без него
      // персонажи остаются вкладкой «Картинок»
      {
        name: CHARACTERS_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <CharactersContextPanel ctx={ctx} />,
        action: {
          title: 'Персонажи',
          icon: <Contact size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: () => getFlag(FLAGS.imageEditor) && getFlag(FLAGS.composerContextRow),
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
