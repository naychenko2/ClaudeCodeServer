// Манифест MF-модуля «Видео» (ADR-022). Ключ совпадает с VideoEditorSubsystem.Key бэкенда: гейт слотов
// сверяется с активными подсистемами из /api/auth/me. Фич-флаг владельца (video-editor) проверяют сами
// входы — каждый вклад через isAvailable с getFlag(FLAGS.videoEditor).

import { Clapperboard } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ComposerChipCtx, ComposerStripCtx, ComposerStripShortcut, SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { VideoChatWatcher } from './composer/VideoChatWatcher';
import { VideoPanel } from './panel/VideoPanel';
import { VideoSheet } from './panel/VideoSheet';
import { openVideoShortcut } from './scene/actions';
import { VideoStrip, videoStripStatus } from './strip/VideoStrip';
import { VIDEO_PANEL, VIDEO_STRIP } from './store/videoStore';

const enabled = () => getFlag(FLAGS.videoEditor);

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
    // Полоса «Видео» над композером: выбор сцены открывает её сам (стор нитей)
    'composer-strip': [
      {
        name: VIDEO_STRIP, order: 26,
        render: (ctx: ComposerStripCtx) => <VideoStrip ctx={ctx} />,
        action: {
          title: 'Видео',
          icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          isAvailable: () => enabled(),
          status: ({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }) => videoStripStatus(projectId, sessionId),
          shortcuts: videoShortcuts,
        },
      },
    ],
    // Загрузка сцен чата и шторка панели на телефоне — вкладами, что живут при любой полосе
    'composer-chip': [
      { name: 'video-watch', render: (ctx: ComposerChipCtx) => <VideoChatWatcher ctx={ctx} /> },
      { name: 'video-sheet', render: (ctx: ComposerChipCtx) => <VideoSheet ctx={ctx} /> },
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
