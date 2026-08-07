// Вкладка «Квоты и деньги» раздела «Модели и расход» (макет docs/mockups/models-spend-v3.html §1).
// KPI-лента → переход в аналитику → карточки квот → денежные балансы (админ) → все провайдеры.
// Состояния §4: скелетон, ошибка-баннер, протухшие данные, пусто, недоступная/исчерпанная квота.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, ExternalLink } from 'lucide-react';
import type { ProviderBalanceInfo, SpendOverviewResponse, UsageResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, GROUP_COLORS, R, SP } from '../../lib/design';
import { Button, Dot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import {
  cliProviderKeys, getModels, getProviders, providerCapsByKey, providerLabel, useModels,
} from '../../lib/models';
import { latestPerWindow, windowLabel } from '../../lib/rateLimit';
import { addDaysUtc, openSpend, plural, spendQuery, todayUtc } from '../../lib/spend';
import { freeSourceLabel, isFreeSource } from '../../lib/spendSources';
import { showToast } from '../../lib/toast';
import { KpiRibbon } from './KpiRibbon';
import { ProviderCard } from './ProviderCard';
import type { ProviderCardData } from './ProviderCard';

// === Локальные хелперы (не вынесены в lib — повторяем) ===

const STALE_MS = 30 * 60 * 1000;
// Каденс авто-ретрая упавших провайдеров (и обновления снимков): те же 60с, что и
// интервал опроса ниже. Счётчик «повтор через N сек» на карточке ошибки идёт от этого окна.
const RETRY_INTERVAL_MS = 60_000;
const fmtAgo = (iso: string) => {
  const t = new Date(iso).getTime();
  if (isNaN(t)) return null;
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return 'только что';
  if (mins < 60) return `${mins} мин назад`;
  const h = Math.floor(mins / 60);
  return h < 24 ? `${h} ч назад` : `${Math.floor(h / 24)} дн назад`;
};
const fmtClock = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
};
const isStale = (iso?: string | null) => {
  if (!iso) return false;
  const t = new Date(iso).getTime();
  return !isNaN(t) && Date.now() - t > STALE_MS;
};
const money = (c: number) => (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));

const TOPUP_URL: Record<string, string> = {
  deepseek: 'https://platform.deepseek.com/top_up',
  openrouter: 'https://openrouter.ai/settings/credits',
};
const CABINET_URL: Record<string, string> = {
  glm: 'https://z.ai/manage-apikey/rate-limits',
  deepseek: 'https://platform.deepseek.com/usage',
  openrouter: 'https://openrouter.ai/activity',
};

// Цвет точки провайдера — детерминированный из палитры групп (как на экране «Использование»)
const SOURCE_IDX: Record<string, number> = {
  glm: 0, minimax: 1, alibabacloud: 3, deepseek: 5, kimi: 6,
};
function sourceColor(key: string): string {
  const idx = SOURCE_IDX[key];
  if (idx != null) return GROUP_COLORS[idx];
  let h = 0;
  for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) >>> 0;
  return GROUP_COLORS[h % GROUP_COLORS.length];
}

type ProvUsage = { balance: ProviderBalanceInfo | null; snapshots: { timestamp: string; balance: number; currency: string }[] };
type ProvState = ProvUsage | undefined | null;   // undefined=загрузка, null=ошибка

// === Заголовок полосы ===
function Lane({ title, hint, right }: { title: string; hint?: string; right?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap', margin: `${SP.lg}px 0 ${SP.sm}px` }}>
      <div>
        <span style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{title}</span>
        {hint && <div style={{ marginTop: 2, fontSize: FS.xs, color: C.textMuted }}>{hint}</div>}
      </div>
      {right}
    </div>
  );
}

// === Денежная плитка (.mtile) — только админ ===
interface MoneyTileData {
  key: string; name: string; color: string;
  amount: number; asOf?: string | null;
  grantedBalance?: number | null;
  keyLimit?: { remaining: number; total: number } | null;
  spend?: { daily: number; weekly: number; monthly: number } | null;
  topupUrl?: string;
}
function MoneyTile({ d, stale }: { d: MoneyTileData; stale: boolean }) {
  // Предел ключа: осталось ≤20% ИЛИ <1$ → .low (dangerBg/border)
  const limit = d.keyLimit;
  const limitLow = limit && (limit.remaining / Math.max(0.0001, limit.total) <= 0.2 || limit.remaining < 1);
  const low = !!limitLow;
  const limitUsed = limit ? Math.min(100, Math.max(0, (1 - limit.remaining / Math.max(0.0001, limit.total)) * 100)) : null;

  const actionUrl = d.topupUrl ?? CABINET_URL[d.key];
  const actionLabel = limit ? 'поднять лимит ↗' : d.topupUrl ? 'пополнить ↗' : actionUrl ? `кабинет ${d.name} ↗` : null;

  return (
    <div style={{
      flex: '1 1 190px', minWidth: 170, maxWidth: 360,
      background: low ? C.dangerBg : C.bgCard, border: `1px solid ${low ? C.dangerBorder : C.border}`,
      borderRadius: R.xl, padding: `${SP.md}px ${SP.lg}px`, opacity: stale ? 0.55 : 1,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: 4 }}>
        <Dot color={d.color} size={9} />
        <span style={{ flex: 1, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{d.name}</span>
        {d.asOf && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: stale ? C.warningText : C.textMuted, whiteSpace: 'nowrap' }}>
            <Dot color={stale ? C.warning : C.success} size={6} />
            {stale ? `на ${fmtClock(d.asOf)}` : fmtAgo(d.asOf)}
          </span>
        )}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.h2, fontWeight: 700, color: low ? C.dangerText : C.textHeading }}>
        <span style={{ fontSize: FS.lg, color: C.textMuted }}>$</span>{money(d.amount)}
      </div>
      <div style={{ marginTop: 3, fontSize: FS.xs, color: C.textMuted }}>
        {limit
          ? `осталось из $${money(limit.total)} лимита ключа`
          : `баланс${d.grantedBalance ? ` · из них $${money(d.grantedBalance)} подарочных` : ''}`}
      </div>
      {/* Шкала лимита ключа (только когда есть предел) */}
      {limitUsed !== null && (
        <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ flex: 1, display: 'block', height: 6, borderRadius: 3, background: C.track, overflow: 'hidden' }}>
            <span style={{ display: 'block', width: `${Math.max(2, limitUsed)}%`, height: '100%', background: limitLow ? C.danger : C.warning }} />
          </span>
        </div>
      )}
      {/* Расход по данным провайдера */}
      {d.spend && (
        <div style={{ marginTop: 8, fontSize: FS.sm, color: C.textSecondary }}>
          ${money(d.spend.weekly)} расход за неделю
          {d.key === 'openrouter' && <span style={{ marginLeft: 4, fontSize: FS.xs, color: C.textMuted }}>по данным OpenRouter</span>}
        </div>
      )}
      {actionUrl && actionLabel && (
        <a href={actionUrl} target="_blank" rel="noreferrer"
          style={{ display: 'inline-flex', alignItems: 'center', gap: 5, marginTop: 8, fontSize: FS.xs, color: C.accent, textDecoration: 'none' }}>
          {actionLabel.replace(' ↗', '')} <ExternalLink size={12} strokeWidth={ICON_STROKE} />
        </a>
      )}
    </div>
  );
}

// === Чип провайдера в полосе «Все провайдеры» (.pchip) ===
function ProviderChip({ name, color, off, badge, count }: { name: string; color: string; off?: boolean; badge?: string | null; count?: string }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 7, padding: '6px 11px',
      background: C.bgWhite, border: off ? `1px dashed ${C.dashed}` : `1px solid ${C.border}`, borderRadius: 999,
      fontSize: FS.sm, color: off ? C.textMuted : C.textPrimary,
    }}>
      <Dot color={off ? C.textMuted : color} size={7} />
      {name}
      {badge && (
        <span style={{ background: C.successBg, border: `1px solid ${C.success}`, color: C.successText, padding: '1px 7px', borderRadius: R.md, fontSize: FS.xs, fontWeight: 600 }}>
          {badge}
        </span>
      )}
      {count && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{count}</span>}
    </span>
  );
}

export function QuotasTab({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  useModels();   // ре-рендер, когда каталог моделей догрузится (для pgrid-счётчика)

  const [me, setMe] = useState<Awaited<ReturnType<typeof api.auth.me>> | null>(null);
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [prov, setProv] = useState<Record<string, ProvState>>({});
  const [spend, setSpend] = useState<SpendOverviewResponse | null>(null);
  const [usageError, setUsageError] = useState(false);
  const [tick, setTick] = useState(0);
  // Момент (Date.now()) следующей авто-попытки дотянуть упавших провайдеров. Карточка
  // ошибки ведёт по нему обратный отсчёт «повтор через N сек», чтобы было видно, что
  // респроба идёт сама, а не ждёт ручного «Повторить». null — авто-ретрая нет на вкладке.
  const [retryAt, setRetryAt] = useState<number | null>(() => Date.now() + RETRY_INTERVAL_MS);
  // Предыдущий снимок состояний провайдеров — чтобы заметить переход ошибка → данные
  // (авто-ретрай вытянул упавшего) и коротко сообщить об этом тостом.
  const prevProvRef = useRef<Record<string, ProvState>>({});

  const isAdmin = me?.role === 'admin';

  const providerKeys = useMemo(() => cliProviderKeys(), []);
  const balanceKeys = useMemo(
    () => providerKeys.filter(k => { const c = providerCapsByKey(k); return c.hasBalance && c.configured !== false; }),
    [providerKeys],
  );

  const loadUsage = useCallback(() => {
    setUsageError(false);
    api.usage.get().then(setUsage).catch(() => { setUsageError(true); setUsage(null); });
  }, []);
  const loadProvider = useCallback((key: string) => {
    setProv(prev => ({ ...prev, [key]: undefined }));
    api.providers.usage(key).then(d => setProv(prev => ({ ...prev, [key]: d }))).catch(() => setProv(prev => ({ ...prev, [key]: null })));
  }, []);
  const loadSpend = useCallback(() => {
    api.spend.overview(spendQuery({ from: addDaysUtc(todayUtc(), -4), to: todayUtc(), scope: 'all' }))
      .then(setSpend).catch(() => setSpend(null));
  }, []);

  useEffect(() => {
    let c = false;
    api.auth.me().then(d => { if (!c) setMe(d); }).catch(() => { if (!c) setMe(null); });
    loadUsage();
    for (const key of balanceKeys) loadProvider(key);
    return () => { c = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick]);

  // costUsd нужен только админу — грузим, когда роль известна
  useEffect(() => { if (isAdmin) loadSpend(); }, [isAdmin, loadSpend]);

  // Раз в минуту — свежие снимки и повтор упавших (без визуала раньше выглядело, будто
  // ждёт ручного «Повторить»: теперь карточка ошибки ведёт обратный отсчёт до этого тика)
  useEffect(() => {
    const id = setInterval(() => {
      api.usage.get().then(setUsage).catch(() => {});
      setProv(prev => { for (const [k, v] of Object.entries(prev)) if (v === null) loadProvider(k); return prev; });
      setRetryAt(Date.now() + RETRY_INTERVAL_MS);
    }, RETRY_INTERVAL_MS);
    return () => clearInterval(id);
  }, [loadProvider]);

  // Восстановление упавшего провайдера (null → данные): авто-ретрай или ручной «Повторить»
  // вытянули баланс — короткий тост, чтобы человек понял, что связь вернулась сама.
  useEffect(() => {
    const prev = prevProvRef.current;
    const names: string[] = [];
    for (const [k, v] of Object.entries(prov)) {
      if (prev[k] === null && v) names.push(providerLabel(k));
    }
    prevProvRef.current = prov;
    if (names.length) showToast('Связь восстановлена', names.join(', '), 'info');
  }, [prov]);

  // === Вычисления ===

  // Пусто: ни квотных, ни денежных провайдеров, ни подписок Claude
  const hasAny = (usage?.subscriptions && Object.keys(usage.subscriptions).length > 0) || balanceKeys.length > 0;
  const loading = !me || (!usage && !usageError);

  // KPI «Ближе всего к пределу»: max usedPct среди percent-окон Claude (подписки) и CLI-провайдеров
  const hot = useMemo(() => {
    const cand: { usedPct: number; label: string }[] = [];
    for (const [key, sub] of Object.entries(usage?.subscriptions ?? {})) {
      for (const w of latestPerWindow(sub.snapshots)) {
        const name = sub.name ?? key;
        cand.push({ usedPct: w.pct, label: `${name} · ${windowLabel(w.limitType)}` });
      }
    }
    for (const [key, data] of Object.entries(prov)) {
      const bal = data?.balance;
      if (!bal) continue;
      for (const w of bal.windows ?? []) {
        if (w.unit !== 'percent') continue;
        const rem = parseFloat(w.value);
        if (isNaN(rem)) continue;
        cand.push({ usedPct: Math.round(Math.min(100, Math.max(0, 100 - rem))), label: `${providerLabel(key)} · ${w.label}` });
      }
    }
    return cand.sort((a, b) => b.usedPct - a.usedPct)[0] ?? null;
  }, [usage, prov]);

  // KPI «Деньги на счетах» (админ): сумма денежных балансов CLI-провайдеров
  const moneyKpi = useMemo(() => {
    if (!isAdmin) return null;
    let amount = 0, accounts = 0;
    for (const [, data] of Object.entries(prov)) {
      const bal = data?.balance;
      if (!bal) continue;
      const isQuota = bal.currency === '%' || (bal.windows?.length ?? 0) > 0;
      if (isQuota) continue;
      const num = parseFloat(bal.totalBalance);
      if (isNaN(num) || num <= 0) continue;
      amount += num; accounts++;
    }
    return accounts > 0 ? { amount, accounts } : null;
  }, [prov, isAdmin]);

  // KPI «По тарифам API · 5 дней» (админ): costUsd из SpendStore
  const costKpi = (isAdmin && spend?.costUsd != null) ? { amount: spend.costUsd } : null;

  // KPI «Бесплатно работают»: из здоровья FreeLLM
  const freeKpi = useMemo(() => {
    let alive = 0, total = 0, rate = 0; let found = false;
    for (const [, data] of Object.entries(prov)) {
      const h = data?.balance?.health;
      if (!h) continue;
      found = true;
      if (h.platformsAlive != null) alive += h.platformsAlive;
      if (h.platformsTotal != null) total += h.platformsTotal;
      if (h.successRate != null) rate = Math.max(rate, h.successRate);
    }
    return found && total > 0 ? { alive, total, successRate: rate } : null;
  }, [prov]);

  // Карточки квот (qgrid): квотные провайдеры + ненастроенные-без-баланса (Alibaba)
  const quotaCards: ProviderCardData[] = useMemo(() => {
    const out: ProviderCardData[] = [];
    for (const key of balanceKeys) {
      const data = prov[key];
      const bal = data?.balance ?? null;
      const isQuota = bal?.currency === '%' || (bal?.windows?.length ?? 0) > 0;
      if (data === null) {
        // Ошибка — тип источника неизвестен, живёт в квотах (строчная раскладка, «—» не ломает ряд)
        out.push({ key, name: providerLabel(key), color: sourceColor(key), balance: null, snapshots: [], state: 'error', isFree: isFreeSource(key), retryAt, cabinetUrl: CABINET_URL[key], onRetry: () => loadProvider(key) });
        continue;
      }
      if (data === undefined || !bal) {
        out.push({ key, name: providerLabel(key), color: sourceColor(key), balance: null, snapshots: [], state: 'loading', isFree: isFreeSource(key), cabinetUrl: CABINET_URL[key], onRetry: () => loadProvider(key) });
        continue;
      }
      if (isQuota) {
        out.push({
          key, name: providerLabel(key), color: sourceColor(key), balance: bal,
          snapshots: data.snapshots ?? [], state: 'ready', isFree: isFreeSource(key),
          cabinetUrl: CABINET_URL[key], onRetry: () => loadProvider(key),
        });
      }
    }
    // Настроен, но квоту не отдаёт (Alibaba Cloud) — отдельная строка
    for (const key of providerKeys) {
      const c = providerCapsByKey(key);
      if (c.hasBalance || c.configured === false) continue;
      if (balanceKeys.includes(key)) continue;
      out.push({ key, name: providerLabel(key), color: sourceColor(key), balance: null, snapshots: [], state: 'unavailable', isFree: isFreeSource(key), onRetry: () => {} });
    }
    return out;
  }, [prov, balanceKeys, providerKeys, loadProvider, retryAt]);

  // Денежные плитки (админ): денежные CLI-провайдеры
  const moneyTiles: MoneyTileData[] = useMemo(() => {
    if (!isAdmin) return [];
    const out: MoneyTileData[] = [];
    for (const key of balanceKeys) {
      const data = prov[key];
      const bal = data?.balance ?? null;
      if (!bal) continue;
      const isQuota = bal.currency === '%' || (bal.windows?.length ?? 0) > 0;
      if (isQuota) continue;   // источник с квотой И деньгами — в «Деньгах» только сумма
      const num = parseFloat(bal.totalBalance);
      if (isNaN(num)) continue;
      out.push({
        key, name: providerLabel(key), color: sourceColor(key), amount: num, asOf: bal.asOf ?? null,
        grantedBalance: bal.grantedBalance ?? null, keyLimit: bal.keyLimit ?? null, spend: bal.spend ?? null,
        topupUrl: TOPUP_URL[key],
      });
    }
    return out;
  }, [prov, balanceKeys, isAdmin]);

  // Все провайдеры (pgrid) — счётчик моделей из каталога
  const allProviders = useMemo(() => {
    const models = getModels();
    return getProviders().map(({ key, caps }) => {
      let count: string | undefined;
      if (key === 'claude') {
        const n = usage?.subscriptions ? Object.keys(usage.subscriptions).length : 0;
        count = n > 0 ? plural(n, 'подписка', 'подписки', 'подписок') : undefined;
      } else if (key === 'ollama') {
        count = 'каталог';
      } else {
        const n = models.filter(m => m.provider === key).length;
        count = n > 0 ? plural(n, 'модель', 'модели', 'моделей') : undefined;
      }
      return { key, name: providerLabel(key), color: sourceColor(key), off: caps.configured === false, badge: freeSourceLabel(key), count };
    });
  }, [usage]);

  const qGridCols = isMobile ? '1fr' : '1fr 1fr';

  // === Рендер ===

  // Пустое состояние
  if (!loading && !hasAny && quotaCards.length === 0 && moneyTiles.length === 0) {
    return (
      <div style={{ paddingTop: SP.md }}>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading }}>Ни один провайдер не подключён</div>
        <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textSecondary, maxWidth: 460 }}>
          Ключи провайдеров задаются в файле настроек сервера. Добавьте ключ — и здесь появятся баланс, квоты и расход.
        </div>
        <div style={{ marginTop: SP.md, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          appsettings.Local.json · LlmProviders
        </div>
      </div>
    );
  }

  return (
    <div style={{ paddingTop: SP.md }}>
      {/* Ошибка загрузки сводки — баннер + повтор (отдельные провайдер-ошибки видны в карточках) */}
      {usageError && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.sm,
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
          marginBottom: SP.md,
        }}>
          <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.danger, flexShrink: 0 }} />
          <span style={{ flex: 1, fontSize: FS.sm, color: C.dangerText }}>
            Не удалось получить данные провайдеров — сервер не ответил. Показаны последние сохранённые значения.
          </span>
          <Button size="sm" onClick={() => { loadUsage(); setTick(t => t + 1); }}>Повторить</Button>
        </div>
      )}

      {/* KPI-лента */}
      <KpiRibbon
        hot={hot}
        money={moneyKpi}
        cost={costKpi}
        free={freeKpi}
        loading={loading}
        isMobile={isMobile}
      />

      {/* Расход — переход в подробную аналитику (диаграмма переехала туда) */}
      <Lane title="Расход" />
      <button
        type="button"
        onClick={() => { onClose(); openSpend(); }}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', textAlign: 'left',
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: `${SP.md}px ${SP.lg}px`,
          cursor: 'pointer', font: 'inherit',
        }}
      >
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Расход по дням и местам — в отдельной аналитике</div>
          <div style={{ marginTop: 3, fontSize: FS.xs, color: C.textMuted }}>Диаграмма переезжает в подробный разрез: дни, места, модели, стоимость по тарифам.</div>
        </div>
        <span style={{ flexShrink: 0, fontSize: FS.sm, color: C.accent, fontWeight: 600 }}>Подробная аналитика →</span>
      </button>

      {/* Квоты подписок — карточки-индикаторы */}
      {quotaCards.length > 0 && (
        <>
          <Lane title="Квоты подписок · израсходовано" />
          <div style={{ display: 'grid', gridTemplateColumns: qGridCols, gap: SP.sm }}>
            {quotaCards.map(c => <ProviderCard key={c.key} data={c} />)}
          </div>
        </>
      )}

      {/* Деньги — балансы (только админ) */}
      {isAdmin && moneyTiles.length > 0 && (
        <>
          <Lane title="Деньги" />
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
            {moneyTiles.map(d => <MoneyTile key={d.key} d={d} stale={isStale(d.asOf)} />)}
          </div>
        </>
      )}

      {/* Все провайдеры */}
      <Lane title="Все провайдеры" />
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
        {allProviders.map(p => (
          <ProviderChip key={p.key} name={p.name} color={p.color} off={p.off} badge={p.badge} count={p.count} />
        ))}
      </div>

      {/* Подсказка не-админу */}
      {!isAdmin && (
        <div style={{ marginTop: SP.lg, fontSize: FS.xs, color: C.textMuted }}>
          Балансы и расход показываются только администратору.
        </div>
      )}
    </div>
  );
}
