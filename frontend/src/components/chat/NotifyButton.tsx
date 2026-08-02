import { useState } from 'react';
import { Bell, BellOff } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarIconButton } from '../Toolbar';
import { isNotifySupported, isNotifyEnabled, setNotifyEnabled } from '../../lib/notify';
import { showToast } from '../../lib/toast';

// Тумблер браузерных уведомлений о событиях чата («нужно решение» / «ход завершён»).
// Настройка ГЛОБАЛЬНАЯ (localStorage, не поле сессии) — кнопка стоит в шапке чата
// просто потому, что сигналит она именно про ход в чате.
//
// Переключение обязано жить в обработчике клика: включение дёргает
// Notification.requestPermission(), а браузер даёт разрешение только по жесту
// пользователя — из эффекта запрос молча отклоняется.
export function NotifyButton({ isMobile }: { isMobile?: boolean }) {
  const [on, setOn] = useState(isNotifyEnabled);

  // Браузер без Notification API (или iOS-Safari вне PWA) — кнопке нечем управлять
  if (!isNotifySupported()) return null;

  const toggle = async () => {
    const next = await setNotifyEnabled(!on);
    setOn(next);
    // Просили включить, а вернулось «выключено» — разрешение не выдано (или отозвано
    // в настройках сайта). Без пояснения кнопка выглядит сломанной.
    if (!on && !next) {
      showToast('Уведомления', 'Браузер не дал разрешение на уведомления', 'info');
    }
  };

  return (
    <ToolbarIconButton
      onClick={toggle}
      active={on}
      isMobile={isMobile}
      title={on
        ? 'Уведомления браузера включены — сигнал, когда нужно решение или ход завершён'
        : 'Уведомления браузера выключены'}
    >
      {on
        ? <Bell size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        : <BellOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
    </ToolbarIconButton>
  );
}
