import { Pencil } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, R, SHADOW, Z } from '../../lib/design';

// Плавающая кнопка «Редактировать профиль» — только мобилка, режим просмотра персоны.
// ЛЕВЫЙ нижний угол: правый занят глобальным AiLauncher (⌘/Ctrl+K) на всех экранах,
// поэтому FAB справа гарантированно наложился бы. Цвет — акцент персоны (как полоса
// тулбара, роль и кнопка «Поговорить»).
export function PersonaEditFab({ accent, onClick }: { accent: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label="Редактировать профиль"
      title="Редактировать профиль"
      style={{
        position: 'fixed', left: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + 20px)',
        width: 52, height: 52, borderRadius: R.full, border: 'none', cursor: 'pointer',
        background: accent, color: C.onAccent,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        boxShadow: SHADOW.fab, zIndex: Z.modal - 1,
      }}
    >
      <Pencil size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
    </button>
  );
}
