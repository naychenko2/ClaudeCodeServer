// Design-kit ядра для внешних модулей — единый entry MF-expose
// 'aihome_shell/design-kit' (контракт §7.1, docs/module-platform-integration-contract.md).
//
// Правила состава (R16): мажор кита = мажор schemaVersion контракта; добавление
// экспорта — минор, удаление или смена публичного API — мажор. Модуль сверяет
// мажор DESIGN_KIT_VERSION при загрузке и при несовпадении отказывается монтироваться.
//
// Инвариант чистоты: entry реэкспортирует ТОЛЬКО leaf-файлы (design.ts, breakpoints.ts,
// components/ui/*) — без тяги к приложению, роутеру и сторам, иначе expose-чанк
// утащит половину оболочки. Новый примитив добавлять сюда только если он leaf.

export const DESIGN_KIT_VERSION = '1.0.0';

// === Токены (R14) ===
export {
  C, FONT, FS, R, SP, SHADOW, ISLAND, TB, Z,
  MODAL_W, CHAT_MAX_W, FIELD, GROUP_COLORS,
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
