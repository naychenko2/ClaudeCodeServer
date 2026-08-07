// Карточка квотного провайдера (.qcard в полосе «Квоты подписок»). Состояния:
// загрузка/ошибка/недоступно/готово. На поверхности — percent-окна и здоровье FreeLLM;
// count-окна (сессии), тренд и кабинет — в раскрытии (моментальный снимок, не расход).
import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { ChevronRight, ExternalLink, RotateCw } from 'lucide-react';
import type { ProviderBalanceInfo } from '../../types';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Dot, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { fmtReset } from '../../lib/rateLimit';
import { QuotaWindow, parseQuotaWindow, barTextTone } from './QuotaWindow';

export interface ProviderCardData {
  key: string;
  name: string;
  color: string;
  balance: ProviderBalanceInfo | null;
  snapshots: { timestamp: string; balance: number; currency: string }[];
  state: 'loading' | 'error' | 'ready' | 'unavailable';
  isFree: boolean;
  // Момент следующей авто-попытки (Date.now()) — карточка ошибки ведёт по нему отсчёт
  // «повтор через N сек». null/undefined — авто-ретрая нет, просто «не удалось получить».
  retryAt?: number | null;
  cabinetUrl?: string;
  onRetry: () => void;
}

const cardBase: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: `${SP.sm + 2}px ${SP.md}px`,
};

const nameStyle: CSSProperties = { fontSize: FS.base, fontWeight: 600, color: C.textHeading };

// Пилюля тарифа (planLabel без «Подписка:») и бейдж «бесплатный»/«Предел» — один стиль
function Pill({ children, tone = 'plain' }: { children: ReactNode; tone?: 'plain' | 'danger' | 'free' }) {
  const styles: Record<string, CSSProperties> = {
    plain:  { background: C.bgCard, border: `1px solid ${C.border}`, color: C.textSecondary },
    free:   { background: C.successBg, border: `1px solid ${C.success}`, color: C.successText },
    danger: { background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, color: C.dangerText },
  };
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', padding: '2px 8px', borderRadius: R.md, fontSize: FS.xs, fontWeight: 600, ...styles[tone] }}>
      {children}
    </span>
  );
}

// Простой спарклайн тренда расхода окна (точки {t,u}, u 0..1)
function Sparkline({ points, height = 30 }: { points: { t: number; u: number }[]; height?: number }) {
  if (points.length < 2) return null;
  const w = 560, pad = 3;
  const ts = points.map(p => p.t);
  const tmin = Math.min(...ts), tmax = Math.max(...ts), span = tmax - tmin || 1;
  const xy = points.map(p => {
    const x = pad + (w - 2 * pad) * (p.t - tmin) / span;
    const y = pad + (height - 2 * pad) * (1 - Math.min(1, Math.max(0, p.u)));
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  return (
    <svg width="100%" height={height} viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" style={{ display: 'block' }}>
      <polyline points={xy} fill="none" stroke={C.accent} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

// Свежесть данных: точка + возраст. Протухло (>30 мин) — янтарная точка и время снимка
const STALE_MS = 30 * 60 * 1000;
const fmtClock = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
};
const fmtAgo = (iso: string) => {
  const t = new Date(iso).getTime();
  if (isNaN(t)) return null;
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return 'только что';
  if (mins < 60) return `${mins} мин назад`;
  const h = Math.floor(mins / 60);
  return h < 24 ? `${h} ч назад` : `${Math.floor(h / 24)} дн назад`;
};
const isStale = (iso?: string | null) => {
  if (!iso) return false;
  const t = new Date(iso).getTime();
  return !isNaN(t) && Date.now() - t > STALE_MS;
};
function Freshness({ asOf }: { asOf?: string | null }) {
  if (!asOf || !fmtAgo(asOf)) return null;
  const stale = isStale(asOf);
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
      fontSize: FS.xs, color: stale ? C.warningText : C.textMuted, whiteSpace: 'nowrap',
    }}>
      <Dot color={stale ? C.warning : C.success} size={6} />
      {stale ? `на ${fmtClock(asOf)}` : fmtAgo(asOf)}
    </span>
  );
}

export function ProviderCard({ data }: { data: ProviderCardData }) {
  const [open, setOpen] = useState(false);
  const { name, color, balance, state, isFree, retryAt, onRetry } = data;

  // Обратный отсчёт до авто-ретрая: тикает ежесекундно, только пока карточка в ошибке
  // и известен момент следующей попытки. Хуки безусловные (нельзя ставить в ветке), но
  // вне error-состояния таймер не заводится — лишних ре-рендеров нет.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (state !== 'error' || !retryAt) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [state, retryAt]);

  // --- Загрузка: скелет (данные приходят разными запросами, мигающий текст мешал бы) ---
  if (state === 'loading') {
    return (
      <div style={cardBase}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          <span style={{ width: 7, height: 7, borderRadius: R.full, background: C.bgSelected, flexShrink: 0 }} />
          <span style={{ height: 11, width: '38%', borderRadius: R.sm, background: C.bgSelected }} />
        </div>
        <div style={{ marginTop: 8, display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span style={{ height: 8, width: '70%', borderRadius: R.sm, background: C.bgSelected }} />
          <span style={{ height: 8, width: '54%', borderRadius: R.sm, background: C.bgSelected }} />
        </div>
      </div>
    );
  }

  // --- Ошибка: «не удалось получить» + обратный отсчёт до авто-ретрая + ручной повтор ---
  if (state === 'error') {
    // Сколько секунд до очередной авто-попытки. null — авто-ретрая на вкладке нет.
    const remaining = retryAt ? Math.max(0, Math.ceil((retryAt - now) / 1000)) : null;
    return (
      <div style={{ ...cardBase, cursor: 'default' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          <Dot color={color} size={7} />
          <span style={nameStyle}>{name}</span>
          <span style={{ marginLeft: 'auto' }} />
          <IconButton size="xs" onClick={onRetry} title="Повторить сейчас">
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </div>
        <div style={{ marginTop: 6, fontSize: FS.xs, color: C.textMuted }}>
          {remaining == null
            ? 'не удалось получить данные'
            : remaining > 0
              ? `не удалось получить · повтор через ${remaining} сек`
              : 'повторяем…'}
        </div>
      </div>
    );
  }

  // --- Недоступно: настроен, но квоту не отдаёт (Alibaba Cloud) — без выдуманных нулей ---
  if (state === 'unavailable') {
    return (
      <div style={{ ...cardBase, cursor: 'default' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          <Dot color={color} size={7} />
          <span style={nameStyle}>{name}</span>
          {isFree && <Pill tone="free">бесплатный</Pill>}
        </div>
        <div style={{ marginTop: 6, fontSize: FS.xs, color: C.textMuted }}>
          Квота недоступна — эндпоинт требует веб-сессию консоли. Расход виден в аналитике.
        </div>
      </div>
    );
  }

  // --- Готово ---
  const bal = balance!;
  const windows = (bal.windows ?? []).map(parseQuotaWindow);
  const percentWindows = windows.filter(w => w.kind === 'percent');
  const countWindows = windows.filter(w => w.kind === 'count');
  const hasExhausted = windows.some(w => w.exhausted);
  const health = bal.health ?? null;

  // Есть ли что раскрывать: count-окна, тренд или кабинет
  const trend = data.snapshots.length
    ? data.snapshots
        .filter(s => !isNaN(s.balance))
        .map(s => ({ t: new Date(s.timestamp).getTime(), u: Math.max(0, Math.min(1, (100 - s.balance) / 100)) }))
        .filter(p => !isNaN(p.t))
        .sort((a, b) => a.t - b.t)
    : [];
  const expandable = countWindows.length > 0 || trend.length >= 2 || !!data.cabinetUrl;

  const topRight = <Freshness asOf={bal.asOf} />;

  return (
    <div
      onClick={expandable ? () => setOpen(v => !v) : undefined}
      role={expandable ? 'button' : undefined}
      style={{
        ...cardBase,
        cursor: expandable ? 'pointer' : 'default',
        borderColor: hasExhausted ? C.warning : C.border,
      }}
    >
      {/* .top — имя, пилюли, свежесть */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
        <Dot color={color} size={7} />
        <span style={{ ...nameStyle, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
        {bal.planLabel && <Pill>{bal.planLabel}</Pill>}
        {isFree && <Pill tone="free">бесплатный</Pill>}
        {hasExhausted && <Pill tone="danger">Предел</Pill>}
        <span style={{ marginLeft: 'auto' }}>{topRight}</span>
        {expandable && (
          <ChevronRight size={15} strokeWidth={ICON_STROKE} style={{
            flexShrink: 0, color: C.textMuted, transition: 'transform .15s', transform: open ? 'rotate(90deg)' : 'none',
          }} />
        )}
      </div>

      {/* .wins — percent-окна на поверхности */}
      {percentWindows.length > 0 && (
        <div style={{ marginTop: 6, display: 'flex', flexDirection: 'column', gap: 5 }}>
          {percentWindows.map((w, i) => <QuotaWindow key={i} w={w} />)}
        </div>
      )}

      {/* Здоровье FreeLLM — видное место на поверхности */}
      {health && (health.platformsTotal ?? 0) > 0 && (
        <FreeLlmHealth health={health} />
      )}

      {/* Раскрытие: count-окна сегментами + моментальный снимок + тренд + кабинет */}
      {open && expandable && (
        <div style={{ marginTop: 10, borderTop: `1px dashed ${C.dashed}`, paddingTop: 10, display: 'flex', flexDirection: 'column', gap: 10 }}>
          {countWindows.map((w, i) => (
            <div key={i}>
              <QuotaWindow w={w} />
              <div style={{ marginTop: 4, fontSize: FS.xs, color: C.textMuted }}>моментальный снимок, не расход</div>
            </div>
          ))}
          {countWindows.length === 0 && percentWindows.length > 0 && trend.length >= 2 && (
            <>
              <div style={{ fontSize: FS.xs, color: C.textSecondary }}>Расход окна во времени</div>
              <Sparkline points={trend} />
            </>
          )}
          {data.cabinetUrl && (
            <a href={data.cabinetUrl} target="_blank" rel="noreferrer"
              style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: FS.xs, color: C.accent, textDecoration: 'none' }}>
              кабинет {name} <ExternalLink size={12} strokeWidth={ICON_STROKE} />
            </a>
          )}
        </div>
      )}

      {/* Сброс исчерпанного окна — когда именно отпустит */}
      {hasExhausted && (
        <div style={{ marginTop: 6, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          сброс {fmtReset(windows.find(w => w.exhausted)?.resetsAt ?? undefined)}
        </div>
      )}
    </div>
  );
}

// Здоровье FreeLLM: платформы сегментами + сводка за 24ч
function FreeLlmHealth({ health }: {
  health: { requests24h?: number | null; successRate?: number | null; platformsAlive?: number | null; platformsTotal?: number | null };
}) {
  const alive = health.platformsAlive ?? 0;
  const total = health.platformsTotal ?? 0;
  const rate = health.successRate;
  const req = health.requests24h;
  return (
    <>
      {/* Сегменты платформ: живые — C.info, остальные — C.track */}
      <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{ width: 64, flexShrink: 0, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Платформы</span>
        <span style={{ display: 'flex', gap: 3, flex: 1, flexWrap: 'wrap' }}>
          {Array.from({ length: total }, (_, i) => (
            <span key={i} style={{ display: 'block', width: 14, height: 6, borderRadius: 3, background: i < alive ? C.info : C.track }} />
          ))}
        </span>
        <span style={{ flexShrink: 0, minWidth: 34, textAlign: 'right', fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: barTextTone(0) }}>
          {alive} из {total}
        </span>
      </div>
      <div style={{ marginTop: 5, fontSize: FS.xs, color: C.textSecondary }}>
        за 24ч: {req != null ? `${req.toLocaleString('ru-RU')} запросов` : '—'}
        {rate != null ? ` · успех ${Math.round(rate)}%` : ''}
        {` · обслуживает ${alive} ${pluralPlaces(alive)} мест`}
      </div>
    </>
  );
}

const pluralPlaces = (n: number) => {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'фонового';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'фоновых';
  return 'фоновых';
};
