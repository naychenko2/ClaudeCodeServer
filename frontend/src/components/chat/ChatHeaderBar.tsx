import { useState, useEffect, useRef, type ReactNode } from 'react';
import { Plus, Archive, ArchiveRestore, Menu as MenuIcon, Tags, Bell, BellOff, History, Hourglass, ListChecks, NotebookPen, Pencil, Pin, Columns3, Trash2, Eye, EyeOff, MoreHorizontal } from 'lucide-react';
import type { Project, Session, ClaudeBilling, Persona, ProjectTag } from '../../types';
import { api } from '../../lib/api';
import { TagAssignMenu } from '../TagChip';
import { modelLabel, modelProvider, assistantName } from '../../lib/models';
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
import { AGENT_COLORS, agentDotColor } from '../AgentSelector';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, worstWindow } from '../../lib/rateLimit';
import { type ContextEstimate } from '../../lib/context';
import { ContextThresholdsDialog } from '../ContextThresholdsDialog';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FONT, R, SP, SHADOW, TB, CHAT_MAX_W, MODAL_W, GROUP_COLORS } from '../../lib/design';
import { useWindowWidth, MOBILE_MAX, TABLET_WIDE_MIN } from '../../lib/breakpoints';
import { Toolbar, ToolbarIconButton } from '../Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';
import { BackButton, ChatTopicIcon, Modal, ModalActions, ConfirmDialog, TextField, Menu, MenuItem, MenuSep } from '../ui';
import { bumpNotes } from '../../lib/notes';
import { createTask } from '../../lib/tasks';
import { showToast } from '../../lib/toast';
import { beginAiBusy, endAiBusy } from '../../lib/ai/busy';
import { openNoteById } from '../../features/notes/saveToNote';
import type { ExtractedTaskCandidate } from '../../types';
import { ChatOriginBadge } from '../ChatOriginBadge';
import { TeamMechanicBadge } from '../../features/team/TeamMechanicBadge';
import type { TeamMechanicId } from '../../features/team/teamMechanics';
import { resolveChatOrigin } from '../../lib/chatOrigin';
import { SpendBadge } from '../../features/spend/SpendBadge';
import { type GlifGenStats, fmtCredits } from './glifStats';
import { useActionVisibility } from '../../hooks/useActionVisibility';
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
interface ProviderBalance { available: boolean; currency: string; totalBalance: string }

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

// Строка одного окна лимита в выпадашке (метка + бар + % + сброс)
function RateRow({ w }: { w: RateWindow }) {
  const c = RATE_COLORS[w.level];
  const reset = fmtReset(w.resetsAt);
  return (
    <div style={{ padding: '3px 0' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
        <span style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary }}>
          {windowLabel(w.limitType)}{w.isUsingOverage ? ' · перерасход' : ''}
        </span>
        <span style={{ fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: c.text }}>{w.pct}%{w.isUsingOverage ? '+' : ''}</span>
      </div>
      <div style={{ height: 4, borderRadius: 2, background: C.track, overflow: 'hidden', margin: '3px 0' }}>
        <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
      </div>
      {reset && <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted }}>сброс {reset}</div>}
    </div>
  );
}

// Общая оболочка бейджа стоимости: пилюля с подписью + суммой и выпадающая разбивка по клику.
// tone окрашивает пилюлю при приближении к лимиту (warn/danger).
// stacked — двухстрочная пилюля (label скрыт/опущен): содержимое amount в столбик,
// компактнее по ширине (для мобильного объединённого чипа).
// wide — более широкий поповер на узких раскладках (мобил/планшет: для объединённого
// чипа с двумя секциями — шире → меньше переносов → ниже по высоте, помещается на экран).
// isCompact — планшет (использует мобильную механику поповера wide).
function BadgeShell({ label, amount, title, isMobile, isCompact, tone, stacked, wide, pulse, resetKey, children }: {
  label?: string; amount: React.ReactNode; title: string; isMobile?: boolean; isCompact?: boolean;
  tone?: 'warn' | 'danger'; stacked?: boolean; wide?: boolean; pulse?: boolean;
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
  return (
    <div style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        title={title}
        style={{
          display: 'flex',
          flexDirection: stacked ? 'column' : 'row',
          alignItems: stacked ? 'flex-start' : 'center',
          gap: stacked ? 1 : 4, padding: stacked ? '2px 9px' : '3px 9px',
          lineHeight: stacked ? 1.2 : undefined,
          background: toneBg, border: `1px solid ${toneBorder}`, borderRadius: R.lg,
          cursor: 'pointer', fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: C.accent,
        }}
      >
        {label && <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.3 }}>{label}</span>}
        {amount}
      </button>
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
                  minWidth: compact ? 200 : 240,
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
// Вынесено для переиспользования в отдельном CostBadge и в объединённом мобильном чипе.
function ClaudeCostPopoverBody({ stats, billing, onBillingChange, windows }: {
  stats: CostStats; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void; windows: RateWindow[];
}) {
  const sub = billing === 'subscription';
  return (
    <>
      <div style={badgeTitleStyle}>{sub ? 'Claude · ≈ по API-тарифу' : 'Стоимость Claude'}</div>
      {sub && (
        <div style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, marginBottom: 8, lineHeight: 1.45 }}>
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
      {windows.length > 0 && (
        <>
          <div style={badgeSectionStyle}>Лимиты подписки</div>
          {windows.map(w => <RateRow key={w.limitType} w={w} />)}
        </>
      )}
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

// Бейдж стоимости Claude (токены/ходы). Клик раскрывает разбивку (аналог /cost).
// В режиме подписки сумма — это ≈ API-эквивалент (отдельно не списывается), что и поясняется.
// Проп isMobile: на планшете передаём isCompact через isMobile — узкие раскладки
// ведут себя одинаково (мини-размеры, wide-поповер).
function CostBadge({ stats, isMobile, billing, onBillingChange, windows, resetKey }: {
  stats: CostStats; isMobile?: boolean; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void;
  windows: RateWindow[]; resetKey?: string;
}) {
  const worst = worstWindow(windows);
  if (!hasClaudeCostInfo(stats, windows)) return null;
  const sub = billing === 'subscription';
  const tone = worst && worst.level !== 'normal' ? worst.level : undefined;
  const amountNode = (
    <>
      <span>{stats.cost > 0 ? (sub ? '≈ ' : '') + fmtUsd(stats.cost) : '—'}</span>
      {tone && worst && (
        <span style={{ marginLeft: 5, color: RATE_COLORS[worst.level].text, fontWeight: 700 }}>· {worst.pct}%</span>
      )}
    </>
  );
  return (
    <BadgeShell
      label="Claude"
      amount={amountNode}
      isCompact={isMobile}
      tone={tone}
      resetKey={resetKey}
      title={sub
        ? 'Claude ≈ по API-тарифу · по подписке отдельно не списывается'
        : 'Стоимость Claude — нажмите для разбивки'}
    >
      <ClaudeCostPopoverBody stats={stats} billing={billing} onBillingChange={onBillingChange} windows={windows} />
    </BadgeShell>
  );
}

// Бейдж статистики CLI-провайдера (DeepSeek/GLM): стоимость сессии + токены + баланс.
// У таких провайдеров нет лимитов подписки Claude — вместо окон показываем остаток
// средств (если провайдер отдаёт баланс) с подсветкой при низком уровне.
// Провайдер без цен и баланса (GLM) — показываем токены как меру расхода.
// Заменяет CostBadge для сессий сторонних провайдеров.
// Есть ли что показывать в provider-cost бейдже (активность или баланс)
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
function ProviderCostPopoverBody({ providerName, stats, balance }: {
  providerName: string; stats: CostStats; balance: ProviderBalance | null;
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
          <div style={badgeSectionStyle}>{isQuota ? 'Квота подписки' : 'Баланс аккаунта'}</div>
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

function ProviderCostBadge({ providerName, stats, balance, isMobile, resetKey }: {
  providerName: string; stats: CostStats; balance: ProviderBalance | null; isMobile?: boolean; resetKey?: string;
}) {
  // Есть активность (хотя бы один ход) или баланс — иначе в начале сессии прячем
  if (!hasProviderCostInfo(stats, balance)) return null;
  const tone = providerBalanceTone(balance);
  const hasCost = stats.cost > 0;
  const totalTokens = stats.input + stats.output;
  const isQuota = balance?.currency === '%';
  const usedPct = isQuota ? quotaUsedPct(balance) : null;
  // Сумма в пилюле: деньги, если считаем стоимость; иначе токены; иначе прочерк
  const amountNode = (
    <>
      <span>{hasCost ? fmtUsd(stats.cost) : totalTokens > 0 ? `${fmtTokens(totalTokens)} ток.` : '—'}</span>
      {tone && balance && (
        <span style={{ marginLeft: 5, color: RATE_COLORS[tone].text, fontWeight: 700 }}>
          · {isQuota ? (usedPct !== null ? `${usedPct}%` : '—') : <>{balance.totalBalance} {balance.currency}</>}
        </span>
      )}
    </>
  );
  return (
    <BadgeShell
      label={providerName}
      amount={amountNode}
      isCompact={isMobile}
      tone={tone}
      resetKey={resetKey}
      title={`Статистика сессии ${providerName} — нажмите для разбивки`}
    >
      <ProviderCostPopoverBody providerName={providerName} stats={stats} balance={balance} />
    </BadgeShell>
  );
}

// Показывать ли контекст-пилюлю: в начале сессии (нет оценки и не свёрнут) — нет
function hasContextInfo(estimate: ContextEstimate): boolean {
  return estimate.pct !== undefined || estimate.fresh;
}

// Компактная сводка контекста для пилюли (мини-бар + процент). Используется как в
// отдельном ContextBadge, так и в объединённом мобильном чипе.
function ContextAmount({ estimate, isCompacting, isMobile }: {
  estimate: ContextEstimate; isCompacting: boolean; isMobile?: boolean;
}) {
  const c = RATE_COLORS[estimate.level];
  const tone = estimate.level !== 'normal' ? estimate.level : undefined;
  const hasPct = estimate.pct !== undefined;
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
      {isCompacting ? (
        <div className="tool-spinner" style={{ width: 10, height: 10 }} />
      ) : hasPct ? (
        <span style={{ width: isMobile ? 18 : 26, height: 5, borderRadius: 3, background: C.track, overflow: 'hidden', display: 'inline-block' }}>
          <span style={{ display: 'block', width: `${estimate.pct}%`, height: '100%', background: c.fill }} />
        </span>
      ) : null}
      <span style={{ color: tone ? c.text : undefined }}>
        {isCompacting ? '…' : hasPct ? `${estimate.pct}%` : estimate.fresh ? '✦' : '—'}
      </span>
    </span>
  );
}

// Тело поповера контекста (детали заполнения + «Сжать контекст» + «Настроить пороги»).
// Вынесено, чтобы переиспользовать в отдельном ContextBadge и в объединённом чипе.
function ContextPopoverBody({ estimate, isWaiting, isCompacting, canCompact, compactNote, onCompact, online, assistantName = 'Ассистент' }: {
  estimate: ContextEstimate; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean;
  assistantName?: string;
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
      <div style={badgeTitleStyle}>Контекст сессии</div>
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
            : `Оценка появится после первого ответа ${assistantName}.`}
        </div>
      )}
      {estimate.lastCompact?.post !== undefined && (
        // Итог последнего сжатия — про объём ИСТОРИИ, а не про окно (системный промпт и
        // инструменты в это число не входят), поэтому отдельной строкой рядом с пояснением ниже
        <BadgeRow k="Сжатие истории" v={estimate.lastCompact.pre !== undefined
          ? `${fmtTokens(estimate.lastCompact.pre)} → ${fmtTokens(estimate.lastCompact.post)}`
          : fmtTokens(estimate.lastCompact.post)} />
      )}
      <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, marginTop: 6, lineHeight: 1.4 }}>
        Сжимает историю диалога в саммари, освобождая место в окне. При заполнении {assistantName} делает это автоматически.
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

// Индикатор заполнения контекстного окна: пилюля с мини-баром и процентом.
// Клик — попап с деталями и кнопкой «Свернуть контекст» (/compact); пороги
// подсветки настраиваются per-user (модалка «Настроить пороги…»).
function ContextBadge(props: {
  estimate: ContextEstimate; isMobile?: boolean; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean;
  assistantName?: string; resetKey?: string;
}) {
  // Внутренний бейдж: проп называется isMobile по историческим причинам, но на
  // планшете снаружи передаётся isCompact. Узкие раскладки ведут себя одинаково
  // (мини-размеры, wide-поповер), так что переименовывать проп тут не нужно.
  const { estimate, isMobile, isCompacting } = props;
  const tone = estimate.level !== 'normal' ? estimate.level : undefined;

  // В начале сессии показывать нечего (нет оценки и контекст не свёрнут) — прячем пилюлю
  if (!hasContextInfo(estimate)) return null;

  return (
    <BadgeShell
      label={isMobile ? 'Ctx' : 'Контекст'}
      amount={<ContextAmount estimate={estimate} isCompacting={isCompacting} isMobile={isMobile} />}
      isCompact={isMobile}
      tone={tone}
      resetKey={props.resetKey}
      title="Заполнение контекста сессии — нажмите для деталей"
    >
      <ContextPopoverBody {...props} />
    </BadgeShell>
  );
}

// Тело поповера трат fal.ai: остаток баланса (асинхронно) + траты чата + ссылка на статистику.
// Вынесено для переиспользования в отдельном FalCostBadge и в объединённом мобильном чипе.
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
// Вынесено для переиспользования в отдельном GlifCostBadge и в объединённом мобильном чипе.
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

// Мобильный объединённый бейдж: контекст + стоимость/расход одной пилюлей и одним
// поповером с двумя секциями. Экономит ширину узкого тулбара (вместо двух чипов — один).
// Провайдер: Claude → стоимость + лимиты подписки + fal; CLI (DeepSeek/GLM) → стоимость/токены + баланс.
function MobileCombinedBadge(props: {
  // контекст
  estimate: ContextEstimate; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean; assistantName: string;
  // стоимость
  isCliProvider: boolean; providerName: string; cost: CostStats; falCost: FalCostStats; glifCost: GlifGenStats;
  balance: ProviderBalance | null; billing: ClaudeBilling; onBillingChange?: (b: ClaudeBilling) => void;
  windows: RateWindow[];
  // workflow (мобилка): прогресс фаз втягивается в этот же чип вместо отдельного бейджа
  activeWorkflow?: { phasesDone: number; phasesTotal: number };
  // Сброс поповера при смене чата
  resetKey?: string;
}) {
  const {
    estimate, isCompacting, isCliProvider, providerName, cost, falCost, glifCost, balance, billing, windows, activeWorkflow,
  } = props;
  const wfActive = !!activeWorkflow;

  // Что доступно к показу в каждой секции
  const showCtx = hasContextInfo(estimate);
  const showCost = isCliProvider
    ? hasProviderCostInfo(cost, balance)
    : hasClaudeCostInfo(cost, windows);
  const hasFal = !isCliProvider && falCost.total > 0;
  const hasGlif = !isCliProvider && glifCost.count > 0;
  // Совсем нечего показывать — прячем чип (но активный workflow держит чип на экране)
  if (!showCtx && !showCost && !hasFal && !hasGlif && !wfActive) return null;

  // Подсветка пилюли — худшая из контекста и стоимости
  const ctxTone = estimate.level !== 'normal' ? estimate.level : undefined;
  const worst = worstWindow(windows);
  const costTone = isCliProvider
    ? providerBalanceTone(balance)
    : (worst && worst.level !== 'normal' ? worst.level : undefined);
  const tone = worseTone(ctxTone, costTone);

  // Краткая сумма стоимости в пилюле
  const sub = billing === 'subscription';
  const totalTokens = cost.input + cost.output;
  const costSummary = isCliProvider
    ? (cost.cost > 0 ? fmtUsd(cost.cost) : totalTokens > 0 ? `${fmtTokens(totalTokens)} ток.` : '—')
    : (cost.cost > 0 ? (sub ? '≈' : '') + fmtUsd(cost.cost) : '—');

  // Пилюля в две строки (без текстового лейбла): строка 1 — контекст, строка 2 — стоимость.
  // Компактнее по ширине, чтобы не распирать узкую мобильную шапку.
  const amountNode = (
    <span style={{ display: 'inline-flex', flexDirection: 'column', alignItems: 'flex-start', gap: 0, minWidth: 0 }}>
      {/* Пока идёт workflow — на лицевой стороне спиннер + прогресс фаз (вместо % контекста);
          сам контекст остаётся доступен в поповере */}
      {wfActive ? (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <div className="tool-spinner" style={{ width: 10, height: 10 }} />
          <span style={{ fontWeight: 700, color: C.accent, letterSpacing: 0.3 }}>WF</span>
          <span>{activeWorkflow!.phasesTotal > 0 ? `${activeWorkflow!.phasesDone}/${activeWorkflow!.phasesTotal}` : ''}</span>
        </span>
      ) : showCtx ? <ContextAmount estimate={estimate} isCompacting={isCompacting} isMobile /> : null}
      {(showCost || hasFal || hasGlif) && (
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{costSummary}</span>
      )}
    </span>
  );

  const sectionDivider: React.CSSProperties = {
    marginTop: 12, paddingTop: 10, borderTop: `1px solid ${C.bgInset}`,
  };

  return (
    <BadgeShell
      amount={amountNode}
      isMobile
      tone={tone}
      stacked
      wide
      pulse={wfActive}
      resetKey={props.resetKey}
      title="Контекст и расход сессии — нажмите для деталей"
    >
      {wfActive && (
        <div>
          <div style={badgeTitleStyle}>Workflow</div>
          <BadgeRow k="Фаза" v={activeWorkflow!.phasesTotal > 0 ? `${activeWorkflow!.phasesDone}/${activeWorkflow!.phasesTotal}` : 'идёт'} />
        </div>
      )}
      {showCtx && <div style={wfActive ? sectionDivider : undefined}><ContextPopoverBody {...props} /></div>}
      {showCost && (
        <div style={(wfActive || showCtx) ? sectionDivider : undefined}>
          {isCliProvider
            ? <ProviderCostPopoverBody providerName={providerName} stats={cost} balance={balance} />
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
}

// «Итог сессии в заметку» — теперь запускается ТОЛЬКО через AI-палитру (действие
// chat.summary). Компонент невидим, но остаётся смонтированным ради слушателя
// cc-ai-run; при успехе открывает созданную заметку.
function SessionSummaryButton({ session, hasMessages, online }: { session: Session; hasMessages: boolean; online: boolean }) {
  const [busy, setBusy] = useState(false);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс busy при смене чата
  useEffect(() => { setBusy(false); }, [session.id]);
  const run = () => {
    if (busy) return;
    setBusy(true);
    beginAiBusy();
    api.sessions.summary(session.id)
      .then(n => { bumpNotes(); openNoteById(n.id); })
      .catch(() => showToast('Итог сессии', 'Не удалось составить итог (claude не залогинен?)', 'info'))
      .finally(() => { setBusy(false); endAiBusy(); });
  };
  useEffect(() => {
    if (!online || !hasMessages) return;
    const onRun = (e: Event) => { if ((e as CustomEvent<{ action?: string }>).detail?.action === 'chat.summary') run(); };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, session.id, hasMessages, busy]);
  return null;
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

export function ChatHeaderBar({ session, project, hasMessages, online, cost, falCost, glifCost, billing, onBillingChange, rateWindows, isMobile, onBack, activeWorkflow, lastMechanic, onOpenSidebar, ctxEstimate, isWaiting, isCompacting, canCompact, compactNote, onCompact, persona, personaZoneName, agent, participants, onSessionUpdated, onAddToWall, onChatDeleted, island, compact, contextBar }: ChatHeaderBarProps) {
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
  const isCliProvider = providerKey !== 'claude';
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
  const personaIsProject = persona?.scope === 'project';
  const personaZoneText = personaIsProject
    ? (personaZoneName ? `Проект · ${personaZoneName}` : 'Проект')
    : 'Глобальный';
  // Происхождение чата (задача/автоматизация) — рисуется в мета-строке заголовка
  // (см. metaRow): на мобиле компактной иконкой, на десктопе коротким бейджем.
  const origin = resolveChatOrigin(session);
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
  const zoneDiffers = personaIsProject ? !project : !!project;
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
        background: `${personaAccent}${personaIsProject ? '2E' : '17'}`, color: personaAccent,
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
  // они и есть сводка состояния чата. На мобиле отдельные пилюли склеены в один
  // чип (MobileCombinedBadge), и ключ 'mobile-pills' гасит/возвращает его целиком:
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
  const ctxBadge = (
    <ContextBadge estimate={ctxEstimate} isMobile={isCompact} isWaiting={isWaiting}
      isCompacting={isCompacting} canCompact={canCompact} compactNote={compactNote}
      onCompact={onCompact} online={online} assistantName={asstName} resetKey={session.id} />
  );
  // Плашка стоимости: у стороннего провайдера — своя (стоимость + баланс),
  // у Claude — CostBadge с лимитами подписки
  const providerCostBadge = isCliProvider
    ? <ProviderCostBadge providerName={asstName} stats={cost} balance={provBalance} isMobile={isCompact} resetKey={session.id} />
    : <CostBadge stats={cost} isMobile={isCompact} billing={billing} onBillingChange={onBillingChange} windows={rateWindows} resetKey={session.id} />;
  // Бейдж расхода токенов чата (аналитика v2): обновляется по завершению хода —
  // триггер cost.results растёт вместе с result-сообщениями ленты
  const spendBadge = (
    <SpendBadge sessionId={session.id} chatName={session.name} resultCount={cost.results} isMobile={isCompact} />
  );
  // compact (колонка стены): плашек контекста, стоимости и расхода нет — в узкой
  // шапке они занимают всю ширину и переносят строку, а следить за деньгами и
  // контекстом уместнее в полном виде чата (открывается кнопкой из ярлыка колонки)
  const costBadges = compact ? null : isCompact ? (
    // Мобил/планшет: один объединённый чип (контекст + стоимость/расход) — не распирает шапку.
    // Чип можно скрыть целиком глазиком «Пилюли в шапке» в «⋯» (ключ mobile-pills)
    <>
      {mobilePillsVisible && (
        <MobileCombinedBadge
          estimate={ctxEstimate} isWaiting={isWaiting} isCompacting={isCompacting}
          canCompact={canCompact} compactNote={compactNote} onCompact={onCompact}
          online={online} assistantName={asstName}
          isCliProvider={isCliProvider} providerName={asstName} cost={cost} falCost={falCost} glifCost={glifCost}
          balance={provBalance} billing={billing} onBillingChange={onBillingChange} windows={rateWindows}
          activeWorkflow={activeWorkflow}
          resetKey={session.id}
        />
      )}
      {badgeVisible('spend') && spendBadge}
    </>
  ) : (
    // Десктопная шапка: пилюли по отдельности, и каждую можно убрать глазиком.
    // На мобиле они склеены в один чип, прятать там по частям нечего
    <>
      {badgeVisible('context') && ctxBadge}
      {badgeVisible('cost') && providerCostBadge}
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
  const summaryBtn = <SessionSummaryButton session={session} hasMessages={hasMessages} online={online} />;
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
    archive: online,
    delete: !!onChatDeleted && online && !compact,
  };
  const headerActions = CHAT_ACTION_ORDER.filter(k => headerActionAvailable[k]);
  // Порядок ряда — канонический: набор скрытых фильтрует ряд, сохраняя привычную
  // расстановку оставшихся действий относительно друг друга
  // Возврат из архива — вне настройки видимости: у архивного чата кнопка в ряду ВСЕГДА
  // (то же правило, что на карточке в списке). Само «В архив» по умолчанию спрятано в
  // «⋯», и без этого исключения выход из архива пришлось бы искать в меню.
  // Место — каноническое (перед «Удалить»), а не первое: ряд не должен перетасовываться
  // от того, что чат убрали в архив
  const chatArchived = !!session.archivedAt;
  const visibleActions = chatArchived && headerActionAvailable.archive
    ? headerActions.filter(k => k === 'archive' || headerVis.isVisible(k))
    : headerActions.filter(k => headerVis.isVisible(k));

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
        void updateChatFields(session, { archived: !session.archivedAt })
          .then(s => {
            onSessionUpdated?.(s);
            showToast('Архив', s.archivedAt ? 'Чат убран в архив' : 'Чат вернулся в список', 'info');
            // Вернули из архива: список своей области выходит из архивного вида. Чат
            // и так открыт — переключать нечего, но оставлять список показывать архив,
            // где этого чата уже нет, значит прятать его от человека второй раз
            if (session.archivedAt && !s.archivedAt) leaveChatArchiveView(chatFilterScope(s));
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
      // Архив не подсвечиваем акцентом по той же причине, что досье: акцент читается как
      // «включено и важно», а «этот чат убран с глаз» — состояние тихое
      case 'archive': return session.archivedAt
        ? { icon: <ArchiveRestore size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Вернуть из архива' }
        : { icon: <Archive size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'В архив' };
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
    context: hasContextInfo(ctxEstimate),
    cost: isCliProvider ? hasProviderCostInfo(cost, provBalance) : hasClaudeCostInfo(cost, rateWindows),
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
      case 'context': return ctxBadge;
      case 'cost': return providerCostBadge;
      case 'fal': return <FalCostBadge stats={falCost} isCompact={isCompact} resetKey={session.id} />;
      case 'glif': return <GlifCostBadge stats={glifCost} isCompact={isCompact} resetKey={session.id} />;
      case 'spend': return spendBadge;
    }
  };
  const availableBadges = compact ? [] : isCompact
    // Мобил/планшет: в шапке ДВЕ пилюли — объединённый чип (контекст+стоимость)
    // и отдельный «Расход токенов». Чипу — своя строка с превью (по названию не
    // понять, что внутри составного чипа), расходу — обычная строка, как на
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
        ? <MobileCombinedBadge
            estimate={ctxEstimate} isWaiting={isWaiting} isCompacting={isCompacting}
            canCompact={canCompact} compactNote={compactNote} onCompact={() => {}}
            online={online} assistantName={asstName}
            isCliProvider={isCliProvider} providerName={asstName} cost={cost} falCost={falCost} glifCost={glifCost}
            balance={provBalance} billing={billing} onBillingChange={onBillingChange} windows={rateWindows}
            activeWorkflow={activeWorkflow}
            resetKey={`menu-${session.id}`}
          />
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
        // У возврата из архива глазика нет: кнопка стоит в ряду всегда, и переключатель
        // предлагал бы то, что ни на что не влияет
        const pinned = k === 'archive' && chatArchived;
        return {
          key: k,
          icon: m.icon,
          label: m.label,
          danger: m.danger,
          // Теги и срок открывают свои поповеры по якорю «⋯» — сама кнопка исчезнет
          // вместе с меню, и её rect брать было бы неоткуда
          onClick: () => runAction(k, overflowAnchorRef.current ?? undefined),
          action: pinned ? undefined : {
            icon: visible ? <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
            title: visible ? 'Убрать в меню' : 'Показывать кнопкой в ряду',
            onClick: () => headerVis.toggle(k),
          },
        };
      }),
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
          <MenuItem
            icon={<NotebookPen size={15} strokeWidth={2} />}
            label="Итог сессии в заметку"
            onClick={() => { setCtxMenu(null); runAi('chat.summary'); }}
          />
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
      // Ни подложки, ни линии, ни тени: границу шапки к ленте держит САМА ЛЕНТА —
      // её верхний край растворяется при прокрутке (ChatPanel, FEED_FADE). Подложка
      // мутила бы дудл-холст, а линия поверх растворения читалась бы вторым
      // разделителем подряд
      <div style={{ position: 'relative', flexShrink: 0, width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box' }}>
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
    // колонки, плотный тулбар закрывал бы дудл-холст под шапкой
    <Toolbar isMobile={isCompact} noBorder={island || compact} bg={island || compact ? 'transparent' : undefined}
      // Правый клик по шапке — меню действий у курсора (desktop, см. ctxMenuEl)
      onContextMenu={isCompact ? undefined : e => {
        e.preventDefault();
        setCtxMenu(new DOMRect(e.clientX, e.clientY, 0, 0));
      }}
      style={{
        ...(personaAccent ? { borderLeft: `3px solid ${personaAccent}` } : null),
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
