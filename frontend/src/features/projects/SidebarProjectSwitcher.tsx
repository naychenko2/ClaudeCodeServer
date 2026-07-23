import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Pin, Plus, Search, Settings } from 'lucide-react';
import { C, R, FS, FONT, Z, SHADOW } from '../../lib/design';
import type { Project } from '../../types';
import { ProjectIcon } from './ProjectIcon';
import { useAllProjects, openProjectViaEvent } from './useAllProjects';
import { usePinnedIds, useSwitcherOrder, recordSwitcherProject, isPinned, togglePin, unpinProject, pinInsertAt, switcherInsertBefore } from '../../lib/pinnedProjects';
import { useProjectActivity, type ProjectActivity } from '../../lib/projectActivity';
import { ProjectPalette } from './ProjectPalette';

// Переключатель проектов в плашке сайдбара — единая строка. Проекты идут в СТАБИЛЬНОМ
// порядке (закрепленные > незакрепленные, append-only); активный стоит НА СВОЕМ месте и
// разворачивается в «чип» [иконка + имя + шестерёнка], остальные — компактные иконки.
// При выборе другого проекта чип просто переезжает на него — порядок НЕ меняется.
// Когда проект один — чип растягивается на всю строку (аккуратная «шапка»).
// В хвосте: «+» новый проект (только если всё влезло) либо лупа «+N» → палитра.
// Перетаскивание иконок — pointer-events: призрак + placeholder; сторона разделителя
// решает пин/недавние. Активный чип в перетаскивании не участвует (только позиция).

const ICON_W = 38;    // шаг иконки проекта (36px кнопка + gap 2)
const CHIP_MIN = 84;  // минимальная ширина чипа (иконка + немного имени)
const CHIP_RICH = 140; // ширина чипа, при которой имя читаемо → включаем «богатый» режим
const PLUS_W = 36;    // кнопка «+ новый проект»
const LUPA_W = 46;    // резерв под лупу «+N»
const SEP_W = 10;     // вертикальный разделитель групп + отступы
const MAX_SLOTS = 16; // страховочный потолок значков
const DRAG_THRESHOLD = 5;  // порог в px: клик → перетаскивание
const GAP_OPEN = 22;       // расступание иконок перед placeholder-линией

const STATUS_COLOR: Record<ProjectActivity['status'], string> = {
  waiting: C.accent,
  working: C.success,
};

const STATUS_TITLE: Record<ProjectActivity['status'], string> = {
  waiting: 'агент ждет ответа',
  working: 'агент работает',
};

// Иконка проекта-кандидата (не активного) со статус-точкой и контекст-меню.
// Перетаскивание — снаружи (pointer-события ловит родитель).
function CandidateIcon({ p, activity, active, dragging, shift, onPointerDown, onClick, onContextMenu }: {
  p: Project;
  activity?: ProjectActivity;
  active?: boolean;               // компактный режим: текущий проект — двойное акцент-кольцо
  dragging: boolean;
  shift: number;
  onPointerDown: (e: React.PointerEvent, p: Project) => void;
  onClick: (p: Project) => void;
  onContextMenu: (e: React.MouseEvent, p: Project) => void;
}) {
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (pressTimer.current) clearTimeout(pressTimer.current); }, []);
  const title = activity ? `${p.name} — ${STATUS_TITLE[activity.status]}` : p.name;
  return (
    <button
      data-swicon={p.id}
      title={title}
      aria-label={title}
      onPointerDown={e => onPointerDown(e, p)}
      onClick={() => onClick(p)}
      onContextMenu={e => onContextMenu(e, p)}
      onTouchStart={e => {
        const t = e.touches[0];
        pressTimer.current = setTimeout(() => {
          onContextMenu({ preventDefault: () => {}, clientX: t.clientX, clientY: t.clientY } as React.MouseEvent, p);
        }, 500);
      }}
      onTouchEnd={() => { if (pressTimer.current) clearTimeout(pressTimer.current); }}
      onTouchMove={() => { if (pressTimer.current) clearTimeout(pressTimer.current); }}
      style={{
        position: 'relative', padding: 2, border: 'none', background: 'transparent',
        cursor: 'pointer', display: 'flex', flexShrink: 0, borderRadius: R.sm,
        opacity: dragging ? 0.35 : 1, transform: shift ? `translateX(${shift}px)` : undefined,
        transition: 'opacity 0.12s, transform 0.13s cubic-bezier(0.2, 0, 0, 1)', touchAction: 'none',
      }}
    >
      <span style={{
        display: 'flex', borderRadius: 8, pointerEvents: 'none',
        boxShadow: active ? `0 0 0 2px ${C.bgPanel}, 0 0 0 4px ${C.accent}` : undefined,
      }}>
        <ProjectIcon project={p} size={32} radius={8} />
      </span>
      {activity && (
        <span style={{
          position: 'absolute', right: 0, top: 0, width: 10, height: 10, borderRadius: '50%',
          background: STATUS_COLOR[activity.status], border: `2px solid ${C.bgPanel}`,
          boxSizing: 'content-box', pointerEvents: 'none',
        }} />
      )}
    </button>
  );
}

export function SidebarProjectSwitcher({ project, onOpenSettings }: {
  project: Project;               // активный проект (свежая версия из WorkspacePage)
  onOpenSettings: () => void;     // клик по чипу активного — настройки проекта
}) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const switcherOrder = useSwitcherOrder();
  const activity = useProjectActivity();

  useEffect(() => { recordSwitcherProject(project.id); }, [project.id]);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [chipHover, setChipHover] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number; p: Project } | null>(null);

  const rowRef = useRef<HTMLDivElement>(null);
  const [rowW, setRowW] = useState(0);
  useLayoutEffect(() => {
    const el = rowRef.current;
    if (!el) return;
    setRowW(el.getBoundingClientRect().width);
    const ro = new ResizeObserver(entries => setRowW(entries[0]?.contentRect.width ?? 0));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Все проекты плашки в СТАБИЛЬНОМ порядке, включая активного НА ЕГО месте (пины >
  // незакрепленные append-only). Активный не прыгает при переключении — просто становится чипом.
  const items = useMemo(() => {
    const byId = new Map(projects.map(p => [p.id, p]));
    const seen = new Set<string>();
    const out: Project[] = [];
    const push = (id: string) => {
      if (seen.has(id)) return;
      const p = byId.get(id);
      if (!p) return;
      seen.add(id);
      out.push(p);
    };
    pinnedIds.forEach(push);
    switcherOrder.forEach(push);
    if (!seen.has(project.id)) {
      const p = byId.get(project.id);
      if (p) out.push(p);
    }
    return out;
  }, [projects, project.id, pinnedIds, switcherOrder]);

  const hasPins = items.some(p => isPinned(p.id));
  const hasRecent = items.some(p => !isPinned(p.id));
  const sepReserve = hasPins && hasRecent ? SEP_W : 0;
  const otherCount = Math.max(0, items.length - 1);
  // «Богатый» режим (чип активного с именем) — пока читаемый чип + все остальные иконки
  // влезают. Тесно → «компактный»: активный обычной иконкой с кольцом. Режим зависит от
  // rowW → при ресайзе панели переключается автоматически.
  const rich = rowW > 0 && (CHIP_RICH + otherCount * ICON_W + sepReserve + PLUS_W + 6) <= rowW;
  let shown: Project[];
  if (rich) {
    shown = items.slice(0, MAX_SLOTS);
  } else {
    // Компактный: все проекты как иконки. Влезают все → «+», иначе окно + лупа «+N».
    const fitAll = Math.floor((rowW - PLUS_W - sepReserve - 6) / ICON_W) >= items.length;
    const slots = Math.max(0, Math.min(MAX_SLOTS, fitAll
      ? items.length
      : Math.floor((rowW - LUPA_W - sepReserve - 6) / ICON_W)));
    shown = items.slice(0, slots);
    // Активный обязан быть виден (редкий случай переполнения)
    if (slots > 0 && !shown.some(p => p.id === project.id)) {
      const a = items.find(p => p.id === project.id);
      if (a) shown = [...items.slice(0, slots - 1), a];
    }
  }
  const firstRecentIdx = shown.findIndex(p => !isPinned(p.id));
  const hiddenCount = Math.max(0, projects.length - shown.length);
  const shownIds = new Set(shown.map(p => p.id));
  const hiddenWaiting = items.some(p => !shownIds.has(p.id) && activity.get(p.id)?.status === 'waiting');
  const pinsShown = shown.some(p => isPinned(p.id));

  const shownRef = useRef<Project[]>(shown);
  shownRef.current = shown;

  const openCandidate = useCallback((p: Project) => {
    const a = activity.get(p.id);
    if (a?.status === 'waiting' && a.waitingChatId) {
      window.dispatchEvent(new CustomEvent('cc-open-url', {
        detail: { url: `#/project/${p.id}/chat/${encodeURIComponent(a.waitingChatId)}` },
      }));
      return;
    }
    openProjectViaEvent(p);
  }, [activity]);

  const openNewProject = () => {
    sessionStorage.setItem('cc_pending_new_project', '1');
    window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: '#/projects' } }));
  };

  // === Перетаскивание на pointer-событиях (среди иконок; активный чип — только позиция) ===
  const dragRef = useRef<{ id: string; sx: number; sy: number; started: boolean; insertIdx: number; zone: 'pin' | 'recent' } | null>(null);
  const suppressClick = useRef(false);
  const [dragView, setDragView] = useState<{ id: string; x: number; y: number; lineLeft: number; insertIdx: number; zone: 'pin' | 'recent' } | null>(null);

  const computeInsert = useCallback((clientX: number) => {
    const row = rowRef.current;
    if (!row) return { insertIdx: 0, lineLeft: 0, zone: 'recent' as const };
    const localX = clientX - row.getBoundingClientRect().left;
    const icons = Array.from(row.querySelectorAll<HTMLElement>('[data-swicon]'));
    let idx = icons.length;
    for (let i = 0; i < icons.length; i++) {
      const el = icons[i];
      if (localX < el.offsetLeft + el.offsetWidth / 2) { idx = i; break; }
    }
    let lineLeft: number;
    if (idx < icons.length) lineLeft = icons[idx].offsetLeft + GAP_OPEN / 2 - 1;
    else if (icons.length) { const last = icons[icons.length - 1]; lineLeft = last.offsetLeft + last.offsetWidth + 2; }
    else lineLeft = 0;
    const sep = row.querySelector<HTMLElement>('[data-sep]');
    let zone: 'pin' | 'recent';
    if (sep) zone = localX < sep.offsetLeft + sep.offsetWidth / 2 ? 'pin' : 'recent';
    else {
      const sh = shownRef.current;
      zone = sh.length > 0 && sh.every(p => isPinned(p.id)) ? 'pin' : 'recent';
    }
    return { insertIdx: idx, lineLeft, zone };
  }, []);

  // insertIdx считается среди ВСЕХ data-swicon (иконки + чип активного). Для магазина
  // порядок shown совпадает со стором (пины > недавние), поэтому индексы согласованы.
  const applyDrop = useCallback((id: string, insertIdx: number, zone: 'pin' | 'recent') => {
    const sh = shownRef.current;
    const pinsCount = sh.filter(p => isPinned(p.id)).length;
    if (zone === 'pin') {
      pinInsertAt(id, Math.min(insertIdx, pinsCount));
    } else {
      if (isPinned(id)) unpinProject(id);
      const beforeId = insertIdx < sh.length ? sh[insertIdx].id : null;
      switcherInsertBefore(id, beforeId);
    }
  }, []);

  const onDragMove = useCallback((e: PointerEvent) => {
    const g = dragRef.current;
    if (!g) return;
    if (!g.started) {
      if (Math.hypot(e.clientX - g.sx, e.clientY - g.sy) < DRAG_THRESHOLD) return;
      g.started = true;
    }
    const { insertIdx, lineLeft, zone } = computeInsert(e.clientX);
    g.insertIdx = insertIdx;
    g.zone = zone;
    setDragView({ id: g.id, x: e.clientX, y: e.clientY, lineLeft, insertIdx, zone });
  }, [computeInsert]);

  const onDragUp = useCallback(() => {
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', onDragUp);
    const g = dragRef.current;
    dragRef.current = null;
    setDragView(null);
    if (g?.started) { suppressClick.current = true; applyDrop(g.id, g.insertIdx, g.zone); }
  }, [onDragMove, applyDrop]);

  const onIconPointerDown = useCallback((e: React.PointerEvent, p: Project) => {
    if (e.button !== 0) return;
    dragRef.current = { id: p.id, sx: e.clientX, sy: e.clientY, started: false, insertIdx: 0, zone: 'recent' };
    window.addEventListener('pointermove', onDragMove);
    window.addEventListener('pointerup', onDragUp);
  }, [onDragMove, onDragUp]);

  useEffect(() => () => {
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', onDragUp);
  }, [onDragMove, onDragUp]);

  const onIconClick = useCallback((p: Project) => {
    if (suppressClick.current) { suppressClick.current = false; return; }
    // Компактный режим: клик по активной иконке (шестерёнки нет) — настройки проекта
    if (p.id === project.id) onOpenSettings();
    else openCandidate(p);
  }, [openCandidate, onOpenSettings, project.id]);

  const openMenu = (e: React.MouseEvent, p: Project) => {
    e.preventDefault();
    const x = Math.min(e.clientX, window.innerWidth - 180);
    const y = Math.min(e.clientY, window.innerHeight - 90);
    setMenu({ x, y, p });
  };

  useEffect(() => {
    if (!menu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [menu]);

  const menuItemStyle: React.CSSProperties = {
    display: 'block', width: '100%', textAlign: 'left', padding: '7px 14px',
    border: 'none', background: 'transparent', cursor: 'pointer',
    fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
  };

  const dragProject = dragView ? projects.find(p => p.id === dragView.id) : null;
  const iconBtn: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
    border: 'none', background: 'transparent', cursor: 'pointer', color: C.textSecondary,
  };

  return (
    <div style={{ display: 'flex', flex: 1, minWidth: 0 }}>
      <style>{`
        @keyframes ccGhostPop { from { transform: scale(0.8) rotate(-3deg); opacity: 0 } to { transform: scale(1) rotate(-3deg); opacity: 0.95 } }
        @keyframes ccLineIn { from { opacity: 0; transform: scaleY(0.4) } to { opacity: 1; transform: scaleY(1) } }
      `}</style>
      <div ref={rowRef} style={{ position: 'relative', display: 'flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 0 }}>
        {/* Пинов среди показанных нет: при драге СЛЕВА (зона пинов) появляется цель
            «закрепить» + разделитель. Сторона разделителя решает пин/недавние. */}
        {dragView && !pinsShown && (
          <>
            <span aria-hidden title="Перетащите сюда, чтобы закрепить" style={{
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
              width: 34, height: 34, borderRadius: 8,
              border: `2px dashed ${dragView.zone === 'pin' ? C.accent : C.textMuted}`,
              background: dragView.zone === 'pin' ? C.bgSelected : 'transparent',
              color: C.accent, transition: 'border-color 0.12s, background 0.12s',
            }}>
              <Pin size={14} strokeWidth={2} />
            </span>
            <span data-sep style={{ width: 2, height: 26, background: C.divider, borderRadius: 1, flexShrink: 0, margin: '0 3px' }} />
          </>
        )}
        {shown.map((p, i) => {
          const shift = dragView && i >= dragView.insertIdx ? GAP_OPEN : 0;
          const sepShift = dragView && firstRecentIdx >= dragView.insertIdx ? GAP_OPEN : 0;
          const sep = i === firstRecentIdx && firstRecentIdx > 0 ? (
            <span data-sep style={{
              width: 2, height: 26, background: C.divider, borderRadius: 1, flexShrink: 0, margin: '0 3px',
              transform: sepShift ? `translateX(${sepShift}px)` : undefined,
              transition: 'transform 0.13s cubic-bezier(0.2, 0, 0, 1)',
            }} />
          ) : null;
          if (p.id === project.id && rich) {
            // Богатый режим: активный проект — чип с именем на своём месте (drag не участвует)
            return (
              <Fragment key={p.id}>
                {sep}
                <div
                  data-swicon={p.id}
                  title={p.name}
                  onMouseEnter={() => setChipHover(true)}
                  onMouseLeave={() => setChipHover(false)}
                  style={{
                    position: 'relative', display: 'flex', alignItems: 'center', gap: 9,
                    flex: '1 1 auto', minWidth: CHIP_MIN,
                    padding: '4px 30px 4px 5px', textAlign: 'left',
                    background: chipHover ? C.bgInset : C.bgSelected, borderRadius: 9,
                    transform: shift ? `translateX(${shift}px)` : undefined,
                    transition: 'background 0.12s, transform 0.13s cubic-bezier(0.2, 0, 0, 1)',
                  }}
                >
                  <span style={{ display: 'flex', flexShrink: 0 }}><ProjectIcon project={p} size={32} radius={8} /></span>
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading,
                    flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {p.name}
                  </span>
                  {/* Настройки открываются ТОЛЬКО по клику на шестерёнку */}
                  <button
                    title="Настройки проекта"
                    aria-label="Настройки проекта"
                    onClick={e => { e.stopPropagation(); onOpenSettings(); }}
                    style={{
                      position: 'absolute', right: 3, top: '50%', transform: 'translateY(-50%)',
                      display: 'flex', alignItems: 'center', justifyContent: 'center', width: 22, height: 22,
                      padding: 0, border: 'none', background: 'transparent', cursor: 'pointer', borderRadius: R.sm,
                      color: chipHover ? C.textHeading : C.textSecondary, transition: 'color 0.12s',
                    }}
                  >
                    <Settings size={13} strokeWidth={2.2} />
                  </button>
                </div>
              </Fragment>
            );
          }
          return (
            <Fragment key={p.id}>
              {sep}
              <CandidateIcon
                p={p}
                active={p.id === project.id}
                activity={activity.get(p.id)}
                dragging={dragView?.id === p.id}
                shift={shift}
                onPointerDown={onIconPointerDown}
                onClick={onIconClick}
                onContextMenu={openMenu}
              />
            </Fragment>
          );
        })}

        {/* «+» новый проект — только когда есть место (нет «+N») */}
        {hiddenCount === 0 && (
          <button
            aria-label="Новый проект"
            title="Новый проект"
            onClick={openNewProject}
            style={{
              ...iconBtn, width: 32, height: 32, borderRadius: 8, border: `1.5px dashed ${C.dashed}`,
              // В богатом режиме вправо толкает растянутый чип; в компактном — auto-отступ
              marginLeft: rich ? undefined : 'auto',
            }}
          >
            <Plus size={16} strokeWidth={2} />
          </button>
        )}

        {/* Лупа / «+N»: палитра всех проектов; микро-точка — скрытый «ждущий» проект */}
        {hiddenCount > 0 && (
          <button
            aria-label="Все проекты (палитра)"
            title={`Еще ${hiddenCount} проектов`}
            onClick={() => setPaletteOpen(true)}
            style={{
              ...iconBtn, gap: 2, position: 'relative', padding: '3px 6px', borderRadius: R.sm, background: C.bgSelected,
              // В богатом режиме вправо толкает растянутый чип; в компактном — auto-отступ
              marginLeft: rich ? undefined : 'auto',
            }}
          >
            <Search size={16} strokeWidth={2} />
            <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600 }}>+{hiddenCount}</span>
            {hiddenWaiting && (
              <span style={{
                position: 'absolute', right: -1, top: -1, width: 8, height: 8, borderRadius: '50%',
                background: C.accent, border: `2px solid ${C.bgPanel}`, boxSizing: 'content-box',
              }} />
            )}
          </button>
        )}

        {/* Placeholder-линия (прячем, когда цель — зона закрепления) */}
        {dragView && !(dragView.zone === 'pin' && !pinsShown) && (
          <span aria-hidden style={{
            position: 'absolute', left: dragView.lineLeft, top: 2, width: 2, height: 34,
            background: C.accent, borderRadius: 1, pointerEvents: 'none', transformOrigin: 'center',
            transition: 'left 0.13s cubic-bezier(0.2, 0, 0, 1)', animation: 'ccLineIn 0.12s ease-out',
          }} />
        )}
      </div>

      {/* Призрак перетаскиваемой иконки под курсором */}
      {dragView && dragProject && (
        <div aria-hidden style={{
          position: 'fixed', left: dragView.x - 18, top: dragView.y - 18, zIndex: Z.modal,
          pointerEvents: 'none', opacity: 0.95, transform: 'scale(1) rotate(-3deg)',
          boxShadow: SHADOW.dropdown, borderRadius: 8, animation: 'ccGhostPop 0.12s ease-out',
        }}>
          <ProjectIcon project={dragProject} size={36} radius={8} />
        </div>
      )}
      {paletteOpen && <ProjectPalette currentProjectId={project.id} onClose={() => setPaletteOpen(false)} />}
      {menu && (
        <div onClick={() => setMenu(null)} onContextMenu={e => { e.preventDefault(); setMenu(null); }}
          style={{ position: 'fixed', inset: 0, zIndex: Z.modal }}>
          <div
            onClick={e => e.stopPropagation()}
            style={{
              position: 'fixed', left: menu.x, top: menu.y, minWidth: 160,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
              boxShadow: SHADOW.modal, overflow: 'hidden', padding: '4px 0',
            }}
          >
            <button style={menuItemStyle}
              onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              onClick={() => { openCandidate(menu.p); setMenu(null); }}>
              Открыть
            </button>
            <button style={menuItemStyle}
              onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              onClick={() => { togglePin(menu.p.id); setMenu(null); }}>
              {isPinned(menu.p.id) ? 'Открепить' : 'Закрепить'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
