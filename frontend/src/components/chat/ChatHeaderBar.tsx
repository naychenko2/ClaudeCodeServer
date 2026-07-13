import { useState, useEffect } from 'react';
import { AlertTriangle, Ban, Plus, Menu, Hourglass, FileText, Settings } from 'lucide-react';
import type { Project, Session, ClaudeBilling, Persona } from '../../types';
import { api } from '../../lib/api';
import { modelLabel, modelProvider, assistantName, useModelLabel } from '../../lib/models';
import { effortLabel } from '../../lib/effort';
import { formatTimeLeft } from '../../lib/expiry';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { GroupParticipantsPopover } from '../../features/personas/GroupParticipantsPopover';
import { personaTitleLines } from '../../lib/personas';
import { AGENT_COLORS, agentDotColor } from '../AgentSelector';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, worstWindow } from '../../lib/rateLimit';
import { type ContextEstimate } from '../../lib/context';
import { ContextThresholdsDialog } from '../ContextThresholdsDialog';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FONT, R, SHADOW, TB } from '../../lib/design';
import { Toolbar, ToolbarIconButton } from '../Toolbar';
import { BackButton, Modal, ModalActions } from '../ui';
import { bumpNotes } from '../../lib/notes';
import { createTask } from '../../lib/tasks';
import { showToast } from '../../lib/toast';
import { openNoteById } from '../../features/notes/saveToNote';
import type { ExtractedTaskCandidate } from '../../types';

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

// Закреплённая строка-предупреждение над composer (вариант В) — при warning/rejected
export function RateLimitBar({ w }: { w: RateWindow }) {
  const c = RATE_COLORS[w.level];
  const reset = fmtReset(w.resetsAt);
  const reached = w.level === 'danger';
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8, padding: '6px 12px', marginBottom: 8,
      background: c.bg, border: `1px solid ${c.border}`, borderRadius: 8, fontSize: 12, color: c.text,
    }}>
      <span style={{ flexShrink: 0, display: 'flex', color: c.text }}>
        {reached
          ? <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      </span>
      <span style={{ flexShrink: 0, fontFamily: FONT.sans, whiteSpace: 'nowrap' }}>
        {windowLabel(w.limitType)} — {reached ? 'лимит достигнут' : 'лимит близко'}
      </span>
      <div style={{ flex: 1, minWidth: 30, height: 5, borderRadius: 3, background: C.track, overflow: 'hidden' }}>
        <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
      </div>
      <span style={{ flexShrink: 0, fontFamily: FONT.mono, fontWeight: 700 }}>{w.pct}%{w.isUsingOverage ? '+' : ''}</span>
      {reset && <span style={{ flexShrink: 0, fontFamily: FONT.sans, color: C.textMuted, whiteSpace: 'nowrap' }}>· сброс {reset}</span>}
    </div>
  );
}

// Общая оболочка бейджа стоимости: пилюля с подписью + суммой и выпадающая разбивка по клику.
// tone окрашивает пилюлю при приближении к лимиту (warn/danger).
// stacked — двухстрочная пилюля (label скрыт/опущен): содержимое amount в столбик,
// компактнее по ширине (для мобильного объединённого чипа).
// wide — более широкий поповер на мобилке (для объединённого чипа с двумя секциями:
// шире → меньше переносов → ниже по высоте, помещается на экран).
function BadgeShell({ label, amount, title, isMobile, tone, stacked, wide, children }: {
  label?: string; amount: React.ReactNode; title: string; isMobile?: boolean;
  tone?: 'warn' | 'danger'; stacked?: boolean; wide?: boolean; children: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const toneBg = tone === 'danger' ? RATE_COLORS.danger.bg : tone === 'warn' ? RATE_COLORS.warn.bg : C.bgWhite;
  const toneBorder = tone === 'danger' ? RATE_COLORS.danger.border : tone === 'warn' ? RATE_COLORS.warn.border : C.border;
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
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
          <div style={
            // Широкий мобильный поповер: absolute+right:0 привязан к правому краю чипа,
            // а чип не у края экрана (справа кнопки) → широкий блок уезжал влево за экран.
            // Крепим fixed к правому краю ВЬЮПОРТА под тулбаром — всегда на экране.
            wide && isMobile
              ? {
                  position: 'fixed', top: TB.heightMobile + 6, right: 8, zIndex: 41,
                  width: 'min(340px, calc(100vw - 16px))',
                  maxHeight: 'calc(100dvh - 130px)', overflowY: 'auto',
                  padding: '12px 14px',
                  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown,
                }
              : {
                  position: 'absolute', top: '100%', right: 0, marginTop: 6, zIndex: 41,
                  minWidth: isMobile ? 200 : 240,
                  maxWidth: 'calc(100vw - 24px)',
                  maxHeight: isMobile ? 'calc(100dvh - 130px)' : undefined,
                  overflowY: isMobile ? 'auto' : undefined,
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
  stats: CostStats; billing: ClaudeBilling; onBillingChange: (b: ClaudeBilling) => void; windows: RateWindow[];
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
        {(['subscription', 'api'] as ClaudeBilling[]).map(b => (
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
function CostBadge({ stats, isMobile, billing, onBillingChange, windows }: {
  stats: CostStats; isMobile?: boolean; billing: ClaudeBilling; onBillingChange: (b: ClaudeBilling) => void;
  windows: RateWindow[];
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
      isMobile={isMobile}
      tone={tone}
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

// Подсветка по балансу CLI-провайдера: < $1 — предупреждение, < $0.2 — критично
function providerBalanceTone(balance: ProviderBalance | null): 'warn' | 'danger' | undefined {
  if (!balance) return undefined;
  const balNum = parseFloat(balance.totalBalance);
  if (isNaN(balNum)) return undefined;
  return balNum < 0.2 ? 'danger' : balNum < 1 ? 'warn' : undefined;
}

// Тело поповера статистики CLI-провайдера (стоимость/токены/ходы + баланс аккаунта).
function ProviderCostPopoverBody({ providerName, stats, balance }: {
  providerName: string; stats: CostStats; balance: ProviderBalance | null;
}) {
  const tone = providerBalanceTone(balance);
  const hasCost = stats.cost > 0;
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
          <div style={badgeSectionStyle}>Баланс аккаунта</div>
          <BadgeRow k="Остаток" v={`${balance.totalBalance} ${balance.currency}`} />
          {tone && (
            <div style={{ fontFamily: FONT.sans, fontSize: 11, color: RATE_COLORS[tone].text, marginTop: 4, lineHeight: 1.4 }}>
              {tone === 'danger' ? 'Баланс почти исчерпан — пополните аккаунт.' : 'Баланс на исходе.'}
            </div>
          )}
        </>
      )}
      <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, marginTop: 8, lineHeight: 1.4 }}>
        {hasCost
          ? `${providerName} работает по балансовой модели — стоимость списывается с аккаунта по факту.`
          : `${providerName} не отдаёт цены через API — показываем расход в токенах. Квоты смотрите в кабинете провайдера.`}
      </div>
    </>
  );
}

function ProviderCostBadge({ providerName, stats, balance, isMobile }: {
  providerName: string; stats: CostStats; balance: ProviderBalance | null; isMobile?: boolean;
}) {
  // Есть активность (хотя бы один ход) или баланс — иначе в начале сессии прячем
  if (!hasProviderCostInfo(stats, balance)) return null;
  const tone = providerBalanceTone(balance);
  const hasCost = stats.cost > 0;
  const totalTokens = stats.input + stats.output;
  // Сумма в пилюле: деньги, если считаем стоимость; иначе токены; иначе прочерк
  const amountNode = (
    <>
      <span>{hasCost ? fmtUsd(stats.cost) : totalTokens > 0 ? `${fmtTokens(totalTokens)} ток.` : '—'}</span>
      {tone && balance && (
        <span style={{ marginLeft: 5, color: RATE_COLORS[tone].text, fontWeight: 700 }}>
          · {balance.totalBalance} {balance.currency}
        </span>
      )}
    </>
  );
  return (
    <BadgeShell
      label={providerName}
      amount={amountNode}
      isMobile={isMobile}
      tone={tone}
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
  assistantName?: string;
}) {
  const { estimate, isMobile, isCompacting } = props;
  const tone = estimate.level !== 'normal' ? estimate.level : undefined;

  // В начале сессии показывать нечего (нет оценки и контекст не свёрнут) — прячем пилюлю
  if (!hasContextInfo(estimate)) return null;

  return (
    <BadgeShell
      label={isMobile ? 'Ctx' : 'Контекст'}
      amount={<ContextAmount estimate={estimate} isCompacting={isCompacting} isMobile={isMobile} />}
      isMobile={isMobile}
      tone={tone}
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
function FalCostBadge({ stats, isMobile }: { stats: FalCostStats; isMobile?: boolean }) {
  if (stats.total <= 0) return null;
  return (
    <BadgeShell label="fal.ai" amount={fmtUsd(stats.total)} isMobile={isMobile}
      title="Траты на fal.ai (медиа) — нажмите для разбивки">
      <FalPopoverBody stats={stats} />
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
  isCliProvider: boolean; providerName: string; cost: CostStats; falCost: FalCostStats;
  balance: ProviderBalance | null; billing: ClaudeBilling; onBillingChange: (b: ClaudeBilling) => void;
  windows: RateWindow[];
}) {
  const {
    estimate, isCompacting, isCliProvider, providerName, cost, falCost, balance, billing, windows,
  } = props;

  // Что доступно к показу в каждой секции
  const showCtx = hasContextInfo(estimate);
  const showCost = isCliProvider
    ? hasProviderCostInfo(cost, balance)
    : hasClaudeCostInfo(cost, windows);
  const hasFal = !isCliProvider && falCost.total > 0;
  // Совсем нечего показывать — прячем чип
  if (!showCtx && !showCost && !hasFal) return null;

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
      {showCtx && <ContextAmount estimate={estimate} isCompacting={isCompacting} isMobile />}
      {(showCost || hasFal) && (
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
      title="Контекст и расход сессии — нажмите для деталей"
    >
      {showCtx && <ContextPopoverBody {...props} />}
      {showCost && (
        <div style={showCtx ? sectionDivider : undefined}>
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
  billing: ClaudeBilling;
  onBillingChange: (b: ClaudeBilling) => void;
  rateWindows: RateWindow[];
  onOpenSettings: () => void;
  isMobile?: boolean;
  onBack?: () => void;
  activeWorkflow?: { phasesDone: number; phasesTotal: number };
  onOpenSidebar?: () => void;
  artifactsOpen?: boolean;
  onToggleArtifacts?: () => void;
  artifactFileCount?: number;
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
  // Участники группового чата (2-4): стек аватаров вместо одиночного блока персоны;
  // активный спикер (= persona) — с цветным кольцом
  participants?: Persona[] | null;
  // Состав группы изменён через поповер участников — родитель обновляет session
  onSessionUpdated?: (s: Session) => void;
}

// «Итог сессии в заметку» — теперь запускается ТОЛЬКО через AI-палитру (действие
// chat.summary). Компонент невидим, но остаётся смонтированным ради слушателя
// cc-ai-run; при успехе открывает созданную заметку.
function SessionSummaryButton({ session, hasMessages, online }: { session: Session; hasMessages: boolean; online: boolean }) {
  const [busy, setBusy] = useState(false);
  useEffect(() => { setBusy(false); }, [session.id]);
  const run = () => {
    if (busy) return;
    setBusy(true);
    api.sessions.summary(session.id)
      .then(n => { bumpNotes(); openNoteById(n.id); })
      .catch(() => showToast('Итог сессии', 'Не удалось составить итог (claude не залогинен?)', 'info'))
      .finally(() => setBusy(false));
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

// Иконка «задачи из чата» — документ с плюсом
// «Задачи из чата» — запускаются ТОЛЬКО через AI-палитру (действие chat.extract).
// Кнопка убрана; компонент остаётся смонтированным ради слушателя cc-ai-run и
// показывает модалку выбора извлечённых кандидатов.
function ExtractTasksButton({ session, hasMessages, online }: { session: Session; hasMessages: boolean; online: boolean }) {
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [dialog, setDialog] = useState<{ projectId: string | null; items: (ExtractedTaskCandidate & { sel: boolean })[] } | null>(null);
  useEffect(() => { setDialog(null); setBusy(false); }, [session.id]);

  const run = () => {
    if (busy) return;
    setBusy(true);
    api.sessions.extractTasks(session.id)
      .then(r => {
        if (r.tasks.length === 0) {
          showToast('Задачи из чата', 'В этом чате задач-действий не нашлось', 'info');
          return;
        }
        setDialog({ projectId: r.projectId ?? null, items: r.tasks.map(t => ({ ...t, sel: true })) });
      })
      .catch(() => showToast('Задачи из чата', 'Не удалось извлечь задачи из чата', 'info'))
      .finally(() => setBusy(false));
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

export function ChatHeaderBar({ session, project, hasMessages, online, cost, falCost, billing, onBillingChange, rateWindows, onOpenSettings, isMobile, onBack, activeWorkflow, onOpenSidebar, artifactsOpen, onToggleArtifacts, artifactFileCount, ctxEstimate, isWaiting, isCompacting, canCompact, compactNote, onCompact, persona, personaZoneName, agent, participants, onSessionUpdated }: ChatHeaderBarProps) {
  // Поповер управления участниками группового чата (клик по стеку аватаров)
  const [participantsOpen, setParticipantsOpen] = useState(false);
  const sessionModelLabel = useModelLabel(session.model);
  const asstName = assistantName(session.model);
  const providerKey = modelProvider(session.model);
  const isCliProvider = providerKey !== 'claude';
  // Баланс провайдера — только для сессий сторонних провайдеров (для плашки статистики);
  // 404 (провайдер без источника баланса, напр. GLM) — просто без блока баланса
  const [provBalance, setProvBalance] = useState<ProviderBalance | null>(null);
  useEffect(() => {
    // Сброс всегда: при смене провайдера (deepseek → glm) 404 не перезаписал бы
    // стейт в catch — и в плашке остался бы чужой баланс
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
  // Блок названия чата + подзаголовок (режим/модель). На мобиле он целиком кликабелен как «назад».
  // При наличии персоны — её идентификация доминирует: аватар + роль (serif, цвет персоны)
  // и вторая строка «имя · зона · модель». session.name уходит в тултип, чтобы не потеряться.
  const titleBlock = participants && participants.length > 1 ? (
    // Групповой чат: стек аватаров участников (активный спикер — с цветным кольцом)
    // вместо одиночного блока персоны; вторая строка — «Отвечает: Роль (Имя)».
    <div
      style={{ minWidth: 0, flex: 1, display: 'flex', alignItems: 'center', gap: 10 }}
    >
      <div style={{ position: 'relative', flexShrink: 0 }}>
        {/* Стек кликабелен: поповер со списком участников, добавлением и удалением */}
        <button
          type="button"
          onClick={() => setParticipantsOpen(o => !o)}
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
                marginLeft: i === 0 ? 0 : -9,
                borderRadius: '50%',
                border: `2px solid ${ring}`,
                zIndex: active ? participants.length + 1 : participants.length - i,
                position: 'relative',
                background: C.bgMain,
              }}>
                <PersonaAvatar persona={p} size={isMobile ? 24 : 26} />
              </div>
            );
          })}
          {/* «+» — явный вход в управление составом (до 4 участников) */}
          {participants.length < 4 && (
            <span style={{
              marginLeft: -6, zIndex: 0, width: isMobile ? 24 : 26, height: isMobile ? 24 : 26,
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
      <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 16, fontWeight: 600, color: personaAccent ?? C.textHeading, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {session.name ?? 'Групповой чат'}
        </span>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, marginTop: 1 }}>
          <span style={{ fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Отвечает: {persona ? personaTitleLines(persona).primary + (personaTitleLines(persona).secondary ? ` (${personaTitleLines(persona).secondary})` : '') : '—'}
          </span>
          {!isMobile && (
            <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
              · {sessionModelLabel}{session.effort && ` · ${effortLabel(session.effort)}`}
            </span>
          )}
        </div>
      </div>
    </div>
  ) : persona && personaAccent ? (
    <div title={session.name ?? undefined} style={{ minWidth: 0, flex: 1, display: 'flex', alignItems: 'center', gap: 9 }}>
      <PersonaAvatar persona={persona} size={28} />
      <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {/* Роль — заголовок (serif, цвет персоны) */}
        <span style={{ fontFamily: FONT.serif, fontSize: 16, fontWeight: 600, color: personaAccent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {personaTitleLines(persona).primary}
        </span>
        {/* Строка 2: имя персоны + пилюля зоны + модель/effort (компактно) */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, marginTop: 1 }}>
          {personaTitleLines(persona).secondary && (
            <span style={{ flexShrink: 0, fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {personaTitleLines(persona).secondary}
            </span>
          )}
          <span style={{
            flexShrink: 0, fontSize: 10, fontWeight: 600, letterSpacing: '0.02em',
            padding: '1px 7px', borderRadius: R.pill,
            background: `${personaAccent}${personaIsProject ? '2E' : '17'}`, color: personaAccent,
          }}>
            {personaZoneText}
          </span>
          {!isMobile && (
            <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
              {sessionModelLabel}{session.effort && ` · ${effortLabel(session.effort)}`}
            </span>
          )}
        </div>
      </div>
    </div>
  ) : (
    <div style={{ minWidth: 0, flex: 1 }}>
      <div style={{ fontSize: 17, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {session.name ?? 'Новый чат'}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {/* .md-агент чата — лёгкая пометка: цветная точка + имя (не персона-блок) */}
        {agent && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, marginRight: 4, verticalAlign: 'baseline' }}>
            <span style={{ width: 7, height: 7, borderRadius: '50%', background: agentDotColor(agent.color), display: 'inline-block', flexShrink: 0 }} />
            <span style={{ fontFamily: FONT.sans, fontWeight: 600, color: C.textSecondary }}>{agent.name}</span>
            <span> ·</span>
          </span>
        )}
        {/* На мобиле имя проекта не дублируем — оно доступно через кнопку «назад» */}
        {!isMobile && <span>{project ? project.name : 'без проекта'} · </span>}{sessionModelLabel}
        {session.effort && <span> · {effortLabel(session.effort)}</span>}
      </div>
    </div>
  );
  // Элементы шапки — выносим, чтобы отрендерить в двух раскладках (с центр. переключателем и без)
  const openBtn = onOpenSidebar && !isMobile ? (
    <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isMobile}>
      <Menu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </ToolbarIconButton>
  ) : null;
  const titleEl = isMobile && onBack
    ? <BackButton onClick={onBack} style={{ flex: 1 }} title="Назад к списку">{titleBlock}</BackButton>
    : titleBlock;
  // Пилюля временного чата: остаток до авто-удаления; клик — быстрый путь к настройке.
  // На мобиле не показываем — шапка и так плотная, метка есть в списке чатов
  const expiryLeft = formatTimeLeft(session);
  const expiryBadge = expiryLeft && !isMobile ? (
    <button
      type="button"
      onClick={online ? onOpenSettings : undefined}
      title={`Временный чат — удалится ${expiryLeft}, если не будет активности. Нажмите, чтобы изменить.`}
      style={{
        display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, flexShrink: 0,
        cursor: online ? 'pointer' : 'default',
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, whiteSpace: 'nowrap',
      }}
    >
      <Hourglass size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {expiryLeft}
    </button>
  ) : null;
  const workflowBadge = activeWorkflow ? (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, flexShrink: 0,
    }}>
      <div className="tool-spinner" style={{ width: 10, height: 10, flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, whiteSpace: 'nowrap' }}>
        {activeWorkflow.phasesTotal > 0 ? `${activeWorkflow.phasesDone}/${activeWorkflow.phasesTotal}` : 'Workflow'}
      </span>
    </div>
  ) : null;
  const ctxBadge = (
    <ContextBadge estimate={ctxEstimate} isMobile={isMobile} isWaiting={isWaiting}
      isCompacting={isCompacting} canCompact={canCompact} compactNote={compactNote}
      onCompact={onCompact} online={online} assistantName={asstName} />
  );
  // Плашка стоимости: у стороннего провайдера — своя (стоимость + баланс),
  // у Claude — CostBadge с лимитами подписки
  const providerCostBadge = isCliProvider
    ? <ProviderCostBadge providerName={asstName} stats={cost} balance={provBalance} isMobile={isMobile} />
    : <CostBadge stats={cost} isMobile={isMobile} billing={billing} onBillingChange={onBillingChange} windows={rateWindows} />;
  const costBadges = isMobile ? (
    // Мобилка: один объединённый чип (контекст + стоимость/расход) — не распирает шапку
    <MobileCombinedBadge
      estimate={ctxEstimate} isWaiting={isWaiting} isCompacting={isCompacting}
      canCompact={canCompact} compactNote={compactNote} onCompact={onCompact}
      online={online} assistantName={asstName}
      isCliProvider={isCliProvider} providerName={asstName} cost={cost} falCost={falCost}
      balance={provBalance} billing={billing} onBillingChange={onBillingChange} windows={rateWindows}
    />
  ) : (
    <>
      {ctxBadge}
      {providerCostBadge}
      <FalCostBadge stats={falCost} isMobile={isMobile} />
    </>
  );
  const artifactsBtn = onToggleArtifacts ? (
    <ToolbarIconButton onClick={onToggleArtifacts} title="Артефакты сессии" isMobile={isMobile} active={artifactsOpen}>
      <div style={{ position: 'relative', display: 'flex' }}>
        <FileText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        {artifactFileCount !== undefined && artifactFileCount > 0 && (
          <span style={{
            position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
            borderRadius: 7, background: C.accent, color: C.onAccent,
            fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
          }}>
            {artifactFileCount}
          </span>
        )}
      </div>
    </ToolbarIconButton>
  ) : null;
  const settingsBtn = online ? (
    <ToolbarIconButton onClick={onOpenSettings} title="Настройки чата" isMobile={isMobile}>
      <Settings size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </ToolbarIconButton>
  ) : null;

  // На мобилке артефакты и настройки — плотная пара справа (gap 2 вместо TB.gap),
  // читаются как единая группа действий чата; на десктопе — как раньше, врозь.
  const summaryBtn = <SessionSummaryButton session={session} hasMessages={hasMessages} online={online} />;
  const extractBtn = <ExtractTasksButton session={session} hasMessages={hasMessages} online={online} />;
  const actionBtns = isMobile
    ? <div style={{ display: 'flex', alignItems: 'center', gap: 0, flexShrink: 0 }}>{extractBtn}{summaryBtn}{artifactsBtn}{settingsBtn}</div>
    : <>{extractBtn}{summaryBtn}{artifactsBtn}{settingsBtn}</>;

  return (
    <Toolbar isMobile={isMobile} style={personaAccent ? { borderLeft: `3px solid ${personaAccent}` } : undefined}>
      {openBtn}{titleEl}{expiryBadge}{workflowBadge}{costBadges}{actionBtns}
    </Toolbar>
  );
}
