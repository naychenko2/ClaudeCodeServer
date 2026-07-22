// Секция «План»: навигатор планов + статус + оглавление + текст.
// Перенесена из ArtifactsPanel verbatim при разбиении на секции.
import { useState, useRef, useEffect, type CSSProperties } from 'react';
import { ChevronRight, ChevronLeft, ChevronsRight, List } from 'lucide-react';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { MarkdownViewer } from '../MarkdownViewer';
import type { PlanArtifact, PlanStatus } from '../../hooks/useSessionArtifacts';
import { IconNotes } from '../../features/notes/shared';
import { saveChatNote, openNoteById } from '../../features/notes/saveToNote';

// Единый стиль кнопок-чипов в навигаторе плана («последний», «оглавление») —
// утопленный фон (не белый), одинаковые размеры/типографика.
const navChip: CSSProperties = {
  height: 28, padding: '0 10px', borderRadius: R.md, cursor: 'pointer',
  display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
  fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap',
  border: `1px solid ${C.border}`, background: C.bgInset, color: C.textSecondary,
};

// Заголовок оглавления = реальный <h*> узел из отрендеренного плана.
// Единый источник (DOM), чтобы список TOC и цель скролла были тем же узлом —
// иначе строковый парсер разъезжается с рендером remark (Setext, blockquote и пр.).
interface Heading { level: number; text: string; el: HTMLElement }

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

export function PlanSection({ plans, projectId }: { plans: PlanArtifact[]; projectId?: string }) {
  // Навигация по планам: null = «не выбирал» → показываем последний
  const [planIdx, setPlanIdx] = useState<number | null>(null);
  const effIdx = planIdx == null ? plans.length - 1 : Math.min(Math.max(planIdx, 0), plans.length - 1);
  const curPlan = plans[effIdx];

  // Оглавление текущего плана + поповер
  const [tocOpen, setTocOpen] = useState(false);
  const [headings, setHeadings] = useState<Heading[]>([]);
  const planContentRef = useRef<HTMLDivElement>(null);

  // Заголовки берём из реального DOM плана (после рендера MarkdownViewer) — один источник,
  // никакого рассинхрона со строковым парсером. Пересбор при смене текста плана.
  const planText = curPlan?.plan;
  useEffect(() => {
    const root = planContentRef.current;
    if (!root) { setHeadings([]); return; }
    const list: Heading[] = [];
    root.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(n => {
      const el = n as HTMLElement;
      const text = (el.textContent ?? '').trim();
      if (text) list.push({ level: Number(el.tagName[1]), text, el });
    });
    setHeadings(list);
  }, [planText]);

  const scrollToHeading = (h: Heading) => {
    h.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
                  onClick={() => scrollToHeading(h)}
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

      {/* Текст плана (скроллится) */}
      <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px' }}>
        <MarkdownViewer content={curPlan.plan} />
      </div>
    </>
  );
}
