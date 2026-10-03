// Параметры, цена и запуск действий «Видео» (ADR-023 §Д2, §Д2.1): тонкий слой над существующими котировкой,
// запуском съёмки и сборкой фильма. Сцену, кадры (референсы `frame-a`/`frame-b`) и фильм сервер читает из
// стора контекста по ревизии, поэтому здесь только `op`, текст, `params` и `contextRevision`; текст поля
// едет в `params.request` — просьба поверх текста сцены.

import { getChatContextState, revealContextPanel } from 'aihome_shell/kit';
import type { ActionQuote, ContextKindCtx, LaunchHandle, LaunchParam, LaunchRequest } from 'aihome_shell/kit';
import { ERR, errorCode, errorText, videoApi, type FilmBuildStatus, type VideoCatalog, type VideoQuoteRequest } from '../api';
import { DSP_TEXT } from '../editor/montage/FilmTab';
import { openProjectFile } from '../film/nav';
import { currentResolved, runErrorText, stopJob } from '../scene/actions';
import { plural, priceLines, modelLabel, snapTo, type ResolvedScene } from '../scene/model';
import { isPersonalScope, videoScope } from '../scope';
import { cancelBuild, getCatalog, getFilm, getJobsOf, setFilmBuild, subscribeVideoStore } from '../store/videoStore';
import { filmPathOf, sceneOfPrimary } from './state';

// Тело 409 context_changed: хост сам берёт из него свежий контекст, ошибку оставляем как есть
const isContextChanged = (e: unknown) => (e as { body?: { error?: unknown } } | null)?.body?.error === 'context_changed';

// Длительности: у выбранной модели её список, у «Авто» — всё, что умеет хоть одна доступная модель
export function durationOptions(catalog: VideoCatalog | null, r: ResolvedScene): number[] {
  if (r.model?.durations.length) return r.model.durations;
  const all = new Set<number>();
  for (const p of catalog?.providers ?? []) {
    if (p.available) p.models.forEach(m => m.durations.forEach(d => all.add(d)));
  }
  return all.size ? [...all].sort((a, b) => a - b) : [r.durationSec];
}

// «Снять»: варианты и длительность; у «Собрать» параметров нет
export function paramsFor(catalog: VideoCatalog | null, r: ResolvedScene, op: string): LaunchParam[] {
  if (op !== 'shoot') return [];
  const options = durationOptions(catalog, r);
  return [
    { kind: 'variants', min: 1, max: Math.max(1, catalog?.maxCount ?? 4), value: r.count },
    { kind: 'duration', options, value: snapTo(options, r.durationSec) ?? r.durationSec },
  ];
}

function currentScene(ctx: ContextKindCtx) {
  const primary = getChatContextState(ctx.sessionId).primary;
  const scene = sceneOfPrimary(ctx.sessionId, primary);
  if (!scene) throw new Error('Сцена недоступна');
  const scope = videoScope(ctx.projectId);
  return { scene, scope, r: currentResolved(ctx.sessionId, scope, scene) };
}

// Одна сборка тела котировки: цена обязана приходить с теми же параметрами, что и запуск. `sceneId` пуст:
// при ревизии сцену сервер берёт из основного объекта контекста
export function quoteBody(ctx: ContextKindCtx, req: LaunchRequest): { scope: string; body: VideoQuoteRequest } {
  const { scope, r } = currentScene(ctx);
  const variants = req.params.variants;
  const duration = req.params.duration;
  return {
    scope,
    body: {
      sessionId: ctx.sessionId, sceneId: '',
      ...(r.provider && r.model ? { provider: r.provider.key, model: r.model.id } : {}),
      count: typeof variants === 'number' ? variants : r.count,
      durationSec: typeof duration === 'number' ? duration : r.durationSec,
      aspect: r.aspect, sound: r.sound, contextRevision: req.contextRevision,
    },
  };
}

// Цена по op действия. Сборка идёт без ИИ — бесплатно и без запроса; съёмка — котировка по ревизии контекста
export async function quoteAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<ActionQuote> {
  if (req.op === 'build') return { price: 'бесплатно', detail: 'Без ИИ · на сервере' };
  const { scope, body } = quoteBody(ctx, req);
  const q = await videoApi.quote(scope, ctx.sessionId, body);
  const lines = priceLines(q, q.count, q.durationSec);
  const model = modelLabel(getCatalog(scope), q.provider, q.model);
  return {
    price: q.price.unit === 'free' ? 'бесплатно' : lines?.[0] ?? 'цена станет известна после запуска',
    detail: [model, lines?.[1]].filter(Boolean).join(' · '),
  };
}

// События задачи начинаем слушать, как только известен jobId, — до возврата дескриптора: быстрая
// задача могла завершиться раньше, чем хост подпишется. Прогресс берём из стора «Видео» — того же, что
// рисует карточку ленты: первое событие приходит раньше подписки, а стор его уже учёл
type Ev = { progress?: number; result?: { summary: string; open?: () => void }; error?: string; cancelled?: boolean };

// Доля хода съёмки: та же формула, что у полосы карточки (варианты по очереди)
export function jobFraction(j: { stage: string; variant: number; count: number }, fallbackCount: number): number {
  if (j.stage === 'queued') return 0.05;
  const total = j.count || fallbackCount || 1;
  return Math.min(0.95, Math.max(0.05, ((j.variant - 1) / total) + 1 / total / 2));
}

function jobWatcher(scope: string, sessionId: string, sceneId: string, jobId: string, open: () => void) {
  let listener: ((e: Ev) => void) | null = null;
  let cancelAsked = false;
  const buffered: Ev[] = [];
  const emit = (e: Ev) => { if (listener) listener(e); else buffered.push(e); };
  const push = () => {
    const j = getJobsOf(sessionId, sceneId).find(x => x.jobId === jobId);
    if (j) emit({ progress: jobFraction(j, 1) });
  };
  const offStore = subscribeVideoStore(push);
  const offApi = videoApi.subscribe(ev => {
    if (!('jobId' in ev) || ev.jobId !== jobId) return;
    if (ev.type === 'video_edit_completed') {
      const n = ev.variants.length;
      if (ev.error) emit({ error: ev.error });
      else emit({ result: { summary: n > 1 ? `Готово: ${n} ${plural(n, 'вариант', 'варианта', 'вариантов')}` : 'Клип снят', open } });
      offStore(); offApi?.();
    } else if (ev.type === 'video_edit_failed') {
      emit(cancelAsked ? { cancelled: true } : { error: ev.error ?? 'Съёмка не получилась' });
      offStore(); offApi?.();
    }
  });
  push();
  return {
    cancel: async () => { cancelAsked = true; await stopJob(scope, sessionId, jobId); },
    watch(on: (e: Ev) => void) {
      listener = on;
      buffered.splice(0).forEach(on);
      return () => { listener = null; offStore(); offApi?.(); };
    },
  };
}

// Сборка фильма: статус едет в FilmState.build (video_film_changed), его читаем из стора
function buildWatcher(sessionId: string, path: string, first: FilmBuildStatus) {
  return {
    watch(on: (e: Ev) => void) {
      const check = () => {
        const b = getFilm(sessionId, path).state?.build ?? first;
        if (b.state === 'waiting') on({ progress: 0.05 });
        else if (b.state === 'running') on({ progress: Math.max(0.05, b.progress) });
        else if (b.state === 'failed') on({ error: b.error || 'Сборка не получилась' });
        else if (b.state === 'cancelled') on({ cancelled: true });
        else on({ result: { summary: 'Фильм собран', open: b.file ? () => { void openProjectFile(b.file!); } : undefined } });
      };
      const off = subscribeVideoStore(check);
      check();
      return off;
    },
  };
}

async function launchShoot(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  const { scope, body } = quoteBody(ctx, req);
  const { scene } = currentScene(ctx);
  // Котировка с теми же параметрами, что у запуска: иначе сервер откажет в запуске
  const quote = await videoApi.quote(scope, ctx.sessionId, body);
  const ask = req.text.trim();
  const { jobId } = await videoApi.startJob(scope, ctx.sessionId, {
    quoteId: quote.quoteId, sessionId: ctx.sessionId, sceneId: '', ...(ask ? { params: { request: ask } } : {}),
    contextRevision: req.contextRevision,
  });
  if (!jobId) throw new Error('Запуск не удался');
  const w = jobWatcher(scope, ctx.sessionId, scene.sceneId, jobId, () => { revealContextPanel(ctx.sessionId); });
  return { id: jobId, watch: w.watch, cancel: w.cancel };
}

async function launchBuild(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  const scope = videoScope(ctx.projectId);
  const path = filmPathOf(getChatContextState(ctx.sessionId).primary);
  if (!path || isPersonalScope(scope)) throw new Error('Фильм недоступен');
  const first = await videoApi.buildFilm(scope, ctx.sessionId, path, req.contextRevision);
  setFilmBuild(ctx.sessionId, path, first);
  return {
    id: `build:${path}`, watch: buildWatcher(ctx.sessionId, path, first).watch,
    cancel: () => cancelBuild(scope, ctx.sessionId, path),
  };
}

export async function launchAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  try {
    if (req.op === 'build') return await launchBuild(ctx, req);
    if (req.op === 'shoot') return await launchShoot(ctx, req);
  } catch (e) {
    if (isContextChanged(e)) throw e;
    throw new Error(errorCode(e) === ERR.dspUnavailable ? DSP_TEXT : req.op === 'build' ? errorText(e, 'Сборка не запустилась') : runErrorText(e), { cause: e });
  }
  throw new Error(`Операция «${req.op}» недоступна`);
}
