// Манифест MF-модуля «Звук» (ADR-021 §3). Ключ совпадает с AudioEditorSubsystem.Key
// бэкенда: гейт слотов сверяется с активными подсистемами из /api/auth/me. Фич-флаг
// владельца (audio-editor) проверяют сами входы — каждый вклад через isAvailable с
// getFlag(FLAGS.audioEditor).

import { AudioLines, Mic, Music } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ChatItemToolCtx, ComposerChipCtx, ComposerStripCtx, ComposerStripShortcut, SlotContribution, SubsystemManifest,
} from '../../lib/subsystems/registryCore';
import { AudioConcatCard, AudioFocusLine, AudioLaunchCard, AudioPromptCard, AudioServiceLine } from './feed/AgentCards';
import { AUDIO_TOOL } from './feed/parse';
import { SoundChatWatcher } from './composer/SoundChatWatcher';
import { soundMode } from './composer/soundMode';
import { SoundStrip, soundStripStatus } from './strip/SoundStrip';
import { openSoundShortcut } from './thread/actions';
import { SOUND_STRIP } from './thread/threadStore';

const enabled = () => getFlag(FLAGS.audioEditor);

// Карточки вызовов агента audio_*: ключ — полное имя инструмента MCP-сервера audio-editor
const FEED_CARDS: SlotContribution<ChatItemToolCtx>[] = [
  { name: AUDIO_TOOL('audio_generate'), render: ctx => <AudioLaunchCard ctx={ctx} /> },
  { name: AUDIO_TOOL('audio_concat'), render: ctx => <AudioConcatCard ctx={ctx} /> },
  { name: AUDIO_TOOL('audio_suggest_prompt'), render: ctx => <AudioPromptCard ctx={ctx} /> },
  ...['audio_focus', 'audio_new'].map(t => ({ name: AUDIO_TOOL(t), render: (ctx: ChatItemToolCtx) => <AudioFocusLine ctx={ctx} /> })),
  ...['audio_state', 'audio_voices', 'audio_cancel'].map(t => ({ name: AUDIO_TOOL(t), render: (ctx: ChatItemToolCtx) => <AudioServiceLine ctx={ctx} /> })),
];

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
    // Без флага вкладов нет вовсе — лента рисует вызовы как раньше. Геттер читается на каждом
    // чтении слота: реестр пересчитывает вклады вместе со стором подсистем, флаги приходят раньше
    get 'chat-item-tool'() { return enabled() ? FEED_CARDS : []; },
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
    'composer-chip': [{ name: 'sound-watch', render: (ctx: ComposerChipCtx) => <SoundChatWatcher ctx={ctx} /> }],
  },
};
