import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Search, Settings } from 'lucide-react';
import { C, R, FS, FONT, Z, SHADOW } from '../../lib/design';
import type { Project } from '../../types';
import { ProjectIcon } from './ProjectIcon';
import { useAllProjects, openProjectViaEvent } from './useAllProjects';
import { usePinnedIds, useRecentIds, isPinned, togglePin, movePinned } from '../../lib/pinnedProjects';
import { useProjectActivity, type ProjectActivity } from '../../lib/projectActivity';
import { ProjectPalette } from './ProjectPalette';

// Переключатель проектов в плашке сайдбара (флаг sidebar-project-switcher):
// [иконка активного с акцент-кольцом + имя] <-> [иконки проектов со статус-точками]
// [лупа / «+N» → палитра] — полка и лупа прижаты к правому краю, имя занимает
// остаток и жмется ellipsis'ом. Приоритет слотов: ждет ответа > работает >
// закрепленные (по порядку) > недавние (MRU). Активный слот не занимает.
// Клик по «ждущему» проекту открывает его сразу на ждущем чате (cc-open-url).

const ICON_W = 38;    // шаг иконки кандидата (32px + паддинги)
const LUPA_W = 32;    // лупа/«+N»
const ACTIVE_W = 42;  // иконка активного с кольцом и отступами
const NAME_MIN = 64;  // минимум под имя активного, дальше ellipsis
const MAX_SLOTS = 3;

const STATUS_COLOR: Record<ProjectActivity['status'], string> = {
  waiting: C.accent,
  working: C.success,
};

const STATUS_TITLE: Record<ProjectActivity['status'], string> = {
  waiting: 'агент ждет ответа',
  working: 'агент работает',
};

// Иконка проекта-кандидата со статус-точкой, drag-сортировкой пинов и контекст-меню
function CandidateIcon({ p, activity, dragging, over, onOpen, onContextMenu, onDragStart, onDragOver, onDrop, onDragEnd }: {
  p: Project;
  activity?: ProjectActivity;
  dragging: boolean;
  over: boolean;
  onOpen: (p: Project) => void;
  onContextMenu: (e: React.MouseEvent, p: Project) => void;
  onDragStart: () => void; onDragOver: () => void; onDrop: () => void; onDragEnd: () => void;
}) {
  const pinned = isPinned(p.id);
  // long-press (~500мс) как контекст-меню для планшета (там нет правого клика)
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const longPressed = useRef(false);
  // Иконка может уехать из слотов между touchstart и срабатыванием таймера
  useEffect(() => () => { if (pressTimer.current) clearTimeout(pressTimer.current); }, []);
  const title = activity ? `${p.name} — ${STATUS_TITLE[activity.status]}` : p.name;
  return (
    <button
      title={title}
      aria-label={title}
      draggable={pinned}
      onClick={() => { if (!longPressed.current) onOpen(p); longPressed.current = false; }}
      onContextMenu={e => onContextMenu(e, p)}
      onTouchStart={e => {
        const t = e.touches[0];
        pressTimer.current = setTimeout(() => {
          longPressed.current = true;
          onContextMenu({ preventDefault: () => {}, clientX: t.clientX, clientY: t.clientY } as React.MouseEvent, p);
        }, 500);
      }}
      onTouchEnd={() => { if (pressTimer.current) clearTimeout(pressTimer.current); }}
      onTouchMove={() => { if (pressTimer.current) clearTimeout(pressTimer.current); }}
      onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; onDragStart(); }}
      onDragOver={e => { if (pinned) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; onDragOver(); } }}
      onDrop={e => { e.preventDefault(); e.stopPropagation(); onDrop(); }}
      onDragEnd={onDragEnd}
      style={{
        position: 'relative', padding: 3, border: 'none', background: 'transparent',
        cursor: 'pointer', display: 'flex', flexShrink: 0, borderRadius: R.sm,
        opacity: dragging ? 0.4 : 1,
        boxShadow: over ? `0 0 0 2px ${C.bgSelected}, 0 0 0 3px ${C.accent}` : undefined,
        transition: 'opacity 0.12s',
      }}
    >
      <ProjectIcon project={p} size={32} radius={8} />
      {activity && (
        <span style={{
          position: 'absolute', right: 0, top: 0, width: 10, height: 10, borderRadius: '50%',
          background: STATUS_COLOR[activity.status], border: `2px solid ${C.bgPanel}`,
          boxSizing: 'content-box',
        }} />
      )}
    </button>
  );
}

export function SidebarProjectSwitcher({ project, onOpenSettings }: {
  project: Project;               // активный проект (свежая версия из WorkspacePage)
  onOpenSettings: () => void;     // клик по иконке активного — настройки проекта
}) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const recentIds = useRecentIds();
  const activity = useProjectActivity();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [activeHover, setActiveHover] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number; p: Project } | null>(null);
  const [dragId, setDragId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);

  // Ширина строки — от нее число слотов (сайдбар тянется сплиттером)
  const rowRef = useRef<HTMLDivElement>(null);
  const [rowW, setRowW] = useState(0);
  useLayoutEffect(() => {
    const el = rowRef.current;
    if (!el) return;
    // Сидируем ширину синхронно: первая замерка RO приходит не сразу, без сида
    // один кадр рисовался бы «пустой» вариант (slots=0, все проекты в «+N»)
    setRowW(el.getBoundingClientRect().width);
    const ro = new ResizeObserver(entries => setRowW(entries[0]?.contentRect.width ?? 0));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Кандидаты по приоритету: ждущие > работающие > пины (по порядку) > недавние (MRU).
  // Активный проект исключен; дубли схлопываются порядком добавления (пин со
  // статусом уже добавлен как статусный — один слот).
  const candidates = useMemo(() => {
    const byId = new Map(projects.map(p => [p.id, p]));
    const seen = new Set<string>([project.id]);
    const out: Project[] = [];
    const push = (id: string) => {
      if (seen.has(id)) return;
      const p = byId.get(id);
      if (!p) return;
      seen.add(id);
      out.push(p);
    };
    const withStatus = (s: ProjectActivity['status']) =>
      [...activity.entries()].filter(([, a]) => a.status === s).map(([id]) => id);
    withStatus('waiting').forEach(push);
    withStatus('working').forEach(push);
    pinnedIds.forEach(push);
    recentIds.forEach(push);
    return out;
  }, [projects, project.id, activity, pinnedIds, recentIds]);

  // −8 — запас на flex-gap (2px) между элементами строки
  const slots = Math.max(0, Math.min(MAX_SLOTS, Math.floor((rowW - ACTIVE_W - NAME_MIN - LUPA_W - 8) / ICON_W)));
  const shown = candidates.slice(0, slots);
  // «+N» — все непоказанные проекты (кандидаты за слотами + прочие из палитры)
  const hiddenCount = Math.max(0, projects.length - 1 - shown.length);
  // Ждущий проект не влез в слоты → оранжевая микро-точка на «+N» (не прячем молча)
  const hiddenWaiting = candidates.slice(slots).some(p => activity.get(p.id)?.status === 'waiting');

  const openCandidate = (p: Project) => {
    const a = activity.get(p.id);
    if (a?.status === 'waiting' && a.waitingChatId) {
      // Сразу в ждущий чат: готовый роутинг диплинков в App (openNotificationUrl)
      window.dispatchEvent(new CustomEvent('cc-open-url', {
        detail: { url: `#/project/${p.id}/chat/${encodeURIComponent(a.waitingChatId)}` },
      }));
      return;
    }
    openProjectViaEvent(p);
  };

  const openMenu = (e: React.MouseEvent, p: Project) => {
    e.preventDefault();
    // Кламп к вьюпорту, чтобы меню не уезжало за правый/нижний край
    const x = Math.min(e.clientX, window.innerWidth - 180);
    const y = Math.min(e.clientY, window.innerHeight - 90);
    setMenu({ x, y, p });
  };

  // Закрытие контекст-меню по Escape (клик закрывает overlay ниже)
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

  return (
    <div ref={rowRef} style={{ display: 'flex', alignItems: 'center', gap: 2, flex: 1, minWidth: 0 }}>
      {/* Активный проект: [иконка с акцент-кольцом и бейджем-шестеренкой][имя] —
          единая кнопка настроек проекта. Бейдж в правом нижнем углу иконки виден
          всегда — назначение читается без наведения; ховер подсвечивает всю зону.
          Кнопка занимает остаток строки (flex:1) — полка и лупа уходят вправо */}
      <button
        title="Настройки проекта"
        aria-label="Настройки проекта"
        onClick={onOpenSettings}
        onMouseEnter={() => setActiveHover(true)}
        onMouseLeave={() => setActiveHover(false)}
        style={{
          display: 'flex', alignItems: 'center', flex: 1, minWidth: 0,
          padding: '3px 6px 3px 3px', margin: '0 2px 0 0', border: 'none', cursor: 'pointer',
          background: activeHover ? C.bgSelected : 'transparent',
          borderRadius: R.md, transition: 'background 0.12s', textAlign: 'left',
        }}
      >
        <span style={{ position: 'relative', display: 'flex', borderRadius: 8, flexShrink: 0, margin: 2, boxShadow: `0 0 0 1.5px ${C.accent}` }}>
          <ProjectIcon project={project} size={32} radius={8} />
          <span style={{
            position: 'absolute', right: -4, bottom: -4, width: 15, height: 15, borderRadius: '50%',
            background: C.bgPanel, border: `1px solid ${C.border}`,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            color: activeHover ? C.textHeading : C.textSecondary, transition: 'color 0.12s',
          }}>
            <Settings size={10} strokeWidth={2.2} />
          </span>
        </span>
        <span style={{
          fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading,
          flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          margin: '0 6px 0 9px',
        }}>
          {project.name}
        </span>
      </button>
      {shown.map(p => (
        <CandidateIcon
          key={p.id}
          p={p}
          activity={activity.get(p.id)}
          dragging={dragId === p.id}
          over={!!dragId && overId === p.id && dragId !== p.id}
          onOpen={openCandidate}
          onContextMenu={openMenu}
          onDragStart={() => setDragId(p.id)}
          onDragOver={() => setOverId(p.id)}
          onDrop={() => { if (dragId && dragId !== p.id) movePinned(dragId, p.id); setDragId(null); setOverId(null); }}
          onDragEnd={() => { setDragId(null); setOverId(null); }}
        />
      ))}
      {/* Лупа / «+N»: палитра всех проектов; микро-точка — скрытый «ждущий» проект */}
      <button
        aria-label="Все проекты (палитра)"
        title={hiddenCount > 0 ? `Еще ${hiddenCount} проектов` : 'Поиск проектов'}
        onClick={() => setPaletteOpen(true)}
        style={{
          position: 'relative', display: 'inline-flex', alignItems: 'center', gap: 2,
          padding: hiddenCount > 0 ? '3px 6px' : 3, border: 'none', borderRadius: R.sm,
          background: hiddenCount > 0 ? C.bgSelected : 'transparent',
          color: C.textSecondary, cursor: 'pointer', flexShrink: 0,
        }}
      >
        <Search size={16} strokeWidth={2} />
        {hiddenCount > 0 && (
          <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600 }}>+{hiddenCount}</span>
        )}
        {hiddenWaiting && (
          <span style={{
            position: 'absolute', right: -1, top: -1, width: 8, height: 8, borderRadius: '50%',
            background: C.accent, border: `2px solid ${C.bgPanel}`, boxSizing: 'content-box',
          }} />
        )}
      </button>
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
