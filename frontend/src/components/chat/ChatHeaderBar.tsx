import { useState, useEffect, useRef, useMemo, type ReactNode } from 'react';
import { Plus, Menu as MenuIcon, Tags, Bell, BellOff, History, Hourglass, ListChecks, Pencil, Pin, Columns3, Trash2, Eye, EyeOff, MoreHorizontal, Archive, ArchiveRestore, HardDrive, ChevronRight, Sparkles } from 'lucide-react';
import { OPEN_AI_EVENT } from '../../lib/ai/openAiEvent';
import type { Project, Session, ClaudeBilling, Persona, ProjectTag } from '../../types';
import { api } from '../../lib/api';
import { isArchivedChat } from '../../lib/chatFilters';
import { TagAssignMenu } from '../TagChip';
import { modelLabel, modelProvider, assistantName, useProviders } from '../../lib/models';
import { effortLabel } from '../../lib/effort';
import { ExpiryButton } from './ExpiryButton';
import { ExpiryPicker } from './ExpiryPicker';
import { DossierOptOutButton } from './DossierOptOutButton';
import { NotifyButton } from './NotifyButton';
import { updateChatFields } from '../../lib/chatUpdate';
import { isNotifySupported, useChatNotifyOn } from '../../lib/notify';
import { expiresAt, formatTimeLeft, formatExpiryDate } from '../../lib/expiry';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { PersonaFace } from '../../features/personas/PersonaFace';
import { GroupParticipantsPopover } from '../../features/personas/GroupParticipantsPopover';
import { personaTitleLines } from '../../lib/personas';
import { isProjectPersona, isSpherePersona, zoneLabel } from '../../lib/personaZone';
import { useSpheres } from '../../lib/useSpheres';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { AGENT_COLORS, agentDotColor } from '../AgentSelector';
import { type RateWindow, type RatePillSegment, type RingPillCandidate, RATE_COLORS, RING_WINDOWS, windowLabel, fmtReset, worstWindow, withAccountFallback, ratePillSegments, ratePillVisible, windowCandidates, ringPillHead, ringArcLength } from '../../lib/rateLimit';
import { useAccountUsage, accountSnapshotsFor } from '../../lib/accountUsage';
import { type ContextEstimate } from '../../lib/context';
import { prunedSummaryText } from '../../lib/contextPruned';
import { ContextThresholdsDialog } from '../ContextThresholdsDialog';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FONT, FS, R, SP, SHADOW, TB, CHAT_MAX_W, MODAL_W, GROUP_COLORS } from '../../lib/design';
import { useWindowWidth, MOBILE_MAX, TABLET_WIDE_MIN } from '../../lib/breakpoints';
import { Toolbar, ToolbarIconButton } from '../Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';
import { Badge, BackButton, ChatTopicIcon, Modal, ModalActions, ConfirmDialog, TextField, Menu, MenuItem, MenuSep, Tooltip } from '../ui';
import { createTask } from '../../lib/tasks';
import { showToast } from '../../lib/toast';
import { beginAiBusy, endAiBusy } from '../../lib/ai/busy';
import { useSlotItem } from '../../lib/subsystems/registry';
import type { ChatHeaderSummaryCtx, ChatHeaderMenuItemCtx, ChatHeaderBadgeCtx } from '../../lib/subsystems/registryCore';
import type { ExtractedTaskCandidate } from '../../types';
import { ChatOriginBadge } from '../ChatOriginBadge';
import { TeamMechanicBadge } from '../../features/team/TeamMechanicBadge';
import type { TeamMechanicId } from '../../features/team/teamMechanics';
import { resolveChatOrigin } from '../../lib/chatOrigin';
import { projectDeviceBadge } from '../../lib/projectCapabilities';
import { type GlifGenStats, fmtCredits } from './glifStats';
import { useActionVisibility } from '../../hooks/useActionVisibility';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import { CHAT_ACTION_ORDER, CHAT_BADGE_ORDER, CHAT_BADGE_LABELS, HEADER_ACTIONS_HIDDEN_BY_DEFAULT, HEADER_COMPACT_HIDDEN_BY_DEFAULT, WALL_ACTIONS_HIDDEN_BY_DEFAULT, type ChatActionKey, type ChatBadgeKey } from '../../lib/chatActions';
import { chatFilterScope, leaveChatArchiveView } from '../../lib/chatFilters';

// Накопительная статистика стоимости/токенов по всем result-элементам ленты
export interface CostStats {
  cost: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheCreate: number;
  turns: number;
  results: number;
}

// Накопительная стоимость генераций fal.ai (фактически списанная, приходит с backend).
// byModel — разбивка по endpoint_id: число генераций и сумма.
export interface FalCostStats {
  total: number;
  count: number;
  byModel: Map<string, { count: number; cost: number }>;
}

// Баланс аккаунта CLI-провайдера (GET /api/providers/{key}/balance)
export interface ProviderBalance { available: boolean; currency: string; totalBalance: string }

const fmtUsd = (c: number) => '$' + (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
const fmtTokens = (n: number) =>
  n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);

// Строка разбивки в выпадашке бейджа
const badgeRowStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between', gap: 16,
  fontFamily: FONT.mono, fontSize: 12, color: C.textSecondary, padding: '2px 0',
};
const badgeTitleStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, color: C.textHeading, marginBottom: 8,
};
function BadgeRow({ k, v }: { k: string; v: string }) {
  return <div style={badgeRowStyle}><span style={{ color: C.textMuted }}>{k}</span><span style={{ fontWeight: 600 }}>{v}</span></div>;
}
const badgeSectionStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '10px 0 4px',
};

// Строка одного окна лимита в выпадашке (пип кольца + метка + бар + % + сброс).
// pip — мини-иконка кольца этой строки; у окон без кольца — пустое место той же ширины
function RateRow({ w, pip }: { w: RateWindow; pip?: ReactNode }) {
  const c = RATE_COLORS[w.level];
  const reset = fmtReset(w.resetsAt);
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.xs + 2, padding: '3px 0' }}>
      <span style={{ display: 'flex', width: 12, height: 12, flexShrink: 0, marginTop: SP.xxs }}>{pip}</span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <span style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary }}>
            {windowLabel(w.limitType)}{w.isUsingOverage ? ' · перерасход' : ''}
          </span>
          {/* Процента нет (событие хода без utilization) — не «0%», а «в пределах нормы», как на экране «Использование» */}
          <span style={{ fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: c.text }}>
            {w.stale ? '—' : w.hasUtil ? `${w.pct}%${w.isUsingOverage ? '+' : ''}` : 'в пределах нормы'}
          </span>
        </div>
        {!w.stale && w.hasUtil && (
          <div style={{ height: 4, borderRadius: 2, background: C.track, overflow: 'hidden', margin: '3px 0' }}>
            <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
          </div>
        )}
        {w.stale
          ? <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted }}>данные устарели</div>
          : reset && <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted }}>сброс {reset}</div>}
      </div>
    </div>
  );
}

// Общая оболочка бейджа стоимости: пилюля с подписью + суммой и выпадающая разбивка по клику.
// tone окрашивает пилюлю при приближении к лимиту (warn/danger).
// ring — кольцевая пилюля (капсула высотой 30 с иконкой колец слева, поповер шире).
// wide — более широкий поповер на узких раскладках (мобил/планшет: для объединённого
// чипа с несколькими секциями — шире → меньше переносов → ниже по высоте, помещается на экран).
// isCompact — планшет (использует мобильную механику поповера wide).
function BadgeShell({ label, amount, title, tip, ariaLabel, isMobile, isCompact, tone, ring, wide, pulse, resetKey, children }: {
  label?: string; amount: React.ReactNode; title: string; ariaLabel?: string; isMobile?: boolean; isCompact?: boolean;
  // Оформленная подсказка вместо нативного title (тот тогда не ставится — иначе две подсказки)
  tip?: React.ReactNode;
  tone?: 'warn' | 'danger'; ring?: boolean; wide?: boolean; pulse?: boolean;
  // Попап показывает данные конкретного чата — при смене чата закрываем
  resetKey?: string;
  children: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  // Сброс при смене чата: попап не переживает переключение сессии
  useEffect(() => { setOpen(false); }, [resetKey]);
  const toneBg = tone === 'danger' ? RATE_COLORS.danger.bg : tone === 'warn' ? RATE_COLORS.warn.bg : C.bgWhite;
  const toneBorder = tone === 'danger' ? RATE_COLORS.danger.border : tone === 'warn' ? RATE_COLORS.warn.border : C.border;
  // На планшете поповер следует той же мобильной геометрии — wide крепится fixed к краю
  // экрана, иначе absolute+right:0 уезжает влево за экран.
  const compact = isCompact || isMobile;
  const button = (
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        title={tip ? undefined : title}
        aria-label={ariaLabel}
        style={{
          display: 'flex', alignItems: 'center',
          gap: SP.xs,
          // Кольцевая: иконка 24 + 2+2 + рамка 1+1 = 30; слева 3 — иконка прижата к скруглению
          padding: ring ? `${SP.xxs}px ${SP.sm + 1}px ${SP.xxs}px 3px` : '3px 9px',
          height: ring ? 30 : undefined, boxSizing: 'border-box',
          background: toneBg, border: `1px solid ${toneBorder}`, borderRadius: ring ? R.max : R.lg,
          cursor: 'pointer', fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.accent,
          whiteSpace: 'nowrap',
        }}
      >
        {label && <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.3 }}>{label}</span>}
        {amount}
      </button>
  );
  return (
    <div style={{ position: 'relative', flexShrink: 0 }}>
      {/* Пилюли стоят у правой кромки шапки — плашка прижата к правому краю. Пока открыт
          поповер, подсказка про то же самое не нужна */}
      {tip ? <Tooltip content={tip} align="end" disabled={open}>{button}</Tooltip> : button}
      {/* Пульс-индикатор «на бегу» — сигнал активного workflow без отдельного чипа в ряду */}
      {pulse && <span style={{ position: 'absolute', top: -3, right: -3, width: 9, height: 9, borderRadius: '50%', background: C.accent, border: `2px solid ${C.bgPanel}`, animation: 'pulsedot 1.2s ease-in-out infinite', pointerEvents: 'none' }} />}
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
          <div style={
            // Широкий мобильный поповер: absolute+right:0 привязан к правому краю чипа,
            // а чип не у края экрана (справа кнопки) → широкий блок уезжал влево за экран.
            // Крепим fixed к правому краю ВЬЮПОРТА под тулбаром — всегда на экране.
            wide && compact
              ? {
                  // На планшете шапка — десктопная (TB.heightDesktop), а на мобиле —
                  // мобильная. Используем высоту тулбара чата по факту, чтобы поповер не
                  // прилипал к неверной кромке после поворота экрана.
                  position: 'fixed', top: (isMobile ? TB.heightMobile : TB.heightDesktop) + 6, right: 8, zIndex: 41,
                  width: 'min(340px, calc(100vw - 16px))',
                  maxHeight: 'calc(100dvh - 130px)', overflowY: 'auto',
                  padding: '12px 14px',
                  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown,
                }
              : {
                  position: 'absolute', top: '100%', right: 0, marginTop: 6, zIndex: 41,
                  minWidth: compact ? 200 : ring ? 280 : 240,
                  maxWidth: 'calc(100vw - 24px)',
                  maxHeight: compact ? 'calc(100dvh - 130px)' : undefined,
                  overflowY: compact ? 'auto' : undefined,
                  padding: '12px 14px',
                  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown,
                }
          }>
            {children}
          </div>
        </>
      )}
    </div>
  );
}

// Есть ли что показывать в Claude-cost бейдже (стоимость или активный лимит)
function hasClaudeCostInfo(stats: CostStats, windows: RateWindow[]): boolean {
  return stats.cost > 0 || !!worstWindow(windows);
}

// Тело поповера стоимости Claude (разбивка токенов/ходов + лимиты подписки + переключатель оплаты).
// Вынесено отдельно: секция поповера кольцевой пилюли.
function ClaudeCostPopoverBody({ stats, billing, onBillingChange, windows }: {
  stats: CostStats; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void; windows: RateWindow[];
}) {
  const sub = billing === 'subscription';
  const [costOpen, setCostOpen] = useState(false);
  const costBody = <>
    {sub && (
      <div style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, marginBottom: 6, lineHeight: 1.45 }}>
        Эквивалент на pay-as-you-go API. По подписке покрыто абонплатой — отдельно не списывается.
      </div>
    )}
    {stats.cost > 0 && <>
      <BadgeRow k={sub ? '≈ Всего' : 'Всего'} v={fmtUsd(stats.cost)} />
      <BadgeRow k="Ходов" v={String(stats.turns || stats.results)} />
      <BadgeRow k="Входные токены" v={fmtTokens(stats.input)} />
      <BadgeRow k="Выходные токены" v={fmtTokens(stats.output)} />
      <BadgeRow k="Кэш (чтение)" v={fmtTokens(stats.cacheRead)} />
      <BadgeRow k="Кэш (запись)" v={fmtTokens(stats.cacheCreate)} />
    </>}
  </>;
  // Есть лимиты — они главное, идут первыми; расход по API-тарифу свёрнут внизу над
  // чертой «Оплата». Лимитов нет (оплата по ключу) — расход и есть содержимое, без сворачивания
  if (windows.length === 0) {
    return <>
      <div style={badgeTitleStyle}>{sub ? 'Claude · ≈ по API-тарифу' : 'Стоимость Claude'}</div>
      {costBody}
      <BillingLine sub={sub} billing={billing} onBillingChange={onBillingChange} />
    </>;
  }
  return (
    <>
      <div style={badgeTitleStyle}>Лимиты Claude</div>
      {/* Порядок — как у колец на пилюле (5 часов → неделя → по моделям), а не по
          проценту: иначе строки попапа не совпадали бы с кольцами и прыгали местами.
          Первые RING_WINDOWS окон — с пипом своего кольца (mid, inner), остальные без */}
      {ratePillSegments(windows).map(s => windows.find(w => w.limitType === s.limitType)!)
        .map((w, i) => (
          <RateRow key={w.limitType} w={w}
            pip={i < RING_WINDOWS ? <RingPip slot={(i + 1) as 1 | 2} level={w.level} /> : undefined} />
        ))}
      <button type="button" onClick={() => setCostOpen(o => !o)} aria-expanded={costOpen}
        style={{
          ...badgeSectionStyle, display: 'flex', alignItems: 'center', gap: 4, width: '100%',
          border: 'none', background: 'none', padding: 0, cursor: 'pointer',
        }}>
        <ChevronRight size={12} strokeWidth={ICON_STROKE} style={{ transform: costOpen ? 'rotate(90deg)' : undefined, transition: 'transform 120ms' }} />
        {sub ? '≈ по API-тарифу' : 'Стоимость'}
        {stats.cost > 0 && <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, textTransform: 'none' }}>{fmtUsd(stats.cost)}</span>}
      </button>
      {costOpen && costBody}
      <BillingLine sub={sub} billing={billing} onBillingChange={onBillingChange} />
    </>
  );
}

// Строка «Оплата: Подписка | API-ключ» под чертой в конце поповера Claude
function BillingLine({ sub, billing, onBillingChange }: {
  sub: boolean; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void;
}) {
  return (
    <>
      <div style={{ marginTop: 10, paddingTop: 8, borderTop: `1px solid ${C.bgInset}`, display: 'flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: 11 }}>
        <span style={{ color: C.textMuted }}>Оплата:</span>
        {/* Настройка серверная, общая для всех — не-админу показываем режим без переключателя */}
        {!onBillingChange ? (
          <span style={{ color: C.textSecondary, fontWeight: 600 }}>
            {sub ? 'Подписка' : 'API-ключ'}
          </span>
        ) : (['subscription', 'api'] as ClaudeBilling[]).map(b => (
          <button key={b} type="button" onClick={() => onBillingChange(b)}
            style={{
              padding: '2px 9px', borderRadius: 6, cursor: 'pointer', fontSize: 11,
              fontFamily: FONT.sans, fontWeight: billing === b ? 700 : 500,
              border: `1px solid ${billing === b ? C.accent : C.border}`,
              background: billing === b ? C.accentLight : C.bgWhite,
              color: billing === b ? C.accent : C.textMuted,
            }}>
            {b === 'subscription' ? 'Подписка' : 'API-ключ'}
          </button>
        ))}
      </div>
    </>
  );
}

// === Кольцевая пилюля шапки (вариант C2) ===
// Спецификация — заметка «Шапка чата — пилюля «кольца» (C2), спецификация».
// Три кольца снаружи внутрь: контекст, 1-е окно, 2-е окно. Слоты не сдвигаются: нет
// окна — его кольца нет вовсе, нет оценки контекста — внешнее кольцо одной дорожкой,
// поэтому кольца не прыгают, когда появляется оценка.
interface RingSlot { pct: number | null; level: RateWindow['level'] }

// Радиусы колец (outer, mid, inner) в viewBox 24×24: толщина 2.5, зазор 1px, дырка Ø4
const RING_GEOMETRY = [10.25, 6.75, 3.25].map(r => ({ r, L: 2 * Math.PI * r }));
const RING_STROKE = 2.5;

// Цвета — через style: C.* это var(--…), а в атрибутах stroke/fill CSS-переменные не резолвятся
function RingCircle({ r, stroke, dash }: { r: number; stroke: string; dash?: string }) {
  return (
    <circle cx={12} cy={12} r={r} fill="none" strokeWidth={RING_STROKE} strokeLinecap="butt"
      strokeDasharray={dash} style={{ stroke }} />
  );
}

// Иконка пилюли 24×24. slots — [outer, mid, inner]; undefined — слота нет (не рисуется),
// pct null — только дорожка. spinOuter — идёт сжатие: внешнее кольцо становится спиннером.
// forceReducedMotion — только для витрины (5.2r), в продукте решает настройка ОС
function RingIcon({ slots, spinOuter, forceReducedMotion }: {
  slots: [RingSlot, RingSlot | undefined, RingSlot | undefined]; spinOuter?: boolean; forceReducedMotion?: boolean;
}) {
  const reducedMotion = usePrefersReducedMotion() || !!forceReducedMotion;
  const outer = RING_GEOMETRY[0];
  return (
    <svg width={24} height={24} viewBox="0 0 24 24" aria-hidden shapeRendering="geometricPrecision"
      style={{ flexShrink: 0, display: 'block' }}>
      {/* Старт дуг сверху, по часовой */}
      <g transform="rotate(-90 12 12)">
        {slots.map((s, i) => {
          if (!s || (i === 0 && spinOuter)) return null;
          const { r, L } = RING_GEOMETRY[i];
          const arc = ringArcLength(s.pct, L);
          return (
            <g key={i}>
              <RingCircle r={r} stroke={C.progressTrack} />
              {arc > 0 && <RingCircle r={r} stroke={RATE_COLORS[s.level].fill} dash={`${arc} ${L}`} />}
            </g>
          );
        })}
      </g>
      {spinOuter && (
        // Вращается только внешнее кольцо; при reduced-motion — неподвижная четверть
        // поверх пунктирной дорожки, чтобы «идёт работа» читалось и без анимации
        <g style={{ transformOrigin: '12px 12px', animation: reducedMotion ? undefined : 'cc-spin 0.9s linear infinite' }}>
          <RingCircle r={outer.r} stroke={C.progressTrack} dash={reducedMotion ? '2 2' : undefined} />
          <RingCircle r={outer.r} stroke={C.accent} dash={`${outer.L * 0.25} ${outer.L}`} />
        </g>
      )}
    </svg>
  );
}

// Пип строки поповера — мини-копия иконки 12×12: все три слота дорожками, слот этой
// строки залит целиком цветом уровня. Просто цветная точка не годится: все окна в норме
// дали бы одинаковые серые точки, а по месту в мини-иконке кольцо узнаётся всегда
function RingPip({ slot, level }: { slot: 0 | 1 | 2; level: RateWindow['level'] }) {
  return (
    <svg width={12} height={12} viewBox="0 0 24 24" aria-hidden shapeRendering="geometricPrecision"
      style={{ flexShrink: 0, display: 'block' }}>
      {RING_GEOMETRY.map(({ r }, i) => (
        <RingCircle key={i} r={r} stroke={i === slot ? RATE_COLORS[level].fill : C.progressTrack} />
      ))}
    </svg>
  );
}

// Сторонний провайдер (DeepSeek/GLM): у него нет лимитов подписки Claude — вместо окон
// квота подписки или остаток средств с подсветкой при низком уровне; провайдер без цен
// и баланса — расход в токенах. Есть ли что показывать (активность или баланс):
function hasProviderCostInfo(stats: CostStats, balance: ProviderBalance | null): boolean {
  return stats.results > 0 || !!balance;
}

// Подсветка по балансу CLI-провайдера. Деньги (<$1 warn, <$0.2 danger) и квота
// подписки в процентах (currency='%', остаток; <10% warn, <3% danger) — разные шкалы.
function providerBalanceTone(balance: ProviderBalance | null): 'warn' | 'danger' | undefined {
  if (!balance) return undefined;
  const balNum = parseFloat(balance.totalBalance);
  if (isNaN(balNum)) return undefined;
  if (balance.currency === '%')
    return balNum < 3 ? 'danger' : balNum < 10 ? 'warn' : undefined;
  return balNum < 0.2 ? 'danger' : balNum < 1 ? 'warn' : undefined;
}

// Квоту подписки бэкенд отдаёт остатком окна, а шапка и раздел «Модели и расход» говорят
// языком расхода — переводим остаток в израсходованное (как в карточках квот).
function quotaUsedPct(balance: ProviderBalance | null): number | null {
  if (!balance) return null;
  const remaining = parseFloat(balance.totalBalance);
  if (isNaN(remaining)) return null;
  return Math.round(Math.min(100, Math.max(0, 100 - remaining)));
}

// Тело поповера статистики CLI-провайдера (стоимость/токены/ходы + баланс аккаунта).
// quotaPip — метка mid-кольца у строки квоты (у GLM квота занимает это кольцо).
function ProviderCostPopoverBody({ providerName, stats, balance, quotaPip }: {
  providerName: string; stats: CostStats; balance: ProviderBalance | null; quotaPip?: ReactNode;
}) {
  const tone = providerBalanceTone(balance);
  const hasCost = stats.cost > 0;
  const isQuota = balance?.currency === '%';
  const usedPct = isQuota ? quotaUsedPct(balance) : null;
  return (
    <>
      <div style={badgeTitleStyle}>{hasCost ? 'Стоимость' : 'Расход'} {providerName}</div>
      {stats.results > 0 && <>
        {hasCost && <BadgeRow k="Всего" v={fmtUsd(stats.cost)} />}
        <BadgeRow k="Ходов" v={String(stats.turns || stats.results)} />
        <BadgeRow k="Входные токены" v={fmtTokens(stats.input)} />
        <BadgeRow k="Выходные токены" v={fmtTokens(stats.output)} />
        <BadgeRow k="Кэш (чтение)" v={fmtTokens(stats.cacheRead)} />
      </>}
      {balance && (
        <>
          <div style={{ ...badgeSectionStyle, display: 'flex', alignItems: 'center', gap: SP.xs + 2 }}>
            {quotaPip}{isQuota ? 'Квота подписки' : 'Баланс аккаунта'}
          </div>
          <BadgeRow k={isQuota ? 'Израсходовано' : 'Остаток'}
            v={isQuota ? (usedPct !== null ? `${usedPct}%` : '—') : `${balance.totalBalance} ${balance.currency}`} />
          {tone && (
            <div style={{ fontFamily: FONT.sans, fontSize: 11, color: RATE_COLORS[tone].text, marginTop: 4, lineHeight: 1.4 }}>
              {isQuota
                ? (tone === 'danger' ? 'Квота почти исчерпана — дождитесь сброса окна.' : 'Квота на исходе.')
                : (tone === 'danger' ? 'Баланс почти исчерпан — пополните аккаунт.' : 'Баланс на исходе.')}
            </div>
          )}
        </>
      )}
      <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, marginTop: 8, lineHeight: 1.4 }}>
        {hasCost
          ? `${providerName} работает по балансовой модели — стоимость списывается с аккаунта по факту.`
          : isQuota
            ? `${providerName} работает по подписке — показываем, сколько квоты израсходовано; расход в токенах для справки.`
            : `${providerName} не отдаёт цены через API — показываем расход в токенах. Квоты смотрите в кабинете провайдера.`}
      </div>
    </>
  );
}

// Значение квоты или баланса стороннего провайдера (для подсказки пилюли): квота
// подписки (GLM) — израсходованный процент, денежный баланс (DeepSeek) — остаток.
function providerPillLabel(balance: ProviderBalance | null): string {
  if (!balance) return '—';
  if (balance.currency === '%') {
    const used = quotaUsedPct(balance);
    return used !== null ? `${used}%` : '—';
  }
  return `${balance.totalBalance} ${balance.currency}`;
}

// Показывать ли контекст-пилюлю: в начале сессии (нет оценки и не свёрнут) — нет
function hasContextInfo(estimate: ContextEstimate): boolean {
  return estimate.pct !== undefined || estimate.fresh;
}

// Тело поповера контекста (детали заполнения + «Сжать контекст» + «Настроить пороги»).
// Вынесено отдельно: секция поповера кольцевой пилюли; pip — метка внешнего кольца у заголовка.
function ContextPopoverBody({ estimate, isWaiting, isCompacting, canCompact, compactNote, onCompact, online, assistantName = 'Ассистент', pip }: {
  estimate: ContextEstimate; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean;
  assistantName?: string; pip?: ReactNode;
}) {
  const [showThresholds, setShowThresholds] = useState(false);
  const c = RATE_COLORS[estimate.level];
  const hasPct = estimate.pct !== undefined;

  // Кнопка сжатия недоступна: ход идёт, компакт идёт, оценки нет, контекст только что сжат,
  // или сжимать ещё нечего (слишком мало ходов — CLI вернёт «not enough messages»)
  const compactDisabled = isWaiting || isCompacting || !hasPct || estimate.fresh || !canCompact || !online;
  const compactTitle = !canCompact && !isWaiting && !isCompacting
    ? 'Пока нечего сжимать — слишком мало сообщений'
    : isWaiting && !isCompacting ? 'Дождитесь завершения текущего хода' : undefined;

  return (
    <>
      <div style={{ ...badgeTitleStyle, display: 'flex', alignItems: 'center', gap: SP.xs + 2 }}>{pip}Контекст сессии</div>
      {hasPct ? (
        <>
          <div style={{ height: 5, borderRadius: 3, background: C.track, overflow: 'hidden', margin: '2px 0 6px' }}>
            <div style={{ width: `${estimate.pct}%`, height: '100%', background: c.fill }} />
          </div>
          <BadgeRow k="Заполнено" v={`${estimate.pct}%`} />
          <BadgeRow k="≈ Токенов" v={`${fmtTokens(estimate.tokens!)} из ${fmtTokens(estimate.window)}`} />
          {estimate.model && <BadgeRow k="Модель" v={modelLabel(estimate.model)} />}
        </>
      ) : (
        <div style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, lineHeight: 1.45 }}>
          {estimate.fresh
            ? 'Контекст сжат — точная оценка появится после следующего хода.'
            : `${assistantName}: оценка появится после первого ответа.`}
        </div>
      )}
      {estimate.lastCompact?.post !== undefined && (
        // Итог последнего сжатия — про объём ИСТОРИИ, а не про окно (системный промпт и
        // инструменты в это число не входят), поэтому отдельной строкой рядом с пояснением ниже
        <BadgeRow k="Сжатие истории" v={estimate.lastCompact.pre !== undefined
          ? `${fmtTokens(estimate.lastCompact.pre)} → ${fmtTokens(estimate.lastCompact.post)}`
          : fmtTokens(estimate.lastCompact.post)} />
      )}
      {estimate.pruned && (
        // Итог обрезок прокси локальной модели за чат: сколько раз двигали контекст и
        // сколько суммарно срезали. Каждый сдвиг отмечен карточкой в ленте, здесь — сумма
        <BadgeRow k="Обрезка контекста" v={prunedSummaryText(estimate.pruned)} />
      )}
      <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, marginTop: 6, lineHeight: 1.4 }}>
        Заменит историю кратким пересказом. При заполнении {assistantName} сожмёт сам.
      </div>
      {compactNote && (
        <div style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, marginTop: 8, padding: '6px 9px', background: C.bgInset, borderRadius: 6, lineHeight: 1.4 }}>
          {compactNote}
        </div>
      )}
      <button
        type="button"
        disabled={compactDisabled}
        onClick={onCompact}
        title={compactTitle}
        style={{
          marginTop: 10, width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
          padding: '6px 10px', borderRadius: 7, border: `1px solid ${compactDisabled ? C.border : C.borderLight}`,
          background: C.bgWhite, cursor: compactDisabled ? 'default' : 'pointer',
          fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
          color: compactDisabled ? C.textMuted : C.textHeading, opacity: compactDisabled ? 0.65 : 1,
        }}
      >
        {isCompacting && <div className="tool-spinner" style={{ width: 11, height: 11 }} />}
        {isCompacting ? 'Сжимаю…' : 'Сжать контекст'}
      </button>
      <div style={{ marginTop: 8, textAlign: 'center' }}>
        <button
          type="button"
          onClick={() => setShowThresholds(true)}
          style={{
            border: 'none', background: 'none', padding: 0, cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, textDecoration: 'underline',
          }}
        >
          Настроить пороги…
        </button>
      </div>
      {showThresholds && <ContextThresholdsDialog onClose={() => setShowThresholds(false)} />}
    </>
  );
}

// Тело поповера трат fal.ai: остаток баланса (асинхронно) + траты чата + ссылка на статистику.
// Вынесено для переиспользования в отдельном FalCostBadge и в секции поповера кольцевой пилюли (мобила).
function FalPopoverBody({ stats }: { stats: FalCostStats }) {
  // undefined = грузится, null = недоступно, number = баланс
  const [balance, setBalance] = useState<number | null | undefined>(undefined);
  useEffect(() => {
    let cancelled = false;
    api.fal.account(7)
      .then(d => { if (!cancelled) setBalance(d.enabled ? (d.balance ?? null) : null); })
      .catch(() => { if (!cancelled) setBalance(null); });
    return () => { cancelled = true; };
  }, []);
  const lowBal = typeof balance === 'number' && balance < 5;
  const balanceText = balance === undefined ? '…' : typeof balance === 'number' ? fmtUsd(balance) : '—';
  // Разбивка по моделям одной inline-строкой: топ-2 + «+N в статистике»
  const entries = [...stats.byModel.entries()].sort((a, b) => b[1].cost - a[1].cost);
  const topModels = entries.slice(0, 2);
  const moreCount = entries.length - topModels.length;
  const inline = topModels
    .map(([ep, m]) => `${ep.split('/').pop()}${m.count > 1 ? ` ×${m.count}` : ''} ${fmtUsd(m.cost)}`)
    .join('  ·  ');
  return (
    <>
      {/* Герой — траты этого чата (за этим и кликнули) */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        <span>Траты fal.ai · этот чат</span>
        <span style={{ letterSpacing: 0 }}>{stats.count} ген.</span>
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 22, fontWeight: 700, color: C.accent, margin: '2px 0 4px' }}>{fmtUsd(stats.total)}</div>
      {inline && (
        <div style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textSecondary, marginBottom: 4, lineHeight: 1.4 }}>
          {inline}{moreCount > 0 ? `  ·  +${moreCount} в статистике` : ''}
        </div>
      )}
      {/* Баланс аккаунта — отдельной плашкой (другая сущность). Краснеет при низком остатке. */}
      <div style={{
        marginTop: 8, padding: '8px 10px', borderRadius: R.lg,
        background: lowBal ? C.warningBg : C.bgInset, border: lowBal ? `1px solid ${C.warning}` : 'none',
        display: 'flex', justifyContent: 'space-between', alignItems: 'baseline',
        fontFamily: FONT.sans, fontSize: 12, color: lowBal ? C.warningText : C.textSecondary,
      }}>
        <span>Счёт fal.ai <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: lowBal ? C.warningText : C.accent }}>{balanceText}</span></span>
        <a href="https://fal.ai/dashboard/billing" target="_blank" rel="noopener noreferrer"
          style={{ color: C.accent, fontWeight: 600, textDecoration: 'none', flexShrink: 0, marginLeft: 8 }}>пополнить ↗</a>
      </div>
      <div style={{ marginTop: 10 }}>
        <button type="button" onClick={() => window.dispatchEvent(new Event('open-fal-stats'))}
          style={{ border: 'none', background: 'none', cursor: 'pointer', padding: 0, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.accent }}>
          Подробная статистика →
        </button>
      </div>
    </>
  );
}

// Бейдж трат на fal.ai (медиа). Отдельная от Claude цифра. Разбивка по моделям.
function FalCostBadge({ stats, isCompact, resetKey }: { stats: FalCostStats; isCompact?: boolean; resetKey?: string }) {
  if (stats.total <= 0) return null;
  return (
    <BadgeShell label="fal.ai" amount={fmtUsd(stats.total)} isCompact={isCompact} resetKey={resetKey}
      title="Траты на fal.ai (медиа) — нажмите для разбивки">
      <FalPopoverBody stats={stats} />
    </BadgeShell>
  );
}

// Тело поповера генераций glif: разбивка по типам медиа + кредиты (когда billing доехал)
// + баланс аккаунта (асинхронно) + ссылка на статистику.
// Вынесено для переиспользования в отдельном GlifCostBadge и в секции поповера кольцевой пилюли (мобила).
function GlifPopoverBody({ stats }: { stats: GlifGenStats }) {
  // undefined = грузится, null = недоступно, number = баланс кредитов
  const [balance, setBalance] = useState<number | null | undefined>(undefined);
  useEffect(() => {
    let cancelled = false;
    api.glif.account()
      .then(d => { if (!cancelled) setBalance(d.enabled ? (d.balance ?? null) : null); })
      .catch(() => { if (!cancelled) setBalance(null); });
    return () => { cancelled = true; };
  }, []);
  const balanceText = balance === undefined ? '…' : typeof balance === 'number' ? fmtCredits(balance) : '—';
  // Разбивка по типам одной inline-строкой: топ-2 + «+N в статистике» (как у fal по моделям)
  const entries = [...stats.byType.entries()].sort((a, b) => b[1] - a[1]);
  const topTypes = entries.slice(0, 2);
  const moreCount = entries.length - topTypes.length;
  const inline = topTypes
    .map(([t, n]) => `${t}${n > 1 ? ` ×${n}` : ''}`)
    .join('  ·  ');
  return (
    <>
      {/* Герой — генерации этого чата (за этим и кликнули) */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        <span>Генерации glif · этот чат</span>
        {stats.hasCredits && <span style={{ letterSpacing: 0 }}>{fmtCredits(stats.credits)}</span>}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 22, fontWeight: 700, color: C.accent, margin: '2px 0 4px' }}>{stats.count} ген.</div>
      {inline && (
        <div style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textSecondary, marginBottom: 4, lineHeight: 1.4 }}>
          {inline}{moreCount > 0 ? `  ·  +${moreCount} в статистике` : ''}
        </div>
      )}
      {/* Баланс аккаунта — отдельной плашкой (другая сущность), в кредитах */}
      <div style={{
        marginTop: 8, padding: '8px 10px', borderRadius: R.lg,
        background: C.bgInset,
        display: 'flex', justifyContent: 'space-between', alignItems: 'baseline',
        fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary,
      }}>
        <span>Счёт glif <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.accent }}>{balanceText}</span></span>
      </div>
      <div style={{ marginTop: 10 }}>
        <button type="button" onClick={() => window.dispatchEvent(new Event('open-fal-stats'))}
          style={{ border: 'none', background: 'none', cursor: 'pointer', padding: 0, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.accent }}>
          Подробная статистика →
        </button>
      </div>
    </>
  );
}

// Бейдж генераций glif (медиа). Пер-кредитной цены нет — значение это счётчик
// генераций (+ сумма кредитов, когда billing приехал). Разбивка по типам медиа.
function GlifCostBadge({ stats, isCompact, resetKey }: { stats: GlifGenStats; isCompact?: boolean; resetKey?: string }) {
  if (stats.count <= 0) return null;
  const amount = `${stats.count} ген.` + (stats.hasCredits ? ` · ${fmtCredits(stats.credits)}` : '');
  return (
    <BadgeShell label="glif" amount={amount} isCompact={isCompact} resetKey={resetKey}
      title="Генерации glif (медиа) — нажмите для разбивки">
      <GlifPopoverBody stats={stats} />
    </BadgeShell>
  );
}

// Приоритет tone: danger важнее warn (для объединения подсветок контекста и стоимости)
function worseTone(a?: 'warn' | 'danger', b?: 'warn' | 'danger'): 'warn' | 'danger' | undefined {
  if (a === 'danger' || b === 'danger') return 'danger';
  if (a === 'warn' || b === 'warn') return 'warn';
  return undefined;
}

// Кольцевая пилюля шапки — одна на все раскладки (вариант C2): иконка из трёх колец
// (контекст, 1-е и 2-е окно) и текст ОДНОГО худшего показателя («5ч 81%», «Ctx 92%»),
// без счётчика «+N». Все окна, контекст и стоимость — в одном поповере.
// Провайдер: Claude → лимиты подписки; CLI (DeepSeek/GLM) → квота или баланс.
// На мобиле/планшете (isCompact) в неё же втянуты прогресс workflow и секции fal/glif,
// на десктопе они — отдельные бейджи.
export function RingPillBadge(props: {
  // контекст
  estimate: ContextEstimate; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean; assistantName: string;
  // стоимость
  isCliProvider: boolean; providerName: string; cost: CostStats; falCost: FalCostStats; glifCost: GlifGenStats;
  balance: ProviderBalance | null; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void;
  windows: RateWindow[];
  // workflow (только мобила/планшет): прогресс фаз втягивается в эту же пилюлю
  activeWorkflow?: { phasesDone: number; phasesTotal: number };
  isMobile?: boolean;
  // Узкая раскладка (мобила или планшет): широкий поповер, workflow и fal/glif внутри
  isCompact?: boolean;
  // Сброс поповера при смене чата
  resetKey?: string;
  // Только витрина: показать состояние 5.2r без смены настройки ОС
  forceReducedMotion?: boolean;
}) {
  const {
    estimate, isCompacting, isCliProvider, providerName, cost, falCost, glifCost, balance, billing, windows, isMobile, isCompact,
  } = props;
  const activeWorkflow = isCompact ? props.activeWorkflow : undefined;
  const wfActive = !!activeWorkflow;

  // Что доступно к показу в каждой секции
  const showCtx = hasContextInfo(estimate);
  const showCost = isCliProvider
    ? hasProviderCostInfo(cost, balance)
    : hasClaudeCostInfo(cost, windows);
  const hasFal = !!isCompact && !isCliProvider && falCost.total > 0;
  const hasGlif = !!isCompact && !isCliProvider && glifCost.count > 0;
  // Совсем нечего показывать — прячем пилюлю (но активный workflow держит её на экране)
  if (!showCtx && !showCost && !hasFal && !hasGlif && !wfActive) return null;

  // Квота подписки (GLM) — процентом в mid-кольце; денежный баланс (DeepSeek) процента
  // не имеет и кольца не получает, но в тревожном тоне выходит текстом на пилюлю
  const provTone = isCliProvider ? providerBalanceTone(balance) : undefined;
  const provLevel: RateWindow['level'] = provTone ?? 'normal';
  const isQuota = isCliProvider && balance?.currency === '%';
  const quotaPct = isQuota ? quotaUsedPct(balance) : null;

  const segs = isCliProvider ? [] : ratePillVisible(windows, RING_WINDOWS).segments;
  const slot = (s?: RatePillSegment): RingSlot | undefined => s && { pct: s.pct, level: s.level };
  const slots: [RingSlot, RingSlot | undefined, RingSlot | undefined] = [
    { pct: showCtx ? estimate.pct ?? null : null, level: estimate.level },
    isCliProvider ? (isQuota ? { pct: quotaPct, level: provLevel } : undefined) : slot(segs[0]),
    isCliProvider ? undefined : slot(segs[1]),
  ];

  // Кандидаты на текст — контекст и ВСЕ окна (в том числе вне колец) либо квота/баланс провайдера
  const others: RingPillCandidate[] = !isCliProvider
    ? windowCandidates(windows)
    : isQuota
      ? (quotaPct !== null ? [{ label: providerName, text: `${quotaPct}%`, pct: quotaPct, level: provLevel }] : [])
      : (provTone && balance ? [{ label: '', text: `${balance.totalBalance} ${balance.currency}`, pct: null, level: provTone }] : []);
  // Денежный баланс в норме выходит на пилюлю, только пока оценки контекста нет (5.8b):
  // иначе в начале сессии DeepSeek на пилюле стояло бы «—»
  const idle: RingPillCandidate | undefined = isCliProvider && !isQuota && balance && !provTone
    ? { label: '', text: `${balance.totalBalance} ${balance.currency}`, pct: null, level: 'normal' }
    : undefined;
  const head = ringPillHead(showCtx ? estimate : null, others, showCtx ? undefined : idle);

  // Подсветка пилюли — худшая из контекста и ВСЕХ окон (или провайдера)
  const ctxTone = estimate.level !== 'normal' ? estimate.level : undefined;
  const worst = worstWindow(windows);
  const costTone = isCliProvider
    ? provTone
    : (worst && worst.level !== 'normal' ? worst.level : undefined);
  const tone = worseTone(ctxTone, costTone);

  // По API-ключу деньги реальные — сумма идёт первой; по подписке это лишь API-эквивалент
  const apiCost = !isCliProvider && billing === 'api' && cost.cost > 0;
  const labelStyle: React.CSSProperties = { color: C.textMuted, fontWeight: 600 };
  const face: ReactNode = wfActive ? (
    <span style={{ color: C.accent }}>
      WF{activeWorkflow!.phasesTotal > 0 ? ` ${activeWorkflow!.phasesDone}/${activeWorkflow!.phasesTotal}` : ''}
    </span>
  ) : isCompacting ? (
    // Идёт сжатие — пользователь ждёт именно контекст, он перебивает худшее окно
    <><span style={labelStyle}>Ctx</span> <span style={{ color: C.accent }}>…</span></>
  ) : !head ? (
    apiCost ? null : <span style={{ color: C.textMuted }}>—</span>
  ) : head.kind === 'fresh' ? (
    <><span style={labelStyle}>Ctx</span> <span style={{ color: C.accent }}>✦</span></>
  ) : head.kind === 'calm' ? (
    <span style={{ color: C.textSecondary }}>в норме</span>
  ) : head.kind === 'unknown' ? (
    <span style={{ color: C.textMuted }}>—</span>
  ) : (
    <>
      {head.label && <><span style={labelStyle}>{head.label}</span>{' '}</>}
      <span style={{ color: head.level === 'normal' ? C.textSecondary : RATE_COLORS[head.level].text }}>{head.text}</span>
    </>
  );

  // Подсказка и aria-label — всё полными подписями, доступно без клика. У строк, за
  // которыми стоит кольцо, — пип этого кольца (как в поповере): так видно, что есть что
  const parts: { text: string; pip?: { slot: 0 | 1 | 2; level: RateWindow['level'] } }[] = [];
  if (wfActive) parts.push({ text: `Workflow ${activeWorkflow!.phasesTotal > 0 ? `${activeWorkflow!.phasesDone}/${activeWorkflow!.phasesTotal}` : 'идёт'}` });
  if (apiCost) parts.push({ text: `Claude по API-ключу ${fmtUsd(cost.cost)}` });
  const ctxPip = { slot: 0 as const, level: estimate.level };
  if (isCompacting) parts.push({ text: 'Контекст: идёт сжатие', pip: ctxPip });
  else if (showCtx) parts.push({ text: estimate.pct !== undefined ? `Контекст ${estimate.pct}%` : 'Контекст сжат', pip: ctxPip });
  if (isCliProvider) {
    if (balance) parts.push({
      text: `${providerName}: ${isQuota ? 'израсходовано' : 'остаток'} ${providerPillLabel(balance)}`,
      pip: isQuota ? { slot: 1, level: provLevel } : undefined,
    });
  } else {
    parts.push(...ratePillSegments(windows).map((s, i) => ({
      text: `${windowLabel(s.limitType)} ${s.text}${s.stale ? ' (данные устарели)' : ''}`,
      pip: i < RING_WINDOWS ? { slot: (i + 1) as 1 | 2, level: s.level } : undefined,
    })));
  }
  const title = (parts.length > 0 ? parts.map(p => p.text).join(' · ') : 'Контекст и расход сессии') + ' — нажмите для деталей';
  const tip = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs + 1 }}>
      {parts.length > 0 ? parts.map(p => (
        <div key={p.text} style={{ display: 'flex', alignItems: 'center', gap: SP.xs + 2 }}>
          {/* Строки без кольца — с пустым местом под пип, чтобы текст шёл одной колонкой */}
          {p.pip ? <RingPip slot={p.pip.slot} level={p.pip.level} /> : <span style={{ width: 12, flexShrink: 0 }} />}
          <span>{p.text}</span>
        </div>
      )) : <div>Контекст и расход сессии</div>}
      <div style={{ color: C.textMuted, fontSize: FS.xs }}>Нажмите для деталей</div>
    </div>
  );

  const sectionDivider: React.CSSProperties = {
    marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.bgInset}`,
  };

  return (
    <BadgeShell
      ring
      amount={<>
        <RingIcon slots={slots} spinOuter={isCompacting} forceReducedMotion={props.forceReducedMotion} />
        <span>
          {apiCost && <>
            <span style={{ color: C.textSecondary }}>{fmtUsd(cost.cost)}</span>
            {face && <span style={{ color: C.textMuted, fontWeight: 400 }}> · </span>}
          </>}
          {face}
        </span>
      </>}
      isMobile={isMobile}
      isCompact={isCompact}
      tone={tone}
      wide={isCompact}
      pulse={wfActive}
      resetKey={props.resetKey}
      title={title}
      tip={tip}
      ariaLabel={title}
    >
      {wfActive && (
        <div>
          <div style={badgeTitleStyle}>Workflow</div>
          <BadgeRow k="Фаза" v={activeWorkflow!.phasesTotal > 0 ? `${activeWorkflow!.phasesDone}/${activeWorkflow!.phasesTotal}` : 'идёт'} />
        </div>
      )}
      {showCtx && (
        <div style={wfActive ? sectionDivider : undefined}>
          <ContextPopoverBody {...props} pip={<RingPip slot={0} level={estimate.level} />} />
        </div>
      )}
      {showCost && (
        <div style={(wfActive || showCtx) ? sectionDivider : undefined}>
          {isCliProvider
            ? <ProviderCostPopoverBody providerName={providerName} stats={cost} balance={balance}
                quotaPip={isQuota ? <RingPip slot={1} level={provLevel} /> : undefined} />
            : <ClaudeCostPopoverBody stats={cost} billing={billing} onBillingChange={props.onBillingChange} windows={windows} />}
        </div>
      )}
      {hasFal && (
        <div style={sectionDivider}>
          <FalPopoverBody stats={falCost} />
        </div>
      )}
      {hasGlif && (
        <div style={sectionDivider}>
          <GlifPopoverBody stats={glifCost} />
        </div>
      )}
    </BadgeShell>
  );
}

interface ChatHeaderBarProps {
  session: Session;
  project?: Project;
  // Есть ли в чате переписка (из ленты) — показ кнопок «Итог сессии»/«Задачи из чата»
  hasMessages: boolean;
  online: boolean;
  cost: CostStats;
  falCost: FalCostStats;
  glifCost: GlifGenStats;
  billing: ClaudeBilling;
  // Не задан — переключать нельзя (не админ): показывается только текущий режим
  onBillingChange?: (b: ClaudeBilling) => void;
  rateWindows: RateWindow[];
  isMobile?: boolean;
  onBack?: () => void;
  activeWorkflow?: { phasesDone: number; phasesTotal: number };
  // Последняя запущенная в чате механика «Обсудить с командой» — компактный бейдж в шапке
  lastMechanic?: TeamMechanicId | null;
  onOpenSidebar?: () => void;
  ctxEstimate: ContextEstimate;
  isWaiting: boolean;
  isCompacting: boolean;
  canCompact: boolean;
  compactNote?: string;
  onCompact: () => void;
  // Персона чата — идентификация встроена прямо в тулбар
  persona?: Persona | null;
  personaZoneName?: string | null;         // имя проекта для бейджа зоны проектной персоны
  // .md-агент чата (когда персоны нет) — компактная точка + имя в подзаголовке
  agent?: { name: string; color?: string } | null;
  // Участники группового чата (2-8): стек аватаров вместо одиночного блока персоны;
  // активный спикер (= persona) — с цветным кольцом
  participants?: Persona[] | null;
  // Состав группы изменён через поповер участников — родитель обновляет session
  onSessionUpdated?: (s: Session) => void;
  // «На стену» — набор стены живёт в воркспейсе; не задан — действия нет
  onAddToWall?: () => void;
  // Чат удалён из шапки: уйти из него и обновить список должен владелец экрана.
  // Не задан — действия «Удалить» в шапке нет вовсе
  onChatDeleted?: (sessionId: string) => void;
  // Шапка живёт в собственном острове (Islands): фон и нижнюю границу даёт
  // карточка-остров, тулбар рисуется прозрачным и без borderBottom
  island?: boolean;
  // Узкая колонка «Стены»: прячем кнопку настроек чата (её диалог шире колонки)
  compact?: boolean;
  // Полоса контекста чата (фича chat-context): отдельная строка ПОД заголовком —
  // и в hero-шапке, и в тулбарной. Не задана — шапка ровно такая, как была
  contextBar?: ReactNode;
  // Лента прокручена от начала — только тогда шапку отделяет линия. В начале ленты
  // отделять нечего, и черта лежала бы поперёк пустого чата без причины
  scrolled?: boolean;
}

// «Обновить название чата» — запускается через AI-палитру (действие chat.retitle).
// Невидимый слушатель cc-ai-run: перечитывает переписку и переименовывает чат по её смыслу.
function RetitleButton({ session, hasMessages, online }: { session: Session; hasMessages: boolean; online: boolean }) {
  const [busy, setBusy] = useState(false);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс busy при смене чата
  useEffect(() => { setBusy(false); }, [session.id]);
  const run = () => {
    if (busy) return;
    setBusy(true);
    beginAiBusy();
    api.chats.retitle(session.id)
      .then(s => showToast('Название обновлено', s.name ?? '', 'claude'))
      .catch(() => showToast('Название чата', 'Не удалось обновить название', 'info'))
      .finally(() => { setBusy(false); endAiBusy(); });
  };
  useEffect(() => {
    if (!online || !hasMessages) return;
    const onRun = (e: Event) => { if ((e as CustomEvent<{ action?: string }>).detail?.action === 'chat.retitle') run(); };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, session.id, hasMessages, busy]);
  return null;
}

// Иконка «задачи из чата» — документ с плюсом
// «Задачи из чата» — запускаются ТОЛЬКО через AI-палитру (действие chat.extract).
// Кнопка убрана; компонент остаётся смонтированным ради слушателя cc-ai-run и
// показывает модалку выбора извлечённых кандидатов.
function ExtractTasksButton({ session, hasMessages, online }: { session: Session; hasMessages: boolean; online: boolean }) {
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [dialog, setDialog] = useState<{ projectId: string | null; items: (ExtractedTaskCandidate & { sel: boolean })[] } | null>(null);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс модалки и busy при смене чата
  useEffect(() => { setDialog(null); setBusy(false); }, [session.id]);

  const run = () => {
    if (busy) return;
    setBusy(true);
    beginAiBusy();
    api.sessions.extractTasks(session.id)
      .then(r => {
        if (r.tasks.length === 0) {
          showToast('Задачи из чата', 'В этом чате задач-действий не нашлось', 'info');
          return;
        }
        setDialog({ projectId: r.projectId ?? null, items: r.tasks.map(t => ({ ...t, sel: true })) });
      })
      .catch(() => showToast('Задачи из чата', 'Не удалось извлечь задачи из чата', 'info'))
      .finally(() => { setBusy(false); endAiBusy(); });
  };
  // AI-хаб: запуск «Задачи из чата» из палитры/подсказки (тот же обработчик, что и кнопка)
  useEffect(() => {
    if (!online || !hasMessages) return;
    const onRun = (e: Event) => { if ((e as CustomEvent<{ action?: string }>).detail?.action === 'chat.extract') run(); };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, session.id, hasMessages, busy]);
  if (!online || !hasMessages) return null;
  const toggle = (i: number) =>
    setDialog(d => d && ({ ...d, items: d.items.map((t, idx) => idx === i ? { ...t, sel: !t.sel } : t) }));
  const create = async () => {
    if (!dialog) return;
    const chosen = dialog.items.filter(t => t.sel);
    if (chosen.length === 0) { setDialog(null); return; }
    setCreating(true);
    try {
      for (const t of chosen)
        await createTask(dialog.projectId, { title: t.title, dueDate: t.due ?? undefined, priority: t.priority ?? undefined });
      setDialog(null);
      showToast('Задачи из чата', `Создано задач: ${chosen.length}`, 'claude');
    } catch { showToast('Задачи из чата', 'Не удалось создать задачи', 'info'); }
    finally { setCreating(false); }
  };
  const selectedCount = dialog?.items.filter(t => t.sel).length ?? 0;

  return (
    <>
      {dialog && (
        <Modal width={460} title="Задачи из чата" subtitle="Отметьте, что добавить в трекер"
          onClose={() => setDialog(null)}
          footer={<ModalActions confirmLabel={`Создать (${selectedCount})`} confirmDisabled={selectedCount === 0}
            loading={creating} onConfirm={create} onCancel={() => setDialog(null)} />}>
          <div style={{ display: 'flex', flexDirection: 'column', maxHeight: 360, overflowY: 'auto' }}>
            {dialog.items.map((t, i) => (
              <label key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 10, padding: '9px 4px', cursor: 'pointer', borderBottom: `1px solid ${C.border}` }}>
                <input type="checkbox" checked={t.sel} onChange={() => toggle(i)} style={{ marginTop: 3, accentColor: C.accent }} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13.5, fontFamily: FONT.sans, color: C.textPrimary }}>{t.title}</span>
                  {(t.due || t.priority) && (
                    <span style={{ display: 'flex', gap: 8, marginTop: 3 }}>
                      {t.due && <span style={{ fontSize: 11, color: C.textSecondary }}>📅 {t.due}</span>}
                      {t.priority && <span style={{ fontSize: 11, color: C.textMuted }}>{t.priority}</span>}
                    </span>
                  )}
                </span>
              </label>
            ))}
          </div>
        </Modal>
      )}
    </>
  );
}

export function ChatHeaderBar({ session, project, hasMessages, online, cost, falCost, glifCost, billing, onBillingChange, rateWindows, isMobile, onBack, activeWorkflow, lastMechanic, onOpenSidebar, ctxEstimate, isWaiting, isCompacting, canCompact, compactNote, onCompact, persona, personaZoneName, agent, participants, onSessionUpdated, onAddToWall, onChatDeleted, island, compact, contextBar, scrolled }: ChatHeaderBarProps) {
  // Вклады слота chat-header-action: невидимый слушатель «Итог сессии в заметку»
  // и его пункт в правом клик-меню. Нет подсистемы — нет и вкладов, остальные
  // AI-действия чата к заметкам не относятся и остаются.
  const summaryAction = useSlotItem<ChatHeaderSummaryCtx>('chat-header-action', 'session-summary');
  const summaryMenuItem = useSlotItem<ChatHeaderMenuItemCtx>('chat-header-action', 'summary-menu-item');
  const spendBadgeSlot = useSlotItem<ChatHeaderBadgeCtx>('chat-header-badge', 'spend-badge');
  // УЗКИЙ планшет (601 – TABLET_WIDE_MIN): мобильная механика — объединённый чип,
  // wide-поповер, плотная группа кнопок, заголовок с многоточием. Объединяем с mobile
  // через `isCompact`, чтобы не дублировать ветки внутри costBadges / rightCluster /
  // actionBtns.
  //
  // Верхняя граница — TABLET_WIDE_MIN, а не TABLET_MAX: тулбарная ветка ниже
  // растягивается на всю ширину острова, тогда как лента и композер зажаты в
  // CHAT_MAX_W = 950 и центрированы. На широком планшете (1120 у MatePad) остров
  // шире — шапка вылезала за колонку сообщений на 37px с каждой стороны, и контролы
  // висели левее и правее всего остального. Hero-ветка такой ширины не имеет:
  // maxWidth: CHAT_MAX_W ставит её ровно над лентой.
  const ww = useWindowWidth();
  const isTablet = !isMobile && ww > MOBILE_MAX && ww < TABLET_WIDE_MIN;
  const isCompact = isMobile || isTablet;

  // Теги чата: реестр проекта (для чата вне проекта тегов нет — кнопки тоже нет).
  // Локальная копия, чтобы создание тега сразу отражалось в меню без перезагрузки
  const [tagRegistry, setTagRegistry] = useState<ProjectTag[]>(() => project?.tagRegistry ?? []);
  useEffect(() => { setTagRegistry(project?.tagRegistry ?? []); }, [project?.id, project?.tagRegistry]);
  const [tagMenu, setTagMenu] = useState<DOMRect | null>(null);
  const projectId = project?.id;
  const canTag = online && !!projectId;

  // Переключить тег на чате: optimistic через onSessionUpdated, PUT, откат при сбое
  const toggleTag = (name: string) => {
    if (!projectId) return;
    const cur = session.tags ?? [];
    const has = cur.some(t => t.toLowerCase() === name.toLowerCase());
    const next = has ? cur.filter(t => t.toLowerCase() !== name.toLowerCase()) : [...cur, name];
    onSessionUpdated?.({ ...session, tags: next });
    api.sessions.update(projectId, session.id, { tags: next })
      .then(updated => onSessionUpdated?.(updated))
      .catch(() => onSessionUpdated?.({ ...session, tags: cur }));
  };

  // Новый тег: в реестр проекта (цвет — следующий из палитры по кругу) и сразу на чат
  const createTag = (name: string) => {
    if (!projectId) return;
    const color = GROUP_COLORS[tagRegistry.length % GROUP_COLORS.length];
    const nextReg = [...tagRegistry, { name, order: tagRegistry.length, color }];
    setTagRegistry(nextReg);
    api.projects.updateTags(projectId, nextReg)
      .then(p => setTagRegistry(p.tagRegistry ?? nextReg))
      .catch(() => setTagRegistry(tagRegistry));
    if (!(session.tags ?? []).some(t => t.toLowerCase() === name.toLowerCase())) {
      const next = [...(session.tags ?? []), name];
      onSessionUpdated?.({ ...session, tags: next });
      api.sessions.update(projectId, session.id, { tags: next }).catch(() => {});
    }
  };

  // Поповер управления участниками группового чата (клик по стеку аватаров)
  const [participantsOpen, setParticipantsOpen] = useState(false);
  // Right-click меню шапки: якорь — точка курсора (desktop). Состав — действия чата,
  // которые в ряду живут по отдельности (теги/уведомления/досье/срок) + AI-действия.
  // Здесь же живут тумблеры пинирования: «⋯» при активных пинах открывает это же меню
  const [ctxMenu, setCtxMenu] = useState<DOMRect | null>(null);
  // Пины шапки: пока список пуст — ряд дефолтный (все действия видны); первый пин
  // включает ручной режим (pinned в ряду, остальные в «⋯»)
  // Стена настраивается отдельно и разом для всех своих колонок; обычная шапка —
  // своим набором, у мобильной он ýже (ряд не переносится, место дорогое)
  const headerVis = useActionVisibility(
    compact ? 'chat-wall' : 'chat-header',
    compact ? WALL_ACTIONS_HIDDEN_BY_DEFAULT
      : isCompact ? HEADER_COMPACT_HIDDEN_BY_DEFAULT : HEADER_ACTIONS_HIDDEN_BY_DEFAULT,
  );
  // Пикер срока по якорю из right-click меню (паттерн expiryMenu из ChatCard)
  const [expiryMenu, setExpiryMenu] = useState<DOMRect | null>(null);
  // При смене чата попапы тулбара закрываются: данные привязаны к сессии, и
  // показывать стейт предыдущего чата в новом — дефект UX
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс стейтов попапов тулбара при смене чата
  useEffect(() => { setTagMenu(null); setParticipantsOpen(false); setCtxMenu(null); setExpiryMenu(null); }, [session.id]);
  // Клик по блоку персоны — карточка персоны: в проектном чате открывается в контентной зоне
  // проекта (вкладка «Команда», #/project/{id}/persona/{pid}), в глобальном — раздел «Персоны».
  // На мобиле блок вложен в BackButton («назад к списку») — там клик остаётся за ним.
  const [personaHover, setPersonaHover] = useState(false);
  const openPersonaCard = persona
    ? () => {
        const url = project
          ? `#/project/${project.id}/persona/${encodeURIComponent(persona.id)}`
          : `#/personas/${encodeURIComponent(persona.id)}`;
        window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
      }
    : null;
  // compact (колонка стены): блок персоны — просто подпись, без перехода. Уводить
  // с экрана из шапки колонки нельзя: единственный выход отсюда — кнопка перехода
  // в ярлыке колонки, и она ведёт к самому чату, а не в чужой раздел.
  const personaCardLink = openPersonaCard && !compact && !(isCompact && onBack) ? {
    role: 'button' as const, tabIndex: 0,
    onClick: openPersonaCard,
    onKeyDown: (e: React.KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openPersonaCard(); } },
    onMouseEnter: () => setPersonaHover(true),
    onMouseLeave: () => setPersonaHover(false),
  } : null;
  const asstName = assistantName(session.model);
  const providerKey = session.provider ?? modelProvider(session.model);
  // Сторонний провайдер — только ключ из каталога провайдеров. Дополнительные подписки пула
  // Claude названы произвольно (claude-3, work-account) и остаются Claude.
  // useProviders — подписка на каталог: до его загрузки чат перерисуется, когда тот придёт
  const isCliProvider = useProviders().some(p => p.key !== 'claude' && p.key === providerKey);
  // Окна лимитов: живые события чата, а где процента нет — последний снимок аккаунта
  // этого чата (цифры видны с открытия, до первого хода)
  const accountUsage = useAccountUsage(!isCliProvider && !compact);
  const limitWindows = useMemo(
    () => isCliProvider ? rateWindows : withAccountFallback(rateWindows, accountSnapshotsFor(accountUsage, providerKey)),
    [isCliProvider, rateWindows, accountUsage, providerKey],
  );
  // Баланс провайдера — только для сессий сторонних провайдеров (для плашки статистики);
  // 404 (провайдер без источника баланса, напр. GLM) — просто без блока баланса
  const [provBalance, setProvBalance] = useState<ProviderBalance | null>(null);
  useEffect(() => {
    // Сброс всегда: при смене провайдера (deepseek → glm) 404 не перезаписал бы
    // стейт в catch — и в плашке остался бы чужой баланс
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс устаревшего баланса провайдера перед загрузкой
    setProvBalance(null);
    if (!isCliProvider) return;
    let alive = true;
    api.providers.balance(providerKey)
      .then(b => { if (alive) setProvBalance(b); })
      .catch(() => { /* баланс — необязательная информация */ });
    return () => { alive = false; };
  }, [session.model, providerKey, isCliProvider]);
  // Цвет персоны (её акцент бренда) — тонирует заголовок, пилюлю зоны и левую границу тулбара.
  const personaAccent = persona ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent) : null;
  const personaIsProject = isProjectPersona(persona);
  const personaIsSphere = isSpherePersona(persona);
  const spheresOn = useFeature(FLAGS.spheres);
  const spheres = useSpheres(spheresOn);
  const personaZoneText = personaIsSphere && persona
    ? zoneLabel(persona, { sphereName: id => spheres.find(x => x.id === id)?.name }, true)
    : personaIsProject
      ? (personaZoneName ? `Проект · ${personaZoneName}` : 'Проект')
      : 'Глобальный';
  // Происхождение чата (задача/автоматизация) — рисуется в мета-строке заголовка
  // (см. metaRow): на мобиле компактной иконкой, на десктопе коротким бейджем.
  const origin = resolveChatOrigin(session);
  const deviceBadge = projectDeviceBadge(project);
  // Блок названия чата. На мобиле он целиком кликабелен как «назад».
  // Кликабельный стек аватаров группового чата (активный спикер — с цветным
  // кольцом) + поповер управления составом. Размер аватара параметром: компактный
  // в тулбарной шапке, крупнее в hero-шапке. stopPropagation — на мобиле стек
  // живёт внутри BackButton-обёртки, клики не должны уходить в «Назад к списку».
  const participantsStack = (avatarSize: number) => participants && participants.length > 1 ? (
    <div style={{ position: 'relative', flexShrink: 0 }} onClick={e => e.stopPropagation()}>
      <button
        type="button"
        onClick={e => { e.stopPropagation(); setParticipantsOpen(o => !o); }}
        title="Участники чата — нажмите, чтобы добавить или убрать"
        style={{ display: 'flex', alignItems: 'center', border: 'none', background: 'none', cursor: 'pointer', padding: 0 }}
      >
        {participants.map((p, i) => {
          const active = p.id === persona?.id;
          const ring = active
            ? (AGENT_COLORS[p.avatar?.color ?? ''] ?? C.accent)
            : C.bgMain;
          return (
            <div key={p.id} style={{
              marginLeft: i === 0 ? 0 : -Math.round(avatarSize / 3),
              borderRadius: '50%',
              border: `2px solid ${ring}`,
              zIndex: active ? participants.length + 1 : participants.length - i,
              position: 'relative',
              background: C.bgMain,
            }}>
              <PersonaAvatar persona={p} size={avatarSize} />
            </div>
          );
        })}
        {/* «+» — явный вход в управление составом (до 4 участников) */}
        {participants.length < 4 && (
          <span style={{
            marginLeft: -Math.round(avatarSize / 4), zIndex: 0, width: avatarSize, height: avatarSize,
            borderRadius: '50%', border: `1.5px dashed ${C.border}`, background: C.bgWhite,
            display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
          }}>
            <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </span>
        )}
      </button>
      {participantsOpen && (
        <GroupParticipantsPopover
          session={session}
          participants={participants}
          onUpdated={s => { onSessionUpdated?.(s); }}
          onClose={() => setParticipantsOpen(false)}
        />
      )}
    </div>
  ) : null;

  // Меню маркировки тегами (портал — рендерится в обоих return ниже)
  const tagMenuEl = tagMenu ? (
    <TagAssignMenu
      anchor={tagMenu}
      registry={tagRegistry}
      selected={session.tags ?? []}
      onToggle={toggleTag}
      onCreate={createTag}
      onClose={() => setTagMenu(null)}
    />
  ) : null;

  // На десктопе заголовок держит минимум ~20 символов (не ужимается в «З…»);
  // при нехватке места правый кластер бейджей уходит второй строкой (flexWrap ряда)
  const titleMinW = isCompact ? 0 : 180;

  // === Единая формула шапки: одна разметка на все типы чата и оба размера ===
  // Заголовок — собеседник, когда он есть: персона «Роль · Имя», команда — имена
  // участников. У чата без собеседника заголовок занимает название чата. Вторая
  // строка (мета) везде одного состава и порядка: название чата → происхождение →
  // активный спикер (команда) → зона → агент → усилие. Раньше здесь жили шесть
  // разных разметок с тремя разными правилами «что главное», и название чата в
  // паре с персоной не показывалось вовсе — только тултипом.
  const isGroup = !!participants && participants.length > 1;
  const chatName = session.name?.trim() || null;
  const personaLines = persona ? personaTitleLines(persona) : null;
  // Состав команды в заголовке: имена участников, при переполнении — «и ещё N»
  // (кто именно — читается по стеку аватаров слева и через поповер состава)
  const groupNames = isGroup ? participants!.map(p => p.name) : null;
  const groupTitle = groupNames
    ? (groupNames.length > 3
        ? `${groupNames.slice(0, 3).join(', ')} и ещё ${groupNames.length - 3}`
        : groupNames.join(', '))
    : null;
  const titleText = groupTitle ?? personaLines?.primary ?? chatName ?? 'Новый чат';
  // Имя персоны — приглушённый хвост заголовка: роль ведёт, имя уточняет
  const titleSuffix = groupTitle ? null : personaLines?.secondary ?? null;
  // В мете название чата нужно, только когда заголовок занят собеседником
  const metaChatName = (groupTitle || personaLines) ? chatName : null;
  // Пилюля зоны — только когда зона персоны ОТЛИЧАЕТСЯ от контекста чата
  // (глобальная персона в проектном чате и т.п.): совпадающая зона — шум,
  // проект и так виден в сайдбаре воркспейса
  const zoneDiffers = personaIsProject ? !project : personaIsSphere ? false : !!project;
  const speakerName = personaLines ? personaLines.secondary ?? personaLines.primary : null;

  // Мета-строка шапки: слоты опциональны и схлопываются. Ужимается первым название
  // чата — бейджи и пилюли короткие и места не уступают.
  const metaRow = (hero: boolean) => {
    const fs = hero ? 12 : 11.5;
    const slots: ReactNode[] = [];
    if (metaChatName) slots.push(
      // Тема идёт со своим именем: у чата с собеседником имя живёт здесь, в мете
      <span key="name" style={{ display: 'flex', alignItems: 'center', gap: 4, minWidth: 0 }}>
        <ChatTopicIcon topic={session.topic} size={14} />
        <span style={{ minWidth: 0, fontSize: fs, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {metaChatName}
        </span>
      </span>
    );
    // Локальный проект (ADR-016): где живут файлы и в сети ли устройство — иначе
    // закрытый гейт хода и пропавшие панели выглядят поломкой
    if (deviceBadge) slots.push(
      <span key="device" data-project-device-badge style={{ display: 'inline-flex', minWidth: 0, maxWidth: 240 }}>
        <Badge size="xs" tone={deviceBadge.offline ? 'warning' : 'neutral'} title={deviceBadge.title}
          icon={<HardDrive size={11} strokeWidth={ICON_STROKE} aria-hidden />}>
          {isCompact ? deviceBadge.short : deviceBadge.text}
        </Badge>
      </span>
    );
    // Происхождение живёт здесь в ОБОИХ размерах и на обеих платформах: в правом
    // ряду длинный заголовок задачи выдавливал чипы и резался на 220px
    if (origin) slots.push(
      <ChatOriginBadge key="origin" origin={origin} compact iconOnly={isCompact} style={{ flexShrink: 0, maxWidth: 260 }} />
    );
    if (groupTitle) slots.push(
      <span key="speaker" style={{ flexShrink: 0, fontSize: fs, color: C.textMuted, whiteSpace: 'nowrap' }}>
        отвечает {speakerName ?? '—'}
      </span>
    );
    if (persona && personaAccent && zoneDiffers) slots.push(
      <span key="zone" style={{
        flexShrink: 0, fontSize: 10, fontWeight: 600, letterSpacing: '0.02em',
        padding: '1px 7px', borderRadius: R.pill,
        background: `${personaAccent}${personaIsProject || personaIsSphere ? '2E' : '17'}`, color: personaAccent,
      }}>
        {personaZoneText}
      </span>
    );
    // .md-агент чата — лёгкая пометка: цветная точка + имя (не персона-блок)
    if (agent && !persona && !isGroup) slots.push(
      <span key="agent" style={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: fs, fontWeight: 600, color: C.textSecondary, whiteSpace: 'nowrap' }}>
        <span style={{ width: 7, height: 7, borderRadius: '50%', background: agentDotColor(agent.color), display: 'inline-block', flexShrink: 0 }} />
        {agent.name}
      </span>
    );
    // Только усилие, и только выбранное ЯВНО: метка «По умолчанию» ничего не сообщает.
    // Модель ушла к постам — в шапке она врала после смены модели по ходу разговора
    if (session.effort && !isCompact) slots.push(
      <span key="effort" style={{ flexShrink: 0, fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>
        {effortLabel(session.effort)}
      </span>
    );
    if (!slots.length) return null;
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, marginTop: hero ? 3 : 1 }}>
        {slots}
      </div>
    );
  };

  // Слот идентичности слева: стек участников (команда), аватар персоны, иначе пусто.
  // В hero у персоны — фото скруглённым квадратом с чётким краем (вариант A).
  const identity = (hero: boolean) => {
    if (isGroup) return participantsStack(hero ? 34 : (isCompact ? 24 : 26));
    if (!persona) return null;
    return hero ? (
      <PersonaFace
        persona={persona} align="center" fontSize={24}
        style={{
          width: 52, height: 52, flexShrink: 0,
          borderRadius: R.xl, border: `1px solid ${C.borderLight}`, boxSizing: 'border-box',
        }}
      />
    ) : <PersonaAvatar persona={persona} size={28} />;
  };

  // Заголовок целиком. hero — крупная шапка-остров на холсте, иначе тулбарная строка.
  // У чата с персоной весь блок — ссылка на её карточку (personaCardLink): клик, Enter/Space
  // и подчёркивание заголовка по наведению.
  const titleContent = (hero: boolean) => (
    <div
      {...(persona ? (personaCardLink ?? {}) : {})}
      title={persona && personaCardLink
        ? `${chatName ? `${chatName} · ` : ''}Открыть карточку персоны`
        : (chatName ?? undefined)}
      style={{
        minWidth: hero ? 240 : titleMinW, flex: 1, display: 'flex', alignItems: 'center', gap: hero ? 12 : 9,
        cursor: persona && personaCardLink ? 'pointer' : undefined,
      }}
    >
      {identity(hero)}
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: hero ? 8 : 6, minWidth: 0 }}>
          {/* Значок темы — только когда титул занят именем чата: у персоны и группы
              там собеседник, и тема уезжает в мета-строку вместе со своим именем.
              Стоит ВНЕ текстового блока, иначе flex снял бы с него обрезку многоточием */}
          {!metaChatName && <ChatTopicIcon topic={session.topic} size={hero ? 20 : 15} />}
          <div style={{
            fontFamily: FONT.serif, fontSize: hero ? 28 : 16, fontWeight: hero ? 500 : 600,
            color: personaAccent ?? C.textHeading, letterSpacing: '-0.01em', lineHeight: hero ? 1.25 : 1.3,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
            textDecoration: persona && personaHover ? 'underline' : undefined,
          }}>
            {titleText}
            {titleSuffix && (
              <span style={{ color: C.textMuted, fontSize: hero ? 21 : 13.5 }}> · {titleSuffix}</span>
            )}
          </div>
        </div>
        {metaRow(hero)}
      </div>
    </div>
  );
  const titleBlock = titleContent(false);
  // Элементы шапки — выносим, чтобы отрендерить в двух раскладках (с центр. переключателем и без)
  const openBtn = onOpenSidebar && !isCompact ? (
    <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isCompact}>
      <MenuIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </ToolbarIconButton>
  ) : null;
  const titleEl = isMobile && onBack
    ? <BackButton onClick={onBack} style={{ flex: 1 }} title="Назад к списку">{titleBlock}</BackButton>
    : titleBlock;
  // Бейдж последней запущенной механики команды (только на десктопе)
  // Видимость пилюль (индикаторов) — тем же глазиком, что и кнопки действий, но
  // только в ОБЫЧНОЙ шапке: на стене пилюль нет вовсе. По умолчанию показаны все —
  // они и есть сводка состояния чата. На мобиле все пилюли склеены в одну
  // кольцевую (RingPillBadge), и ключ 'mobile-pills' гасит/возвращает её целиком:
  // прятать по частям внутри чипа нечего, а совсем без глазика мобильную шапку
  // распирающим чипом не освободить
  const badgeVisible = (key: ChatBadgeKey) => !compact && headerVis.isVisible(key);
  const mobilePillsVisible = !compact && headerVis.isVisible('mobile-pills');
  const mechanicBadge = lastMechanic && !isCompact ? <TeamMechanicBadge id={lastMechanic} size="sm" /> : null;

  // Время жизни чата: у временного — остаток до авто-удаления, у бессрочного —
  // приглушённая иконка. Клик открывает выбор срока прямо здесь (в офлайне менять
  // нечего — сохранение всё равно не пройдёт)
  const expiryBadge = online ? (
    <ExpiryButton session={session} isMobile={isCompact} onSessionUpdated={onSessionUpdated} />
  ) : null;
  // Opt-out «не сохранять решения из этого чата» — только у проектных чатов, рядом со
  // «Временем жизни» (тем же паттерном кнопки в шапке)
  const dossierBtn = project && online ? (
    <DossierOptOutButton session={session} isMobile={isCompact} onSessionUpdated={onSessionUpdated} />
  ) : null;
  // На узких раскладках (мобил/планшет) прогресс workflow втянут в объединённый чип
  // (costBadges) — отдельный бейдж рисуем только на десктопе, где ряду хватает места.
  const workflowBadge = activeWorkflow && !isCompact ? (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, flexShrink: 0,
    }}>
      <div className="tool-spinner" style={{ width: 10, height: 10, flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 700, color: C.accent, letterSpacing: 0.3, whiteSpace: 'nowrap' }}>WF</span>
      <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, whiteSpace: 'nowrap' }}>
        {activeWorkflow.phasesTotal > 0 ? `${activeWorkflow.phasesDone}/${activeWorkflow.phasesTotal} этапов` : 'Workflow'}
      </span>
    </div>
  ) : null;
  // Кольцевая пилюля (контекст + лимиты/квота) — одна на все раскладки. Превью в «⋯»
  // рисует её же и в том же состоянии (идущее сжатие видно и там), но кнопка «Сжать» в
  // его поповере ничего не делает, а ключ сброса поповера свой
  const ringPill = (preview?: boolean) => (
    <RingPillBadge
      estimate={ctxEstimate} isWaiting={isWaiting} isCompacting={isCompacting}
      canCompact={canCompact} compactNote={compactNote} onCompact={preview ? () => {} : onCompact}
      online={online} assistantName={asstName}
      isCliProvider={isCliProvider} providerName={asstName} cost={cost} falCost={falCost} glifCost={glifCost}
      balance={provBalance} billing={billing} onBillingChange={onBillingChange} windows={limitWindows}
      activeWorkflow={activeWorkflow} isMobile={isMobile} isCompact={isCompact}
      resetKey={preview ? `menu-${session.id}` : session.id}
    />
  );
  // Бейдж расхода токенов чата (аналитика v2): обновляется по завершению хода —
  // триггер cost.results растёт вместе с result-сообщениями ленты
  const spendBadge = spendBadgeSlot?.render?.({ sessionId: session.id, chatName: session.name, resultCount: cost.results, isMobile: isCompact });
  // compact (колонка стены): плашек контекста, стоимости и расхода нет — в узкой
  // шапке они занимают всю ширину и переносят строку, а следить за деньгами и
  // контекстом уместнее в полном виде чата (открывается кнопкой из ярлыка колонки)
  const costBadges = compact ? null : isCompact ? (
    // Мобил/планшет: та же кольцевая пилюля, в неё же втянуты workflow и fal/glif — не
    // распирает шапку. Скрывается целиком глазиком «Пилюли в шапке» в «⋯» (ключ mobile-pills)
    <>
      {mobilePillsVisible && ringPill()}
      {badgeVisible('spend') && spendBadge}
    </>
  ) : (
    // Десктопная шапка: кольцевая пилюля (ключ cost — контекст в ней же) и отдельные
    // fal/glif/расход, каждую можно убрать глазиком
    <>
      {badgeVisible('cost') && ringPill()}
      {badgeVisible('fal') && <FalCostBadge stats={falCost} isCompact={isCompact} resetKey={session.id} />}
      {badgeVisible('glif') && <GlifCostBadge stats={glifCost} isCompact={isCompact} resetKey={session.id} />}
      {badgeVisible('spend') && spendBadge}
    </>
  );
  // Тумблер уведомлений ЭТОГО чата — сигнал о завершённом ходе, когда вкладка не в фокусе.
  // compact (колонка стены): не показываем — в тесной колонке хватает срока жизни,
  // а заглушить чат можно из меню его карточки в списке. Общий рубильник уведомлений
  // живёт в разделе «Уведомления»
  const notifyBtn = online
    ? <NotifyButton session={session} isMobile={isCompact} onSessionUpdated={onSessionUpdated} />
    : null;
  // На узких раскладках артефакты и настройки — плотная пара справа (gap 0 вместо
  // TB.gap), читаются как единая группа действий чата; на десктопе — как раньше, врозь.
  const summaryBtn = summaryAction?.render?.({ session, hasMessages, online });
  const extractBtn = <ExtractTasksButton session={session} hasMessages={hasMessages} online={online} />;
  const retitleBtn = <RetitleButton session={session} hasMessages={hasMessages} online={online} />;

  // === Видимость ряда действий ===
  // «⋯» стоит в ряду ВСЕГДА, а тумблеры внутри решают, что показывать рядом с ним.
  // По умолчанию скрытых нет — ряд выглядит как раньше, плюс постоянная кнопка меню
  const notifyOn = useChatNotifyOn(session);
  // Набор действий — общий каталог чата (тот же, что у карточки в списке).
  // Доступность решает контекст: закрепление живёт только у чатов вне проекта
  // (у проектных сессий его нет в API), досье — только у проектных, стена и
  // удаление — только там, где владелец экрана дал колбэк
  // Гейта по compact здесь больше нет: в узкой колонке «Стены» действия раньше
  // просто отключались, потому что ряд не вмещал их все. Теперь состав ряда
  // выбирает пользователь глазиком (по умолчанию наружу выведен только срок),
  // а «⋯» на месте всегда — значит прятать сами действия незачем
  const headerActionAvailable: Record<ChatActionKey, boolean> = {
    rename: online,
    pin: online && !session.projectId,
    tags: canTag,
    wall: !!onAddToWall,
    notify: online && isNotifySupported(),
    dossier: !!project && online,
    expiry: online,
    // Архив доступен и в узкой колонке «Стены»: сетевой клиент
    // PUT /api/chats/{id}/archived есть в любом онлайн-чате, а ряд от него не распухнет —
    // на стене действие по умолчанию лежит в «⋯» (WALL_ACTIONS_HIDDEN_BY_DEFAULT)
    archive: online,
    delete: !!onChatDeleted && online && !compact,
  };
  const headerActions = CHAT_ACTION_ORDER.filter(k => headerActionAvailable[k]);
  // Порядок ряда — канонический: набор скрытых фильтрует ряд, сохраняя привычную
  // расстановку оставшихся действий относительно друг друга
  const visibleActions = headerActions.filter(k => headerVis.isVisible(k));

  // Исполнение действий шапки. Часть уже живёт готовыми кнопками (у них свои
  // поповеры и состояния) — их узлы в rowNode; остальные исполняются здесь
  const [renameDialog, setRenameDialog] = useState<string | null>(null);
  const [deleteAsk, setDeleteAsk] = useState(false);
  // Сохранение имени — одна точка на кнопку «Сохранить» и на Enter в поле
  const saveRename = () => {
    const next = (renameDialog ?? '').trim();
    setRenameDialog(null);
    if (!next || next === (session.name ?? '')) return;
    void updateChatFields(session, { name: next })
      .then(s => onSessionUpdated?.(s))
      .catch(() => showToast('Чат', 'Не удалось переименовать чат', 'info'));
  };
  const runAction = (key: ChatActionKey, anchor?: DOMRect) => {
    switch (key) {
      case 'rename': setRenameDialog(session.name ?? ''); break;
      case 'pin':
        void api.chats.update(session.id, { pinned: !session.isPinned })
          .then(s => onSessionUpdated?.(s))
          .catch(() => showToast('Чат', 'Не удалось изменить закрепление', 'info'));
        break;
      case 'tags': if (anchor) setTagMenu(anchor); break;
      case 'wall': onAddToWall?.(); break;
      case 'notify':
        void updateChatFields(session, { notificationsMuted: notifyOn })
          .then(s => onSessionUpdated?.(s))
          .catch(() => showToast('Уведомления', 'Не удалось изменить уведомления чата', 'info'));
        break;
      case 'dossier':
        void updateChatFields(session, { excludeFromDossiers: !session.excludeFromDossiers })
          .then(s => onSessionUpdated?.(s))
          .catch(() => showToast('История решений', 'Не удалось изменить настройку чата', 'info'));
        break;
      case 'expiry': if (anchor) setExpiryMenu(anchor); break;
      case 'archive':
        // Архивация из шапки: владелец экрана реагирует на onSessionUpdated сам — центр
        // воркспейса/«Чатов» уходит на соседа по списку, колонка стены убирается.
        // Здесь только запрос и тост.
        // Направление и итог читаем через isArchivedChat, а НЕ через archivedAt: признак
        // архива производный (IsArchived = ArchivedAt != null && UpdatedAt <= ArchivedAt),
        // и у чата с активностью после архивации archivedAt непустой, а чат — живой.
        void updateChatFields(session, { archived: !isArchivedChat(session) })
          .then(s => {
            onSessionUpdated?.(s);
            showToast('Архив', isArchivedChat(s) ? 'Чат убран в архив' : 'Чат вернулся в список', 'info');
            // Вернули из архива: список своей области выходит из архивного вида. Чат
            // и так открыт — переключать нечего, но оставлять список показывать архив,
            // где этого чата уже нет, значит прятать его от человека второй раз
            if (isArchivedChat(session) && !isArchivedChat(s)) leaveChatArchiveView(chatFilterScope(s));
          })
          .catch(() => showToast('Архив', 'Не удалось изменить архив чата', 'info'));
        break;
      case 'delete': setDeleteAsk(true); break;
    }
  };
  // Подпись и иконка действия — с текущим состоянием (мьют, срок, закрепление):
  // одна точка на ряд, «⋯» и меню правого клика
  const actionMeta = (key: ChatActionKey): { icon: ReactNode; label: string; active?: boolean; danger?: boolean } => {
    switch (key) {
      case 'rename': return { icon: <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Переименовать' };
      // Состояние тумблеров показываем САМОЙ иконкой (Pin с заливкой, Bell/BellOff),
      // а не акцентной плашкой: у закреплённого временного чата с уведомлениями
      // подряд горели четыре оранжевых кнопки — рядом с «WF» и «Отправить» это
      // спорит за внимание с главным действием экрана (accent-дисциплина гайда).
      // Акцент оставлен одному индикатору — сроку хранения
      case 'pin': return {
        icon: <Pin size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} fill={session.isPinned ? 'currentColor' : 'none'} />,
        label: session.isPinned ? 'Открепить' : 'Закрепить',
      };
      case 'tags': return { icon: <Tags size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Теги чата', active: !!tagMenu };
      case 'wall': return { icon: <Columns3 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'На стену' };
      case 'notify': return {
        icon: notifyOn ? <Bell size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : <BellOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: notifyOn ? 'Уведомления: включены' : 'Уведомления: выключены',
      };
      // Досье НЕ подсвечиваем: акцент в системе читается как «включено», а горело
      // бы отрицательное состояние («решения не сохраняются»)
      case 'dossier': return {
        icon: <History size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: session.excludeFromDossiers ? 'Досье: не сохраняются' : 'Досье: сохраняются',
      };
      case 'expiry': return {
        icon: <Hourglass size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: session.expiresAfterMinutes != null ? `Хранить: ${formatTimeLeft(session) ?? 'по сроку'}` : 'Срок хранения',
        active: session.expiresAfterMinutes != null,
      };
      // Направление архива читаем через isArchivedChat (НЕ archivedAt) — наш
      // производный bool с бэка, иконка и подпись переключаются по нему. 409
      // «в чате идёт ход» приходит текстом сервера в e.message и уходит в тост
      // без нашей обёртки (как в ChatsPage.handleArchive и SessionList)
      case 'archive': return {
        icon: isArchivedChat(session)
          ? <ArchiveRestore size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          : <Archive size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: isArchivedChat(session) ? 'Вернуть из архива' : 'В архив',
      };
      case 'delete': return { icon: <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Удалить чат', danger: true };
    }
  };
  // Узел кнопки ряда: у досье и срока свои готовые компоненты (у них внутри
  // поповеры и собственная разметка), остальные — обычная icon-кнопка тулбара
  const rowNode = (key: ChatActionKey): ReactNode => {
    if (key === 'dossier') return dossierBtn;
    if (key === 'expiry') return expiryBadge;
    if (key === 'notify') return notifyBtn;
    const m = actionMeta(key);
    return (
      <ToolbarIconButton
        onClick={e => runAction(key, (e.currentTarget as HTMLElement).getBoundingClientRect())}
        title={m.label}
        isMobile={isCompact}
        active={m.active}
        color={m.danger ? C.danger : undefined}
        className={m.active ? 'cc-ghost-live' : undefined}
      >
        {m.icon}
      </ToolbarIconButton>
    );
  };
  // Якорь для поповеров, открываемых из «⋯» (теги/срок): rect триггера «⋯»,
  // обновляется при каждом рендере — в onClick он ещё живой
  const overflowAnchorRef = useRef<DOMRect | null>(null);
  // Меню «⋯» — постоянная кнопка ряда (не появляется и не исчезает по обстоятельствам).
  // Внутри — ВСЕ действия чата: клик по строке выполняет действие, глазик справа
  // показывает, стоит ли эта кнопка в самом ряду, и переключает её видимость
  // Строки пилюль в том же меню: у них нет «действия», клик по строке и есть
  // переключение видимости (keepOpen — меню не закрывается). Только в обычной
  // шапке: на стене пилюль нет, на мобиле они склеены в один чип
  // В меню попадают только те пилюли, которым в ЭТОМ чате есть что показать:
  // сами они рисуются условно (механика — если команда работала, workflow — пока
  // идёт прогон, fal/glif — если были генерации), и полный каталог в списке врал
  // бы про состав шапки. Скрытая глазиком пилюля из списка не исчезает — её
  // доступность считается по данным, а не по видимости
  const badgeAvailable: Record<ChatBadgeKey, boolean> = {
    mechanic: !!lastMechanic,
    workflow: !!activeWorkflow,
    // Контекст живёт в кольцевой пилюле вместе с лимитами — её ключ cost
    cost: hasContextInfo(ctxEstimate)
      || (isCliProvider ? hasProviderCostInfo(cost, provBalance) : hasClaudeCostInfo(cost, limitWindows)),
    fal: falCost.total > 0,
    glif: glifCost.count > 0,
    // У расхода собственный источник (SpendBadge грузит его сам), снаружи виден
    // только факт, что ходы в чате были
    spend: cost.results > 0,
    // Мобильный чип не раскладывается на части: его «доступность» решает не
    // данные, а сам факт мобильной шапки (см. availableBadges)
    'mobile-pills': true,
  };
  // Превью строки — САМА пилюля в том виде, в каком она стоит в шапке: узнать её
  // по картинке быстрее, чем по названию. Внутри меню превью неинтерактивно
  // (ItemRow гасит указатель), свои поповеры пилюли открывают только из шапки
  const badgePreview = (k: ChatBadgeKey): ReactNode => {
    switch (k) {
      case 'mechanic': return mechanicBadge;
      case 'workflow': return workflowBadge;
      case 'cost': return ringPill(true);
      case 'fal': return <FalCostBadge stats={falCost} isCompact={isCompact} resetKey={session.id} />;
      case 'glif': return <GlifCostBadge stats={glifCost} isCompact={isCompact} resetKey={session.id} />;
      case 'spend': return spendBadge;
    }
  };
  const availableBadges = compact ? [] : isCompact
    // Мобил/планшет: в шапке ДВЕ пилюли — кольцевая (контекст+лимиты+медиа)
    // и отдельный «Расход токенов». Кольцевой — своя строка с превью (по названию не
    // понять, что внутри составной пилюли), расходу — обычная строка, как на
    // десктопе. Строка чипа всегда: он условен по данным, но возможность его
    // спрятать не должна зависеть от того, показался ли он в этом чате; строка
    // расхода — только когда в чате были ходы (пилюли без данных в меню не бывает)
    ? ['mobile-pills', ...(badgeAvailable.spend ? ['spend' as const] : [])] as ChatBadgeKey[]
    : CHAT_BADGE_ORDER.filter(k => badgeAvailable[k]);
  const badgeItems: OverflowItem[] = availableBadges.map((k, i) => {
    const visible = headerVis.isVisible(k);
    return {
      key: `badge-${k}`,
      // Подпись остаётся именем пилюли — она читается скринридером и служит
      // запасным вариантом, если превью почему-то пустое
      label: CHAT_BADGE_LABELS[k],
      preview: k === 'mobile-pills'
        ? ringPill(true)
        : visible ? undefined : badgePreview(k),
      // Линия перед первой пилюлей отбивает их от действий: выше — что чат умеет,
      // ниже — что показывать в шапке
      separator: i === 0,
      keepOpen: true,
      onClick: () => headerVis.toggle(k),
      action: {
        icon: visible ? <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        title: visible ? 'Скрыть пилюлю' : 'Показать пилюлю',
        onClick: () => headerVis.toggle(k),
      },
    };
  });
  const headerOverflow = headerActions.length > 0 ? (
    <ToolbarOverflowMenu title="Ещё" isMobile={isCompact} items={[
      ...headerActions.map(k => {
        const m = actionMeta(k);
        const visible = headerVis.isVisible(k);
        return {
          key: k,
          icon: m.icon,
          label: m.label,
          danger: m.danger,
          // Теги и срок открывают свои поповеры по якорю «⋯» — сама кнопка исчезнет
          // вместе с меню, и её rect брать было бы неоткуда
          onClick: () => runAction(k, overflowAnchorRef.current ?? undefined),
          action: {
            icon: visible ? <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
            title: visible ? 'Убрать в меню' : 'Показывать кнопкой в ряду',
            onClick: () => headerVis.toggle(k),
          },
        };
      }),
      // Запасной вход в AI-палитру на телефоне: ⌘K там нет, а круглешок прячется, когда
      // композер растянут во весь экран и места ему не осталось (placeFab, hidden)
      ...(isMobile ? [{
        key: 'ai-hub', icon: <Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'AI-действия',
        onClick: () => window.dispatchEvent(new Event(OPEN_AI_EVENT)),
      }] : []),
      ...badgeItems,
    ] as OverflowItem[]}
      // Триггер-обёртка фиксирует свой rect в ref: теги/срок из меню откроются
      // по нему (кнопка «⋯» скроется вместе с меню, rect из события был бы пуст)
      renderTrigger={({ toggle, ref }) => (
        <span ref={el => {
          ref(el);
          if (el) overflowAnchorRef.current = el.getBoundingClientRect();
        }}>
          <ToolbarIconButton onClick={toggle} title="Ещё действия">
            <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        </span>
      )} />
  ) : null;
  // Ghost-заглушение ряда действий — только на десктопе (класс сам гасится вне
  // hover-media): компактные раскладки получают полную непрозрачность
  const actionBtns = isCompact
    ? <div style={{ display: 'flex', alignItems: 'center', gap: 0, flexShrink: 0 }}>{retitleBtn}{extractBtn}{summaryBtn}{visibleActions.map(k => <span key={k} style={{ display: 'flex', flexShrink: 0 }}>{rowNode(k)}</span>)}{headerOverflow}</div>
    // На десктопе кнопки — неразрывная группа: при переносе кластера уходят вниз целиком,
    // оставаясь последними у правого края (мышечная память на позицию). Ghost-класс
    // приглушает ряд в покое; «⋯» стоит ВНЕ него — это единственный путь к скрытым
    // действиям, и гасить его нельзя
    : (
      <div style={{ display: 'flex', alignItems: 'center', gap: TB.gap, flexShrink: 0 }}>
        <div className="cc-ghost-actions" style={{ display: 'flex', alignItems: 'center', gap: TB.gap }}>
          {retitleBtn}{extractBtn}{summaryBtn}
          {visibleActions.map(k => <span key={k} style={{ display: 'flex', flexShrink: 0 }}>{rowNode(k)}</span>)}
        </div>
        {headerOverflow}
      </div>
    );

  // Правый кластер шапки (бейджи + кнопки) единым flex-элементом: при тесноте узкого
  // десктопа переносится под заголовок ЦЕЛИКОМ (два чистых состояния вместо рваных
  // промежуточных), прижат вправо; внутри себя тоже умеет переноситься. На узких
  // раскладках (мобил/планшет) — однорядный хвост без переноса.
  const rightCluster = (
    <div style={{
      display: 'flex', alignItems: 'center', gap: TB.gap, marginLeft: 'auto', minWidth: 0,
      ...(isCompact ? null : { flexWrap: 'wrap' as const, justifyContent: 'flex-end' as const }),
    }}>
      {badgeVisible('mechanic') && mechanicBadge}{badgeVisible('workflow') && workflowBadge}{costBadges}{actionBtns}
    </div>
  );

  // === Right-click меню шапки (desktop) ===
  // Якорь — точка курсора; состав повторяет ряд действий + AI-действия из палитры.
  // На компакте/таче не вешаем: там нет правой кнопки, а long-press в шапке не нужен
  // (ряд и так весь на экране). Гейт по online — как у кнопок ряда
  const dossierExcluded = !!session.excludeFromDossiers;
  const chatTemporary = session.expiresAfterMinutes != null;
  // AI-действия из палитры: слушатели уже смонтированы в шапке (cc-ai-run)
  const runAi = (action: string) =>
    window.dispatchEvent(new CustomEvent('cc-ai-run', { detail: { action } }));
  // Глазик-спутник строки: показывает, стоит ли эта кнопка в самом ряду шапки,
  // и переключает её видимость. Меню при этом не закрывается (клик гасит всплытие
  // внутри MenuItem.action) — весь набор выставляется одним заходом
  const visAction = (key: string) => ({
    icon: headerVis.isVisible(key)
      ? <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      : <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
    title: headerVis.isVisible(key) ? 'Убрать в меню' : 'Показывать кнопкой в ряду',
    onClick: () => headerVis.toggle(key),
  });
  const ctxMenuEl = ctxMenu && !isCompact ? (
    <Menu anchor={ctxMenu} onClose={() => setCtxMenu(null)} minWidth={240} maxHeight={340}>
      {canTag && (
        <MenuItem
          icon={<Tags size={15} strokeWidth={2} />}
          label="Теги чата"
          action={visAction('tags')}
          // Меню тегов открывается по тому же якорю: это меню уже закрылось бы,
          // и rect взять неоткуда — фиксируем якорь до закрытия (приём ChatCard)
          onClick={() => { const a = ctxMenu; setCtxMenu(null); setTagMenu(a); }}
        />
      )}
      {isNotifySupported() && (
        <MenuItem
          icon={notifyOn ? <Bell size={15} strokeWidth={2} /> : <BellOff size={15} strokeWidth={2} />}
          label={notifyOn ? 'Уведомления: включены' : 'Уведомления: выключены'}
          action={visAction('notify')}
          onClick={() => {
            setCtxMenu(null);
            void updateChatFields(session, { notificationsMuted: notifyOn })
              .then(s => onSessionUpdated?.(s))
              .catch(() => showToast('Уведомления', 'Не удалось изменить уведомления чата', 'info'));
          }}
        />
      )}
      {project && (
        <MenuItem
          icon={<History size={15} strokeWidth={2} />}
          label={dossierExcluded ? 'Досье: не сохраняются' : 'Досье: сохраняются'}
          action={visAction('dossier')}
          onClick={() => {
            setCtxMenu(null);
            void updateChatFields(session, { excludeFromDossiers: !dossierExcluded })
              .then(s => onSessionUpdated?.(s))
              .catch(() => showToast('История решений', 'Не удалось изменить настройку чата', 'info'));
          }}
        />
      )}
      <MenuItem
        icon={<Hourglass size={15} strokeWidth={2} />}
        label={chatTemporary ? `Хранить: ${formatTimeLeft(session) ?? 'по сроку'}` : 'Срок хранения…'}
        action={visAction('expiry')}
        onClick={() => { const a = ctxMenu; setCtxMenu(null); setExpiryMenu(a); }}
      />
      {online && hasMessages && (
        <>
          <MenuSep />
          <div style={{ padding: '4px 10px', fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.6, color: C.textMuted }}>
            AI
          </div>
          <MenuItem
            icon={<Pencil size={15} strokeWidth={2} />}
            label="Переименовать по переписке"
            onClick={() => { setCtxMenu(null); runAi('chat.retitle'); }}
          />
          <MenuItem
            icon={<ListChecks size={15} strokeWidth={2} />}
            label="Задачи из чата"
            onClick={() => { setCtxMenu(null); runAi('chat.extract'); }}
          />
          {summaryMenuItem?.render?.({ run: () => { setCtxMenu(null); runAi('chat.summary'); } })}
        </>
      )}
    </Menu>
  ) : null;
  // Пикер срока из right-click меню — по сохранённому якорю (тот же паттерн, что
  // ExpiryButton, но якорь приходит из ctx-меню, а не с собственной кнопки)
  const expiryAt = expiresAt(session);
  // Диалоги действий шапки: ручное переименование и подтверждение удаления.
  // Удаление необратимо, поэтому спрашиваем всегда — как в списке чатов
  const actionDialogsEl = (
    <>
      {renameDialog !== null && (
        <Modal
          width={MODAL_W.form}
          title="Переименовать чат"
          onClose={() => setRenameDialog(null)}
          footer={
            <ModalActions
              confirmLabel="Сохранить"
              confirmDisabled={!renameDialog.trim()}
              onConfirm={saveRename}
              onCancel={() => setRenameDialog(null)}
            />
          }
        >
          {/* Enter сохраняет, Esc закрывает — в диалоге с кнопкой «Сохранить»
              клавиша обязана делать то же, что кнопка */}
          <TextField
            autoFocus
            value={renameDialog}
            onChange={setRenameDialog}
            onEnter={saveRename}
            onEscape={() => setRenameDialog(null)}
            title="Название чата"
          />
        </Modal>
      )}
      {deleteAsk && (
        <ConfirmDialog
          title="Удалить чат?"
          subtitle={<>Чат «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{session.name ?? 'Новый чат'}</strong>» будет удалён без возможности восстановления.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          // Промис — чтобы кнопка показывала спиннер, пока идёт запрос: удаление
          // чата с транскриптом не мгновенное, а гасить диалог раньше ответа значит
          // врать про результат
          onConfirm={() => {
            const del = session.projectId
              ? api.sessions.delete(session.projectId, session.id)
              : api.chats.delete(session.id);
            // Уйти из удалённого чата и обновить список — дело владельца экрана
            return del
              .then(() => { setDeleteAsk(false); onChatDeleted?.(session.id); })
              .catch(() => { setDeleteAsk(false); showToast('Чат', 'Не удалось удалить чат', 'info'); });
          }}
          onCancel={() => setDeleteAsk(false)}
        />
      )}
    </>
  );
  const ctxExpiryMenuEl = expiryMenu && !isCompact ? (
    <Menu anchor={expiryMenu} onClose={() => setExpiryMenu(null)} minWidth={300} maxHeight={190}>
      <div style={{ padding: '6px 8px 8px' }}>
        <ExpiryPicker
          value={session.expiresAfterMinutes}
          columns={3}
          onChange={minutes => {
            setExpiryMenu(null);
            if (minutes === (session.expiresAfterMinutes ?? null)) return;
            void updateChatFields(session, { expiresAfterMinutes: minutes })
              .then(s => onSessionUpdated?.(s))
              .catch(() => showToast('Время жизни', 'Не удалось изменить срок жизни чата', 'info'));
          }}
        />
        {expiryAt && (
          <p style={{ margin: '8px 0 0', fontSize: 11.5, color: C.textMuted, lineHeight: 1.4 }}>
            Удалится ~{formatExpiryDate(expiryAt)}, если не будет активности.
          </p>
        )}
      </div>
    </Menu>
  ) : null;

  // Hero-шапка (Islands, десктоп): не тулбар в коробке, а заголовок раздела прямо
  // на холсте — как шапка «Календаря». У персоны слева фото скруглённым квадратом
  // с чётким краем (не в круге); рядом крупная serif-идентификация, справа контролы.
  if (island && !isCompact) {
    // Та же формула, что и в тулбарной шапке — крупным кеглем (minWidth 240:
    // serif-28 при меньшей ширине ломается)
    const heroTitle = titleContent(true);
    return (
      // Полоса снизу — мягкая граница шапки к ленте (как у тулбара, но на холсте).
      // Шапка не растягивается на всю зону: её ширина = колонке ленты чата
      // (CHAT_MAX_W по центру), заголовок стоит над сообщениями.
      // БЕЗ overflow:hidden — поповеры бейджей (контекст, стоимость, участники)
      // выпадают ниже шапки и не должны обрезаться её границей.
      // openBtn обязателен: без него свёрнутый сайдбар не вернуть при открытом чате
      // Подложки и тени нет — шапка стоит прямо на холсте, а границу к ленте держит
      // тонкая линия снизу, как у тулбарной шапки. Стекло мутило дудл-холст, тень
      // поверх него читалась тяжело, поэтому вернулись к простому разделителю.
      // Линия ПРОЗРАЧНАЯ, пока лента в начале: меняется только цвет, место под неё
      // занято всегда — иначе появление черты дёргало бы шапку на пиксель
      <div style={{
        position: 'relative', flexShrink: 0, width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto',
        boxSizing: 'border-box',
        borderBottom: `1px solid ${scrolled ? C.borderLight : 'transparent'}`,
        transition: 'border-color 0.18s ease-out',
      }}>
        {/* flexWrap: при узком окне правый кластер уходит второй строкой — остров подрастает */}
        <div
          onContextMenu={e => {
            e.preventDefault();
            setCtxMenu(new DOMRect(e.clientX, e.clientY, 0, 0));
          }}
          style={{ position: 'relative', display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: TB.gap, padding: '12px 18px 10px' }}>
          {openBtn}
          {heroTitle}
          {rightCluster}
        </div>
        {/* Контекст чата — своей строкой под заголовком, в том же острове: материалы
            стоят над лентой, а не сбоку от неё */}
        {contextBar && <div style={{ padding: '0 18px 10px' }}>{contextBar}</div>}
        {tagMenuEl}
        {ctxMenuEl}
        {ctxExpiryMenuEl}
      {actionDialogsEl}
      </div>
    );
  }

  const toolbarEl = (
    // compact (колонка стены): фон прозрачный — подложку даёт стеклянный остров
    // колонки, плотный тулбар закрывал бы дудл-холст под шапкой. Линия снизу при этом
    // остаётся: она и отделяет шапку от ленты
    <Toolbar isMobile={isCompact} noBorder={island} bg={island || compact ? 'transparent' : undefined}
      // Правый клик по шапке — меню действий у курсора (desktop, см. ctxMenuEl)
      onContextMenu={isCompact ? undefined : e => {
        e.preventDefault();
        setCtxMenu(new DOMRect(e.clientX, e.clientY, 0, 0));
      }}
      style={{
        ...(personaAccent ? { borderLeft: `3px solid ${personaAccent}` } : null),
        // Линия к ленте — только когда лента прокручена, и тоном мягче обычной границы:
        // это разделитель ВНУТРИ одной поверхности, а не край панели. Место под неё
        // занято всегда (прозрачный цвет), поэтому шапка не дёргается по высоте.
        // На острове (island) границу даёт сама карточка — там своей линии не рисуем
        ...(island ? null : {
          borderBottom: `1px solid ${scrolled ? C.borderLight : 'transparent'}`,
          transition: 'border-color 0.18s ease-out',
        }),
        // Узкий десктоп: фиксированную высоту отпускаем, кластер переносится второй строкой
        ...(isCompact ? null : { flexWrap: 'wrap' as const, height: 'auto', minHeight: TB.heightDesktop, padding: `6px ${TB.padX}px` }),
      }}>
      {openBtn}{titleEl}{rightCluster}
      {tagMenuEl}
      {ctxMenuEl}
      {ctxExpiryMenuEl}
      {actionDialogsEl}
    </Toolbar>
  );
  if (!contextBar) return toolbarEl;
  // Контекст чата — строкой под тулбаром, до ленты: фон свой не нужен (шапка уже
  // отделена), линия снизу отбивает материалы от переписки
  return (
    <>
      {toolbarEl}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center',
        padding: `${SP.xs}px ${isCompact ? TB.padXMobile : TB.padX}px`,
        borderBottom: `1px solid ${C.border}`,
      }}>
        {contextBar}
      </div>
    </>
  );
}
