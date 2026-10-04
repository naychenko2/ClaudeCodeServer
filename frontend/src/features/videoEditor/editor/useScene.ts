// Состояние вкладки «Сцена»: настройки (сцена → правка → префы), котировка, причина отказа,
// ход съёмки и закреплённый низ. Один хук на панель и композер.

import { useEffect, useState } from 'react';
import { clearGenDraft, type GenerationFoot } from 'aihome_shell/kit';
import { videoApi, type VideoCatalog, type VideoQuote, type VideoScene } from '../api';
import { changeSettings, currentResolved, runScene, stopJob } from '../scene/actions';
import {
  hasClip, LOCAL_PROVIDER, plural, priceLines, runReason, type ResolvedScene,
} from '../scene/model';
import {
  getCatalog, getFailure, setPriceHint, getFocusedScene, getJobsOf, jobFraction, sceneDraftKey, useJobTick, useVideoStoreVersion, useVideoThreads,
  type JobProgress, type LaunchFailure,
} from '../store/videoStore';
import { isPersonalScope, videoScope } from '../scope';

const QUOTE_DELAY = 600;

// Пока цены нет — как у «Картинок»: честно «уточняется», а не пустое место
const PRICE_PENDING = (count: number, sec: number): [string, string] => ['Цена уточняется', `${count} ${plural(count, 'вариант', 'варианта', 'вариантов')} · ${sec} с`];

export interface SceneModel {
  scope: string;
  personal: boolean;
  sessionId: string | null;
  scene: VideoScene | null;
  catalog: VideoCatalog | null;
  r: ResolvedScene;
  change: (patch: Parameters<typeof changeSettings>[2], debounced?: boolean) => void;
  quote: VideoQuote | null;
  quoteError: string | null;
  reason: string | null;
  running: boolean;
  jobs: JobProgress[];
  failure: LaunchFailure | null;
  draftKey: string | null;
  foot: GenerationFoot;
  run: (extra?: string) => Promise<boolean>;
}

export const headline = (q: VideoQuote) => priceLines(q, q.count, q.durationSec)?.[0] ?? null;

// Одна котировка на набор настроек: панель, полоса и кнопка композера смотрят в один запрос
const QUOTE_TTL = 15_000;
const _quotes = new Map<string, { at: number; p: Promise<VideoQuote> }>();
function quoteOnce(scope: string, sessionId: string, body: Parameters<typeof videoApi.quote>[2]): Promise<VideoQuote> {
  const key = [scope, sessionId, body.sceneId, body.provider, body.model, body.count, body.durationSec, body.aspect, body.sound].join('|');
  const hit = _quotes.get(key);
  if (hit && Date.now() - hit.at < QUOTE_TTL) return hit.p;
  const p = videoApi.quote(scope, sessionId, body);
  _quotes.set(key, { at: Date.now(), p });
  p.catch(() => { if (_quotes.get(key)?.p === p) _quotes.delete(key); });
  return p;
}

// Котировка: цена и ETA до запуска; пересчёт по смыслу настроек, а не по ссылкам. Цена уходит в стор —
// из него её берут полоса и кнопка композера, пока панель закрыта
export function useSceneQuote(scope: string, sessionId: string | null, scene: VideoScene | null, catalog: VideoCatalog | null, r: ResolvedScene) {
  const [quote, setQuote] = useState<VideoQuote | null>(null);
  const [quoteError, setQuoteError] = useState<string | null>(null);
  const providerKey = r.provider?.key ?? null;
  const modelId = r.model?.id ?? null;
  // Без двух кадров сервер отвечает 409: котировку не просим, причина «нужен кадр» видна и так
  const canQuote = !!sessionId && !!scene && !!catalog && catalog.providers.some(p => p.available) && !!r.frameA && !!r.frameB;
  useEffect(() => {
    setQuote(null);
    setQuoteError(null);
    if (!canQuote || !sessionId || !scene) return;
    let alive = true;
    const t = setTimeout(() => {
      quoteOnce(scope, sessionId, {
        sessionId, sceneId: scene.sceneId, ...(providerKey && modelId ? { provider: providerKey, model: modelId } : {}),
        count: r.count, durationSec: r.durationSec, aspect: r.aspect, sound: r.sound,
      }).then(
        q => { if (alive) setQuote(q); },
        (e: Error) => { if (alive) setQuoteError(e.message || 'Цена не посчиталась'); },
      );
    }, QUOTE_DELAY);
    return () => { alive = false; clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по смыслу настроек
  }, [canQuote, scope, sessionId, scene?.sceneId, providerKey, modelId, r.count, r.durationSec, r.aspect, r.sound]);
  useEffect(() => {
    if (sessionId && scene) setPriceHint(sessionId, scene.sceneId, quote ? headline(quote) : null);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по сцене и котировке
  }, [sessionId, scene?.sceneId, quote]);
  return { quote, quoteError, setQuoteError };
}

export const progressLabel = (scene: VideoScene | null, jobs: JobProgress[], count: number): { label: string; p?: number } => {
  const j = jobs[0];
  const name = scene?.name ?? 'Сцена';
  if (!j) return { label: `${name}: запускаем…` };
  if (j.stage === 'queued') {
    return { label: j.queuePosition ? `${name}: GPU, ${j.queuePosition}-я в очереди` : `${name}: ждём очередь` };
  }
  const total = j.count || count;
  const word = plural(total, 'вариант', 'варианта', 'вариантов');
  const p = Math.round(jobFraction(j) * 100);
  return { label: `${name}: снимаем ${total} ${word} · ${p} %`, p };
};

export function useScene(projectId: string | null, sessionId: string | null): SceneModel {
  const scope = videoScope(projectId);
  const personal = isPersonalScope(scope);
  useVideoThreads(scope, sessionId);
  useVideoStoreVersion();
  const scene = getFocusedScene(sessionId);
  const catalog = getCatalog(scope);
  const r = currentResolved(sessionId, scope, scene);
  const change = (patch: Parameters<typeof changeSettings>[2], debounced = false) => {
    if (sessionId) changeSettings(scope, sessionId, patch, debounced);
  };
  const jobs = getJobsOf(sessionId, scene?.sceneId ?? null);
  const launching = !!scene?.launches.some(l => l.status === 'running');
  const running = launching || jobs.length > 0;
  useJobTick(jobs.length > 0);
  const failure = getFailure(sessionId, scene?.sceneId ?? null);
  const draftKey = scene ? sceneDraftKey(scene.sceneId) : null;

  const providerKey = r.provider?.key ?? null;
  const modelId = r.model?.id ?? null;
  const { quote, quoteError, setQuoteError } = useSceneQuote(scope, sessionId, scene, catalog, r);

  const anyAvailable = !!catalog?.providers.some(p => p.available);
  const providerOk = r.auto ? anyAvailable : !!r.provider?.available;
  const reason = runReason({
    sessionId, r, personal, providerOk,
    providerReason: !catalog ? 'Загружаем каталог…' : !anyAvailable ? 'Снимать нечем: поставщиков не настроил администратор' : r.provider?.reason,
    quoteError, running,
  });

  // Цена ещё в пути (клик сразу после правки) — котировку берём тут же: запуск всё равно строго по ней
  const run = async (extra = '') => {
    if (!sessionId || !scene || reason) return false;
    const q = quote ?? await videoApi.quote(scope, sessionId, {
      sessionId, sceneId: scene.sceneId, ...(providerKey && modelId ? { provider: providerKey, model: modelId } : {}),
      count: r.count, durationSec: r.durationSec, aspect: r.aspect, sound: r.sound,
    }).catch((e: Error) => { setQuoteError(e.message || 'Цена не посчиталась'); return null; });
    if (!q) return false;
    return runScene({ scope, sessionId, scene, r, quote: q }, extra);
  };

  const prog = progressLabel(scene, jobs, r.count);
  const price = priceLines(quote, r.count, r.durationSec);
  const localQueue = providerKey === LOCAL_PROVIDER || quote?.price.unit === 'free';
  const queue = localQueue && quote?.price.queueLength ? `очередь GPU: ${quote.price.queueLength}` : undefined;
  const base = {
    reason: reason ?? undefined,
    queue,
    count: r.count,
    price: price ?? PRICE_PENDING(r.count, r.durationSec),
    runLabel: hasClip(scene) ? 'Переснять' : 'Снять',
    onRun: () => { void run().then(ok => { if (ok) clearGenDraft(draftKey); }); },
  };
  const foot: GenerationFoot = running && scene
    ? {
      ...base,
      count: r.count, maxCount: catalog?.maxCount ?? 4, onCountChange: () => {},
      progress: {
        label: prog.label, p: prog.p,
        onCancel: sessionId && jobs[0] ? () => { void stopJob(scope, sessionId, jobs[0].jobId); } : undefined,
      },
    }
    : { ...base, maxCount: catalog?.maxCount ?? 4, onCountChange: (n: number) => change({ count: n }) };
  return { scope, personal, sessionId, scene, catalog, r, change, quote, quoteError, reason, running, jobs, failure, draftKey, foot, run };
}
