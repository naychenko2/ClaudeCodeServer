import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../../lib/api';
import type { SpendAggregate, DailySpendPoint, ProjectSpendSummary, ModelSpendSummary, SpendEntry, UserSpendSummary } from '../../types';

// Пресеты периодов
export type PeriodPreset = '24h' | '7d' | '30d' | '90d' | 'custom';

export interface Period {
  preset: PeriodPreset;
  from: Date;
  to: Date;
}

export function usePeriod(defaultPreset: PeriodPreset = '30d'): [Period, (p: PeriodPreset) => void] {
  const [preset, setPreset] = useState<PeriodPreset>(defaultPreset);
  const period = useMemo(() => {
    const now = new Date();
    const to = new Date(now);
    let from: Date;
    switch (preset) {
      case '24h': from = new Date(now.getTime() - 24 * 60 * 60 * 1000); break;
      case '7d': from = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000); break;
      case '30d': from = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000); break;
      case '90d': from = new Date(now.getTime() - 90 * 24 * 60 * 60 * 1000); break;
      default: from = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
    }
    return { preset, from, to };
  }, [preset]);
  return [period, setPreset];
}

function iso(d: Date) { return d.toISOString(); }

export function useAggregate(period: Period, projectId?: string, provider?: string, model?: string) {
  const [data, setData] = useState<SpendAggregate | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.aggregate(iso(period.from), iso(period.to), projectId, provider, model)
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime(), projectId, provider, model]);
  return { data, loading };
}

export function useDaily(period: Period, projectId?: string, provider?: string) {
  const [data, setData] = useState<DailySpendPoint[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.daily(iso(period.from), iso(period.to), projectId, provider)
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime(), projectId, provider]);
  return { data, loading };
}

export function useByProject(period: Period) {
  const [data, setData] = useState<ProjectSpendSummary[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.byProject(iso(period.from), iso(period.to))
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime()]);
  return { data, loading };
}

export function useByModel(period: Period) {
  const [data, setData] = useState<ModelSpendSummary[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.byModel(iso(period.from), iso(period.to))
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime()]);
  return { data, loading };
}

export function useEntries(period: Period, projectId?: string, sessionId?: string, source?: string, limit = 100, offset = 0) {
  const [data, setData] = useState<SpendEntry[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.entries(iso(period.from), iso(period.to), projectId, sessionId, source, limit, offset)
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime(), projectId, sessionId, source, limit, offset]);
  return { data, loading };
}

export function useAdminAggregate(period: Period) {
  const [data, setData] = useState<UserSpendSummary[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    setLoading(true);
    api.spend.adminAggregate(iso(period.from), iso(period.to))
      .then(setData).finally(() => setLoading(false));
  }, [period.from.getTime(), period.to.getTime()]);
  return { data, loading };
}

export function useBoundary() {
  const [since, setSince] = useState<string | null>(null);
  useEffect(() => {
    api.spend.boundary().then(b => setSince(b.since ?? null)).catch(() => {});
  }, []);
  return since;
}

// Формат денег
export const fmtMoney = (c: number | null | undefined) => {
  if (c == null) return '—';
  return '$' + (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
};

// Формат токенов
export const fmtTokens = (n: number) => {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M';
  if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K';
  return String(n);
};

// Формат даты
export const fmtDate = (iso: string) => {
  const d = new Date(iso);
  return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
};

export const PRESET_LABELS: Record<PeriodPreset, string> = {
  '24h': '24 часа',
  '7d': '7 дней',
  '30d': '30 дней',
  '90d': '90 дней',
  'custom': 'Свой',
};
