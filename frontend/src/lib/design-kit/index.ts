// Design-kit ядра для внешних модулей — единый entry MF-expose
// 'aihome_shell/design-kit' (контракт §7.1, docs/modules/integration-contract.md).
//
// Правила состава (R16): мажор кита = мажор schemaVersion контракта; добавление
// экспорта — минор, удаление или смена публичного API — мажор. Модуль сверяет
// мажор DESIGN_KIT_VERSION при загрузке и при несовпадении отказывается монтироваться.
//
// Инвариант чистоты: entry реэкспортирует ТОЛЬКО leaf-файлы (design.ts, breakpoints.ts,
// components/ui/*) — без тяги к приложению, роутеру и сторам, иначе expose-чанк
// утащит половину оболочки. Новый примитив добавлять сюда только если он leaf.
//
// Публичная поверхность кита — не только экспорты ниже (R17): глобальные классы и
// keyframes из index.css оболочки (cc-overlay, cc-modal-card, cc-sheet-card, cc-spin,
// cc-fade-in, cc-shimmer-text, cc-smoke, cc-scroll-x, cc-hide-scrollbar, cc-no-scrollbar)
// доступны модулю через DOM-контекст, а cc-iconbtn инжектирует сам IconButton.
// Их переименование или удаление = мажор кита, как и смена API экспортов.

export const DESIGN_KIT_VERSION = '1.2.0';

// === Токены (R14) ===
export {
  C, FONT, FS, R, SP, SHADOW, ISLAND, TB, Z,
  MODAL_W, CHAT_MAX_W, CONTENT_MAX_W, FIELD, GROUP_COLORS,
} from '../design';
export { MOBILE_MAX, TABLET_MAX, MOBILE_QUERY, TABLET_QUERY, useIsMobile, useWindowWidth } from '../breakpoints';

// === Компоненты-примитивы (R15) ===
export { Island, IslandHeader } from '../../components/ui/Island';
export { IslandScaffold } from '../../components/ui/IslandScaffold';
export { Modal } from '../../components/ui/Modal';
export { Button } from '../../components/ui/Button';
export type { ButtonVariant, ButtonSize } from '../../components/ui/Button';
export { IconButton } from '../../components/ui/IconButton';
export type { IconButtonSize, IconButtonTone, IconButtonVariant } from '../../components/ui/IconButton';
export { Field, FieldLabel, TextField, TextArea, IconField } from '../../components/ui/Field';
export { Toggle } from '../../components/ui/Toggle';
export { PillSwitch } from '../../components/ui/PillSwitch';
export { SegmentedControl } from '../../components/ui/Segmented';
export { EmptyState } from '../../components/ui/EmptyState';
export { CanvasBackdrop } from '../../components/ui/CanvasBackdrop';
export { PageCanvas } from '../../components/ui/PageCanvas';

// === Досдача примитивов (v1.5, кит 1.1.0) ===
export { Menu, MenuItem } from '../../components/ui/Menu';
export { BackButton } from '../../components/ui/BackButton';
export { ModalActions } from '../../components/ui/ModalActions';
export { useIsMobileModal } from '../../components/ui/useIsMobileModal';
export { ConfirmDialog } from '../../components/ui/ConfirmDialog';
export { IslandSplitter } from '../../components/ui/IslandSplitter';
export { IslandSidebarSplitter } from '../../components/ui/IslandSidebarSplitter';
export { ICON_SIZE, ICON_STROKE, ICON_PROPS } from '../../components/ui/icons';
