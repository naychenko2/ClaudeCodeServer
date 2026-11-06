// Вспомогательные домены чатов (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// сторожа чатов, workflow-агенты, голоса TTS. Тонкая обёртка над живым REST;
// типы — types/index.ts (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type { TtsVoicesResponse, WorkflowAgentBlock, WorkflowAgentInfo } from '../../types';

// Методы этих доменов; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.watchdogs.* и т. д. остаются прежними для всех потребителей).
export const chatAuxApi = {
  // Сторожа чатов: чаты и проекты владельца с АКТИВНЫМИ сторожами. Снимок нужен потому,
  // что событие watchdogs_changed приходит только на смене состава — открывший список
  // позже постановки иначе о них не узнает
  watchdogs: {
    snapshot: () => request<{ sessions: string[]; projects: string[] }>('/watchdogs'),
  },

  workflow: {
    getAgents: (transcriptDir: string) =>
      request<{ agents: WorkflowAgentInfo[] }>(
        `/workflow-agents?transcriptDir=${encodeURIComponent(transcriptDir)}`
      ),
    // Полный поток одного агента (текст/thinking/инструменты) — лениво при раскрытии карточки
    getTimeline: (transcriptDir: string, agentId: string) =>
      request<{ blocks: WorkflowAgentBlock[] }>(
        `/workflow-agents/timeline?transcriptDir=${encodeURIComponent(transcriptDir)}&agentId=${encodeURIComponent(agentId)}`
      ),
  },

  tts: {
    // Каталог голосов для формы выбора: канонические имена, подписи, пол и амплуа.
    // live — мимо офлайн-кеша: сразу после того, как ключ SpeechKit пропишут в конфиг,
    // из IndexedDB прилетал бы устаревший configured: false и форма врала бы про синтез
    voices: () => request<TtsVoicesResponse>('/tts/voices', { live: true }),
  },
};
