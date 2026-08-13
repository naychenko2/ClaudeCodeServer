import { useEffect, useRef, useState } from 'react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { Button } from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import { showToast } from '../../lib/toast';
import { disableUiInspector } from '../../lib/uiInspector';
import { buildUiChain, type ChainLevel, type ChainNode } from './uiChain';
import { UiNoteDialog } from './UiNoteDialog';

// Подсвеченный элемент под курсором: рамка + плашка с путём к исходнику
interface HoverBox {
  top: number; left: number; width: number; height: number;
  src: string;
}

// Событие пришло из собственного UI инспектора (кнопка выхода, рамка) — не перехватываем,
// иначе инспектор нельзя было бы выключить кликом
function isOwn(t: EventTarget | null): boolean {
  return t instanceof Element && !!t.closest('[data-cc-inspector]');
}

// Позиция пилюли после перетаскивания — переживает выключения режима в рамках вкладки;
// null — дефолт (по центру снизу)
let pillPos: { x: number; y: number } | null = null;

// Оверлей UI-инспектора: App монтирует при включённом режиме. Перехватывает клики в
// capture-фазе (полный набор pointerdown+mousedown+click — иначе dnd-kit/меню стартуют
// по pointerdown раньше клика), строит цепочку исходников и открывает форму аннотации.
export function UiInspectorOverlay() {
  const [hover, setHover] = useState<HoverBox | null>(null);
  const [chain, setChain] = useState<ChainLevel[] | null>(null);
  const isMobile = useIsMobile();
  const formOpen = chain !== null;

  // Перетаскивание пилюли: pointer-события с capture — работает и мышью, и тачем.
  // Drag начинается с любой точки пилюли, кроме кнопки «Выключить»
  const [pos, setPos] = useState(pillPos);
  const drag = useRef<{ dx: number; dy: number } | null>(null);
  const onPillDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.target instanceof Element && e.target.closest('button')) return;
    const r = e.currentTarget.getBoundingClientRect();
    drag.current = { dx: e.clientX - r.left, dy: e.clientY - r.top };
    e.currentTarget.setPointerCapture(e.pointerId);
  };
  const onPillMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    const el = e.currentTarget;
    const x = Math.min(Math.max(e.clientX - drag.current.dx, SP.xs), window.innerWidth - el.offsetWidth - SP.xs);
    const y = Math.min(Math.max(e.clientY - drag.current.dy, SP.xs), window.innerHeight - el.offsetHeight - SP.xs);
    pillPos = { x, y };
    setPos(pillPos);
  };
  const onPillUp = () => { drag.current = null; };

  // Пока открыта форма, Ctrl+Alt+I глотаем в capture-фазе: иначе глобальный хоткей
  // выключит режим, App размонтирует оверлей вместе с диалогом — и набранный
  // комментарий пропадёт
  useEffect(() => {
    if (!formOpen) return;
    const swallow = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.altKey && e.code === 'KeyI') { e.preventDefault(); e.stopPropagation(); }
    };
    document.addEventListener('keydown', swallow, true);
    return () => document.removeEventListener('keydown', swallow, true);
  }, [formOpen]);

  useEffect(() => {
    // Пока открыта форма, перехват снят целиком: модалка живёт порталом в body и
    // capture-слушатели заблокировали бы её собственные клики. Esc при открытой
    // форме ловит Modal (закрывает форму) — приоритет из плана соблюдён сам собой.
    if (formOpen) return;

    // Гасим взаимодействие ДО клика: dnd-kit и меню стартуют по pointerdown
    const block = (e: Event) => {
      if (isOwn(e.target)) return;
      e.preventDefault();
      e.stopPropagation();
    };
    const onClick = (e: MouseEvent) => {
      if (isOwn(e.target)) return;
      e.preventDefault();
      e.stopPropagation();
      // Element структурно совместим с утиным ChainNode
      const levels = buildUiChain(e.target instanceof Element ? (e.target as unknown as ChainNode) : null);
      if (levels.length === 0) {
        showToast('Инспектор UI', 'У этого элемента нет привязки к исходнику');
        return;
      }
      setHover(null);
      setChain(levels);
    };
    const onMove = (e: MouseEvent) => {
      const t = e.target;
      if (!(t instanceof Element) || isOwn(t)) { setHover(null); return; }
      const el = t.closest('[data-cc-src]');
      if (!el) { setHover(null); return; }
      const src = el.getAttribute('data-cc-src') ?? '';
      const r = el.getBoundingClientRect();
      // Обновляем только при реальном изменении — mousemove сыплется десятками в секунду
      setHover(prev =>
        prev && prev.src === src && prev.top === r.top && prev.left === r.left
          && prev.width === r.width && prev.height === r.height
          ? prev
          : { top: r.top, left: r.left, width: r.width, height: r.height, src });
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      e.preventDefault();
      disableUiInspector();
    };
    document.addEventListener('pointerdown', block, true);
    document.addEventListener('mousedown', block, true);
    document.addEventListener('click', onClick, true);
    document.addEventListener('mousemove', onMove, true);
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('pointerdown', block, true);
      document.removeEventListener('mousedown', block, true);
      document.removeEventListener('click', onClick, true);
      document.removeEventListener('mousemove', onMove, true);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [formOpen]);

  // Плашка с путём: под рамкой, а у нижней кромки экрана — над ней
  const labelBelow = hover ? hover.top + hover.height + 30 < window.innerHeight : true;

  return (
    <>
      {hover && !formOpen && (
        <>
          <div data-cc-inspector="1" style={{
            position: 'fixed', top: hover.top - 2, left: hover.left - 2,
            width: hover.width + 4, height: hover.height + 4,
            border: `2px solid ${C.accent}`, borderRadius: R.sm,
            pointerEvents: 'none', zIndex: Z.inspector, boxSizing: 'border-box',
          }} />
          <div data-cc-inspector="1" style={{
            position: 'fixed', left: Math.max(SP.xs, hover.left),
            top: labelBelow ? hover.top + hover.height + 6 : hover.top - 28,
            background: C.navInk, color: C.onNavInk, fontFamily: FONT.mono, fontSize: FS.xs,
            padding: '3px 8px', borderRadius: R.md, pointerEvents: 'none', zIndex: Z.inspector,
            maxWidth: 'calc(100vw - 16px)', overflow: 'hidden', textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}>
            {hover.src}
          </div>
        </>
      )}
      {/* Плавающая пилюля — единственный выход на мобиле (Esc на таче нет); двигается
          перетаскиванием за любое место, кроме кнопки */}
      {!formOpen && (
        <div data-cc-inspector="1"
          onPointerDown={onPillDown} onPointerMove={onPillMove}
          onPointerUp={onPillUp} onPointerCancel={onPillUp}
          style={{
            position: 'fixed',
            ...(pos
              ? { left: pos.x, top: pos.y }
              : { bottom: SP.lg, left: '50%', transform: 'translateX(-50%)' }),
            zIndex: Z.inspector, display: 'flex', alignItems: 'center', gap: SP.sm,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.max,
            boxShadow: SHADOW.dropdown, padding: `6px ${SP.sm}px 6px ${SP.lg}px`,
            maxWidth: 'calc(100vw - 24px)', touchAction: 'none',
            cursor: drag.current ? 'grabbing' : 'grab', userSelect: 'none',
          }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap' }}>
            {isMobile ? 'Инспектор UI: тапни элемент' : 'Инспектор UI: кликни по элементу'}
          </span>
          <Button size="xs" variant="secondary" onClick={disableUiInspector}
            title={isMobile ? undefined : 'Esc'}>
            Выключить
          </Button>
        </div>
      )}
      {chain && <UiNoteDialog chain={chain} onClose={() => setChain(null)} />}
    </>
  );
}
