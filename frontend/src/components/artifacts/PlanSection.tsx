// Секция «План»: навигатор планов + статус + оглавление + текст.
// Перенесена из ArtifactsPanel verbatim при разбиении на секции.
import { useState, useEffect, useRef, type CSSProperties } from 'react';
import { ChevronRight, ChevronLeft, ChevronsRight, List, Network, FileText, AlertCircle, Loader2 } from 'lucide-react';
import { C, FONT, R, SHADOW, SP, FS } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { MarkdownViewer } from '../MarkdownViewer';
import { useHeadings, scrollToHeading, type Heading } from '../../hooks/useHeadings';
import type { PlanArtifact, PlanStatus } from '../../hooks/useSessionArtifacts';
import type { PlanMap } from '../../types';
import { IconNotes } from '../../features/notes/shared';
import { saveChatNote, openNoteById } from '../../features/notes/saveToNote';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { PlanRemarks } from '../../features/plan/PlanRemarks';
import { PlanScheme } from '../plan/PlanScheme';
import { api } from '../../lib/api';

// Единый стиль кнопок-чипов в навигаторе плана («последний», «оглавление») —
// утопленный фон (не белый), одинаковые размеры/типографика.
const navChip: CSSProperties = {
  height: 28, padding: '0 10px', borderRadius: R.md, cursor: 'pointer',
  display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
  fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap',
  border: `1px solid ${C.border}`, background: C.bgInset, color: C.textSecondary,
};

// Заголовок оглавления = реальный <h*> узел из отрендеренного плана; сбор — общий хук
// useHeadings (им же пользуется панель «Документы»).

// Чип «в заметку» в навигаторе плана — сохраняет текущий план в базу заметок
function SavePlanChip({ plan, projectId }: { plan: string; projectId?: string }) {
  const [savedId, setSavedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = () => {
    if (busy) return;
    if (savedId) { openNoteById(savedId); return; }
    setBusy(true);
    saveChatNote({ text: plan, projectId, titlePrefix: 'План: ' })
      .then(n => { setSavedId(n.id); setTimeout(() => setSavedId(null), 6000); })
      .catch(() => {})
      .finally(() => setBusy(false));
  };
  return (
    <button onClick={save} title={savedId ? 'Сохранено — открыть заметку' : 'Сохранить план в заметку'}
      style={savedId
        ? { ...navChip, background: C.successBg, border: `1px solid ${C.successBg}`, color: C.successText }
        : { ...navChip, opacity: busy ? 0.6 : 1 }}>
      <IconNotes size={13} />
      {savedId ? 'открыть' : 'в заметку'}
    </button>
  );
}

const STATUS_META: Record<PlanStatus, { label: string; fg: string; bg: string }> = {
  approved: { label: 'одобрен', fg: C.successText, bg: C.successBg },
  rejected: { label: 'отклонён', fg: C.dangerText, bg: C.dangerBg },
  pending:  { label: 'ожидает', fg: C.textSecondary, bg: C.bgInset },
};

// Иконка-кнопка навигатора планов (стрелка ‹ / ›)
function NavArrow({ dir, disabled, onClick }: { dir: 'prev' | 'next'; disabled: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={dir === 'prev' ? 'Предыдущий план' : 'Следующий план'}
      style={{
        width: 24, height: 24, border: 'none', borderRadius: R.sm, background: 'transparent',
        cursor: disabled ? 'default' : 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: disabled ? C.border : C.textSecondary, flexShrink: 0,
      }}
    >
      {dir === 'prev'
        ? <ChevronLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
    </button>
  );
}

// Сегмент-переключатель «Текстом / Схемой» в навигаторе панели «План». Локальный
// компонент: один внутри карточки чата (PlanReviewView), другой — внутри панели
// «План». Дублирование умышленное — оба места живут в разных композициях, и тащить
// компонент наверх ради двух вызовов преждевременно.
function SegmentedToggle<V extends string>({ options, value, onChange }: {
  options: ReadonlyArray<{ value: V; label: string; icon?: React.ReactNode }>;
  value: V;
  onChange: (v: V) => void;
}) {
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center',
      border: `1px solid ${C.border}`, borderRadius: R.pill,
      background: C.bgInset, padding: 2,
    }}>
      {options.map(opt => {
        const active = opt.value === value;
        return (
          <button key={opt.value} onClick={() => onChange(opt.value)} style={{
            display: 'inline-flex', alignItems: 'center', gap: 4,
            padding: '4px 10px', borderRadius: R.pill,
            border: 'none',
            background: active ? C.plan : 'transparent',
            color: active ? C.onAccent : C.textSecondary,
            cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
          }}>
            {opt.icon}
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}

export function PlanSection({ plans, projectId }: { plans: PlanArtifact[]; projectId?: string }) {
  // Навигация по планам: null = «не выбирал» → показываем последний
  const [planIdx, setPlanIdx] = useState<number | null>(null);
  const effIdx = planIdx == null ? plans.length - 1 : Math.min(Math.max(planIdx, 0), plans.length - 1);
  const curPlan = plans[effIdx];

  // Оглавление текущего плана + поповер
  const [tocOpen, setTocOpen] = useState(false);
  const [copiedHint, setCopiedHint] = useState(false);
  const planContentRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(planContentRef, curPlan?.plan);
  // Фича «Визуальный разворот плана»: в панели `requestId` не хранится
  // (PlanArtifact — проекция истории), отправлять через respondPlan нельзя.
  // Слой замечаний работает на сбор: копирует текст обратной связи в буфер
  // обмена, дальше пользователь открывает план в чате и вставляет его туда.
  const visualPlanEnabled = useFeature(FLAGS.visualPlan);
  // Состояние схемы — отдельная сущность от текста. Идентично PlanReviewView:
  // сборка только по кнопке (вики-план часть B §4), сброс при смене плана.
  const [schemeView, setSchemeView] = useState<'text' | 'scheme'>('text');
  const [map, setMap] = useState<PlanMap | null>(null);
  const [schemeStatus, setSchemeStatus] = useState<'idle' | 'building' | 'ready' | 'failed'>('idle');
  const [schemeError, setSchemeError] = useState<string | null>(null);
  useEffect(() => {
    setMap(null);
    setSchemeStatus('idle');
    setSchemeError(null);
    setSchemeView('text');
  }, [curPlan?.plan]);
  async function buildScheme() {
    if (schemeStatus === 'building' || !curPlan) return;
    setSchemeStatus('building');
    setSchemeError(null);
    try {
      const m = await api.plans.buildMap(curPlan.plan);
      setMap(m);
      setSchemeStatus(m === null ? 'failed' : 'ready');
    } catch (e) {
      const err = e as Error & { status?: number };
      setSchemeError(err.message || 'Не удалось собрать схему');
      setSchemeStatus('failed');
    }
  }

  const goToHeading = (h: Heading) => {
    scrollToHeading(planContentRef.current, h);
    setTocOpen(false);
  };

  if (!curPlan) return null;

  return (
    <>
      {/* Навигатор планов + статус + оглавление */}
      <div style={{
        flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: 6,
        padding: '8px 10px 8px 12px', borderBottom: `1px solid ${C.border}`,
      }}>
        {plans.length > 1 && (
          <NavArrow dir="prev" disabled={effIdx === 0} onClick={() => setPlanIdx(effIdx - 1)} />
        )}
        <span style={{ fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
          {plans.length > 1 ? `План ${effIdx + 1} / ${plans.length}` : 'План'}
        </span>
        {plans.length > 1 && (
          <NavArrow dir="next" disabled={effIdx === plans.length - 1} onClick={() => setPlanIdx(effIdx + 1)} />
        )}
        <span style={{
          fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, padding: '2px 7px', borderRadius: R.sm,
          color: STATUS_META[curPlan.status].fg, background: STATUS_META[curPlan.status].bg, whiteSpace: 'nowrap',
        }}>
          {STATUS_META[curPlan.status].label}
        </span>
        <div style={{ flex: 1 }} />
        <SavePlanChip plan={curPlan.plan} projectId={projectId} />
        {plans.length > 1 && effIdx !== plans.length - 1 && (
          <button
            onClick={() => setPlanIdx(null)}
            title="К последнему плану"
            style={navChip}
          >
            <ChevronsRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            последний
          </button>
        )}
        {headings.length > 0 && (
          <button
            onClick={() => setTocOpen(v => !v)}
            title="Оглавление"
            style={tocOpen
              ? { ...navChip, background: C.accentMuted, border: `1px solid ${C.accentMuted}`, color: C.accent }
              : navChip}
          >
            <List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            оглавление
          </button>
        )}

        {/* Поповер оглавления */}
        {tocOpen && headings.length > 0 && (
          <>
            <div onClick={() => setTocOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
            <div style={{
              position: 'absolute', top: '100%', right: 8, marginTop: 4, zIndex: 41,
              width: 'min(280px, calc(100% - 16px))', maxHeight: 320, overflowY: 'auto',
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              boxShadow: SHADOW.dropdown, padding: '6px 0',
            }}>
              {headings.map((h, i) => (
                <button
                  key={i}
                  onClick={() => goToHeading(h)}
                  style={{
                    width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                    padding: '5px 12px', paddingLeft: 12 + (h.level - 1) * 12,
                    fontFamily: FONT.sans, fontSize: 12.5, color: h.level <= 2 ? C.textHeading : C.textSecondary,
                    fontWeight: h.level <= 2 ? 600 : 400,
                    whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: 1.35,
                  }}
                  onMouseEnter={e => (e.currentTarget.style.background = C.bgSelected)}
                  onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
                >
                  {h.text}
                </button>
              ))}
            </div>
          </>
        )}
      </div>

      {/* Текст плана (скроллится) + слой замечаний под флагом visual-plan */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {visualPlanEnabled && (
          // Переключатель «Схемой / Текстом» + кнопка «Собрать схему» — над телом
          // секции. Сборка ТОЛЬКО по кнопке (вики-план часть B §4): иначе любое
          // открытие плана дёргало бы модель.
          <div style={{
            display: 'flex', alignItems: 'center', gap: 6,
            padding: '8px 12px 0', flexShrink: 0, flexWrap: 'wrap',
          }}>
            <SegmentedToggle
              options={[
                { value: 'text' as const, label: 'Текстом', icon: <FileText size={12} /> },
                { value: 'scheme' as const, label: 'Схемой', icon: <Network size={12} /> },
              ]}
              value={schemeView}
              onChange={setSchemeView}
            />
            {schemeView === 'scheme' && schemeStatus !== 'ready' && (
              <button onClick={buildScheme} disabled={schemeStatus === 'building'} style={{
                display: 'inline-flex', alignItems: 'center', gap: 5,
                padding: '5px 10px', borderRadius: R.md,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                color: C.textHeading, cursor: schemeStatus === 'building' ? 'default' : 'pointer',
                fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                opacity: schemeStatus === 'building' ? 0.6 : 1,
              }}>
                {schemeStatus === 'building'
                  ? <Loader2 size={12} style={{ animation: 'cc-spin 1s linear infinite' }} />
                  : <Network size={12} />}
                {schemeStatus === 'building' ? 'Собираю схему…' : 'Собрать схему'}
              </button>
            )}
          </div>
        )}

        {schemeView === 'scheme' && visualPlanEnabled ? (
          schemeStatus === 'ready' && map ? (
            <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px' }}>
              <PlanScheme map={map} planText={curPlan.plan} contentRef={planContentRef} />
            </div>
          ) : schemeStatus === 'failed' ? (
            <div style={{
              margin: '14px 16px', padding: '10px 12px',
              background: C.warningBg, border: `1px solid ${C.border}`, borderRadius: R.lg,
              display: 'flex', alignItems: 'flex-start', gap: SP.sm,
              fontSize: FS.sm, color: C.textHeading, fontFamily: FONT.sans,
            }}>
              <AlertCircle size={14} style={{ color: C.textMuted, flexShrink: 0, marginTop: 2 }} />
              <div>
                <div style={{ fontWeight: 600 }}>
                  {schemeError || 'Схему собрать не удалось — план открыт текстом, замечания работают.'}
                </div>
                <button onClick={buildScheme} style={{
                  marginTop: 6, padding: '4px 10px', borderRadius: R.sm,
                  border: `1px solid ${C.border}`, background: C.bgWhite,
                  color: C.textSecondary, cursor: 'pointer',
                  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
                }}>Попробовать снова</button>
              </div>
            </div>
          ) : schemeStatus === 'building' ? (
            <div style={{
              margin: '14px 16px', padding: '14px',
              background: C.bgInset, border: `1px dashed ${C.border}`, borderRadius: R.lg,
              textAlign: 'center',
              fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans,
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
            }}>
              <Loader2 size={14} style={{ animation: 'cc-spin 1s linear infinite' }} />
              Собираю схему…
            </div>
          ) : (
            <div style={{
              margin: '14px 16px', padding: '14px',
              background: C.bgInset, border: `1px dashed ${C.border}`, borderRadius: R.lg,
              textAlign: 'center',
              fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans,
            }}>
              Нажмите «Собрать схему», чтобы построить разворот.
            </div>
          )
        ) : (
          <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px' }}>
            <MarkdownViewer content={curPlan.plan} />
          </div>
        )}

        {visualPlanEnabled && schemeView === 'text' && (
          <PlanRemarks
            contentRef={planContentRef}
            planText={curPlan.plan}
            status={curPlan.status === 'pending' ? 'pending' : 'resolved'}
            onSubmit={feedback => {
              // requestId в PlanArtifact нет — отправлять в панели некуда.
              // Сборка текста всё равно полезна: копируем в буфер, дальше
              // пользователь открывает план в чате и отвечает там
              // (onRespond живёт только в PlanReviewView)
              if (navigator.clipboard) {
                navigator.clipboard.writeText(feedback).catch(() => {});
              }
              setCopiedHint(true);
              window.setTimeout(() => setCopiedHint(false), 4000);
            }}
          />
        )}
        {copiedHint && (
          <div style={{
            position: 'absolute', bottom: 18, left: 14, right: 14,
            padding: '8px 12px', background: C.bgCard, color: C.textHeading,
            border: `1px solid ${C.success}`, borderRadius: R.md,
            fontSize: 12, fontFamily: FONT.sans, fontWeight: 600,
            boxShadow: SHADOW.dropdown, zIndex: 10,
          }}>
            Замечания скопированы в буфер. Откройте план в чате, чтобы отправить их планировщику.
          </div>
        )}
      </div>
    </>
  );
}