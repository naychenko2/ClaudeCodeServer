import { useEffect, useState, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { MessageCircle, MessageSquarePlus, Trash2, X } from 'lucide-react';
import type { PlanRemark } from './buildPlanFeedback';
import { buildPlanFeedback } from './buildPlanFeedback';
import { useHeadings } from '../../hooks/useHeadings';
import { C, FONT, R, SHADOW, Z, SP, FS } from '../../lib/design';
import { ICON_SIZE } from '../../components/ui/icons';

// Слой контекстных замечаний к плану. Ядро фичи «Визуальный разворот плана»
// (часть A): работает самостоятельно, без бэкенда.
//
// Архитектура:
// 1. `useHeadings` снимает заголовки с реального DOM контейнера (planText —
//    зависимость: при смене плана DOM перерисовывается, протухшие узлы
//    заменяются живыми, и кнопки у заголовков пересоздаются).
// 2. На каждый заголовок через DOM-инъекцию садится кнопка «Оставить
//    замечание»: проявляется на hover на десктопе и видна всегда на тач-
//    устройствах (детект через media-query (hover: hover)).
// 3. Выделение текста внутри контейнера через Range API → ближайший heading
//    вверх по предкам, цитата и предзаполненный заголовок.
// 4. Замечания живут в локальном state до отправки: хранить их
//    бессмысленно (план переделывается целиком, новый план — новые якоря).
// 5. Отправка — `onSubmit(feedback)` родителем: в PlanReviewView это
//    existing-контракт `onRespond(reqId, false, feedback)`; в PlanSection —
//    аналогичный колбэк, специфичный для панели.
//
// Дизайн-система: только токены C.* и шкалы FS/SP/R. Без Tailwind. Кнопки
// у заголовков и форма — через DOM (кнопки) + портал (форма): прямая
// вёрстка обрезала бы их в scroll-области PlanReviewView и резала бы hover.

interface FormAnchor {
  heading: string;
  quote?: string;
  x: number;   // координаты якоря (для позиционирования попапа)
  y: number;
  placement: 'below' | 'center';
}

interface SelectionInfo {
  text: string;
  heading: string | null;
  x: number;
  y: number;
}

interface Props {
  // Контейнер, внутри которого отрендерен план (MarkdownContent / MarkdownViewer).
  // Ref на СКРОЛЛЕР, чтобы хедеры, в которых рендерятся заголовки, были внутри
  contentRef: RefObject<HTMLElement | null>;
  // Исходный markdown плана — пересчёт headings + сборка обратной связи
  planText: string;
  // Замечания доступны только у плана в статусе pending
  status: 'pending' | 'resolved';
  // Колбэк отправки готового текста обратной связи
  onSubmit: (feedback: string) => void;
  // Необязательное уведомление наружу — для подсветки карточки «замечания
  // ещё не отправлены» или аналога (задача этого пока не требует, оставлено
  // на будущее; сейчас компонент полностью автономен)
  onDirtyChange?: (dirty: boolean) => void;
}

const REMARK_STYLE_ID = 'cc-plan-remark-styles';

function injectRemarkStyles(): void {
  if (document.getElementById(REMARK_STYLE_ID)) return;
  const style = document.createElement('style');
  style.id = REMARK_STYLE_ID;
  style.textContent = `
    [data-remark-host] { position: relative; }
    [data-remark-btn] {
      display: inline-flex; align-items: center; gap: 4px;
      padding: 2px 7px; border-radius: ${R.md}px;
      border: 1px solid ${C.accent}; background: ${C.bgCard};
      color: ${C.accent}; cursor: pointer; font-size: ${FS.xs}px;
      font-family: ${FONT.sans}; font-weight: 600; line-height: 1.2;
      opacity: 0; pointer-events: none; transform: translateY(-1px);
      transition: opacity .12s ease-out;
      flex-shrink: 0;
    }
    [data-remark-host]:hover > [data-remark-btn],
    [data-remark-btn]:focus-visible {
      opacity: 1; pointer-events: auto; transform: none;
    }
    /* Тач-устройство: hover не работает — кнопка видна всегда, ставится
       внутрь строки заголовка рядом с текстом, а не абсолютно справа */
    @media (hover: none) {
      [data-remark-btn] {
        position: static !important;
        opacity: 1 !important;
        pointer-events: auto !important;
        transform: none !important;
        margin-left: 8px;
        vertical-align: middle;
      }
    }
  `;
  document.head.appendChild(style);
}

// Ближайший заголовок раздела выше точки выделения. Поднимаемся по предкам
// от range.startContainer — heading ищем ВВЕРХ, потому что выделение ниже
// принадлежит ближайшему предыдущему заголовку раздела. Если выше heading нет
// (выделение до самого верха документа) — возвращаем null: такое замечание
// нельзя привязать к разделу, и попап не откроется.
function headingForRange(range: Range): HTMLElement | null {
  let n: Node | null = range.startContainer;
  // text node → parent element
  if (n && n.nodeType === Node.TEXT_NODE) n = n.parentElement;
  for (; n; n = (n as Element).parentElement || n.parentNode) {
    if (n.nodeType === Node.ELEMENT_NODE && /^H[1-6]$/.test((n as Element).tagName)) {
      return n as HTMLElement;
    }
  }
  return null;
}

function clamp(v: number, min: number, max: number): number {
  return Math.min(Math.max(v, min), max);
}

export function PlanRemarks({ contentRef, planText, status, onSubmit, onDirtyChange }: Props) {
  const [remarks, setRemarks] = useState<PlanRemark[]>([]);
  const [form, setForm] = useState<FormAnchor | null>(null);
  const [draft, setDraft] = useState('');
  const [selection, setSelection] = useState<SelectionInfo | null>(null);
  const [isMobile, setIsMobile] = useState(false);

  // Список заголовков — берём с реального DOM через общий хук
  const headings = useHeadings(contentRef, planText);

  // Глобальный CSS для кнопок у заголовков и тач-варианта — инжектируем
  // единожды за время жизни компонента
  useEffect(() => { injectRemarkStyles(); return () => { /* не снимаем: на странице
    могут быть другие экземпляры PlanRemarks; сами кнопки чистятся ниже */ }; }, []);

  // Детект тач-устройства для form-размещения (на мобиле — шторка снизу)
  useEffect(() => {
    const mq = window.matchMedia('(hover: none)');
    const apply = () => setIsMobile(mq.matches);
    apply();
    mq.addEventListener?.('change', apply);
    return () => mq.removeEventListener?.('change', apply);
  }, []);

  // Уведомляем родителя о появлении/сбросе неотправленных замечаний
  useEffect(() => { onDirtyChange?.(remarks.length > 0); }, [remarks.length, onDirtyChange]);

  // ── Детект выделения внутри контейнера ──
  // Без active selection попап с кнопкой «Оставить замечание» не появляется.
  // Слушатели: и mouseup (мышь), и selectionchange (страховка для тача)
  useEffect(() => {
    const root = contentRef.current;
    if (!root) return;
    const onUp = () => {
      window.setTimeout(() => {
        const sel = window.getSelection();
        if (!sel || sel.isCollapsed) { setSelection(null); return; }
        const text = sel.toString().trim();
        if (text.length < 3) { setSelection(null); return; }
        const range = sel.getRangeAt(0);
        if (!root.contains(range.commonAncestorContainer)) { setSelection(null); return; }
        const headingEl = headingForRange(range);
        const heading = headingEl?.textContent?.trim() ?? null;
        // Без заголовка замечание некуда привязать — закрываем попап
        if (!heading) { setSelection(null); return; }
        const rect = range.getBoundingClientRect();
        setSelection({
          text,
          heading,
          x: rect.left + rect.width / 2,
          y: rect.top,
        });
      }, 10);
    };
    root.addEventListener('mouseup', onUp);
    root.addEventListener('touchend', onUp);
    return () => {
      root.removeEventListener('mouseup', onUp);
      root.removeEventListener('touchend', onUp);
    };
  }, [contentRef]);

  // Сбрасываем форму/выделение при смене плана (новая версия — новые якоря)
  useEffect(() => {
    setForm(null);
    setDraft('');
    setSelection(null);
  }, [planText]);

  // ── DOM-инъекция кнопок «Оставить замечание» в КАЖДЫЙ заголовок ──
  // Кнопки — DOM-узлы, не React-элементы внутри markdown: иначе они поехали
  // бы по слоям перерисовки remark и (главное) попали бы в textContent, а
  // оглавление уже умеет их вырезать (headingText фильтрует data-ann-marker).
  // Здесь используем тот же приём с собственным data-атрибутом: оглавление
  // фильтрует [data-ann-marker], наши — [data-remark-host]/[data-remark-btn],
  // пересечения нет.
  useEffect(() => {
    if (status !== 'pending') return;
    const root = contentRef.current;
    if (!root) return;
    const cleanups: Array<() => void> = [];
    for (const h of headings) {
      if (!h.el || !h.el.isConnected) continue;
      // Маркируем узел, чтобы CSS-правила выше подхватили hover
      h.el.setAttribute('data-remark-host', '');
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.setAttribute('data-remark-btn', '');
      btn.setAttribute('data-remark-heading', h.text);
      btn.title = 'Оставить замечание';
      btn.innerHTML =
        `<svg viewBox="0 0 24 24" width="${ICON_SIZE.xs}" height="${ICON_SIZE.xs}" `
        + 'fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" '
        + 'stroke-linejoin="round" aria-hidden="true">'
        + '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>'
        + `<span>замечание</span>`;
      const onClick = (e: Event) => {
        e.preventDefault(); e.stopPropagation();
        const rect = h.el.getBoundingClientRect();
        openForm({
          heading: h.text,
          x: rect.left + rect.width / 2,
          y: rect.bottom,
          placement: 'below',
        });
      };
      btn.addEventListener('click', onClick);
      h.el.appendChild(btn);
      cleanups.push(() => {
        btn.removeEventListener('click', onClick);
        btn.remove();
        h.el.removeAttribute('data-remark-host');
      });
    }
    return () => { cleanups.forEach(fn => fn()); };
  }, [headings, contentRef, status]);

  function openForm(anchor: FormAnchor) {
    setForm(anchor);
    setDraft('');
    // Очищаем выделение, чтобы плавающая кнопка над ним не конкурировала
    window.getSelection()?.removeAllRanges();
  }

  function addRemark() {
    if (!form) return;
    const text = draft.trim();
    if (!text) return;
    const next: PlanRemark = { anchorHeading: form.heading, text };
    const q = form.quote?.trim();
    if (q) next.quote = q;
    setRemarks(r => [...r, next]);
    setForm(null);
    setDraft('');
  }

  function removeRemark(idx: number) {
    setRemarks(r => r.filter((_, i) => i !== idx));
  }

  function clearAll() {
    setRemarks([]);
  }

  function submit() {
    const headingTexts = headings.map(h => h.text);
    const fb = buildPlanFeedback(remarks, headingTexts);
    onSubmit(fb);
  }

  // ── Resolved-планы: ничего не рисуем (только чтение) ──
  if (status !== 'pending') return null;

  // Форма ввода: портал с клампом по ширине экрана
  const formPopup = form && createPortal(
    <div style={{
      position: 'fixed', zIndex: Z.modal,
      ...(isMobile
        ? { left: 8, right: 8, bottom: 8, borderRadius: R.modal }
        : {
            left: clamp(form.x - 175, 8, window.innerWidth - 368),
            // При placement='below' — под точкой якоря; иначе по центру
            top: form.placement === 'below'
              ? Math.min(form.y + 8, window.innerHeight - 280)
              : Math.min(Math.max(form.y - 140, 8), window.innerHeight - 280),
            width: 350, borderRadius: R.xl,
          }),
      background: C.bgCard, border: `1px solid ${C.border}`,
      boxShadow: SHADOW.dropdown, overflow: 'hidden',
      fontFamily: FONT.sans,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '11px 14px', borderBottom: `1px solid ${C.border}`, fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>
        <MessageSquarePlus size={14} style={{ color: C.accent, flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={form.heading}>
          {form.heading}
        </span>
        <button onClick={() => setForm(null)} aria-label="Закрыть" style={{ border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex' }}>
          <X size={14} />
        </button>
      </div>
      {form.quote && (
        <div style={{
          margin: '10px 14px 0', borderLeft: `3px solid ${C.accent}`,
          background: C.accentLight, borderRadius: '0 8px 8px 0',
          padding: '6px 10px', fontSize: FS.sm, color: C.textSecondary,
          fontStyle: 'italic', maxHeight: 70, overflow: 'hidden',
        }}>«{form.quote.length > 140 ? form.quote.slice(0, 140) + '…' : form.quote}»</div>
      )}
      <div style={{ padding: `${SP.sm}px ${SP.md}px`, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>Что поправить в этом разделе?</div>
        <textarea
          autoFocus
          value={draft}
          onChange={e => setDraft(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); addRemark(); }
            if (e.key === 'Escape') { e.preventDefault(); setForm(null); }
          }}
          placeholder="Например: ссылка на источник"
          style={{
            width: '100%', boxSizing: 'border-box',
            border: `1px solid ${C.border}`, borderRadius: R.md,
            background: C.bgMain, color: C.textHeading,
            font: `${FS.base}px/1.5 ${FONT.sans}`, padding: '8px 10px',
            resize: 'vertical', minHeight: 64, outline: 'none',
          }}
        />
        <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'flex-end' }}>
          <button onClick={() => setForm(null)} style={{
            padding: '6px 14px', background: 'transparent', color: C.textMuted,
            border: `1px solid ${C.border}`, borderRadius: R.md,
            fontSize: FS.base, cursor: 'pointer', fontFamily: FONT.sans,
          }}>Отмена</button>
          <button onClick={addRemark} disabled={!draft.trim()} style={{
            padding: '6px 14px', background: C.accent, color: C.onAccent,
            border: 'none', borderRadius: R.md,
            fontSize: FS.base, fontWeight: 600, cursor: 'pointer',
            fontFamily: FONT.sans, opacity: draft.trim() ? 1 : 0.5,
          }}>Добавить</button>
        </div>
      </div>
    </div>,
    document.body,
  );

  // Плавающая кнопка над выделением (только если форма ещё не открыта)
  const selectionPopup = selection && !form && createPortal(
    <button onClick={() => openForm({
      heading: selection.heading ?? '—',
      quote: selection.text,
      x: selection.x, y: selection.y,
      placement: 'center',
    })} style={{
      position: 'fixed', zIndex: Z.modal,
      left: clamp(selection.x - 90, 8, window.innerWidth - 200),
      top: Math.max(8, selection.y - 40),
      display: 'flex', alignItems: 'center', gap: 6,
      padding: '6px 12px', background: C.textHeading, color: C.bgMain,
      border: 'none', borderRadius: R.md, fontSize: FS.base, fontWeight: 600,
      cursor: 'pointer', boxShadow: SHADOW.dropdown, fontFamily: FONT.sans,
    }}>
      <MessageCircle size={13} />
      Оставить замечание
    </button>,
    document.body,
  );

  // Низ — счётчик и кнопка отправки
  const bottom = (
    <div style={{
      marginTop: SP.md, paddingTop: SP.md,
      borderTop: `1px dashed ${C.border}`,
      display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      {/* Список замечаний с возможностью удалить */}
      {remarks.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {remarks.map((r, i) => (
            <div key={i} style={{
              border: `1px solid ${C.border}`, borderRadius: R.lg,
              background: C.bgWhite, padding: '8px 10px',
              display: 'flex', flexDirection: 'column', gap: 4,
            }}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs,
                color: C.textMuted,
              }}>
                <MessageSquarePlus size={11} style={{ flexShrink: 0 }} />
                <span style={{
                  flex: 1, minWidth: 0, overflow: 'hidden',
                  textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }} title={r.anchorHeading}>{r.anchorHeading}</span>
                <button onClick={() => removeRemark(i)} aria-label="Удалить замечание" style={{
                  border: 'none', background: 'none', cursor: 'pointer',
                  color: C.textMuted, padding: 0, display: 'flex',
                }}>
                  <Trash2 size={12} />
                </button>
              </div>
              {r.quote && (
                <div style={{
                  fontSize: FS.sm, color: C.textSecondary, fontStyle: 'italic',
                  borderLeft: `2px solid ${C.accent}`, paddingLeft: SP.xs,
                  overflow: 'hidden', display: '-webkit-box',
                  WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
                }}>«{r.quote}»</div>
              )}
              <div style={{ fontSize: FS.base, color: C.textHeading }}>{r.text}</div>
            </div>
          ))}
        </div>
      )}

      {/* Счётчик + кнопка отправки */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{
          display: 'inline-flex', alignItems: 'center', gap: 5,
          fontSize: FS.sm,
          padding: '4px 9px', borderRadius: R.pill,
          background: remarks.length > 0 ? C.accentLight : C.bgInset,
          color: remarks.length > 0 ? C.textHeading : C.textMuted,
          fontWeight: 600, fontFamily: FONT.sans,
        }}>Замечаний: {remarks.length}</span>

        {remarks.length > 0 && (
          <button onClick={clearAll} style={{
            padding: '6px 10px', background: 'transparent',
            border: `1px solid ${C.border}`, color: C.textMuted,
            borderRadius: R.md, fontSize: FS.base, cursor: 'pointer',
            fontFamily: FONT.sans,
          }}>Сбросить</button>
        )}

        <div style={{ flex: 1 }} />

        <button onClick={submit} style={{
          padding: '8px 14px', minHeight: 40,
          background: remarks.length > 0 ? C.accent : C.bgInset,
          color: remarks.length > 0 ? C.onAccent : C.textSecondary,
          border: 'none', borderRadius: R.lg,
          fontSize: FS.base, fontWeight: 600,
          cursor: 'pointer', fontFamily: FONT.sans,
          display: 'inline-flex', alignItems: 'center', gap: 6,
        }}>
          <MessageSquarePlus size={14} />
          Отправить на доработку ({remarks.length})
        </button>
      </div>
    </div>
  );

  return (
    <>
      {selectionPopup}
      {formPopup}
      {bottom}
    </>
  );
}
