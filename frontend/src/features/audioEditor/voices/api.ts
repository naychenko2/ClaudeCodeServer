// Клиент библиотеки «Голоса» (ADR-021 §2): ручки AudioVoicesController
// (/projects/{projectId}/audio-editor/voices) и единственная ручка личного чата
// (/audio-editor/chats/{sessionId}/voices — всегда available:false, поэтому в сеть не ходим).
// Типы — зеркало VoiceDto и VoiceProviderState бэкенда (Voices/VoiceManifest.cs). Мутирующих ручек
// у личного чата нет вовсе: отказ — до запроса.

import { readStoredToken, request } from 'aihome_shell/kit';
import type { AudioQuote } from '../api';
import { audioBase, isPersonalScope } from '../scope';

export type VoiceKind = 'samples' | 'rvc';
export type VoiceProviderKey = 'higgsfield' | 'minimax' | 'falQwen' | 'rvc';
// none — клон не создан, ok — есть, stale — MiniMax мог удалить клон (7 дней без использования)
export type VoiceProviderStatus = 'none' | 'ok' | 'stale';

export interface VoiceProviderState {
  provider: VoiceProviderKey;
  state: VoiceProviderStatus;
  createdAt: string | null;
  lastUsedAt: string | null;
}

export interface AudioVoice {
  slug: string;
  name: string;
  kind: VoiceKind;
  // voices/<slug> от корня проекта
  path: string;
  samples: { file: string }[];
  transcript: string | null;
  createdAt: string;
  providers: VoiceProviderState[];
  needsAttention: boolean;
}

export interface VoicesList { available: boolean; reason?: string | null; voices: AudioVoice[] }

export interface NewVoiceInput {
  name: string;
  transcript?: string | null;
  files?: File[];
  // Пути файлов проекта от корня
  projectFiles?: string[];
}

export const MAX_VOICE_SAMPLES = 5;
export const MAX_VOICE_SAMPLE_MB = 50;

const projectOnly = () => new Error('Библиотека «Голоса» живёт в проекте — в личном чате её нет');

function voicesBase(scope: string): string {
  if (isPersonalScope(scope)) throw projectOnly();
  return `${audioBase(scope, null)}/voices`;
}

const voiceUrl = (scope: string, slug: string) => `${voicesBase(scope)}/${encodeURIComponent(slug)}`;

export function samplesForm(input: Partial<NewVoiceInput>): FormData {
  const form = new FormData();
  if (input.name?.trim()) form.append('name', input.name.trim());
  if (input.transcript?.trim()) form.append('transcript', input.transcript.trim());
  input.files?.forEach(f => form.append('files', f));
  input.projectFiles?.forEach(p => form.append('projectFiles', p));
  return form;
}

export const voicesApi = {
  // Личный чат — тот же ответ, что дал бы сервер (available:false), без запроса
  list: (scope: string, sessionId: string | null): Promise<VoicesList> =>
    isPersonalScope(scope)
      ? Promise.resolve({ available: false, voices: [] })
      : request<VoicesList>(`${audioBase(scope, sessionId)}/voices`, { live: true }),
  create: (scope: string, input: NewVoiceInput) =>
    request<AudioVoice>(voicesBase(scope), { method: 'POST', body: samplesForm(input), timeoutMs: 300_000 }),
  update: (scope: string, slug: string, patch: { name?: string; transcript?: string }) =>
    request<AudioVoice>(voiceUrl(scope, slug), { method: 'PATCH', body: JSON.stringify(patch) }),
  remove: (scope: string, slug: string) =>
    request<void>(voiceUrl(scope, slug), { method: 'DELETE' }),
  addSamples: (scope: string, slug: string, input: { files?: File[]; projectFiles?: string[] }) =>
    request<AudioVoice>(`${voiceUrl(scope, slug)}/samples`, { method: 'POST', body: samplesForm(input), timeoutMs: 300_000 }),
  removeSample: (scope: string, slug: string, file: string) =>
    request<AudioVoice>(`${voiceUrl(scope, slug)}/samples/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  // Пересоздание клона MiniMax в две фазы: без quoteId — котировка с ценой (денег не тратит),
  // с quoteId той котировки — задача. Вторую фазу зовёт только кнопка человека
  recreateQuote: (scope: string, slug: string) =>
    request<AudioQuote>(`${voiceUrl(scope, slug)}/recreate?provider=minimax`, { method: 'POST', timeoutMs: 60_000 }),
  recreate: (scope: string, slug: string, quoteId: string) =>
    request<{ jobId: string }>(`${voiceUrl(scope, slug)}/recreate?${new URLSearchParams({ provider: 'minimax', quoteId })}`,
      { method: 'POST', timeoutMs: 120_000 }),
  // URL образца для <audio src>: токен через ?access_token=, тег заголовков не шлёт
  fileUrl: (scope: string, slug: string, file: string) => {
    const url = `/api${voiceUrl(scope, slug)}/files/${encodeURIComponent(file)}`;
    const token = readStoredToken();
    return token ? `${url}?access_token=${encodeURIComponent(token)}` : url;
  },
};
