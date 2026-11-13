// «Озвучить сцену» (Ш4): звук по видео версии сцены. Видео-редактор собирается отдельным модулем и кода звука
// не импортирует — вызывает ручки модуля «Звук» напрямую: каталог → черновик нити → котировка → запуск
// с версией сцены (сервер сам берёт клип и по готовности добавляет в сцену версию со вклеенным звуком).

import { request } from 'aihome_shell/kit';

const OP = 'videoToAudio';
const MODE = 'music';

export interface SoundModel { id: string; label: string; license: string; heavy: boolean; maxSec: number | null }

export interface SoundOptions {
  models: SoundModel[];
  provider: string;
  // Причина, по которой локальные модели сейчас недоступны (серым с причиной), иначе null
  reason: string | null;
}

interface CatalogModel {
  id: string; label: string;
  caps: { ops: string[]; license: { label: string }; heavyOps?: string[] | null; inputMaxSec?: number | null };
}
interface Catalog { providers: { key: string; available: boolean; reason: string | null; models: CatalogModel[] }[] }

const LOCAL = 'local';

export const audioBase = (scope: string, sessionId: string) =>
  scope === 'personal' ? `/audio-editor/chats/${encodeURIComponent(sessionId)}` : `/projects/${encodeURIComponent(scope)}/audio-editor`;

const chatBase = (scope: string, sessionId: string) =>
  scope === 'personal' ? audioBase(scope, sessionId) : `${audioBase(scope, sessionId)}/sessions/${encodeURIComponent(sessionId)}`;

// Модели каталога, умеющие «звук по видео», в порядке каталога: первая — умолчание
export function soundOptions(catalog: Catalog | null): SoundOptions {
  const local = catalog?.providers.find(p => p.key === LOCAL);
  const models = (local?.models ?? []).filter(m => m.caps.ops.includes(OP)).map(m => ({
    id: m.id, label: m.label, license: m.caps.license.label, heavy: !!m.caps.heavyOps?.includes(OP), maxSec: m.caps.inputMaxSec ?? null,
  }));
  return { models, provider: LOCAL, reason: !local ? 'Локальные модели на этом сервере не заведены' : local.available ? null : local.reason };
}

export const loadSoundOptions = async (scope: string, sessionId: string): Promise<SoundOptions> =>
  soundOptions(await request<Catalog>(`${audioBase(scope, sessionId)}/catalog`));

// «≈ 40 с», «≈ 2 мин»
export function etaLabel(sec: number | null | undefined): string {
  if (sec == null || sec <= 0) return 'время не оценено';
  return sec < 90 ? `≈ ${Math.round(sec)} с` : `≈ ${Math.round(sec / 60)} мин`;
}

// Клип длиннее предела модели озвучить нельзя: кнопка запуска серая с причиной
export function tooLong(model: SoundModel, durationSec: number): string | null {
  return model.maxSec != null && durationSec > model.maxSec + 0.25
    ? `Сцена ${durationSec.toFixed(1)} с, а модель озвучивает клипы до ${model.maxSec} с`
    : null;
}

interface QuoteRes { quoteId: string; price: { eta: number | null } }

const quoteBody = (sessionId: string, model: string, durationSec: number, prompt: string, threadId?: string) => ({
  mode: MODE, operation: OP, provider: LOCAL, model, count: 1, sessionId, durationSec: Math.ceil(durationSec),
  ...(threadId ? { threadId } : {}), prompt: prompt.trim() || null,
});

// Ожидаемое время каждой модели: котировка без запуска (клип ещё не читается, длина — из версии сцены).
// Сбой котировки одной модели — null у неё, остальные не страдают
export async function quoteEtas(scope: string, sessionId: string, models: SoundModel[], durationSec: number): Promise<Record<string, number | null>> {
  const out: Record<string, number | null> = {};
  await Promise.all(models.map(async m => {
    try {
      out[m.id] = (await request<QuoteRes>(`${audioBase(scope, sessionId)}/quote`,
        { method: 'POST', body: JSON.stringify(quoteBody(sessionId, m.id, durationSec, '')), timeoutMs: 60_000 })).price.eta;
    } catch { out[m.id] = null; }
  }));
  return out;
}

interface Threads { revision: number; focus: string | null }

// Запуск: черновик нити звука (результат ляжет в неё версией) → котировка → задача с версией сцены.
// Возвращает id задачи; ошибка — исключением с текстом сервера
export async function voiceScene(p: {
  scope: string; sessionId: string; sceneId: string; versionId: string | null; modelId: string; durationSec: number; prompt: string;
}): Promise<{ jobId: string; threadId: string }> {
  const { scope, sessionId } = p;
  const base = chatBase(scope, sessionId);
  const before = await request<Threads>(`${base}/threads`);
  const opened = await request<Threads & { threads: { id: string }[] }>(`${base}/threads`,
    { method: 'POST', body: JSON.stringify({ draftFolder: '', mode: MODE, revision: before.revision }) });
  const threadId = opened.focus ?? opened.threads[opened.threads.length - 1]?.id;
  if (!threadId) throw new Error('Не удалось завести нить звука');
  const quote = await request<QuoteRes>(`${audioBase(scope, sessionId)}/quote`,
    { method: 'POST', body: JSON.stringify(quoteBody(sessionId, p.modelId, p.durationSec, p.prompt, threadId)), timeoutMs: 60_000 });
  const form = new FormData();
  form.append('quoteId', quote.quoteId);
  form.append('sessionId', sessionId);
  form.append('threadId', threadId);
  form.append('durationSec', String(Math.ceil(p.durationSec)));
  if (p.prompt.trim()) form.append('prompt', p.prompt.trim());
  form.append('videoSceneId', p.sceneId);
  if (p.versionId) form.append('videoVersionId', p.versionId);
  form.append('pasteIntoScene', 'true');
  const started = await request<{ jobId: string }>(`${audioBase(scope, sessionId)}/jobs`, { method: 'POST', body: form, timeoutMs: 300_000 });
  return { jobId: started.jobId, threadId };
}
