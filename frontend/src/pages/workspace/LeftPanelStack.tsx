// Левая рельса — зеркало RightPanelStack (минимальная версия).
//
// Рельса иконок СЛЕВА у левого края окна + открытые панели-карточки, растущие
// ВПРАВО от рельсы. Клик по иконке открывает/закрывает панель.
//
// Пока НЕ реализовано (план Этап 1.1 — полноценное зеркало):
//   - DnD-перестановки панелей
//   - Multi-колонки (пока одна колонка, панели стакаются вертикально)
//   - Планшетный drawer
//
// Базовая логика: toggle через стор (wsLeftPanelStack / chatLeftPanelStack),
// панель рендерится как PanelShell, закрывается кнопкой в шапке.
// Ширина панелей тянется сплиттером справа от зоны и живёт в том же сторе
// (width), что и у правой рельсы, — зеркально handleWidthDrag в RightPanelStack.
//
// sessionOnly=true — только chats (для раздела «Чаты» без проекта).
// sessionOnly=false — все 5: chats/files/tasks/personas (+ tools если toolsEnabled).
import { useEffect, useState, type ReactNode, type PointerEvent as ReactPointerEvent } from 'react';
import { MessageCircle, FolderTree, ListTodo, Users, SquareTerminal, Columns2, Square, ChevronsLeft, ChevronsRight, type LucideIcon } from 'lucide-react';
import { C, FONT, ISLAND } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { ToolbarIconButton } from '../../components/Toolbar';
import { startPointerDrag } from '../../lib/pointerDrag';
import { wsLeftPanelStack, RAIL_W, type LeftPanelKey, type PanelStack } from './panelStackState';

// Мета панелей левой рельсы: иконка + заголовок для шапки PanelShell и tooltip.
const LEFT_PANEL_META: Record<LeftPanelKey, { title: string; Icon: LucideIcon }> = {
  chats:    { title: 'Чаты',       Icon: MessageCircle },
  files:    { title: 'Файлы',      Icon: FolderTree },
  tasks:    { title: 'Задачи',     Icon: ListTodo },
  personas: { title: 'Команда',    Icon: Users },
  tools:    { title: 'Инструменты', Icon: SquareTerminal },
};

// Группа левых панелей: основные инструменты (всегда видны в воркспейсе)
const WORKSPACE_LEFT_KEYS: LeftPanelKey[] = ['chats', 'files', 'tasks', 'personas'];
// Tools доступен только при toolsEnabled проекта
const TOOLS_KEY: LeftPanelKey = 'tools';

// Зазор между рельсой и панелями. То же значение, что в RightPanelStack —
// рельсы обязаны быть зеркальны, иначе левая зона визуально «толще» правой.
const RAIL_GAP = 4;

interface Props {
  // Готовый контент панелек — caller (ChatsPage / WorkspacePage) собирает
  panels: Partial<Record<LeftPanelKey, ReactNode>>;
  // Бейджи-числа на иконках (напр. chats.length). Не обязательно.
  railCounts?: Partial<Record<LeftPanelKey, number>>;
  // Инстанс стора раскладки: воркспейс и «Чаты» держат НЕЗАВИСИМЫЕ раскладки
  panelStack?: { use: () => PanelStack };
  // sessionOnly — только chats (для раздела «Чаты»)
  sessionOnly?: boolean;
  // Терминал и Preview в правой рельсе; tools в левой — аналогично по флагу
  toolsEnabled?: boolean;
}

export function LeftPanelStack({ panels, railCounts, panelStack, sessionOnly = false, toolsEnabled = false }: Props) {
  const usePanels = (panelStack ?? wsLeftPanelStack).use;
  const { layout, mode, toggle, close, collapsed, toggleCollapsed, setMode, width, setWidth } = usePanels();
  const [dragging, setDragging] = useState(false);

  // Drag ширины зоны: тянем ВПРАВО — панели растут (зеркально правой рельсе,
  // где рост идёт влево). Клампы COL_MIN/COL_MAX применяет сам стор.
  const handleWidthDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = width;
    setDragging(true);
    startPointerDrag(
      ev => setWidth(startW + (ev.clientX - startX)),
      { onEnd: () => setDragging(false) },
    );
  };

  // Какие иконки показывать в рельсе
  const visibleKeys: LeftPanelKey[] = sessionOnly
    ? ['chats']
    : [...WORKSPACE_LEFT_KEYS, ...(toolsEnabled ? [TOOLS_KEY] : [])];

  // Панели, у которых есть контент (panels[k] != null). Если ни у одной —
  // возвращаем null, рельса не рендерится вовсе.
  const availableKeys = visibleKeys.filter(k => panels[k] != null);

  // Открытые панели — только те, что available.
  const openKeys = layout.flat().filter(k => availableKeys.includes(k as LeftPanelKey)) as LeftPanelKey[];
  const soloMode = mode === 'solo';

  // === ПРАВИЛО СКРЫТИЯ РЕЛЬСЫ ===
  // Если доступна только ОДНА панель (напр. sessionOnly → только chats) и она
  // ОТКРЫТА — рельса не нужна: панель сама показывает заголовок с иконкой.
  // Если панель ЗАКРЫТА — показываем рельсу с 1 иконкой (чтобы открыть обратно).
  // Если доступно >1 панелей — рельса всегда видна.
  const singlePanelMode = availableKeys.length === 1;
  const showRail = !singlePanelMode || openKeys.length === 0;

  // Сдвиг FAB AI-хаба: левая рельса занимает место слева — пробрасываем в CSS-переменную
  const leftZoneW = availableKeys.length === 0 ? 0 : RAIL_W + (openKeys.length > 0 ? RAIL_GAP + width : RAIL_GAP);
  useEffect(() => {
    document.documentElement.style.setProperty('--cc-fab-left', `${leftZoneW + 20}px`);
    return () => { document.documentElement.style.removeProperty('--cc-fab-left'); };
  }, [leftZoneW]);

  // Ранний return — ПОСЛЕ всех хуков (useSyncExternalStore в usePanels, useEffect выше).
  // Если ни у одной панели нет контента — не рендерим рельсу вовсе.
  if (availableKeys.length === 0) return null;

  // Одна иконка рельсы
  const renderRailIcon = (k: LeftPanelKey): ReactNode => {
    const isOpen = openKeys.includes(k);
    const { title, Icon } = LEFT_PANEL_META[k];
    const count = railCounts?.[k];
    return (
      <ToolbarIconButton
        key={k}
        onClick={() => toggle(k)}
        active={isOpen}
        title={title}
      >
        <div style={{ position: 'relative', display: 'flex' }}>
          <Icon size={17} strokeWidth={ICON_STROKE} />
          {count && count > 0 ? (
            <span style={{
              position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
              borderRadius: 7, background: C.accent, color: C.onAccent,
              fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
            }}>
              {count}
            </span>
          ) : null}
        </div>
      </ToolbarIconButton>
    );
  };

  // Одна панель: PanelShell с иконкой/заголовком + контент из props
  const renderPanel = (k: LeftPanelKey): ReactNode => {
    const { title, Icon } = LEFT_PANEL_META[k];
    return (
      <PanelShell
        key={k}
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        onClose={() => close(k)}
        fill={false}
        slideDirection="left"
      >
        {panels[k] ?? null}
      </PanelShell>
    );
  };

  return (
    <>
      {/* Рельса — рендерится только когда showRail=true.
          singlePanelMode (1 доступная панель):
          - панель открыта → showRail=false → рельса скрыта
          - панель закрыта → showRail=true → рельса с 1 иконкой
          Мульти-режим (>1 панель): рельса всегда видна.
          Анимация: рельса всегда в DOM, при showRail=false плавно схлопывается
          (width→0, opacity→0) через CSS transition — синхронно с placeholder. */}
      <div style={{
        width: showRail ? RAIL_W : 0,
        opacity: showRail ? 1 : 0,
        overflow: 'hidden',
        pointerEvents: showRail ? 'auto' : 'none',
        transition: 'width 0.15s ease-out, opacity 0.12s ease-out',
        flexShrink: 0, alignSelf: 'flex-start',
        display: 'flex', flexDirection: 'column', alignItems: 'center',
        gap: 6, paddingTop: 7, paddingBottom: 7, background: C.bgMain,
          borderRight: `1px solid ${C.border}`,
          borderTop: `1px solid ${C.border}`,
          borderBottom: `1px solid ${C.border}`,
          borderTopRightRadius: ISLAND.radius, borderBottomRightRadius: ISLAND.radius,
          borderTopLeftRadius: 0, borderBottomLeftRadius: 0,
          boxSizing: 'border-box',
          boxShadow: ISLAND.shadow,
          marginRight: openKeys.length === 0 ? RAIL_GAP : 0,
        }}>
          {/* Toggle multi/solo — только в мульти-режиме. */}
          {!singlePanelMode && (
            <>
              <ToolbarIconButton
                onClick={() => setMode(soloMode ? 'multi' : 'solo')}
                title={soloMode ? 'Одна панель — нажмите для раскладки колонками' : 'Раскладка колонками — нажмите для режима одной панели'}

              >
                {soloMode
                  ? <Square size={15} strokeWidth={ICON_STROKE} />
                  : <Columns2 size={15} strokeWidth={ICON_STROKE} />}
              </ToolbarIconButton>
              <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '1px 0 2px' }} />
            </>
          )}

          {availableKeys.map(renderRailIcon)}

          {/* Collapse all — только в мульти-режиме. */}
          {!singlePanelMode && (
            <>
              <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '2px 0 1px' }} />
              {(() => {
                const collapseDisabled = openKeys.length === 0 && !collapsed;
                return (
                  <div style={{ opacity: collapseDisabled ? 0.3 : 1 }}>
                    <ToolbarIconButton
                      onClick={toggleCollapsed}
                      disabled={collapseDisabled}
                      title={collapsed ? 'Открыть свёрнутые панели' : 'Свернуть все панели'}

                    >
                      <div style={{ display: 'flex', color: collapseDisabled ? C.textMuted : undefined }}>
                        {collapsed
                          ? <ChevronsRight size={16} strokeWidth={ICON_STROKE} />
                          : <ChevronsLeft size={16} strokeWidth={ICON_STROKE} />}
                      </div>
                    </ToolbarIconButton>
                  </div>
                );
              })()}
            </>
          )}
        </div>

      {/* Зона открытых панелей — растёт вправо от рельсы.
          Пока одна колонка (панели стакаются вертикально), resizable и multi-col — потом. */}
      {openKeys.length > 0 && (
        <>
          {/* Зазор между рельсой и панелями — только когда рельса видна.
              Если рельса скрыта (singlePanelMode + панель открыта) — оставляем
              placeholder (RAIL_W + RAIL_GAP) чтобы панель стояла на том же месте,
              где была бы если бы рельса была видна. Визуальная консистентность:
              панель не «прыгает» при скрытии/показе рельсы. */}
          {showRail
            ? <div style={{ width: RAIL_GAP, flexShrink: 0, transition: 'width 0.15s ease-out' }} />
            : <div style={{ width: RAIL_W + RAIL_GAP, flexShrink: 0, transition: 'width 0.15s ease-out' }} />
          }
          <div style={{
            width,
            flexShrink: 0,
            display: 'flex',
            flexDirection: 'column',
            gap: RAIL_GAP,
            // Тени панелей-островов не должны срезаться обёрткой
            overflow: 'visible',
          }}>
            {openKeys.map(renderPanel)}
          </div>
          {/* Сплиттер ширины — справа от зоны панелей (у правой рельсы он слева) */}
          <IslandSplitter orientation="v" active={dragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />
        </>
      )}
    </>
  );
}
