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
  getCatalog, getFailure, setPriceHint, getFocusedScene, getJobsOf, sceneDraftKey, useVideoStoreVersion, useVideoThreads,
  type JobProgress, type LaunchFailure,
} from '../store/videoStore';
import { isPersonalScope, videoScope } from '../scope';

const QUOTE_DELAY = 600;

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

const headline = (q: VideoQuote) => priceLines(q, q.count, q.durationSec)?.[0] ?? null;

export const progressLabel = (scene: VideoScene | null, jobs: JobProgress[], count: number): { label: string; p?: number } => {
  const j = jobs[0];
  const name = scene?.name ?? 'Сцена';
  if (!j) return { label: `${name}: запускаем…` };
  if (j.stage === 'queued') {
    return { label: j.queuePosition ? `${name}: GPU, ${j.queuePosition}-я в очереди` : `${name}: ждём очередь` };
  }
  const total = j.count || count;
  const word = plural(total, 'вариант', 'варианта', 'вариантов');
  return { label: `${name}: снимаем ${total} ${word} · ${Math.round(((j.variant - 1) / total) * 100 + 100 / total / 2)} %`, p: Math.round(((j.variant - 1) / total) * 100 + 100 / total / 2) };
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
  const failure = getFailure(sessionId, scene?.sceneId ?? null);
  const draftKey = scene ? sceneDraftKey(scene.sceneId) : null;

  // Котировка: цена и ETA до запуска; пересчёт по смыслу настроек, а не по ссылкам
  const [quote, setQuote] = useState<VideoQuote | null>(null);
  const [quoteError, setQuoteError] = useState<string | null>(null);
  const providerKey = r.provider?.key ?? null;
  const modelId = r.model?.id ?? null;
  const canQuote = !!sessionId && !!scene && !!catalog && catalog.providers.some(p => p.available);
  useEffect(() => {
    setQuote(null);
    setQuoteError(null);
    if (!canQuote || !sessionId || !scene) return;
    let alive = true;
    const t = setTimeout(() => {
      videoApi.quote(scope, sessionId, {
        sessionId, sceneId: scene.sceneId, ...(providerKey && modelId ? { provider: providerKey, model: modelId } : {}),
        count: r.count, durationSec: r.durationSec, aspect: r.aspect, sound: r.sound,
      }).then(
        q => { if (alive) setQuote(q); },
        (e: Error) => { if (alive) setQuoteError(e.message || 'Котировка не получилась'); },
      );
    }, QUOTE_DELAY);
    return () => { alive = false; clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по смыслу настроек
  }, [canQuote, scope, sessionId, scene?.sceneId, providerKey, modelId, r.count, r.durationSec, r.aspect, r.sound]);

  const anyAvailable = !!catalog?.providers.some(p => p.available);
  const providerOk = r.auto ? anyAvailable : !!r.provider?.available;
  const reason = runReason({
    sessionId, r, personal, providerOk,
    providerReason: !catalog ? 'Загружаем каталог…' : !anyAvailable ? 'Снимать нечем: поставщиков не настроил администратор' : r.provider?.reason,
    quoteError, running,
  });

  useEffect(() => {
    if (sessionId && scene) setPriceHint(sessionId, scene.sceneId, quote ? headline(quote) : null);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по сцене и котировке
  }, [sessionId, scene?.sceneId, quote]);

  const run = async (extra = '') => {
    if (!sessionId || !scene || !quote || reason) return false;
    return runScene({ scope, sessionId, scene, r, quote }, extra);
  };

  const prog = progressLabel(scene, jobs, r.count);
  const price = priceLines(quote, r.count, r.durationSec);
  const localQueue = providerKey === LOCAL_PROVIDER || quote?.price.unit === 'free';
  const queue = localQueue && quote?.price.queueLength ? `очередь GPU: ${quote.price.queueLength}` : undefined;
  const base = {
    reason: reason ?? undefined,
    queue,
    count: r.count,
    price: price ?? (scene ? undefined : undefined),
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
