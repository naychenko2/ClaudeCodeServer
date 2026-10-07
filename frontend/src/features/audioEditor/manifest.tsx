// Манифест MF-модуля «Звук» (ADR-021 §3). Ключ совпадает с AudioEditorSubsystem.Key
// бэкенда: гейт слотов сверяется с активными подсистемами из /api/auth/me.

import { Mic } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  ChatItemToolCtx, ComposerChipCtx, ContextOpenerApi, SlotContribution, SubsystemManifest,
  WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { AudioConcatCard, AudioFocusLine, AudioLaunchCard, AudioPromptCard, AudioServiceLine } from './feed/AgentCards';
import { AUDIO_TOOL } from './feed/parse';
import { SoundChatWatcher } from './composer/SoundChatWatcher';
import { VoicesContextPanel } from './voices/VoicesContextPanel';
import { VOICES_KEY } from './thread/panelKey';
import { audioKindApi } from './context/kind';
import { audioRefOfPath, isAudioFile } from './context/opener';
import { RECORD_LAUNCH, RECORD_THREAD, recordKey } from './thread/records';
import { LaunchAnchor, ThreadAnchor } from './thread/ThreadCard';

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

export const manifest: SubsystemManifest = {
  key: 'audioeditor',
  title: 'Звук',
  order: 96,
  noPill: true,
  slots: {
    'chat-item-tool': [...THREAD_ANCHORS, ...FEED_CARDS],
    // Загрузка нитей чата и редактор звука; сам ничего не рисует
    'composer-chip': [
      { name: 'sound-watch', render: (ctx: ComposerChipCtx) => <SoundChatWatcher ctx={ctx} /> },
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
    // Панель «Голоса» (ADR-023 §Д1, 2з-3): только в проекте; у личного чата голосов нет
    'workspace-panel-def': [
      {
        name: VOICES_KEY,
        render: (ctx: WorkspacePanelDefCtx) => <VoicesContextPanel ctx={ctx} layout={ctx.isMobile ? 'sheet' : 'column'} />,
        action: {
          title: 'Голоса',
          icon: <Mic size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          isAvailable: (projectId: string | null) => projectId !== null,
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
