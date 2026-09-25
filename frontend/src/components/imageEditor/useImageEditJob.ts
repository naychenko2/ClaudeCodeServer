// Жизнь задачи генерации (ADR-016, раздел 7): 202 { jobId } → SignalR-события
// image_edit_* → варианты или ошибка. Проценты у поставщиков не приходят, поэтому
// рисуем их от ожидаемой длительности из котировки. Потерянное событие догоняем
// GET …/jobs/{jobId} после переподключения.

import { useCallback, useEffect, useRef, useState } from 'react';
import { onReconnected } from '../../lib/signalr';
import type {
  EditCost, EditOutcome, ImageEditJob, ImageEditJobInput, ImageEditorApi, ImageEditQuote,
} from '../../api/imageEditor';

export type JobPhase = 'idle' | 'starting' | 'running' | 'variants' | 'error';

export interface JobFailure { outcome: EditOutcome; charged: boolean | null; error: string | null; retryQuote: ImageEditQuote | null }

interface State {
  phase: JobPhase;
  jobId: string | null;
  count: number;
  progress: number;
  downloading: boolean;
  variants: number[];
  cost: EditCost | null;
  failure: JobFailure | null;
}

const IDLE: State = { phase: 'idle', jobId: null, count: 0, progress: 0, downloading: false, variants: [], cost: null, failure: null };

export function useImageEditJob(api: ImageEditorApi, projectId: string) {
  const [s, setS] = useState<State>(IDLE);
  const jobRef = useRef<{ id: string; started: number; expectedMs: number } | null>(null);

  // Итоговое состояние задачи из GET — для догонялки после обрыва
  const applyJob = useCallback((job: ImageEditJob) => {
    if (jobRef.current?.id !== job.jobId) return;
    if (job.status === 'completed') {
      jobRef.current = null;
      setS(p => ({ ...p, phase: 'variants', progress: 100, variants: job.variants, cost: job.cost ?? null }));
    } else if (job.status === 'failed' || job.status === 'interrupted') {
      jobRef.current = null;
      setS(p => ({ ...p, phase: 'error', failure: {
        outcome: job.outcome ?? 'failed', charged: job.charged ?? null, error: job.error ?? null, retryQuote: null,
      } }));
    } else if (job.status === 'downloading') {
      setS(p => ({ ...p, downloading: true }));
    }
  }, []);

  useEffect(() => api.subscribe(e => {
    if (jobRef.current?.id !== e.jobId) return;
    if (e.type === 'image_edit_progress') {
      setS(p => ({ ...p, downloading: e.stage === 'downloading' }));
    } else if (e.type === 'image_edit_completed') {
      jobRef.current = null;
      setS(p => ({ ...p, phase: 'variants', progress: 100, variants: e.variants, cost: e.cost ?? null }));
    } else {
      jobRef.current = null;
      setS(p => ({ ...p, phase: 'error', failure: {
        outcome: e.outcome, charged: e.charged ?? null, error: e.error ?? null, retryQuote: e.retryQuote ?? null,
      } }));
    }
  }), [api]);

  useEffect(() => onReconnected(() => {
    const id = jobRef.current?.id;
    if (id) api.getJob(projectId, id).then(applyJob).catch(() => { /* задачи нет — дождёмся события */ });
  }), [api, projectId, applyJob]);

  // Проценты от ожидаемой длительности; до конца дорисовывает событие completed
  useEffect(() => {
    if (s.phase !== 'running') return;
    const t = setInterval(() => {
      const j = jobRef.current;
      if (!j) return;
      const pct = Math.min(95, ((Date.now() - j.started) / j.expectedMs) * 90);
      setS(p => ({ ...p, progress: Math.max(p.progress, p.downloading ? Math.max(pct, 90) : pct) }));
    }, 250);
    return () => clearInterval(t);
  }, [s.phase]);

  const start = useCallback(async (input: ImageEditJobInput, count: number, expectedSeconds?: number | null) => {
    setS({ ...IDLE, phase: 'starting', count });
    try {
      const { jobId } = await api.startJob(projectId, input);
      jobRef.current = { id: jobId, started: Date.now(), expectedMs: Math.max(5, expectedSeconds ?? 30) * 1000 };
      setS(p => ({ ...p, phase: 'running', jobId }));
      // Событие могло прийти раньше, чем мы узнали jobId, — сверяемся с сервером
      api.getJob(projectId, jobId).then(applyJob).catch(() => { /* ждём событие */ });
    } catch (e) {
      jobRef.current = null;
      setS(p => ({ ...p, phase: 'error', failure: { outcome: 'failed', charged: false, error: (e as Error).message, retryQuote: null } }));
    }
  }, [api, projectId, applyJob]);

  const cancel = useCallback(async () => {
    const id = jobRef.current?.id;
    jobRef.current = null;
    setS(IDLE);
    if (id) await api.cancelJob(projectId, id).catch(() => { /* задача уже закончилась */ });
  }, [api, projectId]);

  const reset = useCallback(() => { jobRef.current = null; setS(IDLE); }, []);

  return { ...s, start, cancel, reset };
}
