// Раздел «Аналитика токенов» (Spend Analytics v2) — полноэкранный оверлей.
// Двухуровневый: «Обзор» (сводные карточки) ↔ «Анализ» (pivot-дерево + паспорт).
// Клик по элементу обзора открывает анализ с применённым контекстом. Все суммы —
// в токенах, $ не показывается нигде; fal.ai — счётчик генераций.
import { useCallback, useEffect, useMemo, useState } from 'react';
import { X } from 'lucide-react';
import type { SpendOverviewResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, ISLAND, R, SHADOW, Z, CONTENT_MAX_W } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { MiniSegment } from '../home/WidgetCard';
import {
  SPEND_PERIODS, periodRange, fmtDate, spendQuery,
  type SpendFilter, type SpendLevel, type SpendOpenContext,
} from '../../lib/spend';
import { SkelBlock, LoadError, WindowBadge, Chip } from './spendUi';
import { SpendOverview } from './SpendOverview';
import { SpendAnalysis } from './SpendAnalysis';

// Состояние раздела; living на время жизни оверлея
export interface SpendState {
  screen: 'overview' | 'analysis';
  scope: 'mine' | 'all';
  period: string;                 // ключ SPEND_PERIODS
  day: string | null;             // срез одного дня (клик по бару обзора)
  filters: SpendFilter[];
  levels: SpendLevel[] | null;    // кастомная цепочка уровней; null → от пресета
  preset: string;
  selKey: string | null;          // выбранный узел дерева ('dim:key|…') или 'turn:{id}'
}

function initialState(ctx: SpendOpenContext, isAdmin: boolean): SpendState {
  return {
    screen: ctx.screen ?? 'overview',
    scope: isAdmin ? 'all' : 'mine',
    period: 'month',
    day: ctx.day ?? null,
    filters: ctx.filters ?? [],
    levels: null,
    preset: ctx.preset ?? 'who',
    selKey: ctx.turnId ? `turn:${ctx.turnId}` : null,
  };
}

export function SpendScreen({ ctx, isAdmin, onClose, embedded }: {
  ctx: SpendOpenContext;
  isAdmin: boolean;
  onClose: () => void;
  // true — рендер как содержимое страницы-вкладки (без полноэкранного fixed-overlay):
  // шапка хаба остаётся сверху, раздел занимает оставшееся место
  embedded?: boolean;
}) {
  const isMobile = useIsMobile();
  const [st, setSt] = useState<SpendState>(() => {
    const s = initialState(ctx, isAdmin);
    // «Разложить →» из контекста открытия: разрез — первым уровнем дефолтной цепочки
    if (ctx.pivotDim) {
      const base: SpendLevel[] = isAdmin ? ['user', 'project', 'chat', 'turn'] : ['project', 'chat', 'turn'];
      s.levels = [ctx.pivotDim, ...base.filter(d => d !== ctx.pivotDim)];
      s.screen = 'analysis';
    }
    return s;
  });
  const patch = useCallback((p: Partial<SpendState>) => setSt(prev => ({ ...prev, ...p })), []);

  // Период запроса: срез дня побеждает выбранный период
  const range = useMemo(
    () => (st.day ? { from: st.day, to: st.day } : periodRange(st.period)),
    [st.day, st.period],
  );

  // Обзор среза: и экран «Обзор», и «Итого по срезу» анализа, и бейдж окна детализации
  const [overview, setOverview] = useState<SpendOverviewResponse | null>(null);
  const [ovError, setOvError] = useState(false);
  const [ovTick, setOvTick] = useState(0);
  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс overview/ошибки перед запросом при смене диапазона
    setOverview(null);
    setOvError(false);
    api.spend.overview(spendQuery({ from: range.from, to: range.to, scope: st.scope, filters: st.filters }))
      .then(d => { if (!cancelled) setOverview(d); })
      .catch(() => { if (!cancelled) setOvError(true); });
    return () => { cancelled = true; };
  }, [range.from, range.to, st.scope, st.filters, ovTick]);

  // Esc закрывает раздел
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const showUsers = isAdmin && st.scope === 'all';

  // Переходы Обзор → Анализ с переносом контекста клика
  const openAnalysis = useCallback((patchCtx: {
    filter?: SpendFilter; preset?: string; pivotDim?: string; day?: string; turnId?: string;
  }) => {
    setSt(prev => {
      const next: SpendState = { ...prev, screen: 'analysis', selKey: null };
      if (patchCtx.filter) {
        next.filters = [...prev.filters.filter(f => f.dim !== patchCtx.filter!.dim), patchCtx.filter];
      }
      if (patchCtx.day) next.day = patchCtx.day;
      if (patchCtx.preset) { next.preset = patchCtx.preset; next.levels = null; }
      if (patchCtx.pivotDim) {
        const base: SpendLevel[] = showUsers ? ['user', 'project', 'chat', 'turn'] : ['project', 'chat', 'turn'];
        next.levels = [patchCtx.pivotDim as SpendLevel, ...base.filter(d => d !== patchCtx.pivotDim)];
      }
      if (patchCtx.turnId) next.selKey = `turn:${patchCtx.turnId}`;
      return next;
    });
  }, [showUsers]);

  const header = (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 10, minHeight: 48, padding: '8px 16px',
      background: C.bgInset, borderBottom: `1px solid ${C.borderLight}`, flexWrap: 'wrap',
      borderRadius: isMobile ? 0 : `${ISLAND.radius}px ${ISLAND.radius}px 0 0`,
    }}>
      <span style={{ fontFamily: FONT.serif, fontSize: isMobile ? 16 : 17, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap' }}>
        Аналитика токенов
      </span>
      <MiniSegment
        value={st.screen}
        options={[
          { value: 'overview' as const, label: 'Обзор' },
          { value: 'analysis' as const, label: st.filters.length ? `Анализ · ${st.filters.length}` : 'Анализ' },
        ]}
        onChange={v => patch({ screen: v })}
      />
      {!isMobile && overview && <WindowBadge days={overview.detailDays} />}
      <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        {isAdmin && (
          <MiniSegment
            value={st.scope}
            options={[{ value: 'mine' as const, label: 'Мои' }, { value: 'all' as const, label: 'Все' }]}
            onChange={v => {
              // Уход из «Все»: уровень и фильтр «пользователь» вне admin-скоупа невалидны
              setSt(prev => ({
                ...prev, scope: v, levels: null, selKey: null,
                filters: v === 'all' ? prev.filters : prev.filters.filter(f => f.dim !== 'user'),
              }));
            }}
          />
        )}
        <MiniSegment
          value={st.day ? '' : st.period}
          options={SPEND_PERIODS.map(p => ({ value: p.key, label: isMobile && p.key === 'q' ? '90 д' : p.label }))}
          onChange={v => patch({ period: v, day: null, selKey: null })}
        />
        <button
          onClick={onClose}
          title="Закрыть (Esc)"
          style={{
            border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted,
            padding: 4, borderRadius: R.md, display: 'flex', alignItems: 'center', flexShrink: 0,
          }}
        >
          <X size={18} strokeWidth={2} />
        </button>
      </div>
      {/* Срез одного дня — чип-напоминание с крестиком прямо в шапке */}
      {st.day && (
        <Chip filter onClick={() => patch({ day: null, selKey: null })} title="Срез одного дня — нажмите, чтобы вернуть период">
          День: {fmtDate(st.day)} ×
        </Chip>
      )}
    </div>
  );

  const body = st.screen === 'overview'
    ? (ovError
        ? <LoadError onRetry={() => setOvTick(t => t + 1)} />
        : overview
          ? <SpendOverview data={overview} showUsers={showUsers} isMobile={isMobile} filtersActive={st.filters.length > 0 || !!st.day}
              onOpen={openAnalysis} onClearFilters={() => patch({ filters: [], day: null, selKey: null })} onClose={onClose} />
          : <SkelBlock />)
    : (
      <SpendAnalysis
        st={st} patch={patch} range={range} showUsers={showUsers} isMobile={isMobile}
        overview={overview} overviewError={ovError} onRetryOverview={() => setOvTick(t => t + 1)}
        onCloseScreen={onClose}
      />
    );

  return (
    <div style={embedded
      // Страница-вкладка: шапка хаба над нами, занимаем остаток колонки
      ? { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }
      // Полноэкранный overlay (на случай вызова вне хаба)
      : { position: 'fixed', inset: 0, zIndex: Z.modal, background: C.bgMain,
          display: 'flex', flexDirection: 'column', overflow: 'hidden' }
    }>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? 0 : 16 }}>
        <div style={{
          maxWidth: CONTENT_MAX_W, margin: '0 auto',
          background: C.bgPanel,
          border: isMobile ? 'none' : `1px solid ${C.borderLight}`,
          borderRadius: isMobile ? 0 : ISLAND.radius,
          boxShadow: isMobile ? 'none' : SHADOW.island,
          minHeight: isMobile ? '100dvh' : undefined,
        }}>
          {header}
          {body}
        </div>
      </div>
    </div>
  );
}
