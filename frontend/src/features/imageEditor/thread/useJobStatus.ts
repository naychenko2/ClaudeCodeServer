// Живой статус задачи нити (pendingJobId): события image_edit_* плюс догон GET после
// обрыва. Проценты — от ожидаемой длительности: поставщики их не присылают.

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
}

function statusOf(job: ImageEditJob): JobStatus {
  const base = {
    variants: job.variants, count: job.variants.length, cost: job.cost ?? null, charged: job.charged ?? null,
    error: job.error ?? null, model: job.model, createdAt: Date.parse(job.createdAt) || null,
  };
  switch (job.status) {
    case 'completed': return { ...base, phase: 'done' };
    case 'cancelled': return { ...base, phase: 'cancel' };
    case 'failed': return { ...base, phase: job.outcome === 'cancelled' ? 'cancel' : 'error' };
    case 'interrupted': return { ...base, phase: 'lost' };
    default: return { ...base, phase: 'run' };
  }
}

const LOST: JobStatus = { phase: 'lost', variants: [], count: 0, cost: null, charged: null, error: null, model: null, createdAt: null };

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
      if (e.type === 'image_edit_completed') {
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

// Проценты от ожидаемой длительности
export function useProgress(running: boolean, startedAt: number | null, expectedSeconds: number | null = null): number {
  const [mounted] = useState(() => Date.now());
  const [now, setNow] = useState(mounted);
  useEffect(() => {
    if (!running) return;
    const t = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(t);
  }, [running]);
  const expected = Math.max(5, expectedSeconds ?? 30) * 1000;
  return Math.max(0, Math.min(95, ((now - (startedAt ?? mounted)) / expected) * 90));
}
