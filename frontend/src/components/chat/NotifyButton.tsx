import { useState } from 'react';
import { Bell, BellOff } from 'lucide-react';
import type { Session } from '../../types';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarIconButton } from '../Toolbar';
import { isNotifySupported, setChatNotifyEnabled, useChatNotifyOn } from '../../lib/notify';
import { showToast } from '../../lib/toast';

// Тумблер уведомлений ЭТОГО чата («нужно решение» / «ход завершён»). Состояние живёт
// в сессии (Session.notificationsMuted); общее разрешение браузера — глобальный тумблер
// в разделе «Уведомления».
//
// Включение обязано жить в обработчике клика: если разрешение браузера ещё не выдано,
// setChatNotifyEnabled дёргает Notification.requestPermission(), а браузер даёт
// разрешение только по жесту пользователя — из эффекта запрос молча отклоняется.
export function NotifyButton({ session, isMobile, onSessionUpdated }: {
  session: Session;
  isMobile?: boolean;
  onSessionUpdated?: (s: Session) => void;
}) {
  const [saving, setSaving] = useState(false);
  // Хук зовём до раннего выхода: правило хуков не терпит условного вызова
  const on = useChatNotifyOn(session);

  // Браузер без Notification API (или iOS-Safari вне PWA) — кнопке нечем управлять
  if (!isNotifySupported()) return null;

  const toggle = async () => {
    setSaving(true);
    try {
      const res = await setChatNotifyEnabled(session, !on);
      if (res.session) onSessionUpdated?.(res.session);
      // Просили включить, а разрешение не выдано (или отозвано в настройках сайта).
      // Без пояснения кнопка выглядит сломанной
      if (!on && !res.enabled) {
        showToast('Уведомления', 'Браузер не дал разрешение на уведомления', 'info');
      }
    } catch {
      showToast('Уведомления', 'Не удалось изменить уведомления чата', 'info');
    } finally {
      setSaving(false);
    }
  };

  return (
    <ToolbarIconButton
      onClick={toggle}
      active={on}
      disabled={saving}
      isMobile={isMobile}
      title={on
        ? 'Уведомления по этому чату включены — сигнал, когда нужно решение или ход завершён'
        : 'Уведомления по этому чату выключены'}
    >
      {on
        ? <Bell size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        : <BellOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
    </ToolbarIconButton>
  );
}
