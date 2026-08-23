// Карточка квоты в полосе «Квоты подписок» — единая оболочка для двух источников:
// CLI-провайдеров (GLM/Kimi/…) и аккаунтов подписок Claude. QuotasTab нормализует оба
// в общую вью-модель (см. ProviderCardData); здесь — только размещение и общие
// состояния: загрузка/ошибка/недоступно/готово. Поверхность — percent-окна и здоровье
// FreeLLM; count-окна, тренд, кабинет, тариф и подписка-специфика — в раскрытии.
import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { Check, ChevronRight, Copy, ExternalLink, RotateCw } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Dot, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { fmtReset } from '../../lib/rateLimit';
import { QuotaWindow, CountSegments, type QuotaWindowView } from './QuotaWindow';

export interface PillSpec { label: string; tone: 'plain' | 'danger' | 'free' | 'warn' }

// Свежесть в углу шапки. Логику (канал данных, pollStatus, возраст) считает QuotasTab —
// здесь только размещение: точка + подпись.
export interface FreshnessSpec {
  dot: string;        // цвет точки: success/warning/textMuted
  text: string;       // «3 мин назад» / «на 14:20» / «по ходам · 2 ч назад»
  textTone?: string;  // цвет подписи (по умолчанию textMuted; при stale — warningText)
}

export interface HealthSpec {
  requests24h?: number | null;
  successRate?: number | null;
  platformsAlive?: number | null;
  platformsTotal?: number | null;
}

export interface ProviderCardData {
  key: string;
  name: string;
  color: string;
  state: 'loading' | 'error' | 'ready' | 'unavailable';
  isFree: boolean;
  dim?: boolean;            // притушение: данные устарели при ошибке сводки (как MoneyTile при stale)
  retryAt?: number | null;
  onRetry: () => void;
  // === state === 'ready' ===
  windows: QuotaWindowView[];        // percent-окна на поверхности
  labelWidth?: number;               // подпись QuotaWindow (64 — провайдеры, 92 — подписки)
  pills: PillSpec[];                 // пилюли шапки (planLabel, «бесплатный», «Предел»)
  // Пилюли, дублирующиеся в раскрытии рядом со строкой тарифа — для мобила, где шапка
  // тесна (пилюли скрыты) и warn-пилюли об ограничениях тарифа иначе не видны вообще.
  // Тариф в этот список НЕ входит: он уже идёт отдельной строкой в раскрытии.
  expandedPills?: PillSpec[];
  routingBadge?: { tone: 'ok' | 'warn'; label: string };  // бейдж ротации — только карточка-цель
  freshness?: FreshnessSpec;         // свежесть в углу шапки
  hint?: ReactNode;                  // хинт-строка (подписки: сброс худшего окна + reason)
  health?: HealthSpec | null;        // здоровье FreeLLM (провайдеры)
  hasExhausted: boolean;             // исчерпано ли окно → янтарный бордер
  exhaustedResetAt?: string | null;  // когда отпустит исчерпанное окно (провайдеры, ISO)
  // === раскрытие ===
  expandable: boolean;
  countWindows?: QuotaWindowView[];  // count-окна сегментами (провайдеры)
  trend?: { t: number; u: number }[]; // sparkline — общий
  cabinetUrl?: string;               // кабинет (провайдеры)
  tier?: string | null;              // подписки: пилюля «Тариф: …» в раскрытии
  thresholdNote?: string;            // подписки: пояснение порога вывода из ротации
  freshnessDetail?: ReactNode;       // подписки: полная подпись свежести (из таблицы)
  copyCommand?: string | null;       // подписки: loginCommand — моно-блок + кнопка копирования
  loginCommand?: string | null;      // подписки: та же команда компактной ссылкой, когда моно-блок не нужен
}

const cardBase: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: `${SP.sm + 2}px ${SP.md}px`,
};

const nameStyle: CSSProperties = { fontSize: FS.base, fontWeight: 600, color: C.textHeading };

// Пилюля шапки (planLabel, «бесплатный», «Предел», «Часть платформ недоступна») — один стиль
function Pill({ children, tone = 'plain' }: { children: ReactNode; tone?: 'plain' | 'danger' | 'free' | 'warn' }) {
  const styles: Record<string, CSSProperties> = {
    plain:  { background: C.bgCard, border: `1px solid ${C.border}`, color: C.textSecondary },
    free:   { background: C.successBg, border: `1px solid ${C.success}`, color: C.successText },
    danger: { background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, color: C.dangerText },
    warn:    { background: C.warningBg, border: `1px solid ${C.warning}`, color: C.warningText },
  };
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0, whiteSpace: 'nowrap', padding: '2px 8px', borderRadius: R.md, fontSize: FS.xs, fontWeight: 600, ...styles[tone] }}>
      {children}
    </span>
  );
}

// Бейдж ротации на карточке-цели: успех (цель в ротации) / предупреждение (спилл)
function RoutingPill({ tone, label }: { tone: 'ok' | 'warn'; label: string }) {
  const s = tone === 'ok'
    ? { background: C.successBg, border: C.success, color: C.successText }
    : { background: C.warningBg, border: C.warning, color: C.warningText };
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0, whiteSpace: 'nowrap', padding: '2px 8px', borderRadius: R.md, fontSize: FS.xs, fontWeight: 600, border: `1px solid ${s.border}`, background: s.background, color: s.color }}>
      {label}
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

// Компактный вид той же команды — когда опрос жив и чинить нечего, но перелогинить
// аккаунт всё равно может понадобиться (протух OAuth, сменили профиль). Ссылка вместо
// кнопки с моно-блоком: место в раскрытии дорогое, а действие здесь про запас
function CopyCommandLink({ cmd }: { cmd: string }) {
  const [copied, setCopied] = useState(false);
  const [hovered, setHovered] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(cmd)
      .then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })
      .catch(() => {});
  };
  return (
    <button onClick={copy} type="button"
      onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, padding: 0, border: 'none',
        background: 'none', font: 'inherit', fontSize: FS.xs, cursor: 'pointer',
        color: copied ? C.successText : C.textMuted,
        textDecoration: hovered && !copied ? 'underline' : 'none',
        alignSelf: 'flex-start',
      }}>
      {copied
        ? <Check size={12} color={C.successText} strokeWidth={ICON_STROKE} />
        : <Copy size={12} color={C.textMuted} strokeWidth={ICON_STROKE} />}
      {copied ? 'Команда скопирована' : 'Команда входа для PowerShell'}
    </button>
  );
}

// Кнопка «Скопировать команду» (loginCommand) — единственное действие на вкладке:
// аккаунт без полноценного входа чинится именно ею
function CopyCommandButton({ cmd }: { cmd: string }) {
  const [copied, setCopied] = useState(false);
  const [hovered, setHovered] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(cmd)
      .then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })
      .catch(() => {});
  };
  const hot = hovered && !copied;
  return (
    <button onClick={copy} type="button"
      onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, padding: '3px 9px', borderRadius: R.md,
        border: `1px solid ${hot ? C.accent : C.border}`, background: C.bgWhite, fontSize: FS.xs,
        color: copied ? C.successText : C.textSecondary, cursor: 'pointer', font: 'inherit',
        transition: 'border-color .12s',
      }}>
      {copied
        ? <Check size={12} color={C.successText} strokeWidth={ICON_STROKE} />
        : <Copy size={12} color={hot ? C.accent : C.textSecondary} strokeWidth={ICON_STROKE} />}
      {copied ? 'Скопировано' : 'Скопировать команду'}
    </button>
  );
}

export function ProviderCard({ data, isMobile }: { data: ProviderCardData; isMobile?: boolean }) {
  const [open, setOpen] = useState(false);
  const [pressed, setPressed] = useState(false);
  const { name, color, state, isFree, retryAt, onRetry } = data;

  // Обратный отсчёт до авто-ретрая: тикает ежесекундно, только пока карточка в ошибке.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (state !== 'error' || !retryAt) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [state, retryAt]);

  // --- Загрузка: скелет (данные приходят разными запросами, мигающий текст мешал бы) ---
  if (state === 'loading') {
    return (
      <div style={{ ...cardBase, opacity: data.dim ? 0.55 : 1 }}>
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
    const remaining = retryAt ? Math.max(0, Math.ceil((retryAt - now) / 1000)) : null;
    return (
      <div style={{ ...cardBase, cursor: 'default', opacity: data.dim ? 0.55 : 1 }}>
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
      <div style={{ ...cardBase, cursor: 'default', opacity: data.dim ? 0.55 : 1 }}>
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
  const clickable = data.expandable;
  const pointerProps = clickable ? {
    onPointerDown: () => setPressed(true),
    onPointerUp: () => setPressed(false),
    onPointerLeave: () => setPressed(false),
    onPointerCancel: () => setPressed(false),
  } : {};
  const exhausted = data.hasExhausted;
  const hasCountTrend = !data.countWindows || data.countWindows.length === 0;

  return (
    <div
      onClick={clickable ? () => setOpen(v => !v) : undefined}
      role={clickable ? 'button' : undefined}
      {...pointerProps}
      style={{
        ...cardBase,
        background: pressed ? C.bgSelected : C.bgWhite,
        cursor: clickable ? 'pointer' : 'default',
        borderColor: exhausted ? C.warning : C.border,
        opacity: data.dim ? 0.55 : 1,
        // Карточка живёт в grid-колонке: без этого её содержимое (шапка с пилюлями,
        // ряды окон) диктует ширину и выносит колонку за пределы модалки
        minWidth: 0, overflow: 'hidden',
      }}
    >
      {/* .top — имя, пилюли, бейдж ротации, свежесть */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
        <Dot color={color} size={7} />
        <span style={{ ...nameStyle, flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
        {/* На мобиле шапка тесна: точка, имя, пилюля ротации, свежесть (пилюли тарифа — в раскрытии) */}
        {!isMobile && data.pills.map((p, i) => <Pill key={i} tone={p.tone}>{p.label}</Pill>)}
        {data.routingBadge && <RoutingPill tone={data.routingBadge.tone} label={data.routingBadge.label} />}
        <span style={{ marginLeft: 'auto' }} />
        {data.freshness && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.xs, color: data.freshness.textTone ?? C.textMuted, whiteSpace: 'nowrap' }}>
            <Dot color={data.freshness.dot} size={6} />
            {data.freshness.text}
          </span>
        )}
        {clickable && (
          <ChevronRight size={15} strokeWidth={ICON_STROKE} style={{
            flexShrink: 0, color: C.textMuted, transition: 'transform .15s', transform: open ? 'rotate(90deg)' : 'none',
          }} />
        )}
      </div>

      {/* .wins — percent-окна на поверхности */}
      {data.windows.length > 0 && (
        <div style={{ marginTop: 6, display: 'flex', flexDirection: 'column', gap: 5 }}>
          {data.windows.map((w, i) => <QuotaWindow key={i} w={w} labelWidth={data.labelWidth} />)}
        </div>
      )}

      {/* Здоровье FreeLLM — видное место на поверхности (провайдеры) */}
      {data.health && (data.health.platformsTotal ?? 0) > 0 && (
        <FreeLlmHealth health={data.health} />
      )}

      {/* Хинт-строка (подписки: сброс худшего окна + reason ротации) */}
      {data.hint && (
        <div style={{ marginTop: 6, fontSize: FS.xs, color: C.textMuted }}>{data.hint}</div>
      )}

      {/* Раскрытие: count-окна/тренд/кабинет (провайдеры) + тариф/порог/свежесть/команда (подписки) */}
      {open && clickable && (
        <div style={{ marginTop: 10, borderTop: `1px dashed ${C.dashed}`, paddingTop: 10, display: 'flex', flexDirection: 'column', gap: 10 }}>
          {data.tier && (
            // Строка тарифа + пилюли ограничений (Без Opus / Без 1M). На мобиле шапка тесна
            // и пилюли там не показываются — здесь их единственное место на этом устройстве.
            // Оборачиваем в flex, чтобы пилюли не переносились под длинный тариф, а ужимались
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', minWidth: 0 }}>
              <Pill>Тариф: {data.tier}</Pill>
              {data.expandedPills?.map((p, i) => <Pill key={i} tone={p.tone}>{p.label}</Pill>)}
            </div>
          )}
          {data.thresholdNote && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{data.thresholdNote}</div>}
          {data.freshnessDetail && <div style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45 }}>{data.freshnessDetail}</div>}
          {data.copyCommand ? (
            // Опрос сломан — команда нужна прямо сейчас: показываем её целиком
            <>
              <span style={{ display: 'block', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, background: C.bgInset, padding: '5px 8px', borderRadius: R.md, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{data.copyCommand}</span>
              <CopyCommandButton cmd={data.copyCommand} />
            </>
          ) : data.loginCommand ? (
            // Всё в порядке — команда про запас, одной строкой без моно-блока
            <CopyCommandLink cmd={data.loginCommand} />
          ) : null}
          {data.countWindows?.map((w, i) => (
            <div key={i}>
              <QuotaWindow w={w} labelWidth={data.labelWidth} />
              <div style={{ marginTop: 4, fontSize: FS.xs, color: C.textMuted }}>моментальный снимок, не расход</div>
            </div>
          ))}
          {hasCountTrend && data.windows.length > 0 && data.trend && data.trend.length >= 2 && (
            <>
              <div style={{ fontSize: FS.xs, color: C.textSecondary }}>Расход окна во времени</div>
              <Sparkline points={data.trend} />
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

      {/* Сброс исчерпанного окна (провайдеры) — когда именно отпустит */}
      {exhausted && data.exhaustedResetAt && (
        <div style={{ marginTop: 6, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          сброс {fmtReset(data.exhaustedResetAt ?? undefined)}
        </div>
      )}
    </div>
  );
}

// Здоровье FreeLLM — состояние пула на поверхности карточки: сегменты живых платформ
// и сводка трафика за 24ч. Тексты состояний согласованы с продактом: норма — «N из M
// живы» без алармов; деградация и «всё лежит» маркируются пилюлей в шапке (QuotasTab)
function FreeLlmHealth({ health }: { health: HealthSpec }) {
  const alive = health.platformsAlive ?? 0;
  const total = health.platformsTotal ?? 0;
  const rate = health.successRate;
  const req = health.requests24h;
  return (
    <>
      {/* Сегменты платформ: живые — C.info, остальные — C.track. Разметка общая с квотами:
          CountSegments даёт и плашки, и потолок MAX_SEGMENTS в одной точке */}
      <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{ width: 64, flexShrink: 0, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Платформы</span>
        <CountSegments used={alive} total={total} label="Живых" />
        <span style={{ flexShrink: 0, minWidth: 34, textAlign: 'right', fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textHeading }}>
          {alive} из {total} живы
        </span>
      </div>
      <div style={{ marginTop: 5, fontSize: FS.xs, color: C.textSecondary }}>
        {req != null
          ? <>за 24ч {req.toLocaleString('ru-RU')} запросов{rate != null ? ` · успех ${Math.round(rate)}%` : ''}</>
          : 'Статистика за 24 часа пока не пришла'}
      </div>
    </>
  );
}
