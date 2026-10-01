// Манифест MF-модуля «Звук» (ADR-021 §3). Ключ совпадает с AudioEditorSubsystem.Key
// бэкенда: гейт слотов сверяется с активными подсистемами из /api/auth/me. Фич-флаг
// владельца (audio-editor) проверяют сами входы — каждый вклад через isAvailable с
// getFlag(FLAGS.audioEditor).

import { AudioLines, Mic, Music } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ChatItemToolCtx, ComposerChipCtx, ComposerStripCtx, ComposerStripShortcut, SubsystemManifest,
  WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { SoundChatWatcher } from './composer/SoundChatWatcher';
import { SoundPanel } from './panel/SoundPanel';
import { SoundSheet } from './panel/SoundSheet';
import { soundMode } from './composer/soundMode';
import { SoundStrip, soundStripStatus } from './strip/SoundStrip';
import { openSoundShortcut } from './thread/actions';
import { RECORD_LAUNCH, RECORD_THREAD, recordKey } from './thread/records';
import { LaunchAnchor, ThreadAnchor } from './thread/ThreadCard';
import { SOUND_PANEL, SOUND_STRIP } from './thread/threadStore';

const enabled = () => getFlag(FLAGS.audioEditor);

// Ярлыки «Голос» и «Музыка» в меню полос: полоса «Звук» и панель на «Настройках» в нужном режиме
export function soundShortcuts({ sessionId }: { sessionId: string | null }): ComposerStripShortcut[] {
  return [
    {
      key: 'sound-voice', title: 'Голос', hint: 'озвучить текст, сменить голос, обучить',
      icon: <Mic size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onSelect: () => openSoundShortcut(sessionId, 'voice'),
    },
    {
      key: 'sound-music', title: 'Музыка', hint: 'песня, кавер, звуковой эффект',
      icon: <Music size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onSelect: () => openSoundShortcut(sessionId, 'music'),
    },
  ];
}

export const manifest: SubsystemManifest = {
  key: 'audioeditor',
  title: 'Звук',
  order: 96,
  noPill: true,
  slots: {
    // Карточки ленты (module_record модуля): нить с версиями ‹ › и запуск ИИ с вариантами.
    // Флаг проверяют сами карточки: без него — строка fallback записи
    'chat-item-tool': [
      { name: recordKey(RECORD_THREAD), render: (ctx: ChatItemToolCtx) => <ThreadAnchor ctx={ctx} /> },
      { name: recordKey(RECORD_LAUNCH), render: (ctx: ChatItemToolCtx) => <LaunchAnchor ctx={ctx} /> },
    ],
    // Полоса «Звук» над композером: выбор звука открывает её сам (стор нитей)
    'composer-strip': [
      {
        name: SOUND_STRIP, order: 25,
        render: (ctx: ComposerStripCtx) => <SoundStrip ctx={ctx} />,
        action: {
          title: 'Звук',
          icon: <AudioLines size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          isAvailable: () => enabled(),
          status: ({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }) => soundStripStatus(projectId, sessionId),
          shortcuts: soundShortcuts,
        },
      },
    ],
    // Режим поля ввода «Звук» — только при выбранном звуке
    'composer-mode': [
      { name: 'sound', order: 20, action: soundMode as unknown as Record<string, unknown> },
    ],
    // Загрузка нитей чата и возврат полосы по серверному фокусу; сам ничего не рисует
    // Шторка панели «Звук» на телефоне — тем же вкладом, что живёт при любой полосе
    'composer-chip': [
      { name: 'sound-watch', render: (ctx: ComposerChipCtx) => <SoundChatWatcher ctx={ctx} /> },
      { name: 'sound-sheet', render: (ctx: ComposerChipCtx) => <SoundSheet ctx={ctx} /> },
    ],
    // Панель «Звук»: настройки и голоса вкладками, в проекте и в правой колонке личного чата
    'workspace-panel-def': [
      {
        name: SOUND_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <SoundPanel ctx={ctx} />,
        action: {
          title: 'Звук',
          icon: <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: () => enabled(),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
