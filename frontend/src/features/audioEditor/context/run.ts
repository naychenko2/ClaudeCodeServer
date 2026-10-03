// Параметры, цена и запуск действия звука (ADR-023 §Д2, §Д2.1): тонкий слой над существующими котировкой,
// запуском задачи и ручками без ИИ (`mix`, `concat`). Входы — нить, версия, голос, образец и куски —
// сервер читает из стора контекста по ревизии, поэтому здесь только `op`, текст, `params` и `contextRevision`.

import { ReportedError, etaTicker, getChatContextState, showToast } from 'aihome_shell/kit';
import type { ActionQuote, ContextKindCtx, LaunchHandle, LaunchParam, LaunchRequest } from 'aihome_shell/kit';
import { audioApi, type AudioCatalog, type AudioQuote, type AudioJobInput, type AudioOp, type AudioQuoteRequest, type AudioStemSet } from '../api';
import { isNoAi, opInfo } from '../ops';
import { isStemSet } from '../panel/stems';
import { pieceSeconds } from '../panel/piece';
import { audioScope } from '../scope';
import { isStem, priceText } from '../thread/model';
import { getCatalog, getSelection, mutate } from '../thread/threadStore';
import { getChoice } from './executors';
import { revealSoundPanel } from './reveal';
import { threadOfPrimary, versionOfPrimary } from './state';

// Варианты — только у операций, что создают звук (голос, музыка); обработка даёт один результат
export function paramsFor(catalog: AudioCatalog | null, op: AudioOp): LaunchParam[] {
  const mode = opInfo(op)?.mode;
  // У песни вариантов нет (макет): модель отдаёт один трек
  if (isNoAi(op) || op === 'song' || (mode !== 'voice' && mode !== 'music')) return [];
  return [{ kind: 'variants', min: 1, max: Math.max(1, catalog?.maxCount ?? 4), value: 1 }];
}

// Модель «Стемов» по выбранному набору: явный поставщик исполнителя, иначе первый доступный в порядке
// «Авто», который умеет набор. Набор подбирает именно модель (caps.stemSet), а не id
export function stemModelFor(
  catalog: AudioCatalog | null, set: AudioStemSet, preferred: string | null,
): { provider: string; model: string } | null {
  const providers = catalog?.providers ?? [];
  const order = [...providers.filter(p => p.key === preferred), ...providers.filter(p => p.key !== preferred && p.available)];
  for (const p of order) {
    const m = p.models.find(x => x.caps.ops.includes('separate') && x.caps.stemSet === set);
    if (m) return { provider: p.key, model: m.id };
  }
  return null;
}


interface Resolved { op: AudioOp; scope: string; sessionId: string; threadId: string; quote: AudioQuoteRequest; job: Omit<AudioJobInput, 'quoteId'> }

// Нить основного объекта чата и область звука; нет её (контекст сменился) — запуск и цена отказывают
function currentThread(ctx: ContextKindCtx) {
  const primary = getChatContextState(ctx.sessionId).primary;
  const thread = threadOfPrimary(ctx.sessionId, primary);
  if (!primary || !thread) throw new Error('Звук недоступен');
  return { primary, thread, scope: audioScope(ctx.projectId) };
}

// Одна сборка тела котировки и запуска: цена обязана приходить с теми же текстом и параметрами, что и запуск
export function resolveRequest(ctx: ContextKindCtx, req: LaunchRequest): Resolved {
  const { thread, scope } = currentThread(ctx);
  const op = req.op as AudioOp;
  const info = opInfo(op);
  if (!info) throw new Error(`Операция «${req.op}» недоступна`);
  const catalog = getCatalog(scope);
  const choice = getChoice(ctx.sessionId, op);
  let provider = choice?.provider ?? 'auto';
  let model = choice?.model ?? 'auto';
  if (op === 'separate') {
    const set = req.params.stemSet;
    if (!isStemSet(set)) throw new Error('Выберите набор дорожек');
    const found = stemModelFor(catalog, set, choice?.provider ?? null);
    if (!found) throw new Error('Ни один поставщик не умеет такой набор дорожек');
    ({ provider, model } = found);
  }
  const text = req.text.trim();
  const spoken = info.field === 'text' && text ? text : null;
  const prompt = info.field === 'prompt' && text ? text : null;
  const count = typeof req.params.variants === 'number' ? req.params.variants : null;
  const { startSec, endSec } = op === 'repaint' ? pieceSeconds(getSelection(ctx.sessionId, thread.id)) : { startSec: null, endSec: null };
  return {
    op, scope, sessionId: ctx.sessionId, threadId: thread.id,
    quote: {
      mode: info.mode, operation: op, provider, model, count, sessionId: ctx.sessionId,
      text: spoken, prompt, contextRevision: req.contextRevision,
    },
    job: { sessionId: ctx.sessionId, text: spoken, prompt, startSec, endSec, contextRevision: req.contextRevision },
  };
}

// Цена по op действия. Без ИИ — бесплатно и без запроса; остальное — котировка по ревизии контекста
export async function quoteAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<ActionQuote> {
  if (isNoAi(req.op as AudioOp)) return { price: 'бесплатно', detail: 'Без ИИ · на сервере' };
  const r = resolveRequest(ctx, req);
  const q = await audioApi.quote(r.scope, r.sessionId, r.quote);
  return { price: priceText(q.price) ?? 'цена станет известна после запуска', detail: quoteDetail(getCatalog(r.scope), q) };
}

// «Qwen3-TTS · ~40 с · очередь GPU: 0»: имена из каталога (id поставщика и модели пользователю не показываем)
const etaShort = (sec: number) => (sec < 90 ? `~${Math.max(1, Math.round(sec))} с` : `~${Math.round(sec / 60)} мин`);

export function quoteDetail(catalog: AudioCatalog | null, q: Pick<AudioQuote, 'provider' | 'model' | 'price'>): string {
  const pv = catalog?.providers.find(p => p.key === q.provider);
  const model = pv?.models.find(m => m.id === q.model)?.label ?? q.model;
  const free = q.price.unit === 'free';
  return [
    model,
    q.price.eta != null && q.price.eta > 0 ? etaShort(q.price.eta) : null,
    free && q.price.queueLength != null ? `очередь GPU: ${q.price.queueLength}` : null,
  ].filter(Boolean).join(' · ');
}

// События задачи начинаем слушать, как только известен jobId, — до возврата дескриптора: быстрая
// задача могла завершиться раньше, чем хост подпишется
type Ev = { progress?: number; result?: { summary: string; open?: () => void }; error?: string; cancelled?: boolean };

function jobWatcher(jobId: string, open: () => void, scope: string, sessionId: string) {
  let off: (() => void) | null = null;
  let listener: ((e: Ev) => void) | null = null;
  const buffered: Ev[] = [];
  const emit = (e: Ev) => { if (listener) listener(e); else buffered.push(e); };
  // Точного процента у поставщиков нет: полоса идёт от ожидаемой длительности (в очереди стоит)
  const ticker = etaTicker(f => emit({ progress: f }));
  off = audioApi.subscribe(ev => {
    if (!('jobId' in ev) || ev.jobId !== jobId) return;
    if (ev.type === 'audio_edit_progress') {
      ticker.update({ etaSeconds: ev.etaSeconds, queued: ev.stage === 'queued' }, ev.stage === 'running');
    } else if (ev.type === 'audio_edit_completed') {
      ticker.stop();
      if (ev.error) emit({ error: ev.error });
      else emit({ result: { summary: ev.variants.length > 1 ? `Готово: ${ev.variants.length} варианта` : 'Готово', open } });
      off?.();
    } else if (ev.type === 'audio_edit_failed') {
      ticker.stop();
      emit(ev.outcome === 'cancelled' ? { cancelled: true } : { error: ev.error ?? 'Звук не получился' });
      off?.();
    }
  });
  return {
    cancel() {
      return audioApi.cancelJob(scope, sessionId, jobId).then(() => {}, e => { showToast(`Не удалось отменить: ${(e as Error).message}`, '', 'error'); });
    },
    watch(on: (e: Ev) => void) {
      listener = on;
      buffered.splice(0).forEach(on);
      return () => { listener = null; ticker.stop(); off?.(); };
    },
  };
}

// Правка без ИИ ждёт итог прямо в запросе: дескриптор отдаёт готовый результат
function doneHandle(id: string, summary: string, open: () => void): LaunchHandle {
  return { id, watch: on => { on({ result: { summary, open } }); return () => {}; } };
}

const openSound = (sessionId: string) => () => { revealSoundPanel(sessionId, 'settings'); };

export async function launchAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  const op = req.op as AudioOp;
  const open = openSound(ctx.sessionId);
  if (op === 'mixStems') {
    const { primary, thread, scope } = currentThread(ctx);
    const version = versionOfPrimary(thread, primary);
    const stems = (version?.files ?? []).filter(isStem).map(f => ({ role: f.role, gainDb: 0 }));
    if (!stems.length) throw new Error('У версии нет стемов');
    let jobId = '';
    const ok = await mutate(scope, ctx.sessionId, async rev => {
      const r = await audioApi.mix(scope, ctx.sessionId, thread.id, { stems, revision: rev, contextRevision: req.contextRevision });
      jobId = r.jobId;
      return r.state;
    });
    if (!ok) throw new ReportedError('Свести не получилось');
    return doneHandle(jobId, 'Сведено в новую версию', open);
  }
  if (op === 'concat') {
    const { scope } = currentThread(ctx);
    const r = await audioApi.concat(scope, ctx.sessionId, { contextRevision: req.contextRevision });
    return doneHandle(r.jobId, `Склеено: «${r.name}»`, open);
  }
  const r = resolveRequest(ctx, req);
  // Котировка с теми же текстом и параметрами, что у запуска: иначе сервер откажет в запуске
  const quote = await audioApi.quote(r.scope, r.sessionId, r.quote);
  const { jobId } = await audioApi.startJob(r.scope, r.sessionId, { ...r.job, quoteId: quote.quoteId });
  if (!jobId) throw new ReportedError('Запуск не удался');
  const watcher = jobWatcher(jobId, open, r.scope, r.sessionId);
  return { id: jobId, watch: watcher.watch, cancel: watcher.cancel };
}
