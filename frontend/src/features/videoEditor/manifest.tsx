// Манифест MF-модуля «Видео» (ADR-022). Ключ совпадает с VideoEditorSubsystem.Key бэкенда: гейт слотов
// сверяется с активными подсистемами из /api/auth/me. Фич-флаг владельца (video-editor) проверяют сами
// входы — каждый вклад через isAvailable с getFlag(FLAGS.videoEditor).

import { Clapperboard } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ChatItemToolCtx, ComposerChipCtx, SlotContribution, ComposerStripCtx, ComposerStripShortcut, SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { sceneMode } from './composer/sceneMode';
import { FramePickerHost } from './context/framePicker';
import { videoKindApi } from './context/kind';
import { VideoChatWatcher } from './composer/VideoChatWatcher';
import { LaunchAnchor, QuietLine, SceneAnchor } from './feed/SceneCard';
import { recordKey } from './feed/records';
import { RECORD } from './api';
import { VideoPanel } from './panel/VideoPanel';
import { VideoSheet } from './panel/VideoSheet';
import { openVideoShortcut } from './scene/actions';
import { VideoStrip, videoStripStatus } from './strip/VideoStrip';
import { VIDEO_PANEL, VIDEO_STRIP } from './store/videoStore';

const enabled = () => getFlag(FLAGS.videoEditor);

// Карточки ленты (module_record модуля): якорь сцены, запуск с вариантами, тихие строки.
// Флаг проверяют сами якоря: без него — строка fallback записи
const ANCHORS: SlotContribution<ChatItemToolCtx>[] = [
  { name: recordKey(RECORD.scene), render: ctx => <SceneAnchor ctx={ctx} /> },
  { name: recordKey(RECORD.launchVersions), render: ctx => <LaunchAnchor ctx={ctx} /> },
  ...[RECORD.saved, RECORD.filmBuilt, RECORD.note].map(t => ({ name: recordKey(t), render: (ctx: ChatItemToolCtx) => <QuietLine ctx={ctx} /> })),
];

// Ярлык «Видео» («＋» композера, пустая лента): полоса и панель на «Сцене»
export function videoShortcuts({ sessionId }: { sessionId: string | null }): ComposerStripShortcut[] {
  return [{
    key: VIDEO_STRIP, title: 'Видео', hint: 'сцена и фильм',
    icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onSelect: () => openVideoShortcut(sessionId),
  }];
}

export const manifest: SubsystemManifest = {
  key: 'videoeditor',
  title: 'Видео',
  order: 97,
  noPill: true,
  slots: {
    'chat-item-tool': ANCHORS,
    // Режим поля ввода «Сцена» — только при выбранной сцене
    'composer-mode': [
      { name: 'scene', order: 30, action: sceneMode as unknown as Record<string, unknown> },
    ],
    // Полоса «Видео» над композером: выбор сцены открывает её сам (стор нитей)
    'composer-strip': [
      {
        name: VIDEO_STRIP, order: 26,
        render: (ctx: ComposerStripCtx) => <VideoStrip ctx={ctx} />,
        action: {
          title: 'Видео',
          icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          // При флаге composer-context-row вход «Видео» — create вида контекста, полоса не нужна (без дублей)
          isAvailable: () => enabled() && !getFlag(FLAGS.composerContextRow),
          status: ({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }) => videoStripStatus(projectId, sessionId),
          shortcuts: videoShortcuts,
        },
      },
    ],
    // Вид «видео» контекста хода (ADR-023): чипы сцены и фильма, превью, «Чем» и параметры панели «Контекст»
    'context-kind': [{ name: 'video', action: videoKindApi as unknown as Record<string, unknown> }],
    // Загрузка сцен чата, шторка панели на телефоне и окно «Из проекта» меню кадра — вкладами, что живут при любой полосе
    'composer-chip': [
      { name: 'video-watch', render: (ctx: ComposerChipCtx) => <VideoChatWatcher ctx={ctx} /> },
      { name: 'video-sheet', render: (ctx: ComposerChipCtx) => <VideoSheet ctx={ctx} /> },
      { name: 'video-frame-picker', render: (ctx: ComposerChipCtx) => <FramePickerHost sessionId={ctx.sessionId} /> },
    ],
    // Панель «Видео»: «Сцена» и «Фильм» вкладками, в проекте и в правой колонке личного чата
    'workspace-panel-def': [
      {
        name: VIDEO_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <VideoPanel ctx={ctx} />,
        action: {
          title: 'Видео',
          icon: <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: () => enabled(),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
