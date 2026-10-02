// Живой статус задачи нити (pendingJobId): события image_edit_* плюс догон GET после
// обрыва. Проценты — от ожидаемой длительности прогона: поставщики их не присылают.

import { useEffect, useMemo, useState } from 'react';
import { onReconnected, showToast } from 'aihome_shell/kit';
import { imageEditorApi, type EditCost, type ImageEditJob } from '../api';

export type JobPhase = 'run' | 'done' | 'cancel' | 'error' | 'lost';

export interface JobStatus {
  phase: JobPhase;
  variants: number[];
  count: number;
  cost: EditCost | null;
  charged: boolean | null;
  error: string | null;
  model: string | null;
  createdAt: number | null;
  // Полоса прогресса: номер прогона (с 1), их число и ETA прогона — от драйвера, который их знает
  // (local); runStartedAt — начало текущего прогона по ЛОКАЛЬНЫМ часам, выведенное из
  // runElapsedSeconds бэкенда (метку сервера с часами браузера не сравниваем)
  run: number | null;
  runs: number | null;
  eta: number | null;
  runStartedAt: number | null;
  queuePosition: number | null;
  // ETA из котировки задачи — если прогон свой не сообщил
  fallbackEta: number | null;
}

const localStart = (elapsed: number | null | undefined) => (elapsed == null ? null : Date.now() - elapsed * 1000);

function statusOf(job: ImageEditJob): JobStatus {
  const base = {
    variants: job.variants, count: job.variants.length, cost: job.cost ?? null, charged: job.charged ?? null,
    error: job.error ?? null, model: job.model, createdAt: Date.parse(job.createdAt) || null,
    run: job.run ?? null, runs: job.runs ?? null, eta: job.etaSeconds ?? null,
    runStartedAt: localStart(job.runElapsedSeconds), queuePosition: job.queuePosition ?? null,
    fallbackEta: job.estimate?.etaSeconds ?? null,
  };
  switch (job.status) {
    case 'completed': return { ...base, phase: 'done' };
    case 'cancelled': return { ...base, phase: 'cancel' };
    case 'failed': return { ...base, phase: job.outcome === 'cancelled' ? 'cancel' : 'error' };
    case 'interrupted': return { ...base, phase: 'lost' };
    default: return { ...base, phase: 'run' };
  }
}

const LOST: JobStatus = {
  phase: 'lost', variants: [], count: 0, cost: null, charged: null, error: null, model: null, createdAt: null,
  run: null, runs: null, eta: null, runStartedAt: null, queuePosition: null, fallbackEta: null,
};

export function useJobStatus(projectId: string, jobId: string | null) {
  const api = useMemo(() => imageEditorApi(), []);
  const [state, setState] = useState<{ jobId: string; status: JobStatus } | null>(null);
  useEffect(() => {
    if (!jobId) return;
    let alive = true;
    const set = (fn: (s: JobStatus | null) => JobStatus) =>
      setState(prev => ({ jobId, status: fn(prev?.jobId === jobId ? prev.status : null) }));
    const load = () => api.getJob(projectId, jobId)
      .then(job => { if (alive) set(() => statusOf(job)); })
      // Задачи нет: бэкенд перезапускался, а задачи живут в памяти
      .catch(() => { if (alive) set(s => s ?? LOST); });
    void load();
    const off = api.subscribe(e => {
      if (e.jobId !== jobId) return;
      if (e.type === 'image_edit_progress') {
        set(s => {
          // Запоздавший прогресс не возвращает кончившуюся задачу в «Рисуем…»
          if (s && s.phase !== 'run' && s.phase !== 'lost') return s;
          const prev = s ?? { ...LOST, phase: 'run' as const };
          return {
            ...prev, phase: 'run', run: e.run ?? null, runs: e.runs ?? null, eta: e.etaSeconds ?? null,
            queuePosition: e.stage === 'queued' ? e.queuePosition ?? null : null,
            // Скачивание — хвост того же прогона: отсчёт не сбрасываем
            runStartedAt: e.stage === 'running' ? localStart(e.runElapsedSeconds ?? 0)
              : e.stage === 'queued' ? null : prev.runStartedAt,
          };
        });
      } else if (e.type === 'image_edit_completed') {
        set(s => ({ ...(s ?? LOST), phase: 'done', variants: e.variants, count: e.variants.length, cost: e.cost ?? null }));
      } else if (e.type === 'image_edit_failed') {
        set(s => ({ ...(s ?? LOST), phase: e.outcome === 'cancelled' ? 'cancel' : 'error', charged: e.charged ?? null, error: e.error ?? null }));
      }
    });
    const offRe = onReconnected(() => { void load(); });
    return () => { alive = false; off(); offRe(); };
  }, [api, projectId, jobId]);

  const status = state && state.jobId === jobId ? state.status : null;

  const cancel = async () => {
    if (!jobId) return;
    try {
      const job = await api.cancelJob(projectId, jobId);
      setState({ jobId, status: statusOf(job) });
    } catch (e) {
      showToast(`Не удалось отменить: ${(e as Error).message}`, '', 'error');
    }
  };
  return { status, cancel };
}

const DEFAULT_ETA = 30;

// Хвост строки «Рисуем…», пока задача стоит в очереди
export const queueText = (position: number | null) => (position ? ` · в очереди ${position}` : '');

// Проценты задачи из прогонов: готовые прогоны целиком плюс доля текущего, внутри прогона — не
// выше 95 %, пока он не кончился. Та же формула, что progress_estimate у local-media MCP (Main):
// импортировать её модуль не может, поэтому одна строка продублирована. В очереди доля — ноль
export function progressPercent({ run, runs, etaSeconds, elapsed, fallbackEta, queued }: {
  run: number | null; runs: number | null; etaSeconds: number | null; elapsed: number;
  fallbackEta?: number | null; queued?: boolean;
}): number {
  const total = Math.max(1, runs ?? 1);
  const current = Math.min(total, Math.max(1, run ?? 1));
  const eta = Math.max(1, etaSeconds ?? fallbackEta ?? DEFAULT_ETA);
  const share = queued ? 0 : Math.min(0.95, Math.max(0, elapsed) / eta);
  return ((current - 1 + share) / total) * 100;
}

// Полоса задачи: тикает раз в 500 мс, пока идёт. В очереди полоса стоит, а queuePosition даёт
// подпись «в очереди N». fallbackEta — ожидаемое время из котировки, если задача своё не знает
export function useProgress(status: JobStatus | null, running: boolean, fallbackEta: number | null = null) {
  const [mounted] = useState(() => Date.now());
  const [now, setNow] = useState(mounted);
  useEffect(() => {
    if (!running) return;
    const t = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(t);
  }, [running]);
  const queuePosition = running && status?.queuePosition ? status.queuePosition : null;
  return { percent: jobPercent(status, now, mounted, fallbackEta, queuePosition != null), queuePosition };
}

// Проценты задачи на момент now. Задачу без своих прогонов (fal, Higgsfield) считаем от создания
// через все стадии: отсчёт от перехода в Running откатывал полосу назад
export function jobPercent(status: JobStatus | null, now: number, mounted: number,
  fallbackEta: number | null = null, queued = false): number {
  const start = status?.run != null ? status.runStartedAt ?? now : status?.createdAt ?? mounted;
  return progressPercent({
    run: status?.run ?? null, runs: status?.runs ?? null, etaSeconds: status?.eta ?? null,
    elapsed: (now - start) / 1000, fallbackEta: status?.fallbackEta ?? fallbackEta, queued,
  });
}
