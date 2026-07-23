import type { PointerEvent as ReactPointerEvent, ReactNode } from 'react';
import { C, ISLAND } from '../../lib/design';
import { Island } from './Island';
import { IslandSidebarSplitter } from './IslandSidebarSplitter';

// Общий каркас desktop-ветки хаб-страницы (Чаты/Заметки/Знания/Персоны/Проекты):
// холст + остров-сайдбар + ресайз-зазор + центральный остров. Чисто презентационный —
// состояние (режим сайдбара, ширина, persistence-ключи) остаётся на страницах.
// Паддинги содержимого сайдбара компонент НЕ добавляет — их несёт слот-контент.
// Корень — height:100% (не flex:1): страницы монтируют каркас и как flex-ребёнка
// (ChatsPage), и внутри блочной обёртки (Notes/Knowledge/Personas).
export function IslandScaffold({ sidebarOpen, sidebar, sidebarWidth, sidebarDragging, onSidebarDrag, onSidebarCollapse, center, centerBare, right }: {
  sidebarOpen: boolean;
  sidebar: ReactNode;
  sidebarWidth: number;
  sidebarDragging: boolean;
  onSidebarDrag: (e: ReactPointerEvent) => void;
  onSidebarCollapse: () => void;
  // Контент центрального острова. Фон — bgMain: контент чатов/заметок свёрстан
  // под него, остров читается рамкой и тенью (editor-island)
  center: ReactNode;
  // Центр БЕЗ рамки-острова: контент живёт прямо на холсте (напр. чат, у которого
  // в остров выделена только шапка)
  centerBare?: boolean;
  // Готовые элементы справа от центра (сплиттер + острова артефактов) — как есть
  right?: ReactNode;
}) {
  return (
    <div style={{
      height: '100%', minHeight: 0, display: 'flex', position: 'relative',
      // Сверху — узкий gap под шапкой, по бокам и снизу — просторнее (pad)
      background: ISLAND.canvas, padding: `${ISLAND.gap}px ${ISLAND.pad}px ${ISLAND.pad}px`,
    }}>
      {sidebarOpen && (
        <>
          <Island style={{ width: sidebarWidth, flexShrink: 0 }}>
            {sidebar}
          </Island>
          <IslandSidebarSplitter active={sidebarDragging} onMouseDown={onSidebarDrag} onCollapse={onSidebarCollapse} />
        </>
      )}
      {centerBare ? (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          {center}
        </div>
      ) : (
        <Island bg={C.bgMain} style={{ flex: 1, minWidth: 0 }}>
          {center}
        </Island>
      )}
      {right}
    </div>
  );
}
