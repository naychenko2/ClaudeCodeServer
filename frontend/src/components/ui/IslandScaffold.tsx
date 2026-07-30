import type { PointerEvent as ReactPointerEvent, ReactNode } from 'react';
import { C, ISLAND } from '../../lib/design';
import { useCenterOffset } from '../../lib/centerOffset';
import { Island } from './Island';
import { IslandSidebarSplitter } from './IslandSidebarSplitter';

// Общий каркас desktop-ветки хаб-страницы (Чаты/Заметки/Знания/Персоны/Проекты):
// холст + остров-сайдбар + ресайз-зазор + центральный остров. Чисто презентационный —
// состояние (режим сайдбара, ширина, persistence-ключи) остаётся на страницах.
// Паддинги содержимого сайдбара компонент НЕ добавляет — их несёт слот-контент.
// Корень — height:100% (не flex:1): страницы монтируют каркас и как flex-ребёнка
// (ChatsPage), и внутри блочной обёртки (Notes/Knowledge/Personas).
//
// Левая сторона — ДВА взаимоисключающих слота:
//   left     — готовая рельса панелей (LeftPanelStack), рендерится как есть;
//   sidebar* — старый сайдбар-остров с ресайзом (Notes/Knowledge/Personas).
// Когда передан left, слот sidebar игнорируется.
export function IslandScaffold({ sidebarOpen = false, sidebar, sidebarWidth = 0, sidebarDragging = false, onSidebarDrag, onSidebarCollapse, center, centerBare, centerContentWidth, left, right }: {
  // Старый sidebar slot — ОПЦИОНАЛЕН когда используется left (LeftPanelStack).
  sidebarOpen?: boolean;
  sidebar?: ReactNode;
  sidebarWidth?: number;
  sidebarDragging?: boolean;
  onSidebarDrag?: (e: ReactPointerEvent) => void;
  onSidebarCollapse?: () => void;
  // Контент центрального острова. Фон — bgMain: контент чатов/заметок свёрстан
  // под него, остров читается рамкой и тенью (editor-island)
  center: ReactNode;
  // Центр БЕЗ рамки-острова: контент живёт прямо на холсте (напр. чат, у которого
  // в остров выделена только шапка)
  centerBare?: boolean;
  // Ширина контента внутри центра (CHAT_MAX_W и т.п.). Передана — центр держится
  // середины ОКНА, а не середины остатка между зонами панелей (см. useCenterOffset).
  // Не передана — центр резиновый, компенсировать нечего.
  centerContentWidth?: number;
  // Готовая ЛЕВАЯ рельса (LeftPanelStack) — рендерится как есть, в начале flex-row.
  // Симметрична right: caller передаёт готовый ReactNode (рельса + панели).
  left?: ReactNode;
  // Готовые элементы справа от центра (сплиттер + острова артефактов) — как есть
  right?: ReactNode;
}) {
  const hasLeftSidebar = sidebarOpen && sidebar != null;
  const hasLeftRail = !!left;
  // Рельсы прижимаются к краям окна без отступа (у right так было изначально),
  // старый сайдбар-остров живёт с обычным паддингом холста.
  const paddingRight = right ? 0 : ISLAND.pad;
  const paddingLeft = hasLeftRail ? 0 : ISLAND.pad;
  // Компенсация перекоса зон: центр остаётся посередине окна
  const { rootRef: offsetRootRef, centerRef: offsetCenterRef } = useCenterOffset(centerContentWidth);

  return (
    <div
      ref={offsetRootRef}
      style={{
        height: '100%', minHeight: 0, display: 'flex', position: 'relative',
        // Сверху — узкий gap под шапкой, по бокам и снизу — просторнее (pad).
        // По краям при наличии рельсы — 0, чтобы она прижималась к краю окна
        // ровно как в проекте (DesktopWorkspace).
        // Фон прозрачный: дудл-холст (CanvasBackdrop) рисует корень страницы.
        padding: `${ISLAND.gap}px ${paddingRight}px ${ISLAND.pad}px ${paddingLeft}px`,
      }}
    >
      {/* left — готовая левая рельса (LeftPanelStack). Рендерится как есть. */}
      {left}

      {/* sidebar (старый слот) — только если не используется left.
          Показывается если sidebarOpen И sidebar не пустой (hideIfEmpty в PanelShell
          может вернуть null — тогда не занимаем место под сайдбар). */}
      {!hasLeftRail && hasLeftSidebar && (
        <>
          {/* Фон — bgMain, в тон шапкам островов (единый тон «оправы» интерфейса) */}
          <Island bg={C.bgMain} style={{ width: sidebarWidth, flexShrink: 0 }}>
            {sidebar}
          </Island>
          <IslandSidebarSplitter active={sidebarDragging} onMouseDown={onSidebarDrag ?? (() => {})} onCollapse={onSidebarCollapse ?? (() => {})} />
        </>
      )}
      {centerBare ? (
        <div ref={offsetCenterRef} style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          {center}
        </div>
      ) : (
        <Island rootRef={offsetCenterRef} bg={C.bgMain} style={{ flex: 1, minWidth: 0 }}>
          {center}
        </Island>
      )}
      {right}
    </div>
  );
}
