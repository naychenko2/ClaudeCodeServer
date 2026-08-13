import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { ArrowDownToLine, Pin, Plus, Search } from 'lucide-react';
import { C, R, FS, FONT, Z, SHADOW } from '../../lib/design';
import type { Project } from '../../types';
import { RailCapsule, RailIconButton, RailSep } from '../../components/ui';
import { PanelDropLine } from '../../components/ui/PanelDropGuide';
import { ICON_STROKE } from '../../components/ui/icons';
import { ProjectIcon } from './ProjectIcon';
import { ProjectPalette } from './ProjectPalette';
import { useAllProjects, openProjectViaEvent, openNewProjectFlow } from './useAllProjects';
import { usePinnedIds, useSwitcherOrder, recordSwitcherProject, isPinned, togglePin, unpinProject, pinInsertAt, switcherInsertBefore, removeFromDock } from '../../lib/pinnedProjects';
import { useProjectActivity, STATUS_COLOR, STATUS_PULSE, type ProjectActivity } from '../../lib/projectActivity';

// Вертикальный док проектов — ВТОРАЯ левая рельса, под рельсой панелей. Раньше те же
// иконки лежали горизонтальной строкой внутри панели «Проекты»: ряд в колонке шириной
// с сайдбар вмещал мало и при этом занимал место в рельсе. В вертикали иконок помещается
// столько, сколько даёт высота окна, а сама рельса ширины у контента не отнимает.
//
// Сверху вниз: «+» новый проект | закреплённые | недавние | поиск.
// Порядок проектов СТАБИЛЬНЫЙ (закреплённые > незакреплённые, append-only): выбор другого
// проекта иконки не переставляет, активный лишь подсвечивается, как активная иконка
// в рельсе панелей. Настройки проекта живут в подписи активной иконки (RailFlyout):
// постоянной кнопки они не стоят — это действие ОДНОГО проекта, а не всего дока.
// Перетаскивание — pointer-события: призрак под курсором + линия места вставки; сторона
// разделителя (выше/ниже) решает, попадёт проект в закреплённые или в недавние. Когда
// закреплённых нет вовсе, зона пинов — «выше первой иконки», и линия несёт булавку:
// отдельная мишень-квадрат раньше вставала В ПОТОК и сдвигала весь док вниз.
// Что не влезло по высоте — «+N» на лупе, оттуда палитра со всеми проектами.

const ICON_BOX = 32;          // бокс кнопки проекта — как у кнопок рельсы (IconButton md)
const CAP_GAP = 6;            // зазор между элементами капсулы (как в PanelRail)
const STEP = ICON_BOX + CAP_GAP;
// Высота несменяемой части капсулы: паддинги, «+», два сепаратора и лупа с их
// зазорами. По ней из свободной высоты считается число слотов под иконки.
const FIXED_H = 100;
// Потолок значков. Не про место (по высоте влезло бы вдвое больше), а про то, что
// док ищут глазами: два десятка одинаковых квадратов читаются медленнее, чем поиск
// по имени в палитре, и съедают экран. Что не влезло — «+N» на лупе.
const MAX_SLOTS = 12;
const DRAG_THRESHOLD = 5;     // порог в px: клик → перетаскивание

// Вид точки (цвет + мерцание) — общий для всех рельс: STATUS_COLOR/STATUS_PULSE
// из projectActivity. Здесь остаются только подписи — они говорят про ПРОЕКТ
const STATUS_TITLE: Record<ProjectActivity['status'], string> = {
  waiting: 'агент ждет ответа',
  working: 'агент работает',
  unread: 'есть непрочитанные чаты',
};

// Кнопка проекта в доке. Тот же примитив, что у всех кнопок рельсы (IconButton):
// одинаковые бокс, скругление, hover-подложка, подсветка активного и focus-ring —
// внутри вместо lucide-иконки квадратик проекта. Перетаскивание и контекст-меню
// ловит span-обёртка (как dragProps у иконок панелей): pointer-события кнопке не
// принадлежат, а прокидывать их сквозь примитив значило бы дырявить его API.
function ProjectDockIcon({ p, activity, active, muted, dragging, dragActive, side, onPointerDown, onClick, onContextMenu, onHide }: {
  p: Project;
  activity?: ProjectActivity;
  active: boolean;
  // Приглушить иконку (спящая рельса, НЕ выбранный проект): обесцветить картинку
  // или заменить плашку бледным контуром. Активный muted не получает — он всегда
  // цветной и помечен кольцом кнопки; на фоне спящих он и читается как выбранный.
  // Решает док: состояние общее для всего ряда, кнопке о нём знать неоткуда.
  muted?: boolean;
  dragging: boolean;
  // В доке кого-то тащат: подписи не показываем никому — они лезли бы поверх места
  // вставки и подсказывали не про то, чем человек сейчас занят
  dragActive: boolean;
  side: 'left' | 'right';
  onPointerDown: (e: React.PointerEvent, p: Project) => void;
  onClick: (p: Project) => void;
  onContextMenu: (e: React.MouseEvent, p: Project) => void;
  // Убрать иконку из дока — кнопка в подписи. Не задана (открытый проект) — подпись
  // просто называет иконку.
  onHide?: (p: Project) => void;
}) {
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (pressTimer.current) clearTimeout(pressTimer.current); }, []);
  const label = activity ? `${p.name} — ${STATUS_TITLE[activity.status]}` : p.name;
  return (
    <span
      data-swicon={p.id}
      onPointerDown={e => onPointerDown(e, p)}
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
        display: 'flex', flexShrink: 0, position: 'relative',
        opacity: dragging ? 0.35 : 1,
        transition: 'opacity 0.12s', touchAction: 'none',
      }}
    >
      {/* Кнопка-картинка: иконка проекта занимает бокс ЦЕЛИКОМ (штриховым иконкам
          панелей нужен воздух вокруг глифа, картинке — нет). Активный проект ВСЕГДА
          цветной — картинка в цвете или цветная плашка, — а в покое рельсы спят
          остальные (muted: grayscale/контур). Выбранный при этом помечен кольцом
          кнопки (variant="media" + active); ring включается именно когда иконка не
          приглушена, т.е. у активного в любом состоянии рельсы (см. ProjectIcon). */}
      <RailIconButton
        side={side}
        label={label}
        variant="media"
        active={active && !muted}
        hoverSuppressed={dragActive}
        onClick={() => onClick(p)}
        // Действие живёт в подписи, а не отдельной кнопкой рельсы: убирают ОДИН
        // проект, и целятся при этом в его иконку. Знак — тот же, что у иконок рельсы
        // панелей (ArrowDownToLine / item.onTuck): стрелка ВНИЗ к черте, иконка
        // уезжает в конец столбца — под лупу. Пока иконку тащат, подписи нет вовсе —
        // значит и кнопка не мешает дропу.
        action={onHide && !dragActive ? {
          Icon: ArrowDownToLine,
          title: 'Убрать из дока',
          onClick: () => onHide(p),
        } : undefined}
      >
        <ProjectIcon project={p} size={ICON_BOX} radius={R.md} muted={muted} />
      </RailIconButton>
      {/* Точка статуса живёт НАД кнопкой, а не внутри неё: обесцвечивание невыбранных
          красит всё содержимое кнопки, а «агент ждёт ответа» обязан оставаться
          оранжевым именно у чужого проекта — ради этого сигнала док и смотрят. */}
      {activity && (
        <span className={STATUS_PULSE[activity.status]} style={{
          position: 'absolute', right: -2, top: -2, width: 8, height: 8, borderRadius: R.full,
          // Подложка = цвет холста: она непрозрачна и закрывает иконку проекта под
          // точкой. Цветная заливка живёт в ::after и мерцает, ободок (border) —
          // сплошной, сквозь точку ничего не просвечивает
          background: C.bgMain, border: `2px solid ${C.bgMain}`,
          // Цвет заливки ::after. Подаём переменной: классы cc-dot-* в index.css
          // читают её, а инлайн-фон на ::after анимация бы перебила
          '--cc-dot-c': STATUS_COLOR[activity.status],
          boxSizing: 'content-box', pointerEvents: 'none',
        } as CSSProperties} />
      )}
    </span>
  );
}

export function ProjectRail({ project, onOpenSettings, side = 'left' }: {
  // Активный проект (свежая версия из WorkspacePage). undefined — активного нет:
  // так док выглядит на пустой «Стене», где фокусной колонки ещё не существует.
  // Ряд проектов при этом полноценный, просто ни одна иконка не подсвечена.
  project?: Project;
  onOpenSettings: () => void;   // настройки текущего проекта — из его контекст-меню
  // Сторона окна: в какую сторону раскрываются подписи кнопок
  side?: 'left' | 'right';
}) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const switcherOrder = useSwitcherOrder();
  const activity = useProjectActivity();

  useEffect(() => { if (project) recordSwitcherProject(project.id); }, [project]);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number; p: Project } | null>(null);
  // Курсор в доке — цвет возвращается ВСЕМ иконкам сразу, а не той, что под ним:
  // выбирают проект, сравнивая их между собой, и разглядывать нужно весь ряд.
  const [railHover, setRailHover] = useState(false);

  // Свободная высота под доком: обёртка тянется на весь остаток зоны, капсула внутри
  // стоит по контенту. Отсюда считается, сколько иконок показать.
  const boxRef = useRef<HTMLDivElement>(null);
  const colRef = useRef<HTMLDivElement>(null);
  const [boxH, setBoxH] = useState(0);
  // Меряем РОДИТЕЛЯ (свободное место зоны), а не себя: сам док стоит по контенту,
  // иначе он растягивался бы на весь остаток и выталкивал соседние капсулы (док
  // «Стены») к нижней кромке окна. Родитель — тот, кто отдал доку место.
  useLayoutEffect(() => {
    const el = boxRef.current?.parentElement;
    if (!el) return;
    // borderBoxSize, а не contentRect: у родителя может быть padding, и «место под
    // иконки» считается по внешней коробке. Плюс перезамер следующим кадром —
    // на первом проходе колонка ещё не растянута флексом, и док решил бы, что
    // высоты нет (все проекты уезжали под лупу «ещё N»).
    const measure = () => setBoxH(el.getBoundingClientRect().height);
    measure();
    const raf = requestAnimationFrame(measure);
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => { cancelAnimationFrame(raf); ro.disconnect(); };
  }, []);

  // Проекты дока в СТАБИЛЬНОМ порядке: закреплённые (в порядке закрепления), затем
  // незакреплённые (append-only). Активный, если его в списках ещё нет, — в хвост.
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
    if (project && !seen.has(project.id)) {
      const p = byId.get(project.id);
      if (p) out.push(p);
    }
    return out;
  }, [projects, project, pinnedIds, switcherOrder]);

  // Сколько иконок влезает в свободную высоту. Пока высота не измерена (первый кадр) —
  // не режем: иначе док мигнул бы пустым.
  const slots = boxH > 0
    ? Math.max(0, Math.min(MAX_SLOTS, Math.floor((boxH - FIXED_H) / STEP)))
    : Math.min(MAX_SLOTS, items.length);
  let shown = items.slice(0, slots);
  // Активный обязан быть виден: он и есть точка отсчёта для всего дока
  if (project && slots > 0 && !shown.some(p => p.id === project.id)) {
    const a = items.find(p => p.id === project.id);
    if (a) shown = [...items.slice(0, slots - 1), a];
  }

  const firstRecentIdx = shown.findIndex(p => !isPinned(p.id));
  const shownIds = new Set(shown.map(p => p.id));
  const hiddenCount = Math.max(0, projects.length - shown.length);
  const hiddenWaiting = projects.some(p => !shownIds.has(p.id) && activity.get(p.id)?.status === 'waiting');

  // Зеркало показанного списка для pointer-обработчиков драга (они живут вне рендера)
  const shownRef = useRef<Project[]>(shown);
  useEffect(() => { shownRef.current = shown; });

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

  // === Перетаскивание на pointer-событиях (ось Y) ===
  const dragRef = useRef<{ id: string; sx: number; sy: number; started: boolean; insertIdx: number; zone: 'pin' | 'recent' } | null>(null);
  const suppressClick = useRef(false);
  const [dragView, setDragView] = useState<{ id: string; x: number; y: number; lineTop: number; insertIdx: number; zone: 'pin' | 'recent' } | null>(null);

  const computeInsert = useCallback((clientY: number) => {
    const col = colRef.current;
    if (!col) return { insertIdx: 0, lineTop: 0, zone: 'recent' as const };
    const localY = clientY - col.getBoundingClientRect().top;
    const icons = Array.from(col.querySelectorAll<HTMLElement>('[data-swicon]'));
    let idx = icons.length;
    for (let i = 0; i < icons.length; i++) {
      const el = icons[i];
      if (localY < el.offsetTop + el.offsetHeight / 2) { idx = i; break; }
    }
    // Линия встаёт В ЗАЗОР между иконками — раздвигать их под неё не нужно: в
    // 40px-рельсе любое расступание читается как дёрганье всего дока.
    let lineTop: number;
    if (idx < icons.length) lineTop = icons[idx].offsetTop - CAP_GAP / 2 - 1;
    else if (icons.length) { const last = icons[icons.length - 1]; lineTop = last.offsetTop + last.offsetHeight + CAP_GAP / 2 - 1; }
    else lineTop = 0;
    const sep = col.querySelector<HTMLElement>('[data-sep]');
    let zone: 'pin' | 'recent';
    if (sep) zone = localY < sep.offsetTop + sep.offsetHeight / 2 ? 'pin' : 'recent';
    else {
      // Разделителя нет — значит одна из групп пуста. Все показанные закреплены →
      // и целиться больше некуда. Пинов нет вовсе → зона закрепления это САМЫЙ ВЕРХ
      // дока: линия встала над первой иконкой — проект закрепится. Отдельной мишени
      // под это не заводим: она занимала бы место в потоке и двигала весь док.
      const sh = shownRef.current;
      if (sh.length > 0 && sh.every(p => isPinned(p.id))) zone = 'pin';
      else zone = idx === 0 ? 'pin' : 'recent';
    }
    return { insertIdx: idx, lineTop, zone };
  }, []);

  // insertIdx считается среди ВСЕХ data-swicon дока. Порядок shown совпадает со стором
  // (пины > недавние), поэтому индексы согласованы.
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
    const { insertIdx, lineTop, zone } = computeInsert(e.clientY);
    g.insertIdx = insertIdx;
    g.zone = zone;
    setDragView({ id: g.id, x: e.clientX, y: e.clientY, lineTop, insertIdx, zone });
  }, [computeInsert]);

  // pointerup вешается с { once: true } — снимается сам после срабатывания,
  // поэтому onDragUp не ссылается на себя для removeEventListener
  const onDragUp = useCallback(() => {
    window.removeEventListener('pointermove', onDragMove);
    const g = dragRef.current;
    dragRef.current = null;
    setDragView(null);
    if (g?.started) { suppressClick.current = true; applyDrop(g.id, g.insertIdx, g.zone); }
  }, [onDragMove, applyDrop]);

  const onIconPointerDown = useCallback((e: React.PointerEvent, p: Project) => {
    if (e.button !== 0) return;
    dragRef.current = { id: p.id, sx: e.clientX, sy: e.clientY, started: false, insertIdx: 0, zone: 'recent' };
    window.addEventListener('pointermove', onDragMove);
    window.addEventListener('pointerup', onDragUp, { once: true });
  }, [onDragMove, onDragUp]);

  useEffect(() => () => {
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', onDragUp);
  }, [onDragMove, onDragUp]);

  const onIconHide = useCallback((p: Project) => { removeFromDock(p.id); }, []);

  const onIconClick = useCallback((p: Project) => {
    if (suppressClick.current) { suppressClick.current = false; return; }
    openCandidate(p);
  }, [openCandidate]);


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

  return (
    // Обёртка стоит ПО КОНТЕНТУ (высоту свободного места меряем у родителя — см. выше):
    // так следующая капсула в колонке встаёт сразу под доком, а не улетает вниз.
    // pointerEvents возвращаем: у обёртки рельсы они сняты.
    <div ref={boxRef} style={{
      flexShrink: 0, minHeight: 0, display: 'flex', flexDirection: 'column', alignItems: 'center',
      pointerEvents: 'auto',
    }}>
      <style>{`
        @keyframes ccProjGhostPop { from { transform: scale(0.8) rotate(-3deg); opacity: 0 } to { transform: scale(1) rotate(-3deg); opacity: 0.95 } }
      `}</style>

      {/* Капсула-остров — тот же примитив, что несёт рельсу панелей: геометрия
          (ширина, скругление к центру, бордеры, тень) у всех рельс общая */}
      <RailCapsule
        side={side}
        onMouseEnter={() => setRailHover(true)}
        onMouseLeave={() => setRailHover(false)}
        // Пока иконку тащат, капсула для мыши сквозная: место вставки считается по
        // координатам курсора (события ловит window), а вот наведение на кнопки под
        // ним — чистый мусор. Иначе иконки подсвечивались бы и подпрыгивали ровно там,
        // куда человек целится.
        style={dragView ? { pointerEvents: 'none' } : undefined}
      >
        {/* Поиск по всем проектам: палитра умеет и переход, и создание, и «Все проекты».
            Кружок — сколько проектов не поместилось в док. */}
        <RailIconButton
          side={side}
          label={hiddenCount > 0
            ? `Перейти к проекту (ещё ${hiddenCount}${hiddenWaiting ? ' · агент ждет ответа' : ''})`
            : 'Перейти к проекту'}
          onClick={() => setPaletteOpen(true)}
        >
          <div style={{ position: 'relative', display: 'flex' }}>
            <Search size={17} strokeWidth={ICON_STROKE} />
            {/* Счётчик спрятанных проектов — нейтральный: это справка о размере списка,
                а не событие. Цвет он берёт только когда среди скрытых кто-то ЖДЁТ
                ответа — вот на это оторваться от дела стоит. Тон — тот же warning,
                что у точки waiting и у статуса «ждёт ввода» на карточке чата. */}
            {hiddenCount > 0 && (
              <span style={{
                position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
                borderRadius: 7,
                background: hiddenWaiting ? C.warning : C.bgSelected,
                color: hiddenWaiting ? C.onAccent : C.textSecondary,
                fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
              }}>
                +{hiddenCount}
              </span>
            )}
          </div>
        </RailIconButton>
        <RailSep />

        {/* Столбец иконок: закреплённые, разделитель, недавние */}
        <div ref={colRef} style={{
          position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'center',
          gap: CAP_GAP, flexShrink: 0,
        }}>
          {shown.map((p, i) => {
            const sep = i === firstRecentIdx && firstRecentIdx > 0 ? <RailSep variant="inner" mark /> : null;
            return (
              <Fragment key={p.id}>
                {sep}
                <ProjectDockIcon
                  p={p}
                  side={side}
                  active={p.id === project?.id}
                  // Курсор в рельсе (или там тащат иконку) — ряд просыпается целиком:
                  // все иконки расцвечиваются. Активный цветной ВООБЩЕ ВСЕГДА, даже в
                  // покое — на фоне спящих он и читается как выбранный; плюс кольцо
                  // кнопки. Поэтому muted достаётся только неактивным в спящей рельсе.
                  muted={!railHover && !dragView && p.id !== project?.id}
                  activity={activity.get(p.id)}
                  dragging={dragView?.id === p.id}
                  dragActive={!!dragView}
                  // У открытого проекта кнопки нет: его иконка возвращается в док
                  // как активная (см. items), и нажатие ничего бы не изменило
                  onHide={p.id === project?.id ? undefined : onIconHide}
                  onPointerDown={onIconPointerDown}
                  onClick={onIconClick}
                  onContextMenu={openMenu}
                />
              </Fragment>
            );
          })}

          {/* Место вставки — ОВЕРЛЕЕМ (в потоке его нет, док не дёргается) и тем же
              знаком, что у панелей: направляющая PanelDropLine. Перетаскивание в
              рельсах должно выглядеть одинаково, чем бы ни двигали — панелью или
              проектом. Когда дроп ещё и закрепит проект, на линии сидит булавка: она
              и есть вся разница между «встанет сюда» и «встанет сюда и закрепится». */}
          {dragView && (
            <div aria-hidden style={{
              position: 'absolute', top: dragView.lineTop, left: 0, right: 0,
              display: 'flex', alignItems: 'center', pointerEvents: 'none',
              transition: 'top 0.13s cubic-bezier(0.2, 0, 0, 1)',
            }}>
              {/* Поля по торцам почти нулевые: в 40px-рельсе штатные 8px оставили бы
                  от линии огрызок */}
              <PanelDropLine axis="y" inset={2} />
              {dragView.zone === 'pin' && (
                <span style={{
                  position: 'absolute', left: '50%', top: '50%', transform: 'translate(-50%, -50%)',
                  width: 16, height: 16, borderRadius: R.full, background: C.accent, color: C.onAccent,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}>
                  <Pin size={10} strokeWidth={2.4} />
                </span>
              )}
            </div>
          )}
        </div>

        <RailSep />
        {/* Новый проект. Мастер проекта требует места, которого в рельсе нет, поэтому
            уводим в раздел «Проекты» с уже открытым диалогом. */}
        <RailIconButton side={side} label="Новый проект" onClick={openNewProjectFlow}>
          <Plus size={17} strokeWidth={ICON_STROKE} />
        </RailIconButton>
      </RailCapsule>

      {/* Призрак перетаскиваемой иконки — порталом в body (капсула режет содержимое по
          своим скруглениям) и СБОКУ от курсора, в сторону центра окна. Под курсором он
          накрывал бы собой всю 40px-рельсу вместе с линией места вставки: человек тащил
          вслепую, видя только то, что и так держит в руке. */}
      {dragView && dragProject && createPortal(
        <div aria-hidden style={{
          position: 'fixed',
          left: side === 'left' ? dragView.x + 16 : dragView.x - 52,
          top: dragView.y - 18, zIndex: Z.modal,
          pointerEvents: 'none', opacity: 0.95, transform: 'scale(1) rotate(-3deg)',
          boxShadow: SHADOW.dropdown, borderRadius: 8, animation: 'ccProjGhostPop 0.12s ease-out',
        }}>
          <ProjectIcon project={dragProject} size={36} radius={8} />
        </div>,
        document.body,
      )}

      {paletteOpen && <ProjectPalette currentProjectId={project?.id} onClose={() => setPaletteOpen(false)} />}

      {menu && createPortal(
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
            {/* Настройки — только у текущего проекта: диалог правит ТОТ, что открыт в
                воркспейсе. Отдельной кнопки под них в рельсе нет, и это единственный
                вход — пункт меню обязан быть. */}
            {menu.p.id === project?.id && (
              <button style={menuItemStyle}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                onClick={() => { onOpenSettings(); setMenu(null); }}>
                Настройки проекта
              </button>
            )}
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
            {/* Второй вход в то же действие, что и кнопка в подписи. Обязателен: пальцем
                наведения нет, подпись с кнопкой на планшете не показывается вовсе (см.
                RailIconButton), и меню по долгому нажатию — там единственный путь.
                У открытого проекта пункта нет по той же причине, что и кнопки. */}
            {menu.p.id !== project?.id && (
              <button style={menuItemStyle}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                onClick={() => { removeFromDock(menu.p.id); setMenu(null); }}>
                Убрать из дока
              </button>
            )}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
