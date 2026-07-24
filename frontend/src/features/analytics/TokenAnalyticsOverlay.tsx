import { useCallback, useState } from 'react';
import { X, BarChart3, List, ChevronRight } from 'lucide-react';
import { C, FONT, SHADOW } from '../../lib/design';
import { usePeriod, useAggregate, useDaily, useByProject, useByModel, useEntries, useAdminAggregate, useBoundary, fmtMoney, fmtTokens, fmtDate, PRESET_LABELS, type PeriodPreset } from './analytics';

const MONEY = C.accent;
const STYLES = {
  overlay: {
    position: 'fixed' as const, inset: 0, zIndex: 1000,
    background: 'rgba(0,0,0,.3)', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16,
  },
  card: {
    background: C.bgWhite, borderRadius: 16,
    width: '100%', maxWidth: 960, height: '85vh', maxHeight: 800,
    display: 'flex', flexDirection: 'column' as const,
    boxShadow: '0 8px 40px rgba(0,0,0,.12)', overflow: 'hidden',
  },
  header: {
    display: 'flex', alignItems: 'center', justifyContent: 'space-between',
    padding: '16px 20px', borderBottom: `1px solid ${C.border}`,
  },
  title: { font: `700 18px/1.2 ${FONT.heading}` as any, color: C.textHeading },
  body: { flex: 1, overflow: 'auto', padding: '16px 20px' },
  kpi: { background: C.bgPanel, borderRadius: 10, padding: '12px 14px', border: `1px solid ${C.border}`, minWidth: 0 },
  kpiLabel: { fontSize: 10, textTransform: 'uppercase' as const, letterSpacing: .5, color: C.textMuted, marginBottom: 3 },
  kpiValue: { font: `600 22px/1 ${FONT.mono}` as any, color: C.textHeading },
  kpiSub: { fontSize: 11, color: C.textMuted, marginTop: 2 },
  tab: (active: boolean) => ({
    padding: '6px 12px', fontSize: 13, cursor: 'pointer', border: 'none', background: 'none',
    fontFamily: FONT.sans, color: active ? C.accent : C.textMuted,
    borderBottom: active ? `2px solid ${C.accent}` : '2px solid transparent',
    marginBottom: -1, fontWeight: active ? 600 : 400,
  }),
  chip: (active: boolean) => ({
    fontSize: 11, padding: '3px 10px', borderRadius: 12,
    border: `1px solid ${C.border}`, cursor: 'pointer', background: active ? C.accent : 'none',
    fontFamily: FONT.sans, color: active ? C.onAccent : C.textMuted,
  }),
  row: { display: 'flex', alignItems: 'center', padding: '6px 0', cursor: 'pointer', borderRadius: 6, gap: 8, fontSize: 13 },
};

// Главный оверлей аналитики
export function TokenAnalyticsOverlay({ onClose }: { onClose: () => void }) {
  const [period, setPeriod] = usePeriod('30d');
  const [view, setView] = useState<'dashboard' | 'inspector'>('dashboard');
  const [drillProject, setDrillProject] = useState<string | undefined>(undefined);

  return (
    <div style={STYLES.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div style={STYLES.card}>
        <div style={STYLES.header}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <BarChart3 size={18} color={C.accent} />
            <span style={STYLES.title}>Аналитика токенов</span>
            <BoundaryBadge />
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <PeriodSelector period={period} onChange={p => setPeriod(p)} />
            <button onClick={onClose} style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted }}>
              <X size={18} />
            </button>
          </div>
        </div>
        <div style={{ borderBottom: `1px solid ${C.border}`, padding: '0 20px', display: 'flex', gap: 0 }}>
          <button style={STYLES.tab(view === 'dashboard')} onClick={() => setView('dashboard')}>
            Дашборд
          </button>
          <button style={STYLES.tab(view === 'inspector')} onClick={() => setView('inspector')}>
            Инспектор
          </button>
        </div>
        <div style={STYLES.body}>
          {view === 'dashboard' ? (
            <Dashboard period={period} onDrillProject={setDrillProject} onSwitch={() => { setDrillProject(undefined); setView('inspector'); }} />
          ) : (
            <Inspector period={period} projectId={drillProject} />
          )}
        </div>
      </div>
    </div>
  );
}

// Период-селектор
function PeriodSelector({ period, onChange }: { period: ReturnType<typeof usePeriod>[0]; onChange: (p: PeriodPreset) => void }) {
  const presets: PeriodPreset[] = ['24h', '7d', '30d', '90d'];
  return (
    <div style={{ display: 'flex', gap: 4 }}>
      {presets.map(p => (
        <button key={p} style={STYLES.chip(p === period.preset)} onClick={() => onChange(p)}>
          {PRESET_LABELS[p]}
        </button>
      ))}
    </div>
  );
}

// Метка начала учёта
function BoundaryBadge() {
  const since = useBoundary();
  if (!since) return null;
  const d = new Date(since);
  return (
    <span style={{ fontSize: 10, color: C.textMuted, background: C.bgPanel, padding: '2px 6px', borderRadius: 8 }}>
      Учёт с {d.toLocaleDateString('ru-RU')}
    </span>
  );
}

// ========== DASHBOARD (Концепт A) ==========
function Dashboard({ period, onDrillProject, onSwitch }: {
  period: ReturnType<typeof usePeriod>[0];
  onDrillProject: (id: string | undefined) => void;
  onSwitch: () => void;
}) {
  const { data: agg, loading: aggLoad } = useAggregate(period);
  const { data: daily } = useDaily(period);
  const { data: projects } = useByProject(period);
  const { data: models } = useByModel(period);
  const { data: admins, loading: adminLoad } = useAdminAggregate(period);
  const { data: aggFree, loading: freeLoad } = useAggregate(period, undefined, undefined, undefined, 'ollama');
  const { data: aggFreeDirect, loading: freeDirectLoad } = useAggregate(period, undefined, undefined, undefined, 'openrouter-direct');
  const { data: aggFal, loading: falLoad } = useAggregate(period, undefined, undefined, undefined, 'fal');
  const { data: aggOneshot, loading: oneshotLoad } = useAggregate(period, undefined, undefined, undefined, 'oneshot');
  const [tab, setTab] = useState<'daily' | 'projects' | 'models' | 'sources' | 'users'>('daily');

  const maxDaily = Math.max(...daily.map(d => d.totalTokens), 1);

  return (
    <div>
      {/* KPI row */}
      <div style={{ display: 'flex', gap: 10, marginBottom: 16, flexWrap: 'wrap' }}>
        <KpiCard label="Всего токенов" value={aggLoad ? '…' : fmtTokens(agg?.totalTokens ?? 0)} sub={agg?.completedCount ? `${agg.completedCount} ходов` : undefined} />
        <KpiCard label="Стоимость" value={aggLoad ? '…' : fmtMoney(agg?.costUsd)} color={MONEY} sub={agg?.turnCount ? `${agg.turnCount} ходов` : undefined} />
        <KpiCard label="Вход / Выход" value={aggLoad ? '…' : agg?.inputOutputRatio != null ? `${agg.inputOutputRatio}:1` : '—'} sub={agg?.cacheHitRate != null ? `cache ${(agg.cacheHitRate * 100).toFixed(0)}%` : undefined} />
        <KpiCard label="Прервано" value={aggLoad ? '…' : agg?.completedCount != null ? `${((1 - agg.completedCount / agg.turnCount) * 100).toFixed(0)}%` : '—'} />
      </div>

      {/* Graph + tabs */}
      <div style={{ background: C.bgPanel, borderRadius: 10, padding: 14, border: `1px solid ${C.border}`, marginBottom: 14 }}>
        <div style={{ display: 'flex', gap: 0, borderBottom: `1px solid ${C.border}`, marginBottom: 12 }}>
          <button style={STYLES.tab(tab === 'daily')} onClick={() => setTab('daily')}>По дням</button>
          <button style={STYLES.tab(tab === 'projects')} onClick={() => setTab('projects')}>По проектам</button>
          <button style={STYLES.tab(tab === 'models')} onClick={() => setTab('models')}>По моделям</button>
          <button style={STYLES.tab(tab === 'sources')} onClick={() => setTab('sources')}>По источникам</button>
          {admins.length > 1 && (
            <button style={STYLES.tab(tab === 'users')} onClick={() => setTab('users')}>Пользователи</button>
          )}
        </div>

        {tab === 'daily' && <DailyChart data={daily} maxY={maxDaily} />}
        {tab === 'projects' && <ProjectTable data={projects} onDrill={p => { if (p) { onDrillProject(p); onSwitch(); }}} />}
        {tab === 'models' && <ModelTable data={models} />}
        {tab === 'sources' && <SourceTable agg={agg} freeAgg={aggFree} freeDirectAgg={aggFreeDirect} falAgg={aggFal} oneshotAgg={aggOneshot} />}
        {tab === 'users' && <UserTable data={admins} />}
      </div>
    </div>
  );
}

function KpiCard({ label, value, color, sub }: { label: string; value: string; color?: string; sub?: string }) {
  return (
    <div style={STYLES.kpi}>
      <div style={STYLES.kpiLabel}>{label}</div>
      <div style={{ ...STYLES.kpiValue, color: color ?? C.textHeading }}>{value}</div>
      {sub && <div style={STYLES.kpiSub}>{sub}</div>}
    </div>
  );
}

// График по дням (polyline + бары)
function DailyChart({ data, maxY }: { data: { date: string; totalTokens: number; costUsd: number | null }[]; maxY: number }) {
  const H = 100; const W = 100;
  const pts = data.map((d, i) => `${(i / Math.max(data.length - 1, 1)) * W},${H - (d.totalTokens / maxY) * H * 0.85}`).join(' ');
  return (
    <div>
      {data.length === 0 ? (
        <div style={{ textAlign: 'center', padding: 24, color: C.textMuted, fontSize: 13 }}>Нет данных за выбранный период</div>
      ) : (
        <>
          <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: H }}>
            <polyline points={pts} fill="none" stroke={C.accent} strokeWidth={2} />
            {data.map((d, i) => (
              <circle key={i} cx={(i / Math.max(data.length - 1, 1)) * W} cy={H - (d.totalTokens / maxY) * H * 0.85}
                r={2} fill={C.accent} />
            ))}
          </svg>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 4 }}>
            {data.filter((_, i) => i % Math.max(1, Math.floor(data.length / 7)) === 0).map(d => (
              <span key={d.date} style={{ fontSize: 9, color: C.textMuted }}>{fmtDate(d.date)}</span>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

// Таблица проектов
function ProjectTable({ data, onDrill }: { data: { projectId: string | null; totalTokens: number; costUsd: number | null; turnCount: number }[]; onDrill: (id: string | null) => void }) {
  return (
    <div>
      {data.map(p => (
        <div key={p.projectId ?? '__personal__'} style={STYLES.row} onClick={() => onDrill(p.projectId)}>
          <div style={{ flex: 1 }}>{p.projectId ?? '📄 Вне проекта'}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, width: 70, textAlign: 'right' }}>{fmtTokens(p.totalTokens)}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: MONEY, width: 70, textAlign: 'right' }}>{fmtMoney(p.costUsd)}</div>
          <div style={{ fontSize: 11, color: C.textMuted, width: 50, textAlign: 'right' }}>{p.turnCount} х.</div>
          <ChevronRight size={14} color={C.textMuted} />
        </div>
      ))}
    </div>
  );
}

// Таблица моделей
function ModelTable({ data }: { data: { provider: string; model: string; totalTokens: number; costUsd: number | null; turnCount: number }[] }) {
  return (
    <div>
      {data.map((m, i) => (
        <div key={i} style={{ ...STYLES.row, cursor: 'default' }}>
          <div style={{ flex: 1 }}><span style={{ fontWeight: 500 }}>{m.model}</span> <span style={{ fontSize: 11, color: C.textMuted }}>({m.provider})</span></div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, width: 70, textAlign: 'right' }}>{fmtTokens(m.totalTokens)}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: MONEY, width: 70, textAlign: 'right' }}>{fmtMoney(m.costUsd)}</div>
          <div style={{ fontSize: 11, color: C.textMuted, width: 50, textAlign: 'right' }}>{m.turnCount} х.</div>
        </div>
      ))}
    </div>
  );
}

// Таблица источников (чат, фон, бесплатно, fal)
function SourceTable({ agg, freeAgg, freeDirectAgg, falAgg, oneshotAgg }: {
  agg: SpendAggregate | null;
  freeAgg: SpendAggregate | null;
  freeDirectAgg: SpendAggregate | null;
  falAgg: SpendAggregate | null;
  oneshotAgg: SpendAggregate | null;
}) {
  // Для чатов — из общего агрегата минус oneshot (через CLI, платно)
  const chatTokens = (agg?.totalTokens ?? 0) - (oneshotAgg?.totalTokens ?? 0);
  const chatCost = (agg?.costUsd ?? 0) - (oneshotAgg?.costUsd ?? 0);
  const chatCount = (agg?.turnCount ?? 0) - (oneshotAgg?.turnCount ?? 0);
  const freeTotal = (freeAgg?.totalTokens ?? 0) + (freeDirectAgg?.totalTokens ?? 0);

  const sources = [
    { key: 'chat', label: '💬 Чаты', tokens: Math.max(0, chatTokens), cost: Math.max(0, chatCost), count: Math.max(0, chatCount) },
    { key: 'oneshot', label: '⚙️ Фоновые вызовы', tokens: oneshotAgg?.totalTokens ?? 0, cost: oneshotAgg?.costUsd, count: oneshotAgg?.turnCount },
    { key: 'free', label: '🖥 Бесплатные (Ollama/OpenRouter)', tokens: freeTotal, cost: 0, count: (freeAgg?.turnCount ?? 0) + (freeDirectAgg?.turnCount ?? 0) },
    { key: 'fal', label: '🖼 Изображения (fal.ai)', tokens: falAgg?.totalTokens ?? 0, cost: falAgg?.costUsd, count: falAgg?.turnCount },
  ];
  return (
    <div>
      {sources.map(s => (
        <div key={s.key} style={{ ...STYLES.row, cursor: 'default' }}>
          <div style={{ flex: 1, fontSize: 13 }}>{s.label}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, width: 80, textAlign: 'right' }}>{fmtTokens(s.tokens)}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: s.key === 'free' ? '#7B9E6D' : MONEY, width: 80, textAlign: 'right' }}>{s.key === 'free' ? 'бесплатно' : fmtMoney(s.cost)}</div>
          <div style={{ fontSize: 11, color: C.textMuted, width: 50, textAlign: 'right' }}>{s.count ?? 0} х.</div>
        </div>
      ))}
    </div>
  );
}

// Таблица пользователей (админ)
function UserTable({ data }: { data: { ownerId: string; totalTokens: number; costUsd: number | null; turnCount: number }[] }) {
  return (
    <div>
      {data.map((u, i) => (
        <div key={i} style={{ ...STYLES.row, cursor: 'default' }}>
          <div style={{ flex: 1, fontSize: 12 }}>{u.ownerId}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, width: 70, textAlign: 'right' }}>{fmtTokens(u.totalTokens)}</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: MONEY, width: 70, textAlign: 'right' }}>{fmtMoney(u.costUsd)}</div>
          <div style={{ fontSize: 11, color: C.textMuted, width: 50, textAlign: 'right' }}>{u.turnCount} х.</div>
        </div>
      ))}
    </div>
  );
}

// ========== INSPECTOR (Концепт C) ==========
function Inspector({ period, projectId }: { period: ReturnType<typeof usePeriod>[0]; projectId?: string }) {
  const [sourceFilter, setSourceFilter] = useState<string | undefined>(undefined);
  const { data: entries, loading } = useEntries(period, projectId, undefined, sourceFilter, 200);

  // Группировка по дням
  const groups: Record<string, typeof entries> = {};
  for (const e of entries) {
    const day = e.ts.slice(0, 10);
    if (!groups[day]) groups[day] = [];
    groups[day].push(e);
  }

  const dayLabel = (day: string) => {
    const today = new Date().toISOString().slice(0, 10);
    const yesterday = new Date(Date.now() - 86400000).toISOString().slice(0, 10);
    if (day === today) return 'Сегодня';
    if (day === yesterday) return 'Вчера';
    return new Date(day).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
  };

  return (
    <div>
      {/* Фильтры */}
      <div style={{ display: 'flex', gap: 6, marginBottom: 14, flexWrap: 'wrap' }}>
        {[{ key: undefined, label: 'Все источники' }, { key: 'chat', label: '💬 Чаты' }, { key: 'oneshot', label: '⚙️ Фон' }, { key: 'ollama', label: '🖥 Локально' }, { key: 'openrouter-direct', label: '☁️ Бесплатно' }].map(f => (
          <button key={f.key ?? '__all__'} style={STYLES.chip(sourceFilter === f.key)} onClick={() => setSourceFilter(f.key)}>
            {f.label}
          </button>
        ))}
      </div>

      {/* Лента */}
      <div style={{ position: 'relative', paddingLeft: 20 }}>
        {Object.entries(groups).map(([day, items]) => (
          <div key={day} style={{ marginBottom: 16 }}>
            <div style={{
              fontSize: 12, fontWeight: 600, marginBottom: 6, paddingBottom: 4,
              borderBottom: `1px solid ${C.borderLight ?? C.border}`, color: C.textMuted,
            }}>
              {dayLabel(day)}
            </div>
            <div style={{ position: 'relative' }}>
              {items.map((e, i) => (
                <div key={e.id} style={STYLES.row}>
                  <div style={{
                    width: 8, height: 8, borderRadius: '50%',
                    background: e.source === 'chat' ? C.accent : e.source === 'oneshot' ? '#7B9E6D' : C.textMuted,
                    flexShrink: 0, position: 'relative', zIndex: 1,
                  }} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontWeight: 500, fontSize: 12, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {e.model || e.source}
                      {e.projectId && <span style={{ color: C.textMuted, marginLeft: 4 }}>· {e.projectId.slice(0, 12)}</span>}
                    </div>
                    <div style={{ fontSize: 10, color: C.textMuted }}>
                      {new Date(e.ts).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}
                      {e.source === 'chat' && ' · ход'}
                      {e.source === 'oneshot' && ' · фон'}
                      {e.source === 'ollama' && ' · локально'}
                      {e.completed ? '' : ' · прерван'}
                    </div>
                  </div>
                  <div style={{ fontFamily: FONT.mono, fontSize: 11, fontWeight: 500, textAlign: 'right' }}>
                    <div style={{ color: e.costUsd && e.costUsd > 0.1 ? MONEY : C.textMuted }}>{fmtMoney(e.costUsd)}</div>
                    <div style={{ color: C.textMuted, fontSize: 10 }}>{fmtTokens(e.totalTokens)}</div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        ))}
        {!loading && entries.length === 0 && (
          <div style={{ textAlign: 'center', padding: 24, color: C.textMuted, fontSize: 13 }}>Нет записей за выбранный период</div>
        )}
      </div>
    </div>
  );
}
