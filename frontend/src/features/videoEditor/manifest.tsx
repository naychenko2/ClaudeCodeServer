// Манифест MF-модуля «Видео» (ADR-022). Ключ совпадает с VideoEditorSubsystem.Key бэкенда: гейт слотов
// сверяется с активными подсистемами из /api/auth/me. Фич-флаг владельца (video-editor) проверяют сами
// входы — каждый вклад через isAvailable с getFlag(FLAGS.videoEditor).

import { FLAGS, getFlag } from '../../lib/featureFlags';
import type { ChatItemToolCtx, ComposerChipCtx, ContextOpenerApi, SlotContribution, SubsystemManifest } from '../../lib/subsystems/registryCore';
import { FramePickerHost } from './context/framePicker';
import { videoKindApi } from './context/kind';
import { filmRefOfPath, isFilmFile } from './context/opener';
import { VideoChatWatcher } from './composer/VideoChatWatcher';
import { VideoEditorHost } from './editor/VideoEditorHost';
import { LaunchAnchor, QuietLine, SceneAnchor } from './feed/SceneCard';
import { recordKey } from './feed/records';
import { RECORD } from './api';

// Карточки ленты (module_record модуля): якорь сцены, запуск с вариантами, тихие строки.
// Флаг проверяют сами якоря: без него — строка fallback записи
const ANCHORS: SlotContribution<ChatItemToolCtx>[] = [
  { name: recordKey(RECORD.scene), render: ctx => <SceneAnchor ctx={ctx} /> },
  { name: recordKey(RECORD.launchVersions), render: ctx => <LaunchAnchor ctx={ctx} /> },
  ...[RECORD.saved, RECORD.filmBuilt, RECORD.note].map(t => ({ name: recordKey(t), render: (ctx: ChatItemToolCtx) => <QuietLine ctx={ctx} /> })),
];

export const manifest: SubsystemManifest = {
  key: 'videoeditor',
  title: 'Видео',
  order: 97,
  noPill: true,
  slots: {
    'chat-item-tool': ANCHORS,
    // Вид «видео» контекста хода (ADR-023): чипы сцены и фильма, превью, «Чем» и параметры панели «Контекст»
    'context-kind': [{ name: 'video', action: videoKindApi as unknown as Record<string, unknown> }],
    // Вход из «Файлов» (ADR-023): файл .film становится основным объектом «фильм»
    'context-opener': [
      {
        name: 'video-film',
        action: {
          isOpenable: (path: string) => getFlag(FLAGS.videoEditor) && isFilmFile(path),
          toRef: ({ projectId, sessionId, path }) => filmRefOfPath(projectId, sessionId, path),
        } satisfies ContextOpenerApi as unknown as Record<string, unknown>,
      },
    ],
    // Загрузка сцен чата, окна редакторов и окно «Из проекта» меню кадра — невидимыми вкладами композера
    'composer-chip': [
      { name: 'video-watch', render: (ctx: ComposerChipCtx) => <VideoChatWatcher ctx={ctx} /> },
      { name: 'video-editor', render: (ctx: ComposerChipCtx) => <VideoEditorHost ctx={ctx} /> },
      { name: 'video-frame-picker', render: (ctx: ComposerChipCtx) => <FramePickerHost sessionId={ctx.sessionId} /> },
    ],
  },
};
