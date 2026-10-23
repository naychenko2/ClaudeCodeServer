import { CircleGauge } from 'lucide-react';
import { Island, IslandHeader } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { C, FS, ISLAND, SP } from '../lib/design';
import { RingPillBadge, FalCostBadge, GlifCostBadge, type CostStats, type FalCostStats, type ProviderBalance } from '../components/chat/ChatHeaderBar';
import type { LocalModelLiveState } from '../features/localModel/useLocalModelLive';
import type { ContextEstimate } from '../lib/context';
import type { GlifGenStats } from '../components/chat/glifStats';
import { toRateWindows, type RateWindow } from '../lib/rateLimit';
import type { ChatItem, RateLimitInfo } from '../types';

// Витрина кольцевой пилюли шапки чата (вариант C2): все состояния спецификации §5 —
// заметка «Шапка чата — пилюля «кольца» (C2), спецификация» — и циферблат кэша промпта
// (блок «Кэш»). Тема — общим переключателем страницы; поповеры живые (клик по пилюле),
// сжатие в них ничего не делает.

const noop = () => {};
const NO_COST: CostStats = { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 };
const SOME_COST: CostStats = { cost: 1.24, input: 182_000, output: 12_400, cacheRead: 940_000, cacheCreate: 61_000, turns: 9, results: 9 };
const NO_FAL: FalCostStats = { total: 0, count: 0, byModel: new Map() };
const NO_GLIF: GlifGenStats = { count: 0, credits: 0, hasCredits: false, byType: new Map() };
const FAL: FalCostStats = { total: 0.42, count: 3, byModel: new Map([['fal-ai/flux/dev', { count: 3, cost: 0.42 }]]) };

const ctx = (pct?: number, over: Partial<ContextEstimate> = {}): ContextEstimate => ({
  tokens: pct !== undefined ? Math.round(pct * 2000) : undefined, window: 200_000, pct, fresh: false,
  level: pct === undefined ? 'normal' : pct >= 85 ? 'danger' : pct >= 65 ? 'warn' : 'normal', ...over,
});
const NO_CTX = ctx();

const RESET = new Date(Date.now() + 3 * 3600_000).toISOString();
const wins = (u: Record<string, number | undefined>, over: Record<string, Partial<RateLimitInfo>> = {}): RateWindow[] =>
  toRateWindows(Object.fromEntries(Object.entries(u).map(([k, v]) =>
    [k, { limitType: k, utilization: v, status: 'allowed', resetsAt: RESET, ...over[k] }])));

// Фикстуры для циферблата кэша: лента с одним ходом родного Claude, якорь сдвинут от
// «сейчас» витрины так, чтобы часовой кэш был тёплым, остывающим или остывшим.
// «Идёт ход» — тот же тёплый фикстур с isWaiting: во время хода остаток не показывается.
const NOW = Date.now();
const cacheFeed = (agoMin: number): ChatItem[] => [{
  kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1, contextTokens: 120_000,
  promptCache: { readTokens: 118_000, writtenTokens: 4_000, anchorAtMs: NOW - agoMin * 60_000, ttl: '1h', missReason: 'none' },
}];
const CACHE: Record<'warm' | 'cooling' | 'cold', ChatItem[]> = {
  warm: cacheFeed(12),    // 48 мин до истечения — тёплый
  cooling: cacheFeed(56), // 4 мин — остывает (порог 5 мин)
  cold: cacheFeed(130),   // срок прошёл — холодный
};

interface Sample {
  caption: string;
  estimate?: ContextEstimate;
  windows?: RateWindow[];
  compacting?: boolean;
  billing?: 'subscription' | 'api';
  cost?: CostStats;
  provider?: { name: string; balance: ProviderBalance };
  workflow?: { phasesDone: number; phasesTotal: number };
  fal?: FalCostStats;
  compact?: boolean;
  reducedMotion?: boolean;
  empty?: boolean;                      // пилюли нет: на её месте — пояснение
  localModel?: LocalModelLiveState;    // форсированное состояние локальной модели
  cache?: keyof typeof CACHE;           // фикстур ленты для циферблата кэша
  waiting?: boolean;                    // идёт ход: стрелка крутится, остаток не показан
}

const DS_OK: ProviderBalance = { available: true, currency: 'USD', totalBalance: '4.20' };

// Блок «Кэш»: состояния циферблата (F-1c) — тёплый, остывает, остыл, идёт ход
const CACHE_SAMPLES: Sample[] = [
  { caption: 'кэш тёплый (1h)', estimate: ctx(30), windows: wins({ five_hour: 0.2, seven_day: 0.12 }), cache: 'warm' },
  { caption: 'кэш остывает — «4 мин»', estimate: ctx(40), windows: wins({ five_hour: 0.81, seven_day: 0.3 }), cache: 'cooling' },
  { caption: 'кэш остыл — «остыл»', estimate: ctx(55), windows: wins({ five_hour: 0.3, seven_day: 0.15 }), cache: 'cold' },
  { caption: 'кэш остыл (мобила — без подписи)', estimate: ctx(55), windows: wins({ five_hour: 0.3, seven_day: 0.15 }), cache: 'cold', compact: true },
  { caption: 'идёт ход — стрелка крутится', estimate: ctx(55), windows: wins({ five_hour: 0.3, seven_day: 0.15 }), cache: 'warm', waiting: true },
];

const SAMPLES: Sample[] = [
  { caption: '5.1 обычное — худшее окно', estimate: ctx(62), windows: wins({ five_hour: 0.81, seven_day: 0.34, seven_day_opus: 0.12 }) },
  { caption: '5.1 контекст danger перебивает', estimate: ctx(92), windows: wins({ five_hour: 0.41, seven_day: 0.12 }) },
  { caption: '5.1 окно вне колец (Opus 95%) красит рамку', estimate: ctx(30), windows: wins({ five_hour: 0.2, seven_day: 0.3, seven_day_opus: 0.95 }) },
  { caption: 'минимальная дуга (1%)', estimate: ctx(1), windows: wins({ five_hour: 0.01, seven_day: 0.01 }) },
  { caption: 'перерасход', estimate: ctx(40), windows: wins({ five_hour: 1, seven_day: 0.5 }, { five_hour: { isUsingOverage: true } }) },
  { caption: '5.2 идёт сжатие', estimate: ctx(88), compacting: true, windows: wins({ five_hour: 0.81, seven_day: 0.34 }) },
  { caption: '5.2r сжатие при reduced motion', estimate: ctx(88), compacting: true, reducedMotion: true, windows: wins({ five_hour: 0.81, seven_day: 0.34 }) },
  { caption: '5.3 ✦ после сжатия', estimate: ctx(undefined, { fresh: true }), windows: wins({ five_hour: 0.41, seven_day: 0.12 }) },
  { caption: '5.4 только контекст', estimate: ctx(62) },
  { caption: '5.5 только лимиты', estimate: NO_CTX, windows: wins({ five_hour: 0.81, seven_day: 0.34 }) },
  { caption: '5.6 процентов нет', estimate: NO_CTX, windows: wins({ five_hour: undefined, seven_day: undefined }) },
  { caption: '5.6 все окна устарели', estimate: NO_CTX, windows: wins({ five_hour: undefined, seven_day: undefined }).map(w => ({ ...w, stale: true })) },
  { caption: '5.7 API-ключ', estimate: ctx(62), billing: 'api', cost: SOME_COST },
  { caption: '5.8a GLM — квота', estimate: ctx(20), cost: SOME_COST, provider: { name: 'GLM', balance: { available: true, currency: '%', totalBalance: '66' } } },
  { caption: '5.8b DeepSeek — баланс на исходе', estimate: ctx(62), cost: SOME_COST, provider: { name: 'DeepSeek', balance: { available: true, currency: 'USD', totalBalance: '0.80' } } },
  { caption: '5.8b DeepSeek — баланс в норме', estimate: ctx(62), cost: SOME_COST, provider: { name: 'DeepSeek', balance: DS_OK } },
  { caption: '5.8b DeepSeek — начало сессии, контекста нет', estimate: NO_CTX, provider: { name: 'DeepSeek', balance: DS_OK } },
  { caption: '5.9 workflow (мобила)', estimate: ctx(62), windows: wins({ five_hour: 0.41, seven_day: 0.12 }), workflow: { phasesDone: 2, phasesTotal: 5 }, fal: FAL, compact: true },
  { caption: '5.10 нечего показывать — пилюли нет', estimate: NO_CTX, empty: true },
  // Локальная модель: все состояния
  { caption: 'LM генерация 38 ток/с', estimate: ctx(45), localModel: { seen: true, phase: 'generating', genTps: 38, waitingTotal: 0, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM читает промпт', estimate: ctx(45), localModel: { seen: true, phase: 'prefill', genTps: null, waitingTotal: 0, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM очередь · ждут 3', estimate: ctx(45), localModel: { seen: true, phase: 'queued', genTps: null, waitingTotal: 3, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM после хода (idle)', estimate: ctx(45), localModel: { seen: true, phase: 'idle', genTps: null, waitingTotal: 0, lastTurn: { genTps: 30, prefillTps: 1200, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 }, lastActivityAt: Date.now() } },
  // Мобила, вариант А: лицо заменено компактным словом (≤ 7 знаков)
  { caption: 'LM моб: генерация 38 т/с', estimate: ctx(45), compact: true, localModel: { seen: true, phase: 'generating', genTps: 38, waitingTotal: 0, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM моб: читает', estimate: ctx(45), compact: true, localModel: { seen: true, phase: 'prefill', genTps: null, waitingTotal: 0, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM моб: очередь', estimate: ctx(45), compact: true, localModel: { seen: true, phase: 'queued', genTps: null, waitingTotal: 3, lastTurn: null, lastActivityAt: Date.now() } },
  { caption: 'LM моб: после хода (idle) — Ctx 61k', estimate: ctx(45), compact: true, localModel: { seen: true, phase: 'idle', genTps: null, waitingTotal: 0, lastTurn: { genTps: 30, prefillTps: 1200, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 }, lastActivityAt: Date.now() } },
  { caption: 'LM моб: тревога Ctx 92% побеждает', estimate: ctx(92), compact: true, localModel: { seen: true, phase: 'generating', genTps: 38, waitingTotal: 0, lastTurn: null, lastActivityAt: Date.now() } },
];

function RingSample({ s }: { s: Sample }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, alignItems: 'flex-start' }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{s.caption}</div>
      <RingPillBadge
        estimate={s.estimate ?? NO_CTX} isWaiting={!!s.waiting} isCompacting={!!s.compacting}
        canCompact compactNote={undefined} onCompact={noop} online assistantName={s.provider?.name ?? 'Claude'}
        isCliProvider={!!s.provider} providerName={s.provider?.name ?? 'Claude'}
        cost={s.cost ?? NO_COST} falCost={s.fal ?? NO_FAL} glifCost={NO_GLIF}
        balance={s.provider?.balance ?? null} billing={s.billing ?? 'subscription'} windows={s.windows ?? []}
        activeWorkflow={s.workflow} isMobile={s.compact} isCompact={s.compact} resetKey={s.caption}
        forceReducedMotion={s.reducedMotion}
        forceLocalModel={s.localModel}
        items={s.cache ? CACHE[s.cache] : undefined}
      />
      {s.empty && <div style={{ fontSize: FS.sm, color: C.textMuted }}>(компонент вернул null)</div>}
    </div>
  );
}

const GLIF: GlifGenStats = { count: 4, credits: 0, hasCredits: false, byType: new Map([['image', 4]]) };

// Ряд шапки как на десктопе: кольцевая пилюля рядом с обычными (fal, glif) — высота
// у всех одна, верх и низ рамок на одной линии
function HeaderRowSample() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, alignItems: 'flex-start', gridColumn: '1 / -1' }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>ряд шапки — высота как у соседних</div>
      <div data-testid="ring-pill-row" style={{ display: 'flex', alignItems: 'center', gap: SP.xs + 2 }}>
        <RingPillBadge
          estimate={ctx(62)} isWaiting={false} isCompacting={false}
          canCompact compactNote={undefined} onCompact={noop} online assistantName="Claude"
          isCliProvider={false} providerName="Claude"
          cost={NO_COST} falCost={NO_FAL} glifCost={NO_GLIF}
          balance={null} billing="subscription" windows={wins({ five_hour: 0.81, seven_day: 0.34 })}
          resetKey="header-row" items={CACHE.warm}
        />
        <FalCostBadge stats={FAL} resetKey="header-row" />
        <GlifCostBadge stats={GLIF} resetKey="header-row" />
      </div>
    </div>
  );
}

export function RingPillKitSection() {
  return (
    <Island>
      <IslandHeader
        icon={<CircleGauge size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Пилюля «кольца» шапки чата"
        badge="RingPillBadge · C2"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: ISLAND.gap,
      }}>
        <HeaderRowSample />
        <div style={{ gridColumn: '1 / -1', fontSize: FS.sm, color: C.textMuted }}>циферблат кэша промпта — состояния</div>
        {CACHE_SAMPLES.map(s => <RingSample key={s.caption} s={s} />)}
        {SAMPLES.map(s => <RingSample key={s.caption} s={s} />)}
      </div>
    </Island>
  );
}
