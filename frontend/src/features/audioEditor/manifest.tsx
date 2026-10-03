// Манифест MF-модуля «Звук» (ADR-021 §3). Ключ совпадает с AudioEditorSubsystem.Key
// бэкенда: гейт слотов сверяется с активными подсистемами из /api/auth/me. Фич-флаг
// владельца (audio-editor) проверяют сами входы — каждый вклад через isAvailable с
// getFlag(FLAGS.audioEditor).

import { AudioLines, Mic } from 'lucide-react';
import { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ChatItemToolCtx, ComposerChipCtx, ComposerStripCtx, ComposerStripShortcut, ContextOpenerApi, SlotContribution, SubsystemManifest,
  WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { AudioConcatCard, AudioFocusLine, AudioLaunchCard, AudioPromptCard, AudioServiceLine } from './feed/AgentCards';
import { AUDIO_TOOL } from './feed/parse';
import { SoundChatWatcher } from './composer/SoundChatWatcher';
import { SoundPanel } from './panel/SoundPanel';
import { SoundSheet } from './panel/SoundSheet';
import { VoicesContextPanel } from './voices/VoicesContextPanel';
import { VOICES_KEY } from './thread/panelKey';
import { soundMode } from './composer/soundMode';
import { audioKindApi } from './context/kind';
import { audioRefOfPath, isAudioFile } from './context/opener';
import { SoundStrip, soundStripStatus } from './strip/SoundStrip';
import { openSoundShortcut } from './thread/actions';
import { RECORD_LAUNCH, RECORD_THREAD, recordKey } from './thread/records';
import { LaunchAnchor, ThreadAnchor } from './thread/ThreadCard';
import { SOUND_PANEL, SOUND_STRIP } from './thread/threadStore';

const enabled = () => getFlag(FLAGS.audioEditor);

// Карточки ленты (module_record модуля): карточка версии и запуск ИИ — карточка на каждый вариант
const THREAD_ANCHORS: SlotContribution<ChatItemToolCtx>[] = [
  { name: recordKey(RECORD_THREAD), render: ctx => <ThreadAnchor ctx={ctx} /> },
  { name: recordKey(RECORD_LAUNCH), render: ctx => <LaunchAnchor ctx={ctx} /> },
];

// Карточки вызовов агента audio_*: ключ — полное имя инструмента MCP-сервера audio-editor
const FEED_CARDS: SlotContribution<ChatItemToolCtx>[] = [
  { name: AUDIO_TOOL('audio_generate'), render: ctx => <AudioLaunchCard ctx={ctx} /> },
  { name: AUDIO_TOOL('audio_concat'), render: ctx => <AudioConcatCard ctx={ctx} /> },
  { name: AUDIO_TOOL('audio_suggest_prompt'), render: ctx => <AudioPromptCard ctx={ctx} /> },
  ...['audio_focus', 'audio_new'].map(t => ({ name: AUDIO_TOOL(t), render: (ctx: ChatItemToolCtx) => <AudioFocusLine ctx={ctx} /> })),
  ...['audio_state', 'audio_voices', 'audio_cancel'].map(t => ({ name: AUDIO_TOOL(t), render: (ctx: ChatItemToolCtx) => <AudioServiceLine ctx={ctx} /> })),
];

// Ярлык «Звук» («＋» композера, пустая лента; в меню полос — сам пункт полосы, ключ совпадает):
// полоса «Звук» и панель на «Настройках» в последнем выбранном режиме — режим выбирают в панели
export function soundShortcuts({ sessionId }: { sessionId: string | null }): ComposerStripShortcut[] {
  return [{
    key: SOUND_STRIP, title: 'Звук', hint: 'голос, музыка, обработка',
    icon: <AudioLines size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onSelect: () => openSoundShortcut(sessionId),
  }];
}

export const manifest: SubsystemManifest = {
  key: 'audioeditor',
  title: 'Звук',
  order: 96,
  noPill: true,
  slots: {
    // Якоря нити (module_record модуля) флаг проверяют сами: без него — строка fallback записи.
    // Карточки вызовов агента без флага не вкладываются вовсе — лента рисует вызовы как раньше.
    // Геттер читается на каждом чтении слота: реестр пересчитывает вклады вместе со сторами
    get 'chat-item-tool'() { return enabled() ? [...THREAD_ANCHORS, ...FEED_CARDS] : THREAD_ANCHORS; },
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
    // Вход из «Файлов» в контекст хода (ADR-023): звуковой файл проекта становится нитью-основным объектом
    'context-opener': [
      {
        name: 'audio',
        action: {
          isOpenable: isAudioFile,
          toRef: ({ projectId, sessionId, path }) => audioRefOfPath(projectId, sessionId, path),
        } satisfies ContextOpenerApi as unknown as Record<string, unknown>,
      },
    ],
    // Вид «звук» контекста хода (ADR-023): чипы действий, волна, «Чем» и параметры панели «Контекст»
    'context-kind': [{ name: 'audio', action: audioKindApi as unknown as Record<string, unknown> }],
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
      // «Голоса» отдельной панелью (ADR-023 §Д1, 2з-3): только при флаге composer-context-row и только в проекте —
      // без флага библиотека остаётся вкладкой «Звука», а у личного чата голосов нет
      {
        name: VOICES_KEY,
        render: (ctx: WorkspacePanelDefCtx) => <VoicesContextPanel ctx={ctx} />,
        action: {
          title: 'Голоса',
          icon: <Mic size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: (projectId: string | null) => projectId !== null && enabled() && getFlag(FLAGS.composerContextRow),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
