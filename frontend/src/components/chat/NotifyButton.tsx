import { useState } from 'react';
import { Bell, BellOff } from 'lucide-react';
import type { Project, Session } from '../../types';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarIconButton } from '../Toolbar';
import { isNotifySupported, setChatNotifyEnabled, useChatNotifyOn } from '../../lib/notify';
import { ownerSettingsDeniedReason } from '../../features/projects/members/participantGate';
import { showToast } from '../../lib/toast';

// Тумблер уведомлений ЭТОГО чата («нужно решение» / «ход завершён»). Состояние живёт
// в сессии (Session.notificationsMuted); общее разрешение браузера — глобальный тумблер
// в разделе «Уведомления».
//
// Включение обязано жить в обработчике клика: если разрешение браузера ещё не выдано,
// setChatNotifyEnabled дёргает Notification.requestPermission(), а браузер даёт
// разрешение только по жесту пользователя — из эффекта запрос молча отклоняется.
export function NotifyButton({ session, project, isMobile, onSessionUpdated }: {
  session: Session;
  // Роль в проекте (QA-P1F): notificationsMuted — настройка владельца (бэковый
  // OwnerSettingsGate, 403 owner_settings): участнику кнопка неактивна с объяснением
  // в title. Чат вне проекта и обычный проект (myRole нет) — без изменений
  project?: Pick<Project, 'myRole'> | null;
  isMobile?: boolean;
  onSessionUpdated?: (s: Session) => void;
}) {
  const [saving, setSaving] = useState(false);
  // Хук зовём до раннего выхода: правило хуков не терпит условного вызова
  const on = useChatNotifyOn(session);

  // Браузер без Notification API (или iOS-Safari вне PWA) — кнопке нечем управлять
  if (!isNotifySupported()) return null;

  const denied = ownerSettingsDeniedReason(project);

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
      // Акцентная плашка снята: в ряду шапки такие кнопки стоят подряд, и
      // оранжевый переставал означать главное действие. Состояние показывает
      // сама иконка — Bell против BellOff (образец тона — ExpiryButton)
      disabled={saving || !!denied}
      isMobile={isMobile}
      // Ghost-ряд шапки гасит кнопку в покое; включённые уведомления — состояние,
      // которое должно читаться всегда (cc-ghost-live снимает заглушку)
      className={on ? 'cc-ghost-live' : undefined}
      title={denied
        ? 'Уведомления этого чата включает или глушит только владелец проекта'
        : on
          ? 'Уведомления по этому чату включены — сигнал, когда нужно решение или ход завершён'
          : 'Уведомления по этому чату выключены'}
    >
      {on
        ? <Bell size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        : <BellOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
    </ToolbarIconButton>
  );
}
