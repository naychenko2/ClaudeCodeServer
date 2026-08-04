import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Check, ChevronRight, Copy, Gauge, RotateCw } from 'lucide-react';
import { api } from '../lib/api';
import type {
  UsageResponse, FalAccountResponse, GlifAccountResponse, UsageSnapshot,
  SpendOverviewResponse, ProviderBalanceInfo,
} from '../types';
import { C, FONT, FS, R, SP, GROUP_COLORS, MODAL_W } from '../lib/design';
import {
  type RateWindow, windowLabel, fmtReset, latestPerWindow, latestWithUtilization,
  snapshotFreshnessLabel, overageLabel, seriesByWindow, worstWindow,
} from '../lib/rateLimit';
import { type RotationInfo, rotationBadgeState } from '../lib/rotation';
import { cliProviderKeys, providerCapsByKey, providerLabel } from '../lib/models';
import { SPEND_PERIODS, periodRange, spendQuery, fmtTok, fmtTurns, openSpend } from '../lib/spend';
import { MiniSegment, WidgetAction } from '../features/home/WidgetCard';
import { Modal } from './ui/Modal';
import { Button } from './ui/Button';
import { Dot } from './ui/Dot';
import { EmptyState } from './ui/EmptyState';
import { IconButton } from './ui/IconButton';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useIsMobile } from '../lib/breakpoints';

// Экран собран тремя полосами по ПРИРОДЕ ресурса, а не по провайдеру: квоты подписок,
// деньги, наш собственный расход. Квоты первые — в них работа упирается ежедневно,
// денежные балансы вторичны. Единицы разных типов физически не встречаются в одном
// ряду — доллары не стоят рядом с процентами, а проценты рядом с запросами.

const STALE_MS = 30 * 60 * 1000;
const LOW_MONEY = 1;      // «мало» на денежном балансе CLI-провайдера
const LOW_BALANCE = 5;    // «мало» у fal (доллары) и glif (кредиты)
const ROW_HIT = 44;       // тач-цель строки: раскрытие по тапу по всей строке

// Цвет источника — из палитры групп проектов; новых цветов экран не заводит
const SOURCE_COLOR: Record<string, string> = {
  glm: GROUP_COLORS[0],
  minimax: GROUP_COLORS[1],
  fal: GROUP_COLORS[2],
  alibabacloud: GROUP_COLORS[3],
  glif: GROUP_COLORS[4],
  deepseek: GROUP_COLORS[5],
  kimi: GROUP_COLORS[6],
};
function sourceColor(key: string): string {
  const known = SOURCE_COLOR[key];
  if (known) return known;
  let h = 0;
  for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) >>> 0;
  return GROUP_COLORS[h % GROUP_COLORS.length];
}

const money = (c: number) => (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
const credits = (v: number) => (Number.isInteger(v) ? v.toLocaleString('ru-RU') : v.toFixed(2));

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

const TOPUP_URL: Record<string, string> = {
  deepseek: 'https://platform.deepseek.com/top_up',
  openrouter: 'https://openrouter.ai/settings/credits',
};
const CABINET_URL: Record<string, string> = {
  glm: 'https://z.ai/manage-apikey/rate-limits',
  deepseek: 'https://platform.deepseek.com/usage',
  openrouter: 'https://openrouter.ai/activity',
};
// Ключ конфигурации источника — показывается в свёрнутой группе «Не подключены»
const CONFIG_KEY: Record<string, string> = {
  fal: 'Fal:ApiKey',
  glif: 'Glif:McpToken',
};
const configKey = (key: string) => CONFIG_KEY[key] ?? `LlmProviders:${key}:ApiKey`;

// === Атомы ===

// Тон шкалы квоты по ИЗРАСХОДОВАННОЙ доле: до 70% нейтрально-зелёный, дальше янтарь и красный
const barTone = (used: number) => (used >= 90 ? C.danger : used >= 70 ? C.warning : C.success);
const barTextTone = (used: number) =>
  used >= 90 ? C.dangerText : used >= 70 ? C.warningText : C.textHeading;

// Шкала расхода окна. dim — данные протухли: гасится ТОЛЬКО шкала, текст рядом остаётся читаемым
function Bar({ used, dim }: { used: number; dim?: boolean }) {
  return (
    <span style={{ display: 'block', height: 6, borderRadius: 3, background: C.track, overflow: 'hidden', opacity: dim ? 0.4 : 1 }}>
      <span style={{ display: 'block', width: `${Math.min(100, Math.max(2, used))}%`, height: '100%', background: barTone(used) }} />
    </span>
  );
}

// Свежесть данных источника: точка + возраст. Протухло (>30 мин) — янтарная точка и время снимка
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

// Заголовок полосы — липкий: в длинном списке иначе теряешь, в какой ты полосе.
// pad — верхний отступ скролл-контейнера модалки. Липнем на -pad (к верхней кромке
// карточки, а не к краю padding-box) и тем же pad изнутри закрываем полосу, через
// которую иначе просвечивал бы уезжающий контент. Этот же padding даёт зазор между
// полосами, поэтому у контейнера полос gap не нужен.
// flexWrap: у заголовка с контролами справа (селектор периода, ссылка) на мобильной
// ширине они переносятся под заголовок — перенос допустим, горизонтальный скролл нет.
function LaneHead({ title, pad, right }: { title: string; pad: number; right?: React.ReactNode }) {
  return (
    <div style={{
      position: 'sticky', top: -pad, zIndex: 1, background: C.bgMain,
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap',
      padding: `${pad}px 0 ${SP.sm}px`, marginBottom: SP.xs,
    }}>
      <span style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{title}</span>
      {right}
    </div>
  );
}

// Скелет строк вместо центрированного «Загрузка…»: данные приходят разными запросами,
// и один общий текст мигал бы на каждом ответе
function SkeletonRows({ rows = 2 }: { rows?: number }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} style={{ display: 'flex', alignItems: 'center', gap: SP.md, padding: `${SP.sm}px 0` }}>
          <span style={{ width: 24, height: 24, borderRadius: R.full, background: C.bgSelected, flexShrink: 0 }} />
          <span style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
            <span style={{ height: 11, width: `${35 + ((i * 23) % 40)}%`, borderRadius: R.sm, background: C.bgSelected }} />
            <span style={{ height: 8, width: '28%', borderRadius: R.sm, background: C.bgSelected }} />
          </span>
        </div>
      ))}
    </div>
  );
}

// «Не удалось получить» — приглушённо, без тревоги: следующий опрос идёт через минуту
function LoadError({ onRetry }: { onRetry: () => void }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, color: C.textMuted }}>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.base, color: C.textMuted }}>—</span>
      <span>не удалось получить, повторим через минуту</span>
      <IconButton size="xs" onClick={onRetry} title="Повторить сейчас">
        <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    </div>
  );
}

const HISTORY_EMPTY = 'история появится за пару часов: снимки раз в 5 минут';

function Sparkline({ points, color, height = 30 }: { points: { t: number; u: number }[]; color: string; height?: number }) {
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
      <polyline points={xy} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

// === Полоса «Деньги» (вторая сверху) ===

// Плитка денег: знак валюты приглушён, число моно нейтральным цветом (accent — только у действий)
function MoneyTile({ source }: { source: MoneySource }) {
  const low = source.amount !== null && source.amount < source.lowAt;
  return (
    <div style={{
      flex: '1 1 200px', minWidth: 180, maxWidth: 360,
      background: low ? C.dangerBg : C.bgCard,
      border: `1px solid ${low ? C.dangerBorder : C.border}`,
      borderRadius: R.xl, padding: `${SP.md}px ${SP.md + 2}px`,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.xs }}>
        <Dot color={source.color} size={9} />
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {source.name}
        </span>
        <Freshness asOf={source.asOf} />
      </div>
      {source.error ? (
        <LoadError onRetry={source.onRetry} />
      ) : source.amount === null ? (
        <SkeletonRows rows={1} />
      ) : (
        <>
          <div style={{ fontFamily: FONT.mono, fontSize: FS.h2, fontWeight: 700, color: low ? C.dangerText : C.textHeading, lineHeight: 1.15 }}>
            {source.unit === 'usd'
              ? <><span style={{ color: C.textMuted, fontWeight: 500 }}>$</span>{money(source.amount)}</>
              : <>{credits(source.amount)}<span style={{ fontSize: FS.sm, color: C.textMuted, fontWeight: 500 }}> кр.</span></>}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>баланс</div>
          {source.spend !== undefined && (
            <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.sm }}>
              {source.unit === 'usd' ? `$${money(source.spend)}` : `${credits(source.spend)} кр.`}
              <span style={{ fontSize: FS.xs, color: C.textMuted }}> {source.spendLabel}</span>
            </div>
          )}
          {source.history && (
            source.history.length >= 2
              ? <div style={{ marginTop: SP.sm }}><Sparkline points={source.history} color={C.textMuted} height={26} /></div>
              : <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5 }}>{HISTORY_EMPTY}</div>
          )}
          {source.note && (
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5 }}>{source.note}</div>
          )}
          {source.actionUrl && (
            <a href={source.actionUrl} target="_blank" rel="noopener noreferrer"
              style={{ display: 'inline-block', marginTop: SP.sm, color: C.accent, fontSize: FS.xs, fontWeight: 600, textDecoration: 'none' }}>
              {source.actionLabel}
            </a>
          )}
        </>
      )}
    </div>
  );
}

interface MoneySource {
  key: string;
  name: string;
  color: string;
  unit: 'usd' | 'credits';
  amount: number | null;       // null — ещё грузится
  lowAt: number;
  asOf?: string | null;
  spend?: number;
  spendLabel?: string;
  history?: { t: number; u: number }[];
  note?: string;
  actionUrl?: string;
  actionLabel?: string;
  error?: boolean;
  onRetry: () => void;
}

// === Полоса «Квоты подписок» (первая сверху) ===

// Окно квоты чипом. Число без единицы и без знаменателя крупно не показываем: у процентов
// носитель смысла — шкала, у запросов — знаменатель, поэтому форматы разные.
interface QuotaWindowView {
  label: string;
  used: number | null;      // израсходованная доля окна, 0..100 (null — процент неизвестен)
  value: string;            // «78%» либо «120 / 300»
  unitNote?: string;        // «запросов» — квота Alibaba меряется вызовами модели, не токенами
  hint?: string;            // «в пределах нормы» — расход так мал, что процента ещё нет
  reset?: string;
  resetsAt?: string;        // сырое время сброса — строка-вывод сравнивает исчерпанные окна по нему
  exhausted?: boolean;      // окно израсходовано целиком (100%): работа встала до сброса
  overage?: string;
}

function WindowChip({ w, dim }: { w: QuotaWindowView; dim?: boolean }) {
  return (
    // Окно со знаменателем («120 / 300 запросов») длиннее процентного — даём ему
    // больше базиса, иначе на мобиле подпись окна съедается многоточием
    <div style={{
      flex: w.unitNote ? '1 1 240px' : '1 1 156px', minWidth: w.unitNote ? 210 : 148, maxWidth: 320,
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: `${SP.sm - 1}px ${SP.md - 2}px`,
    }}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm, marginBottom: w.used === null ? 0 : 5 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
          <span style={{ fontSize: FS.xs, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{w.label}</span>
          {w.exhausted && (
            // Заливка danger + onAccent держит контраст в обеих темах; бейдж не сжимается,
            // подпись периода уступает ему многоточием
            <span style={{
              flexShrink: 0, padding: '1px 6px', borderRadius: R.sm, lineHeight: 1.5,
              background: C.danger, color: C.onAccent, fontSize: FS.xs, fontWeight: 600,
            }}>
              Исчерпано
            </span>
          )}
        </span>
        <span style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: w.used === null ? C.textMuted : barTextTone(w.used) }}>{w.value}</span>
          {w.unitNote && <span style={{ fontSize: FS.xs, color: C.textMuted }}> {w.unitNote}</span>}
        </span>
      </div>
      {w.used !== null && <Bar used={w.used} dim={dim} />}
      {w.hint && <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>{w.hint}</div>}
      {w.reset && (w.exhausted ? (
        // Исчерпанному окну главное — «когда отпустит»: сброс не мельче процента
        <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.dangerText, marginTop: SP.xs }}>сброс {w.reset}</div>
      ) : (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>сброс {w.reset}</div>
      ))}
      {w.overage && (
        <div style={{ fontSize: FS.xs, color: C.dangerText, marginTop: SP.xs }}>
          <AlertTriangle size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} style={{ verticalAlign: '-1px', marginRight: 3 }} />
          {w.overage}
        </div>
      )}
    </div>
  );
}

interface QuotaRowData {
  key: string;
  name: string;
  color: string;
  windows: QuotaWindowView[];
  asOf?: string | null;
  unavailable?: boolean;                    // провайдер не отдал квоту
  error?: boolean;
  loading?: boolean;
  cabinetUrl?: string;
  balanceNote?: string;                     // «баланс $2.10» — источник живёт и на деньгах, и на квоте
  trend?: { t: number; u: number }[];
  trendLabel?: string;
  claude?: {
    rotation?: RotationInfo;
    tier?: string;
    pollStatus?: string;
    loginCommand?: string | null;
    freshness?: string | null;
  };
  onRetry: () => void;
}

function QuotaRow({ row, open, onToggle }: { row: QuotaRowData; open: boolean; onToggle: () => void }) {
  const dim = isStale(row.asOf);
  const worst = row.windows.reduce<QuotaWindowView | null>(
    (best, w) => (w.used !== null && (best === null || w.used > (best.used ?? -1)) ? w : best), null);
  const needLogin = row.claude
    && (row.claude.pollStatus === 'unauthorized' || (row.windows.length > 0 && dim));
  // Строке без подробностей (не отдал квоту, не ответил) раскрывать нечего —
  // тогда она не кликается и шеврон не рисуем: пустая раскрытая коробка хуже её отсутствия
  const hasDetail = !!(row.claude || row.balanceNote || row.trend || row.cabinetUrl);

  return (
    <div style={{ borderBottom: `1px solid ${C.borderLight}` }}>
      <button type="button" onClick={hasDetail ? onToggle : undefined} aria-expanded={hasDetail ? open : undefined}
        disabled={!hasDetail}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minHeight: ROW_HIT,
          padding: `${SP.sm}px 0`, border: 'none', background: 'none',
          cursor: hasDetail ? 'pointer' : 'default', textAlign: 'left',
        }}>
        <Dot color={row.color} size={9} />
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {row.name}
        </span>
        <span style={{ flex: 1, minWidth: 0 }}>
          {worst && (
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>
              {' '}<span style={{ fontFamily: FONT.mono, fontWeight: 700, color: barTextTone(worst.used ?? 0) }}>{worst.value}</span>
              {' '}{worst.label.toLowerCase()}
            </span>
          )}
        </span>
        <Freshness asOf={row.asOf} />
        {hasDetail && (
          <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted}
            style={{ flexShrink: 0, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }} />
        )}
      </button>

      <div style={{ paddingBottom: SP.md }}>
        {row.loading ? <SkeletonRows rows={1} />
          : row.error ? <LoadError onRetry={row.onRetry} />
          : row.unavailable ? (
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
              Квота недоступна — провайдер не отдал данные. Расход за период посчитан по нашим замерам.
            </div>
          ) : row.windows.length === 0 ? (
            <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
              Данные о лимитах появятся в течение нескольких минут — они обновляются автоматически.
            </div>
          ) : (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
              {row.windows.map((w, i) => <WindowChip key={`${w.label}-${i}`} w={w} dim={dim} />)}
            </div>
          )}

        {open && hasDetail && (
          <div style={{ marginTop: SP.md, borderTop: `1px dashed ${C.border}`, paddingTop: SP.md }}>
            {row.claude?.rotation && <RotationBadge info={row.claude.rotation} />}
            {row.claude?.tier && <TierPill tier={row.claude.tier} />}
            {needLogin && <NeedLoginBanner loginCommand={row.claude?.loginCommand} />}
            {row.claude?.freshness && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginBottom: SP.sm }}>{row.claude.freshness}</div>
            )}
            {row.balanceNote && (
              <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.sm }}>{row.balanceNote}</div>
            )}
            {row.trend && !row.loading && (
              row.trend.length >= 2 ? (
                <>
                  <div style={{ fontSize: FS.xs, color: C.textMuted, marginBottom: SP.xs }}>{row.trendLabel}</div>
                  <Sparkline points={row.trend} color={C.textMuted} />
                </>
              ) : (
                <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>{HISTORY_EMPTY}</div>
              )
            )}
            {row.cabinetUrl && (
              <a href={row.cabinetUrl} target="_blank" rel="noopener noreferrer"
                style={{ display: 'inline-block', marginTop: SP.sm, color: C.accent, fontSize: FS.xs, fontWeight: 600, textDecoration: 'none' }}>
                кабинет {row.name} ↗
              </a>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

// Статус роутинга аккаунта: куда фактически идут новые чаты (только при пуле подписок).
// Четыре состояния (цель роутинга × в ротации) считает rotationBadgeState — там же и спилл:
// аккаунт перегружен, но принимает чаты, потому что свободных нет.
function RotationBadge({ info }: { info: RotationInfo }) {
  const s = rotationBadgeState(info);
  const warn = s.tone === 'warn';
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center', gap: 7, padding: '5px 11px', borderRadius: R.md,
      background: warn ? C.warningBg : C.bgWhite, border: `1px solid ${warn ? C.warning : C.border}`,
      marginBottom: SP.md, marginRight: SP.sm, fontFamily: FONT.sans, fontSize: FS.sm,
    }}>
      <Dot color={warn ? C.warning : C.success} size={7} />
      <span style={{ fontWeight: 600, color: warn ? C.warningText : C.textHeading }}>{s.label}</span>
      <span style={{ color: C.textMuted }}>{s.reason}</span>
    </div>
  );
}

function TierPill({ tier }: { tier: string }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 5, padding: '5px 11px', borderRadius: R.md,
      background: C.bgWhite, border: `1px solid ${C.border}`, marginBottom: SP.md,
      fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
    }}>
      Тариф: {tier}
    </span>
  );
}

// Плашка «точные проценты недоступны»: поллер получает 401/403 (setup-токен) либо
// свежих (< 30 мин) снимков с процентом просто нет — устаревшие цифры не выдаём за точные.
// Когда бэкенд знает готовую команду входа в профиль аккаунта (loginCommand != null),
// показываем её моноширинным блоком с кнопкой копирования — паттерн как в чате
// (кнопка меняет текст на «Скопировано» на 1.5 с).
function NeedLoginBanner({ loginCommand }: { loginCommand?: string | null }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    if (!loginCommand) return;
    navigator.clipboard?.writeText(loginCommand).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); }).catch(() => {});
  };
  return (
    <div style={{ fontSize: FS.sm, color: C.warningText, background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: R.md, padding: '6px 10px', marginBottom: SP.md, lineHeight: 1.5 }}>
      Точные проценты недоступны: аккаунт работает на setup-токене, а API лимитов принимает
      только полноценный вход. Выполните <code style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>claude login</code> в профиле подписки.
      {loginCommand && (
        <>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm }}>
            <code style={{
              flex: 1, minWidth: 0, overflowX: 'auto', whiteSpace: 'nowrap', fontFamily: FONT.mono, fontSize: FS.xs,
              color: C.textHeading, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '6px 9px',
            }}>
              {loginCommand}
            </code>
            <Button variant="ghostFilled" size="sm" onClick={copy} title="Скопировать команду входа"
              leftIcon={copied
                ? <Check size={ICON_SIZE.xs} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
                : <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
              style={{ flexShrink: 0, minHeight: 30, padding: '4px 11px', fontSize: FS.sm, ...(copied ? { color: C.successText } : {}) }}>
              {copied ? 'Скопировано' : 'Скопировать'}
            </Button>
          </div>
          <div style={{ fontSize: FS.xs, marginTop: SP.sm - 2 }}>
            Выполни на машине сервера и войди в нужный аккаунт — данные обновятся в течение пары минут.
          </div>
        </>
      )}
    </div>
  );
}

// === Полоса 3. Наш расход ===
// Считается по нашим замерам (Spend Analytics), независимо от того, отвечает ли API
// провайдера: у источника без квоты и без баланса это единственная правда о расходе.
function SpendLane({ period, setPeriod, isMobile, pad, onClose }: { period: string; setPeriod: (p: string) => void; isMobile: boolean; pad: number; onClose: () => void }) {
  const [spend, setSpend] = useState<SpendOverviewResponse | null>(null);
  const [error, setError] = useState(false);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс spend/error перед новым запросом при смене периода
    setSpend(null);
    setError(false);
    const { from, to } = periodRange(period);
    api.spend.overview(spendQuery({ from, to }))
      .then(d => { if (!cancelled) setSpend(d); })
      .catch(() => { if (!cancelled) setError(true); });
    return () => { cancelled = true; };
  }, [period, tick]);

  const rows = spend?.cards.providers ?? [];
  const maxTok = rows.reduce((m, r) => Math.max(m, r.tokens.total), 0) || 1;

  // Провал в разрезы: экран — модалка, поэтому перед переходом закрываем её,
  // иначе аналитика откроется под ней. openSpend() — штатный вход в раздел
  // (внутри идёт через lib/nav.ts и переключает таб хаба)
  const goAnalytics = () => { onClose(); openSpend(); };

  return (
    <div>
      <LaneHead title="Наш расход" pad={pad} right={
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap' }}>
          <MiniSegment value={period} options={SPEND_PERIODS.map(p => ({ value: p.key, label: p.label }))} onChange={setPeriod} />
          <WidgetAction label="Подробная аналитика →" onClick={goAnalytics} />
        </div>
      } />
      {error ? <LoadError onRetry={() => setTick(t => t + 1)} />
        : spend === null ? <SkeletonRows rows={3} />
        : rows.length === 0 ? (
          <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>За период ходов не было.</div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {rows.map(r => (
              <div key={r.key} style={{ display: 'flex', alignItems: 'center', gap: SP.md, minHeight: ROW_HIT, padding: `${SP.sm}px 0`, borderBottom: `1px solid ${C.borderLight}` }}>
                <Dot color={sourceColor(r.key)} size={9} />
                <span style={{ flex: isMobile ? 1 : '0 0 130px', minWidth: 0, fontSize: FS.base, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {r.name ?? providerLabel(r.key)}
                </span>
                {/* Полоса доли — только на десктопе: на мобиле её место нужнее числу */}
                {!isMobile && (
                  <span style={{ flex: 1, minWidth: 40, height: 7, background: C.track, borderRadius: 4, overflow: 'hidden' }}>
                    <span style={{ display: 'block', width: `${Math.max(3, Math.round((r.tokens.total / maxTok) * 100))}%`, height: '100%', background: C.textMuted }} />
                  </span>
                )}
                <span style={{ flexShrink: 0, textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.textHeading }}>{fmtTok(r.tokens.total)}</span>
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}> токенов · {fmtTurns(r.turns)}</span>
                </span>
              </div>
            ))}
          </div>
        )}
    </div>
  );
}

// === Свёрнутая группа «Не подключены» ===
function NotConnected({ items }: { items: { key: string; name: string }[] }) {
  const [open, setOpen] = useState(false);
  if (items.length === 0) return null;
  return (
    <div>
      <button type="button" onClick={() => setOpen(o => !o)} aria-expanded={open}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minHeight: ROW_HIT,
          border: 'none', background: 'none', cursor: 'pointer', padding: `${SP.sm}px 0`,
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, textAlign: 'left',
        }}>
        <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }} />
        Не подключены ({items.length})
      </button>
      {open && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, paddingBottom: SP.sm }}>
          {items.map(i => (
            <div key={i.key} style={{ flex: '1 1 200px', minWidth: 180, background: C.bgInset, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: `${SP.sm + 2}px ${SP.md}px` }}>
              <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, marginBottom: SP.xxs }}>{i.name}</div>
              <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis' }}>{configKey(i.key)}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// === Экран ===

type ProviderUsage = {
  balance: ProviderBalanceInfo | null;
  snapshots: { timestamp: string; balance: number; currency: string }[];
};

// Окна квоты CLI-провайдера → вид для чипа. ВАЖНО: в контракте percent — это ОСТАТОК
// окна (ProviderBalanceService считает remaining), а экран говорит на языке расхода,
// как и окна Claude (utilization). Поэтому переводим остаток в израсходованное.
function providerWindows(b: ProviderBalanceInfo): QuotaWindowView[] {
  return (b.windows ?? []).map(w => {
    const reset = w.resetsAt ? fmtReset(w.resetsAt) : '';
    if (w.unit === 'count') {
      // Квота меряется числом вызовов модели: показываем со знаменателем и словом «запросов»
      const m = /^\s*(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s*$/.exec(w.value);
      const used = m ? Math.round((parseFloat(m[1]) / parseFloat(m[2])) * 100) : null;
      return { label: w.label, used, value: w.value, unitNote: 'запросов', reset, resetsAt: w.resetsAt ?? undefined, exhausted: used !== null && used >= 100 };
    }
    const remaining = parseFloat(w.value);
    const used = isNaN(remaining) ? null : Math.round(Math.min(100, Math.max(0, 100 - remaining)));
    return { label: w.label, used, value: used === null ? '—' : `${used}%`, reset, resetsAt: w.resetsAt ?? undefined, exhausted: used !== null && used >= 100 };
  });
}

// Окна подписки Claude → вид для чипа. utilization уже израсходованная доля;
// снимка с процентом может не быть вовсе — при низком расходе API шлёт только статус.
function claudeWindows(windows: RateWindow[]): QuotaWindowView[] {
  return windows.map(w => {
    const used = w.hasUtil ? w.pct : null;
    return {
      label: windowLabel(w.limitType),
      used,
      value: w.hasUtil ? `${w.pct}%` : '—',
      hint: w.hasUtil ? undefined : 'в пределах нормы',
      reset: fmtReset(w.resetsAt),
      resetsAt: w.resetsAt,
      exhausted: used !== null && used >= 100,
      overage: w.isUsingOverage
        ? 'идёт перерасход'
        : (w.overageStatus && w.overageStatus !== 'allowed' ? overageLabel(w.overageStatus) : undefined),
    };
  });
}

export function UsageScreen({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [fal, setFal] = useState<FalAccountResponse | null | undefined>(undefined);
  const [glif, setGlif] = useState<GlifAccountResponse | null | undefined>(undefined);
  const [provData, setProvData] = useState<Record<string, ProviderUsage | null | undefined>>({});
  const [spendPeriod, setSpendPeriod] = useState('week');
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [tick, setTick] = useState(0);

  const providerKeys = cliProviderKeys();
  const balanceKeys = providerKeys.filter(k => {
    const caps = providerCapsByKey(k);
    return caps.hasBalance && caps.configured !== false;
  });

  const loadProvider = useCallback((key: string) => {
    setProvData(prev => ({ ...prev, [key]: undefined }));
    api.providers.usage(key)
      .then(d => setProvData(prev => ({ ...prev, [key]: d })))
      .catch(() => setProvData(prev => ({ ...prev, [key]: null })));
  }, []);

  useEffect(() => {
    let c = false;
    api.usage.get().then(d => { if (!c) setUsage(d); }).catch(() => { if (!c) setUsage({ snapshots: [] }); });
    api.fal.account(7).then(d => { if (!c) setFal(d); }).catch(() => { if (!c) setFal(null); });
    api.glif.account().then(d => { if (!c) setGlif(d); }).catch(() => { if (!c) setGlif(null); });
    // eslint-disable-next-line react-hooks/set-state-in-effect -- параллельная инициализация аккаунтов/балансов при монтировании
    for (const key of balanceKeys) loadProvider(key);
    return () => { c = true; };
    // balanceKeys вычисляется из каталога моделей и на время жизни экрана постоянен;
    // tick — ручной повтор после ошибки
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick, loadProvider]);

  // Пока экран открыт — раз в минуту подтягиваем свежие снимки поллера и перезапрашиваем
  // источники, которые в прошлый раз не ответили (об этом и говорит текст ошибки)
  useEffect(() => {
    const id = setInterval(() => {
      api.usage.get().then(setUsage).catch(() => {});
      setProvData(prev => {
        for (const [key, v] of Object.entries(prev)) if (v === null) loadProvider(key);
        return prev;
      });
    }, 60_000);
    return () => clearInterval(id);
  }, [loadProvider]);

  const toggle = (key: string) => setExpanded(prev => ({ ...prev, [key]: !prev[key] }));

  // Снимки сторонних провайдеров (glm/deepseek) лежат под их ключами — из сводки Claude
  // исключаем, чтобы лимит чужого эндпоинта не выглядел клодовским
  const provSnapKeys = new Set(Object.keys(usage?.providers ?? {}));
  const claudeSnaps = (usage?.snapshots ?? []).filter(s => !s.subscriptionKey || !provSnapKeys.has(s.subscriptionKey));

  const subKeys = usage?.subscriptions ? Object.keys(usage.subscriptions) : [];
  const rotationOf = (key: string): RotationInfo | undefined => {
    const s = usage?.subscriptions?.[key];
    if (!s || s.inRotation === undefined) return undefined;
    const target = usage?.routingTarget;
    return {
      inRotation: s.inRotation, utilization: s.utilization, threshold: usage?.rotationThreshold, exhausted: s.exhausted,
      isTarget: target === key,
      targetName: target ? (usage?.subscriptions?.[target]?.name ?? target) : undefined,
      freeAvailable: Object.values(usage?.subscriptions ?? {}).some(x => x.inRotation === true),
    };
  };

  // --- Полоса «Квоты подписок»: подписки Claude + провайдеры с окнами квоты ---
  const quotaRows: QuotaRowData[] = [];
  const claudeAccounts: { key: string; name: string; snapshots: UsageSnapshot[] | undefined }[] =
    subKeys.length > 0
      ? subKeys.map(k => ({ key: k, name: usage!.subscriptions![k].name ?? (k === 'claude' ? 'Claude' : k), snapshots: usage!.subscriptions![k].snapshots }))
      : [{ key: 'claude', name: 'Claude', snapshots: usage ? claudeSnaps : undefined }];

  for (const acc of claudeAccounts) {
    const snaps = acc.snapshots ?? [];
    const windows = latestPerWindow(snaps);
    const series = seriesByWindow(snaps);
    const latestUtil = latestWithUtilization(snaps);
    const worst = worstWindow(windows);
    quotaRows.push({
      key: `sub:${acc.key}`,
      name: acc.name,
      color: sourceColor(acc.key),
      windows: claudeWindows(windows),
      asOf: latestUtil?.timestamp ?? null,
      loading: usage === null,
      trend: worst ? (series[worst.limitType] ?? []) : [],
      trendLabel: worst ? `Тренд · ${windowLabel(worst.limitType)}` : undefined,
      claude: {
        rotation: rotationOf(acc.key),
        tier: usage?.subscriptions?.[acc.key]?.tier,
        pollStatus: usage?.pollStatuses?.[acc.key],
        loginCommand: usage?.subscriptions?.[acc.key]?.loginCommand,
        freshness: latestUtil ? snapshotFreshnessLabel(latestUtil.source, latestUtil.timestamp) : null,
      },
      onRetry: () => setTick(t => t + 1),
    });
  }

  const moneySources: MoneySource[] = [];

  for (const key of balanceKeys) {
    const data = provData[key];
    const name = providerLabel(key);
    const caps = providerCapsByKey(key);
    const bal = data?.balance ?? null;
    const num = bal ? parseFloat(bal.totalBalance) : NaN;
    const isQuota = bal?.currency === '%' || (bal?.windows?.length ?? 0) > 0;

    if (data === null) {
      // Ответа нет — тип источника (деньги или квота) неизвестен, поэтому строка живёт
      // в полосе квот: там строчная раскладка, и «—» с повтором не ломает ряд плиток
      quotaRows.push({ key, name, color: sourceColor(key), windows: [], error: true, onRetry: () => loadProvider(key) });
      continue;
    }
    if (data === undefined || !bal) {
      quotaRows.push({ key, name, color: sourceColor(key), windows: [], loading: true, onRetry: () => loadProvider(key) });
      continue;
    }
    if (isQuota) {
      const hist = (data.snapshots ?? []).map(s => ({ t: new Date(s.timestamp).getTime(), u: Math.max(0, Math.min(1, (100 - s.balance) / 100)) }));
      quotaRows.push({
        key, name, color: sourceColor(key),
        windows: providerWindows(bal),
        asOf: bal.asOf ?? null,
        cabinetUrl: CABINET_URL[key],
        trend: hist,
        trendLabel: 'Расход окна во времени',
        onRetry: () => loadProvider(key),
      });
      // Источник с квотой И деньгами показывается в обеих полосах: в «Деньгах» — сумма,
      // здесь — окна. Смешивать проценты и доллары в одном ряду нельзя.
      continue;
    }
    if (!caps.hasBalance) continue;
    moneySources.push({
      key, name, color: sourceColor(key), unit: 'usd',
      amount: isNaN(num) ? null : num,
      lowAt: LOW_MONEY,
      asOf: bal.asOf ?? null,
      history: (data.snapshots ?? []).length
        ? data.snapshots.map(s => ({ t: new Date(s.timestamp).getTime(), u: s.balance / Math.max(...data.snapshots.map(x => x.balance), num || 0.0001) }))
        : [],
      actionUrl: TOPUP_URL[key] ?? CABINET_URL[key],
      actionLabel: TOPUP_URL[key] ? 'пополнить ↗' : `кабинет ${name} ↗`,
      onRetry: () => loadProvider(key),
    });
  }

  // Провайдер настроен, но квоту не отдаёт (Alibaba Cloud): строка в полосе остаётся —
  // пояснение стоит там, где его ищут; расход такого источника виден в «Нашем расходе»
  for (const key of providerKeys) {
    const caps = providerCapsByKey(key);
    if (caps.hasBalance || caps.configured === false) continue;
    quotaRows.push({ key, name: providerLabel(key), color: sourceColor(key), windows: [], unavailable: true, onRetry: () => {} });
  }

  // --- fal.ai и glif ---
  if (fal !== undefined && fal?.enabled !== false) {
    moneySources.push({
      key: 'fal', name: 'fal.ai', color: sourceColor('fal'), unit: 'usd',
      amount: fal === null ? null : (typeof fal.balance === 'number' ? fal.balance : null),
      lowAt: LOW_BALANCE,
      spend: fal?.usage?.total,
      spendLabel: `расход за ${fal?.usage?.days ?? 7} дней`,
      actionUrl: 'https://fal.ai/dashboard/billing',
      actionLabel: 'пополнить ↗',
      error: fal === null,
      onRetry: () => setTick(t => t + 1),
    });
  }
  const glifSource: MoneySource | null = (glif !== undefined && glif?.enabled !== false) ? {
    key: 'glif', name: 'glif', color: sourceColor('glif'), unit: 'credits',
    amount: glif === null ? null : (typeof glif.balance === 'number' ? glif.balance : null),
    lowAt: LOW_BALANCE,
    spend: glif?.spend?.last7d,
    spendLabel: 'расход за 7 дней',
    actionUrl: 'https://glif.app',
    actionLabel: 'кабинет ↗',
    error: glif === null,
    onRetry: () => setTick(t => t + 1),
  } : null;

  // --- Не подключены ---
  const notConnected: { key: string; name: string }[] = providerKeys
    .filter(k => providerCapsByKey(k).configured === false)
    .map(k => ({ key: k, name: providerLabel(k) }));
  if (fal?.enabled === false) notConnected.push({ key: 'fal', name: 'fal.ai' });
  if (glif?.enabled === false) notConnected.push({ key: 'glif', name: 'glif' });

  // --- Строка-вывод: что упрётся в потолок первым ---
  // Только фактический потолок и ближайший сброс, никакой экстраполяции «хватит на N дней»:
  // на снимках раз в 5 минут прогноз врал бы, а неверный прогноз хуже отсутствующего.
  // Исчерпанное окно (100%) важнее любого процента: работа уже встала, и вопрос только
  // «когда отпустит» — среди исчерпанных берём ранний сброс, а не максимальный расход.
  const resetTime = (w: QuotaWindowView) => {
    const t = new Date(w.resetsAt ?? '').getTime();
    return isNaN(t) ? Infinity : t;
  };
  let headline: { name: string; window: QuotaWindowView; exhausted: boolean } | null = null;
  for (const row of quotaRows) {
    for (const w of row.windows) {
      if (w.used === null) continue;
      const ex = !!w.exhausted;
      if (!headline || (ex && !headline.exhausted)) { headline = { name: row.name, window: w, exhausted: ex }; continue; }
      if (ex !== headline.exhausted) continue;
      if (ex ? resetTime(w) < resetTime(headline.window) : w.used > (headline.window.used ?? -1))
        headline = { name: row.name, window: w, exhausted: ex };
    }
  }

  // Верхний отступ скролл-контейнера Modal — от него считается прилипание заголовков полос
  const pad = isMobile ? SP.sm : 28;
  const loading = usage === null && fal === undefined && glif === undefined;
  const nothing = !loading
    && moneySources.length === 0 && glifSource === null
    && quotaRows.every(r => r.windows.length === 0 && !r.unavailable && !r.loading);

  return (
    <Modal
      title="Использование"
      onClose={onClose}
      width={MODAL_W.wide}
      cardStyle={isMobile ? undefined : { height: 'min(86vh, 900px)' }}
    >
      {headline && (headline.exhausted ? (
        // Исчерпанному окну процент не нужен: главное — когда сброс, он и несёт смысл строки
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, marginTop: -SP.sm }}>
          <span style={{ fontWeight: 600, color: C.dangerText }}>Предел достигнут:</span>{' '}
          <b style={{ color: C.textHeading }}>{headline.name}</b> · {headline.window.label}
          {headline.window.reset && (
            <> — сброс{' '}<span style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.dangerText }}>{headline.window.reset}</span></>
          )}
        </div>
      ) : (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, marginTop: -SP.sm }}>
          Ближе всего к пределу: <b style={{ color: C.textHeading }}>{headline.name}</b> · {headline.window.label} — израсходовано{' '}
          <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: barTextTone(headline.window.used ?? 0) }}>{headline.window.value}</span>
          {headline.window.unitNote ? ` ${headline.window.unitNote}` : ''}
          {headline.window.reset ? `, сброс ${headline.window.reset}` : ''}
        </div>
      ))}

      {nothing ? (
        <EmptyState
          icon={<Gauge size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Ни один провайдер не подключён"
          subtitle="Добавьте API-ключ в appsettings.Local.json — баланс, квоты и расход появятся здесь."
        />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          <div>
            <LaneHead title="Квоты подписок · израсходовано" pad={pad} />
            {quotaRows.map(row => (
              <QuotaRow key={row.key} row={row} open={!!expanded[row.key]} onToggle={() => toggle(row.key)} />
            ))}
          </div>

          <div>
            <LaneHead title="Деньги" pad={pad} />
            {loading ? <SkeletonRows rows={2} /> : (
              <>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
                  {moneySources.map(s => <MoneyTile key={s.key} source={s} />)}
                  {moneySources.length === 0 && (
                    <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>Денежных балансов нет — все источники живут на квотах подписок.</div>
                  )}
                </div>
                {glifSource && (
                  // Кредиты — отдельная группа, а не «отвалившаяся» плитка: другая единица,
                  // и путать её с долларами нельзя. Этикетка объявляет единицу ДО числа,
                  // сноска «не деньги» поднята из плитки к этикетке — читается сразу.
                  <div style={{ marginTop: SP.xl }}>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, marginBottom: SP.sm }}>
                      <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>Кредиты</span>
                      <span style={{ fontSize: FS.xs, color: C.textMuted }}>не деньги, курс — в кабинете</span>
                    </div>
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
                      <MoneyTile source={glifSource} />
                    </div>
                  </div>
                )}
              </>
            )}
          </div>

          <SpendLane period={spendPeriod} setPeriod={setSpendPeriod} isMobile={isMobile} pad={pad} onClose={onClose} />

          <NotConnected items={notConnected} />
        </div>
      )}
    </Modal>
  );
}
