import { useState, useEffect } from 'react';
import type { Project, Session, ClaudeBilling } from '../../types';
import { api } from '../../lib/api';
import { modelLabel, modelProvider, assistantName, useModelLabel } from '../../lib/models';
import { effortLabel } from '../../lib/effort';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, worstWindow } from '../../lib/rateLimit';
import { type ContextEstimate } from '../../lib/context';
import { ContextThresholdsDialog } from '../ContextThresholdsDialog';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { Toolbar, ToolbarIconButton } from '../Toolbar';
import { BackButton } from '../ui';

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

// Баланс аккаунта DeepSeek (GET /api/providers/deepseek/balance)
interface DeepSeekBalance { available: boolean; currency: string; totalBalance: string }

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
      <div style={{ height: 4, borderRadius: 2, background: '#E5DCCB', overflow: 'hidden', margin: '3px 0' }}>
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
      <span style={{ flexShrink: 0 }}>{reached ? '⛔' : '⚠'}</span>
      <span style={{ flexShrink: 0, fontFamily: FONT.sans, whiteSpace: 'nowrap' }}>
        {windowLabel(w.limitType)} — {reached ? 'лимит достигнут' : 'лимит близко'}
      </span>
      <div style={{ flex: 1, minWidth: 30, height: 5, borderRadius: 3, background: '#E5DCCB', overflow: 'hidden' }}>
        <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
      </div>
      <span style={{ flexShrink: 0, fontFamily: FONT.mono, fontWeight: 700 }}>{w.pct}%{w.isUsingOverage ? '+' : ''}</span>
      {reset && <span style={{ flexShrink: 0, fontFamily: FONT.sans, color: C.textMuted, whiteSpace: 'nowrap' }}>· сброс {reset}</span>}
    </div>
  );
}

// Общая оболочка бейджа стоимости: пилюля с подписью + суммой и выпадающая разбивка по клику.
// tone окрашивает пилюлю при приближении к лимиту (warn/danger).
function BadgeShell({ label, amount, title, isMobile, tone, children }: {
  label: string; amount: React.ReactNode; title: string; isMobile?: boolean;
  tone?: 'warn' | 'danger'; children: React.ReactNode;
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
          display: 'flex', alignItems: 'center', gap: 4, padding: '3px 9px',
          background: toneBg, border: `1px solid ${toneBorder}`, borderRadius: R.lg,
          cursor: 'pointer', fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: '#B05C38',
        }}
      >
        <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.3 }}>{label}</span>
        {amount}
      </button>
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
          <div style={{
            position: 'absolute', top: '100%', right: 0, marginTop: 6, zIndex: 41,
            minWidth: isMobile ? 200 : 240, padding: '12px 14px',
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown,
          }}>
            {children}
          </div>
        </>
      )}
    </div>
  );
}

// Бейдж стоимости Claude (токены/ходы). Клик раскрывает разбивку (аналог /cost).
// В режиме подписки сумма — это ≈ API-эквивалент (отдельно не списывается), что и поясняется.
function CostBadge({ stats, isMobile, billing, onBillingChange, windows }: {
  stats: CostStats; isMobile?: boolean; billing: ClaudeBilling; onBillingChange: (b: ClaudeBilling) => void;
  windows: RateWindow[];
}) {
  const worst = worstWindow(windows);
  if (stats.cost <= 0 && !worst) return null;
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
              border: `1px solid ${billing === b ? '#B05C38' : C.border}`,
              background: billing === b ? '#F1DDD1' : C.bgWhite,
              color: billing === b ? '#B05C38' : C.textMuted,
            }}>
            {b === 'subscription' ? 'Подписка' : 'API-ключ'}
          </button>
        ))}
      </div>
    </BadgeShell>
  );
}

// Бейдж статистики DeepSeek: стоимость сессии + токены + баланс аккаунта.
// У DeepSeek нет лимитов подписки (балансовая модель) — вместо окон показываем остаток
// средств с подсветкой при низком балансе. Заменяет CostBadge для deepseek-сессий.
function DeepSeekBadge({ stats, balance, isMobile }: {
  stats: CostStats; balance: DeepSeekBalance | null; isMobile?: boolean;
}) {
  if (stats.cost <= 0 && !balance) return null;
  const balNum = balance ? parseFloat(balance.totalBalance) : NaN;
  // Подсветка: < $1 — предупреждение, < $0.2 — критично
  const tone = !isNaN(balNum) ? (balNum < 0.2 ? 'danger' : balNum < 1 ? 'warn' : undefined) : undefined;
  const amountNode = (
    <>
      <span>{stats.cost > 0 ? fmtUsd(stats.cost) : '—'}</span>
      {tone && balance && (
        <span style={{ marginLeft: 5, color: RATE_COLORS[tone].text, fontWeight: 700 }}>
          · {balance.totalBalance} {balance.currency}
        </span>
      )}
    </>
  );
  return (
    <BadgeShell
      label="DeepSeek"
      amount={amountNode}
      isMobile={isMobile}
      tone={tone}
      title="Стоимость сессии и баланс DeepSeek — нажмите для разбивки"
    >
      <div style={badgeTitleStyle}>Стоимость DeepSeek</div>
      {stats.cost > 0 && <>
        <BadgeRow k="Всего" v={fmtUsd(stats.cost)} />
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
        DeepSeek работает по балансовой модели — стоимость списывается с аккаунта по факту.
      </div>
    </BadgeShell>
  );
}

// Индикатор заполнения контекстного окна: пилюля с мини-баром и процентом.
// Клик — попап с деталями и кнопкой «Свернуть контекст» (/compact); пороги
// подсветки настраиваются per-user (модалка «Настроить пороги…»).
function ContextBadge({ estimate, isMobile, isWaiting, isCompacting, canCompact, compactNote, onCompact, online, assistantName = 'Claude' }: {
  estimate: ContextEstimate; isMobile?: boolean; isWaiting: boolean; isCompacting: boolean;
  canCompact: boolean; compactNote?: string; onCompact: () => void; online: boolean;
  assistantName?: string;
}) {
  const [showThresholds, setShowThresholds] = useState(false);
  const c = RATE_COLORS[estimate.level];
  const tone = estimate.level !== 'normal' ? estimate.level : undefined;
  const hasPct = estimate.pct !== undefined;

  // В начале сессии показывать нечего (нет оценки и контекст не свёрнут) — прячем пилюлю
  if (!hasPct && !estimate.fresh) return null;

  const amountNode = (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
      {isCompacting ? (
        <div className="tool-spinner" style={{ width: 10, height: 10 }} />
      ) : hasPct ? (
        <span style={{ width: isMobile ? 18 : 26, height: 5, borderRadius: 3, background: '#E5DCCB', overflow: 'hidden', display: 'inline-block' }}>
          <span style={{ display: 'block', width: `${estimate.pct}%`, height: '100%', background: c.fill }} />
        </span>
      ) : null}
      <span style={{ color: tone ? c.text : undefined }}>
        {isCompacting ? '…' : hasPct ? `${estimate.pct}%` : estimate.fresh ? '✦' : '—'}
      </span>
    </span>
  );

  // Кнопка сжатия недоступна: ход идёт, компакт идёт, оценки нет, контекст только что сжат,
  // или сжимать ещё нечего (слишком мало ходов — CLI вернёт «not enough messages»)
  const compactDisabled = isWaiting || isCompacting || !hasPct || estimate.fresh || !canCompact || !online;
  const compactTitle = !canCompact && !isWaiting && !isCompacting
    ? 'Пока нечего сжимать — слишком мало сообщений'
    : isWaiting && !isCompacting ? 'Дождитесь завершения текущего хода' : undefined;

  return (
    <>
      <BadgeShell
        label={isMobile ? 'Ctx' : 'Контекст'}
        amount={amountNode}
        isMobile={isMobile}
        tone={tone}
        title="Заполнение контекста сессии — нажмите для деталей"
      >
        <div style={badgeTitleStyle}>Контекст сессии</div>
        {hasPct ? (
          <>
            <div style={{ height: 5, borderRadius: 3, background: '#E5DCCB', overflow: 'hidden', margin: '2px 0 6px' }}>
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
            padding: '6px 10px', borderRadius: 7, border: `1px solid ${compactDisabled ? C.border : '#C9BEAD'}`,
            background: C.bgWhite, cursor: compactDisabled ? 'default' : 'pointer',
            fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
            color: compactDisabled ? C.textMuted : '#5A5043', opacity: compactDisabled ? 0.65 : 1,
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
      </BadgeShell>
      {showThresholds && <ContextThresholdsDialog onClose={() => setShowThresholds(false)} />}
    </>
  );
}

// Бейдж трат на fal.ai (медиа). Отдельная от Claude цифра. Разбивка по моделям.
// В выпадашке: остаток баланса аккаунта (асинхронно) сверху + траты этого чата + ссылка на статистику.
function FalCostBadge({ stats, isMobile }: { stats: FalCostStats; isMobile?: boolean }) {
  // undefined = грузится, null = недоступно, number = баланс
  const [balance, setBalance] = useState<number | null | undefined>(undefined);
  useEffect(() => {
    let cancelled = false;
    api.fal.account(7)
      .then(d => { if (!cancelled) setBalance(d.enabled ? (d.balance ?? null) : null); })
      .catch(() => { if (!cancelled) setBalance(null); });
    return () => { cancelled = true; };
  }, []);
  if (stats.total <= 0) return null;
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
    <BadgeShell label="fal.ai" amount={fmtUsd(stats.total)} isMobile={isMobile}
      title="Траты на fal.ai (медиа) — нажмите для разбивки">
      {/* Герой — траты этого чата (за этим и кликнули) */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        <span>Траты fal.ai · этот чат</span>
        <span style={{ letterSpacing: 0 }}>{stats.count} ген.</span>
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 22, fontWeight: 700, color: '#B05C38', margin: '2px 0 4px' }}>{fmtUsd(stats.total)}</div>
      {inline && (
        <div style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textSecondary, marginBottom: 4, lineHeight: 1.4 }}>
          {inline}{moreCount > 0 ? `  ·  +${moreCount} в статистике` : ''}
        </div>
      )}
      {/* Баланс аккаунта — отдельной плашкой (другая сущность). Краснеет при низком остатке. */}
      <div style={{
        marginTop: 8, padding: '8px 10px', borderRadius: R.lg,
        background: lowBal ? '#FBF1EC' : C.bgInset, border: lowBal ? '1px solid #F5C6BF' : 'none',
        display: 'flex', justifyContent: 'space-between', alignItems: 'baseline',
        fontFamily: FONT.sans, fontSize: 12, color: lowBal ? '#B4452F' : C.textSecondary,
      }}>
        <span>Счёт fal.ai <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: lowBal ? '#B4452F' : '#B05C38' }}>{balanceText}</span></span>
        <a href="https://fal.ai/dashboard/billing" target="_blank" rel="noopener noreferrer"
          style={{ color: C.accent, fontWeight: 600, textDecoration: 'none', flexShrink: 0, marginLeft: 8 }}>пополнить ↗</a>
      </div>
      <div style={{ marginTop: 10 }}>
        <button type="button" onClick={() => window.dispatchEvent(new Event('open-fal-stats'))}
          style={{ border: 'none', background: 'none', cursor: 'pointer', padding: 0, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.accent }}>
          Подробная статистика →
        </button>
      </div>
    </BadgeShell>
  );
}

// Мобильный объединённый бейдж: Claude и fal.ai одной компактной пилюлей с разделителем.
// Сжимается (minWidth:0 + ellipsis), не распирает узкий тулбар. Клик открывает окно «Использование».
function CombinedCostBadge({ cost, falCost, billing, windows }: {
  cost: CostStats; falCost: FalCostStats; billing: ClaudeBilling; windows: RateWindow[];
}) {
  const worst = worstWindow(windows);
  const hasClaude = cost.cost > 0 || !!worst;
  const hasFal = falCost.total > 0;
  if (!hasClaude && !hasFal) return null;
  const sub = billing === 'subscription';
  const tone = worst && worst.level !== 'normal' ? worst.level : undefined;
  const toneBg = tone === 'danger' ? RATE_COLORS.danger.bg : tone === 'warn' ? RATE_COLORS.warn.bg : C.bgWhite;
  const toneBorder = tone === 'danger' ? RATE_COLORS.danger.border : tone === 'warn' ? RATE_COLORS.warn.border : C.border;
  return (
    <button
      type="button"
      onClick={() => window.dispatchEvent(new Event('open-fal-stats'))}
      title="Использование Claude + fal.ai — открыть статистику"
      style={{
        display: 'flex', alignItems: 'center', gap: 5, padding: '3px 9px', minWidth: 0, flexShrink: 1,
        overflow: 'hidden', whiteSpace: 'nowrap',
        background: toneBg, border: `1px solid ${toneBorder}`, borderRadius: R.lg,
        cursor: 'pointer', fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: '#B05C38',
      }}
    >
      {hasClaude && (
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {cost.cost > 0 ? (sub ? '≈' : '') + fmtUsd(cost.cost) : '—'}
          {tone && worst && <span style={{ marginLeft: 4, color: RATE_COLORS[worst.level].text }}>{worst.pct}%</span>}
        </span>
      )}
      {hasClaude && hasFal && <span style={{ color: C.textMuted, flexShrink: 0 }}>·</span>}
      {hasFal && (
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
          <span style={{ fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.3, marginRight: 3 }}>fal</span>
          {fmtUsd(falCost.total)}
        </span>
      )}
    </button>
  );
}

interface ChatHeaderBarProps {
  session: Session;
  project?: Project;
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
}

export function ChatHeaderBar({ session, project, online, cost, falCost, billing, onBillingChange, rateWindows, onOpenSettings, isMobile, onBack, activeWorkflow, onOpenSidebar, artifactsOpen, onToggleArtifacts, artifactFileCount, ctxEstimate, isWaiting, isCompacting, canCompact, compactNote, onCompact }: ChatHeaderBarProps) {
  const sessionModelLabel = useModelLabel(session.model);
  const asstName = assistantName(session.model);
  const isDeepSeek = modelProvider(session.model) === 'deepseek';
  // Баланс DeepSeek — только для deepseek-сессий (для плашки статистики)
  const [dsBalance, setDsBalance] = useState<DeepSeekBalance | null>(null);
  useEffect(() => {
    if (!isDeepSeek) { setDsBalance(null); return; }
    let alive = true;
    api.providers.deepseekBalance()
      .then(b => { if (alive) setDsBalance(b); })
      .catch(() => { /* баланс — необязательная информация */ });
    return () => { alive = false; };
  }, [session.model, isDeepSeek]);
  // Блок названия чата + подзаголовок (режим/модель). На мобиле он целиком кликабелен как «назад».
  const titleBlock = (
    <div style={{ minWidth: 0, flex: 1 }}>
      <div style={{ fontSize: 17, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {session.name ?? 'Новый чат'}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {/* На мобиле имя проекта не дублируем — оно доступно через кнопку «назад» */}
        {!isMobile && <span>{project ? project.name : 'без проекта'} · </span>}{sessionModelLabel}
        {session.effort && <span> · {effortLabel(session.effort)}</span>}
      </div>
    </div>
  );
  // Элементы шапки — выносим, чтобы отрендерить в двух раскладках (с центр. переключателем и без)
  const openBtn = onOpenSidebar && !isMobile ? (
    <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isMobile}>
      <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
        <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
      </svg>
    </ToolbarIconButton>
  ) : null;
  const titleEl = isMobile && onBack
    ? <BackButton onClick={onBack} style={{ flex: 1 }} title="Назад к списку">{titleBlock}</BackButton>
    : titleBlock;
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
  // Плашка стоимости: у DeepSeek — своя (стоимость + баланс), у Claude — CostBadge с лимитами
  const providerCostBadge = isDeepSeek
    ? <DeepSeekBadge stats={cost} balance={dsBalance} isMobile={isMobile} />
    : <CostBadge stats={cost} isMobile={isMobile} billing={billing} onBillingChange={onBillingChange} windows={rateWindows} />;
  const costBadges = isMobile ? (
    <>
      {ctxBadge}
      {isDeepSeek
        ? <DeepSeekBadge stats={cost} balance={dsBalance} isMobile={isMobile} />
        : <CombinedCostBadge cost={cost} falCost={falCost} billing={billing} windows={rateWindows} />}
    </>
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
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
          <path d="M14 2v6h6M9 13h6M9 17h3" />
        </svg>
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
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
      </svg>
    </ToolbarIconButton>
  ) : null;

  return (
    <Toolbar isMobile={isMobile}>
      {openBtn}{titleEl}{workflowBadge}{costBadges}{artifactsBtn}{settingsBtn}
    </Toolbar>
  );
}
