// Док «Стены» в воркспейсе — единственный вход в режим (вкладки в таббаре нет):
// маленькая капсула ПОД доком проектов. Кнопка с иконкой стены открывает режим,
// бейдж показывает размер набора; чат попадает на стену перетаскиванием карточки
// из панели «Чаты» сюда (или пунктом «На стену» в меню карточки).
//
// При маунте лениво поднимает состав стены (initWall): addChat шлёт PUT полного
// состава, и без загруженного снимка дроп затирал бы чужие монеты.
import { useEffect, useState } from 'react';
import { LayoutGrid } from 'lucide-react';
import { C, R } from '../../lib/design';
import { RailCapsule, RailIconButton } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { showToast } from '../../lib/toast';
import type { Session } from '../../types';
import { useWallState, initWall, addChatSafe } from './wallStore';

// Тип данных перетаскивания карточки чата (кладёт SessionList в плоском режиме)
export const WALL_DRAG_TYPE = 'cc-wall-chat';

// Русское склонение для подписи дока: 1 чат / 2 чата / 5 чатов
function pluralChats(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'чат';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return 'чата';
  return 'чатов';
}

export function WallDock({ onOpenWall }: { onOpenWall: () => void }) {
  const { chats, loaded } = useWallState();
  const [over, setOver] = useState(false);

  useEffect(() => { initWall(undefined); }, []);

  const handleDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    setOver(false);
    const raw = e.dataTransfer.getData(WALL_DRAG_TYPE);
    if (!raw) return;
    try {
      const s = JSON.parse(raw) as Session;
      await addChatSafe(s);
      showToast('Стена', `«${s.name?.trim() || 'Чат'}» на стене`);
    } catch { /* битые данные перетаскивания — игнорируем */ }
  };

  return (
    <RailCapsule
      side="left"
      style={{ marginTop: 8 }}
      onDragOver={e => { if (e.dataTransfer.types.includes(WALL_DRAG_TYPE)) { e.preventDefault(); setOver(true); } }}
      onDragLeave={() => setOver(false)}
      onDrop={handleDrop}
      border={over ? `1.5px dashed ${C.accent}` : undefined}
    >
      <RailIconButton
        side="left"
        label={loaded && chats.length > 0
          ? `Стена — ${chats.length} ${pluralChats(chats.length)}. Клик — открыть, дроп чата — добавить`
          : 'Стена: перетащите сюда чат из списка (в «Иерархии» — пункт меню «На стену»)'}
        onClick={onOpenWall}
      >
        <span style={{ position: 'relative', display: 'flex' }}>
          <LayoutGrid size={16} strokeWidth={ICON_STROKE} color={over ? C.accent : undefined} />
          {chats.length > 0 && (
            <span style={{
              position: 'absolute', top: -6, right: -8, minWidth: 13, height: 13,
              padding: '0 3px', borderRadius: R.full, background: C.accent, color: C.onAccent,
              fontSize: 9, fontWeight: 700, lineHeight: '13px', textAlign: 'center',
              boxSizing: 'border-box', pointerEvents: 'none',
            }}>
              {chats.length}
            </span>
          )}
        </span>
      </RailIconButton>
    </RailCapsule>
  );
}
