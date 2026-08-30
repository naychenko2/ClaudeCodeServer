// Вкладка «Квоты и деньги» раздела «Модели и расход» (макет docs/mockups/models-spend-v3.html §1).
// KPI-лента → переход в аналитику → карточки квот → денежные балансы (админ) → все провайдеры.
// Состояния §4: скелетон, ошибка-баннер, протухшие данные, пусто, недоступная/исчерпанная квота.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, ExternalLink } from 'lucide-react';
import type { ProviderBalanceInfo, SpendOverviewResponse, SubscriptionUsage, UsageResponse, UsageSnapshot } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, GROUP_COLORS, R, SP } from '../../lib/design';
import { Button, Dot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import {
  cliProviderKeys, getModels, getProviders, providerCapsByKey, providerLabel, useModels,
} from '../../lib/models';
import { fmtReset, latestPerWindow, seriesByWindow, snapshotFreshnessLabel, windowLabel, worstWindow } from '../../lib/rateLimit';
import { rotationBadgeState } from '../../lib/rotation';
import type { RotationBadgeState } from '../../lib/rotation';
import { addDaysUtc, openSpend, plural, spendQuery, todayUtc } from '../../lib/spend';
import { freeSourceLabel, isFreeSource } from '../../lib/spendSources';
import { isLocalEngineKey } from '../../lib/localEngine';
import { showToast } from '../../lib/toast';
import { KpiRibbon } from './KpiRibbon';
import { ProviderCard } from './ProviderCard';
import type { FreshnessSpec, PillSpec, ProviderCardData } from './ProviderCard';
import { parseQuotaWindow, type QuotaWindowView } from './QuotaWindow';
import { BalanceChip, type BalanceChipData } from './BalanceChip';

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
  glm: 0, minimax: 1, claude: 2, alibabacloud: 3, deepseek: 5, kimi: 6,
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

// === Нормализация в общую вью-модель (провайдеры + подписки → ProviderCardData) ===

// Последний снимок по времени (любой, не только с долей) — для возраста свежести.
// latestWithUtilization не подходит: аккаунт может приносить только resets-события ходов.
function lastSnapshot(snaps: UsageSnapshot[]): UsageSnapshot | null {
  let best: UsageSnapshot | null = null;
  let bestT = -Infinity;
  for (const s of snaps) {
    const t = new Date(s.timestamp).getTime();
    if (isNaN(t) || t <= bestT) continue;
    best = s; bestT = t;
  }
  return best;
}

function providerFreshness(asOf?: string | null): FreshnessSpec | undefined {
  if (!asOf || !fmtAgo(asOf)) return undefined;
  const stale = isStale(asOf);
  return {
    dot: stale ? C.warning : C.success,
    text: stale ? `на ${fmtClock(asOf)}` : fmtAgo(asOf)!,
    textTone: stale ? C.warningText : undefined,
  };
}

// Префикс сброса худшего окна для хинта: пятичасовое — «сброс окна», недельное — «сброс недели»
const isFiveHourReset = (t: string) => /5|five|hour/i.test(t);

// Хинт подписки: «сброс недели 11 авг, 03:00 · новые чаты направляются сюда».
// Время опасного окна подсвечено dangerText; reason — из rotationBadgeState.
function buildSubHint(worst: ReturnType<typeof worstWindow>, rot: RotationBadgeState): ReactNode {
  if (!worst || !worst.resetsAt) return <>{rot.reason}</>;
  const prefix = isFiveHourReset(worst.limitType)
    ? 'сброс окна'
    : worst.limitType.includes('extra') ? 'сброс перерасхода' : 'сброс недели';
  const resetStr = fmtReset(worst.resetsAt);
  const danger = worst.level === 'danger';
  return <>
    {prefix}{' '}
    <span style={danger ? { color: C.dangerText } : undefined}>{resetStr}</span>
    {' · '}{rot.reason}
  </>;
}

interface SubFreshness { corner: FreshnessSpec; detail: ReactNode; copyCommand: string | null }

// Свежесть подписки по каналу данных (таблица из архитектурного решения): точка/текст в
// углу шапки + полная подпись в раскрытии. Не возраст, а состояние опроса OAuth.
function subFreshness(sub: SubscriptionUsage, pollStatus: string | undefined, lastSnap: UsageSnapshot): SubFreshness {
  const ts = lastSnap.timestamp;
  if (pollStatus === 'unauthorized') {
    return {
      corner: { dot: C.textMuted, text: `по ходам · ${fmtAgo(ts) ?? ''}`, textTone: C.textMuted },
      detail: <>Опрос лимитов недоступен: в профиле аккаунта setup-токен, а не полноценный вход. Цифры обновляются только когда в этом аккаунте идёт чат.</>,
      copyCommand: sub.loginCommand ?? null,
    };
  }
  if (pollStatus === 'error') {
    return {
      corner: { dot: C.warning, text: `на ${fmtClock(ts)}`, textTone: C.warningText },
      detail: <>Опрос лимитов не отвечает — показаны последние снимки.</>,
      copyCommand: null,
    };
  }
  if (isStale(ts)) {
    return {
      corner: { dot: C.warning, text: `на ${fmtClock(ts)}`, textTone: C.warningText },
      detail: <>Опрос лимитов не приносит свежих цифр дольше получаса.</>,
      copyCommand: null,
    };
  }
  return {
    corner: { dot: C.success, text: fmtAgo(ts) ?? '' },
    detail: <>Откуда цифры: {snapshotFreshnessLabel(lastSnap.source, ts)}</>,
    copyCommand: null,
  };
}

interface SubCtx {
  rotationThreshold: number;
  weeklyThreshold: number;
  routingTarget?: string;
  pollStatuses: Record<string, string>;
  freeAvailable: boolean;
  subs: Record<string, SubscriptionUsage>;
  usageError: boolean;
}

// Ранг тарифа по ярлыку с бэка ("Max 20×", "Max 5×", "Max", "Pro") — копия
// ClaudeSubscriptionTier.Rank: пул срезает всё, кроме высшего тарифа набора (TopTier),
// и без ранга карточка не может отличить резерв от рабочего аккаунта. 0 — тариф не
// пришёл или не распознан.
function tierRank(tier: string | null | undefined): number {
  const t = (tier ?? '').toLowerCase().replace(/[^a-zа-я0-9]/gi, '');
  if (!t) return 0;
  if (t.includes('20')) return 4;
  if (t.includes('max') && t.includes('5')) return 3;
  if (t.includes('max')) return 2;
  if (t.includes('pro')) return 1;
  return 0;
}

// Пилюли шапки карточки подписки: тариф + ограничения. Третья ось наблюдаемости рядом
// с бейджем «в ротации»: false → «Без Opus и 1M» (объединяем в одну пилюлю, минус
// ширина и одна ложная «тревога»), null/undefined — поле не пришло со старого бэка
// (обратная совместимость снимков), дефолт true неинформативен и пилюлю не рисует.
// Тон plain: ограничение — постоянное свойство тарифа, а не «сейчас что-то не так»;
// янтарь остаётся только у бейджа ротации.
export function subscriptionPills(sub: SubscriptionUsage): PillSpec[] {
  const pills: PillSpec[] = [];
  if (sub.tier) pills.push({ label: `Тариф: ${sub.tier}`, tone: 'plain' });
  const limits = limitsLabel(sub);
  if (limits) pills.push({ label: limits, tone: 'plain' });
  return pills;
}

// Пилюли ограничений для раскрытия карточки — то же, что в шапке, без тарифа. Тариф НЕ
// включаем: он уже отдельной строкой `<Pill>Тариф: …</Pill>` рядом. Дубль нужен в двух
// случаях: на узких вьюпортах шапка переносится на две строки и читается хуже, и для
// карточек без тарифа (tier: null) — там это единственное место, где ограничения видны.
export function subscriptionExpandedPills(sub: SubscriptionUsage): PillSpec[] {
  const limits = limitsLabel(sub);
  return limits ? [{ label: limits, tone: 'plain' }] : [];
}

// Собираем «Без Opus» / «Без 1M» / «Без Opus и 1M». false на обоих → одна пилюля, не
// две — иначе на узких вьюпортах две warn-плашки съедают место и обесценивают янтарь
// бейджа ротации рядом.
function limitsLabel(sub: SubscriptionUsage): string | null {
  const parts: string[] = [];
  if (sub.supportsOpus === false) parts.push('Opus');
  if (sub.supports1M === false) parts.push('1M');
  return parts.length ? `Без ${parts.join(' и ')}` : null;
}

// Подписка Claude → общая вью-модель. Окна — напрямую из latestPerWindow: при !hasUtil
// не выдумываем «0%», а пишем «в пределах нормы» (как UsageWidget.WindowRow).
export function buildSubscriptionCard(key: string, sub: SubscriptionUsage, ctx: SubCtx): ProviderCardData {
  const name = sub.name ?? key;
  const color = sourceColor('claude');   // оба аккаунта пула — один цвет, различаются именем
  const pollStatus = ctx.pollStatuses[key];
  const lastSnap = lastSnapshot(sub.snapshots);

  // Нет снимков совсем — «пустая» карточка: имя + тариф + хинт, без нулевых шкал (не ошибка)
  if (!lastSnap) {
    const unauthorized = pollStatus === 'unauthorized';
    return {
      key, name, color, state: 'ready', isFree: false, dim: ctx.usageError, onRetry: () => {},
      windows: [], labelWidth: 92,
      // Тариф и ограничения (объединённая «Без Opus и 1M») — в шапке; раскрытия у
      // пустой карточки нет, кроме unauthorized с loginCommand. На карточке без тарифа
      // (tier: null) ограничения попадают только в expandedPills — ProviderCard
      // рендерит их в раскрытии гейтом `data.tier || data.expandedPills?.length`.
      pills: subscriptionPills(sub),
      expandedPills: subscriptionExpandedPills(sub),
      hint: unauthorized
        ? 'Опрос лимитов недоступен — в профиле нет полноценного входа'
        : 'Данных пока нет — цифры появятся после первого хода или ближайшего опроса',
      hasExhausted: false,
      expandable: !!(unauthorized && sub.loginCommand),
      tier: sub.tier ?? null,
      freshnessDetail: unauthorized && sub.loginCommand
        ? <>Опрос лимитов недоступен: в профиле аккаунта setup-токен. Войдите в профиль аккаунта командой ниже — тогда опрос заработает.</>
        : undefined,
      copyCommand: unauthorized ? (sub.loginCommand ?? null) : null,
    };
  }

  const windows = latestPerWindow(sub.snapshots);
  const winViews: QuotaWindowView[] = windows.map(w => ({
    label: windowLabel(w.limitType),
    kind: 'percent',
    usedPct: w.hasUtil ? w.pct : null,
    usedCount: null, totalCount: null,
    valueText: w.hasUtil ? `${w.pct}%` : 'в пределах нормы',
    resetsAt: w.resetsAt ?? null,
    exhausted: w.level === 'danger',
  }));
  const worst = worstWindow(windows);
  const hasExhausted = winViews.some(w => w.exhausted);
  const fresh = subFreshness(sub, pollStatus, lastSnap);

  const isTarget = ctx.routingTarget === key;
  const targetSub = ctx.routingTarget ? ctx.subs[ctx.routingTarget] : undefined;
  // Тариф ниже цели — Pick сюда не придёт (TopTier). Считаем только при ДВУХ известных
  // тарифах: нераспознанный ярлык (rank 0) — незнание, а не «ниже», врать бейджем нельзя.
  const ownRank = tierRank(sub.tier);
  const targetRank = tierRank(targetSub?.tier);
  const tierBelowTarget = !isTarget && ownRank > 0 && targetRank > 0 && ownRank < targetRank;
  const rot = rotationBadgeState({
    inRotation: sub.inRotation,
    utilization: sub.utilization,
    threshold: ctx.rotationThreshold,
    weeklyUtilization: sub.weeklyUtilization,
    weeklyThreshold: ctx.weeklyThreshold,
    exhausted: sub.exhausted,
    isTarget,
    targetName: ctx.routingTarget ? (targetSub?.name ?? ctx.routingTarget) : undefined,
    freeAvailable: ctx.freeAvailable,
    // Ограничения тарифа — третья ось: не ломают бейдж, но подмешиваются в reason
    // («, кроме ходов Opus и 1M»). Подробнее — lib/rotation.ts.
    supportsOpus: sub.supportsOpus ?? undefined,
    supports1M: sub.supports1M ?? undefined,
    tierBelowTarget,
  });
  const series = seriesByWindow(sub.snapshots);
  const trend = worst ? (series[worst.limitType] ?? []) : [];

  return {
    key, name, color, state: 'ready', isFree: false, dim: ctx.usageError, onRetry: () => {},
    windows: winViews, labelWidth: 92,
    pills: subscriptionPills(sub),
    expandedPills: subscriptionExpandedPills(sub),
    routingBadge: isTarget ? { tone: rot.tone, label: rot.label } : undefined,
    freshness: fresh.corner,
    hint: buildSubHint(worst, rot),
    hasExhausted,
    expandable: true,
    tier: sub.tier ?? null,
    // Оба окна: пул выводит аккаунт по любому из них (IsOverloaded), и подпись про одно
    // только 5ч противоречила бы причине в бейдже («нагрузка 7д 99% ≥ порога 95%»)
    thresholdNote: `Из ротации выводит нагрузка 5-часового окна выше ${Math.round((ctx.rotationThreshold || 0.8) * 100)}%`
      + ` или недельного выше ${Math.round((ctx.weeklyThreshold || 0.95) * 100)}%`,
    freshnessDetail: fresh.detail,
    // Команда входа доступна всегда, а не только при сломанном опросе: перелогинить
    // аккаунт бывает нужно и с живым токеном (протух OAuth, сменили профиль). При
    // unauthorized она подана крупно, в остальных случаях — компактной ссылкой
    copyCommand: fresh.copyCommand,
    loginCommand: sub.loginCommand ?? null,
    trend: trend.length >= 2 ? trend : undefined,
  };
}

// Квотный CLI-провайдер → общая вью-модель (готовое состояние)
function providerReadyCard(
  key: string, name: string, color: string,
  bal: ProviderBalanceInfo,
  snapshots: { timestamp: string; balance: number; currency: string }[],
): ProviderCardData {
  const windows = (bal.windows ?? []).map(parseQuotaWindow);
  // На поверхность — percent и consumed (со сбросом, как токен-окна); count (моментальная
  // занятость) живёт в раскрытии со своей подписью. alive (живые платформы FreeLLM) —
  // состояние пула, а не квота: когда здоровье разобралось, его рисует блок FreeLlmHealth
  // и окно дублировало бы его; без здоровья — рисуем окном на поверхности
  const hasPlatformHealth = (bal.health?.platformsTotal ?? 0) > 0;
  const surfaceWindows = windows.filter(w => w.kind !== 'count' && (w.kind !== 'alive' || !hasPlatformHealth));
  const countWindows = windows.filter(w => w.kind === 'count');
  const hasExhausted = windows.some(w => w.exhausted);
  const pills: PillSpec[] = [];
  if (bal.planLabel) pills.push({ label: bal.planLabel, tone: 'plain' });
  // Состояние пула FreeLLM по живым платформам: всё лежит — danger, частичная
  // деградация — warn; полному здоровью аларм не положен («бесплатный» уже есть)
  const alive = bal.health?.platformsAlive;
  const total = bal.health?.platformsTotal;
  if (alive != null && total != null && total > 0) {
    if (alive === 0) pills.push({ label: 'Недоступен', tone: 'danger' });
    else if (alive < total) pills.push({ label: 'Часть платформ недоступна', tone: 'warn' });
  }
  if (isFreeSource(key)) pills.push({ label: 'бесплатный', tone: 'free' });
  if (hasExhausted) pills.push({ label: 'Предел', tone: 'danger' });
  const trend = snapshots.length
    ? snapshots
        .filter(s => !isNaN(s.balance))
        .map(s => ({ t: new Date(s.timestamp).getTime(), u: Math.max(0, Math.min(1, (100 - s.balance) / 100)) }))
        .filter(p => !isNaN(p.t))
        .sort((a, b) => a.t - b.t)
    : [];
  const cabinetUrl = CABINET_URL[key];
  const expandable = countWindows.length > 0 || trend.length >= 2 || !!cabinetUrl;
  return {
    key, name, color, state: 'ready', isFree: isFreeSource(key), dim: false, onRetry: () => {},
    windows: surfaceWindows, labelWidth: 64, pills,
    freshness: providerFreshness(bal.asOf),
    health: bal.health ?? null, hasExhausted,
    exhaustedResetAt: windows.find(w => w.exhausted)?.resetsAt ?? null,
    expandable,
    countWindows: countWindows.length ? countWindows : undefined,
    trend: trend.length >= 2 ? trend : undefined,
    cabinetUrl,
  };
}

export function QuotasTab({ balances, onClose }: { balances?: BalanceChipData[]; onClose: () => void }) {
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
    // При ошибке прошлый usage не обнуляем: карточки подписок остаются на месте
    // (притушенными), а не исчезают под баннером «показаны последние значения».
    api.usage.get().then(setUsage).catch(() => { setUsageError(true); });
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

  // Карточки квот (qgrid): подписки Claude + квотные провайдеры + ненастроенные-без-баланса (Alibaba)
  const quotaCards: ProviderCardData[] = useMemo(() => {
    const out: ProviderCardData[] = [];

    // --- Подписки Claude (данные из сводки usage, не из balanceKeys) ---
    const subs = usage?.subscriptions ?? {};
    const subCtx: SubCtx = {
      rotationThreshold: usage?.rotationThreshold ?? 0.8,
      weeklyThreshold: usage?.weeklyThreshold ?? 0.95,
      routingTarget: usage?.routingTarget,
      pollStatuses: usage?.pollStatuses ?? {},
      freeAvailable: Object.values(subs).some(s => s.inRotation !== false),
      subs,
      usageError,
    };
    for (const [key, sub] of Object.entries(subs)) {
      out.push(buildSubscriptionCard(key, sub, subCtx));
    }

    // --- Квотные CLI-провайдеры ---
    for (const key of balanceKeys) {
      const data = prov[key];
      const bal = data?.balance ?? null;
      const isQuota = bal?.currency === '%' || (bal?.windows?.length ?? 0) > 0;
      if (data === null) {
        out.push({ key, name: providerLabel(key), color: sourceColor(key), state: 'error', isFree: isFreeSource(key), retryAt, onRetry: () => loadProvider(key), windows: [], pills: [], hasExhausted: false, expandable: false });
        continue;
      }
      if (data === undefined || !bal) {
        out.push({ key, name: providerLabel(key), color: sourceColor(key), state: 'loading', isFree: isFreeSource(key), onRetry: () => loadProvider(key), windows: [], pills: [], hasExhausted: false, expandable: false });
        continue;
      }
      if (isQuota) {
        out.push(providerReadyCard(key, providerLabel(key), sourceColor(key), bal, data.snapshots ?? []));
      }
    }
    // Настроен, но квоту не отдаёт (Alibaba Cloud) — отдельная строка
    for (const key of providerKeys) {
      const c = providerCapsByKey(key);
      if (c.hasBalance || c.configured === false) continue;
      if (balanceKeys.includes(key)) continue;
      out.push({ key, name: providerLabel(key), color: sourceColor(key), state: 'unavailable', isFree: isFreeSource(key), onRetry: () => {}, windows: [], pills: [], hasExhausted: false, expandable: false });
    }
    return out;
  }, [prov, balanceKeys, providerKeys, loadProvider, retryAt, usage, usageError]);

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
      } else if (isLocalEngineKey(key)) {
        count = 'каталог';
      } else {
        const n = models.filter(m => m.provider === key).length;
        count = n > 0 ? plural(n, 'модель', 'модели', 'моделей') : undefined;
      }
      return { key, name: providerLabel(key), color: sourceColor(key), off: caps.configured === false, badge: freeSourceLabel(key), count };
    });
  }, [usage]);

  // minmax(0, 1fr), а не 1fr: у grid-колонки min-width по умолчанию auto, и длинное
  // неразрывное содержимое (пилюли + свежесть в шапке, «в пределах нормы» в ряду окна)
  // раздувает колонку за пределы контейнера вместо того, чтобы сжаться и обрезаться
  const qGridCols = isMobile ? 'minmax(0, 1fr)' : 'minmax(0, 1fr) minmax(0, 1fr)';

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

      {/* Внешние сервисы — fal/glif/Yandex. Чипы приходят из виджета «Использование»
          (там же идёт единый фетч с таймером), отдельный запрос из модалки дал бы
          stale-данные и расхождение с главной. Если модалка открыта не из виджета,
          блок просто не рисуется */}
      {balances && balances.length > 0 && (
        <>
          <Lane title="Внешние сервисы" />
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {balances.map(b => <BalanceChip key={b.key} b={b} />)}
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
