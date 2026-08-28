// Секция «План»: навигатор планов + статус + оглавление + текст.
// Перенесена из ArtifactsPanel verbatim при разбиении на секции.
import { useState, useEffect, useRef, type CSSProperties } from 'react';
import { ChevronRight, ChevronLeft, ChevronsRight, List, Network, FileText, AlertCircle, Loader2 } from 'lucide-react';
import { C, FONT, R, SHADOW, SP, FS } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui/Button';
import { IconButton } from '../ui/IconButton';
import { InlineSegmented } from '../ui/InlineSegmented';
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
import { showToast } from '../../lib/toast';

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
    <IconButton
      size="xs"
      tone="muted"
      ariaLabel={dir === 'prev' ? 'Предыдущий план' : 'Следующий план'}
      disabled={disabled}
      onClick={onClick}
    >
      {dir === 'prev'
        ? <ChevronLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
    </IconButton>
  );
}

// Сегмент-переключатель «Текстом / Схемой» — общий InlineSegmented с тоном плана.
// Активный сегмент на C.plan, не C.accent — режим «План» живёт своей гаммой.

export function PlanSection({ plans, projectId }: { plans: PlanArtifact[]; projectId?: string }) {
  // Навигация по планам: null = «не выбирал» → показываем последний
  const [planIdx, setPlanIdx] = useState<number | null>(null);
  const effIdx = planIdx == null ? plans.length - 1 : Math.min(Math.max(planIdx, 0), plans.length - 1);
  const curPlan = plans[effIdx];

  // Состояние схемы объявляется ДО useHeadings: его значение передаётся
  // в deps эффекта как containerToken (см. useHeadings) — иначе при
  // переключении «Текстом ↔ Схемой» заголовки не пересобираются.
  const [schemeView, setSchemeView] = useState<'text' | 'scheme'>('text');
  const [map, setMap] = useState<PlanMap | null>(null);
  const [schemeStatus, setSchemeStatus] = useState<'idle' | 'building' | 'ready' | 'failed'>('idle');
  const [schemeError, setSchemeError] = useState<string | null>(null);

  // Оглавление текущего плана + поповер
  const [tocOpen, setTocOpen] = useState(false);
  const planContentRef = useRef<HTMLDivElement>(null);
  // containerToken=schemeView: при переключении «Текстом ↔ Схемой» ref тот же,
  // но контент другой, и без явного токена useHeadings не пересоберёт заголовки.
  const headings = useHeadings(planContentRef, curPlan?.plan, schemeView);
  // Фича «Визуальный разворот плана»: в панели `requestId` не хранится
  // (PlanArtifact — проекция истории), отправлять через respondPlan нельзя.
  // Слой замечаний работает на сбор: копирует текст обратной связи в буфер
  // обмена, дальше пользователь открывает план в чате и вставляет его туда.
  const visualPlanEnabled = useFeature(FLAGS.visualPlan);
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
        flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: SP.xs,
        padding: '8px 10px 8px 12px', borderBottom: `1px solid ${C.border}`,
      }}>
        {plans.length > 1 && (
          <NavArrow dir="prev" disabled={effIdx === 0} onClick={() => setPlanIdx(effIdx - 1)} />
        )}
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
          {plans.length > 1 ? `План ${effIdx + 1} / ${plans.length}` : 'План'}
        </span>
        {plans.length > 1 && (
          <NavArrow dir="next" disabled={effIdx === plans.length - 1} onClick={() => setPlanIdx(effIdx + 1)} />
        )}
        <span style={{
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, padding: '2px 8px', borderRadius: R.max,
          color: STATUS_META[curPlan.status].fg, background: STATUS_META[curPlan.status].bg, whiteSpace: 'nowrap',
        }}>
          {STATUS_META[curPlan.status].label}
        </span>
        <div style={{ flex: 1 }} />
        <SavePlanChip plan={curPlan.plan} projectId={projectId} />
        {plans.length > 1 && effIdx !== plans.length - 1 && (
          <Button
            variant="ghostFilled"
            size="xs"
            onClick={() => setPlanIdx(null)}
            leftIcon={<ChevronsRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          >
            последний
          </Button>
        )}
        {headings.length > 0 && (
          <Button
            variant={tocOpen ? 'ghostAccent' : 'ghostFilled'}
            size="xs"
            onClick={() => setTocOpen(v => !v)}
            leftIcon={<List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          >
            оглавление
          </Button>
        )}

        {/* Поповер оглавления */}
        {tocOpen && headings.length > 0 && (
          <>
            <div onClick={() => setTocOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
            <div style={{
              position: 'absolute', top: '100%', right: 8, marginTop: SP.xxs, zIndex: 41,
              width: 'min(280px, calc(100% - 16px))', maxHeight: 320, overflowY: 'auto',
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              boxShadow: SHADOW.dropdown, padding: SP.xs + ' 0',
            }}>
              {headings.map((h, i) => (
                <button
                  key={i}
                  onClick={() => goToHeading(h)}
                  style={{
                    width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                    padding: '5px 12px', paddingLeft: 12 + (h.level - 1) * 12,
                    fontFamily: FONT.sans, fontSize: FS.sm, color: h.level <= 2 ? C.textHeading : C.textSecondary,
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
            <InlineSegmented
              value={schemeView}
              onChange={setSchemeView}
              options={[
                { value: 'text', label: 'Текстом', icon: <FileText size={12} />,
                  tone: { bg: C.plan, fg: C.onAccent } },
                { value: 'scheme', label: 'Схемой', icon: <Network size={12} />,
                  tone: { bg: C.plan, fg: C.onAccent } },
              ]}
            />
            {schemeView === 'scheme' && schemeStatus !== 'ready' && (
              <Button
                variant="ghostFilled"
                size="sm"
                loading={schemeStatus === 'building'}
                onClick={buildScheme}
                leftIcon={schemeStatus === 'building'
                  ? <Loader2 size={12} />
                  : <Network size={12} />}
              >
                {schemeStatus === 'building' ? 'Собираю схему…' : 'Собрать схему'}
              </Button>
            )}
          </div>
        )}

        {schemeView === 'scheme' && visualPlanEnabled ? (
          schemeStatus === 'ready' && map ? (
            <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px', position: 'relative' }}>
              {/* Исходный план нужен PlanScheme: useHeadings берёт заголовки
                  из реального DOM, и без него резолв блоков возвращает пустой
                  список (карта вырождается в жанр/фразу/числа). Скрываем
                  position:absolute+1×1+opacity:0 — узлы остаются в DOM и
                  доступны querySelectorAll, но не ломают раскладку панели
                  (visibility:hidden сохранил бы высоту и сдвинул схему).
                  aria-hidden снимает со скринридеров: контент уже виден
                  через схему. */}
              <div aria-hidden="true" style={{
                position: 'absolute', top: 0, left: 0, width: 1, height: 1,
                opacity: 0, overflow: 'hidden', pointerEvents: 'none',
              }}>
                <MarkdownViewer content={curPlan.plan} />
              </div>
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
                <Button
                  variant="ghostFilled"
                  size="xs"
                  onClick={buildScheme}
                  style={{ marginTop: SP.xs }}
                >
                  Попробовать снова
                </Button>
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

        {/* Слой замечаний рендерится ВСЕГДА под флагом: иначе переключение
            «Схемой» размонтировало бы его и унесло несобранные черновики
            (отзыв ревью: «план из 15 разделов — замечания не должны пропадать
            от смены вкладки»). При schemeView='scheme' contentRef указывает на
            контейнер схемы; кнопки замечаний в режиме схемы НЕ показываются —
            замечания из схемы не создаются (вики-план «Визуальный разворот»),
            а кнопки у заголовков лежат в скрытом markdown-слое и пользователю
            не видны: при возврате на текст слой пересобирается с актуальными
            узлами, и кнопки появляются снова. */}
        {visualPlanEnabled && (
          <PlanRemarks
            contentRef={planContentRef}
            planText={curPlan.plan}
            containerToken={schemeView}
            status={curPlan.status === 'pending' ? 'pending' : 'resolved'}
            onSubmit={feedback => {
              // requestId в PlanArtifact нет — отправлять в панели некуда.
              // Сборка текста всё равно полезна: копируем в буфер, дальше
              // пользователь открывает план в чате и отвечает там
              // (onRespond живёт только в PlanReviewView)
              if (navigator.clipboard) {
                navigator.clipboard.writeText(feedback).catch(() => {});
              }
              showToast(
                'Замечания скопированы',
                'Откройте план в чате, чтобы отправить их планировщику.',
              );
            }}
          />
        )}
      </div>
    </>
  );
}