import { useState } from 'react';
import { Hourglass } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, R, TB } from '../../lib/design';
import { formatTimeLeft, expiresAt, formatExpiryDate } from '../../lib/expiry';
import { updateChatFields } from '../../lib/chatUpdate';
import { showToast } from '../../lib/toast';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu } from '../ui';
import { ExpiryPicker } from './ExpiryPicker';

// Время жизни чата прямо в шапке: у временного — остаток до авто-удаления, у
// бессрочного — приглушённая иконка часов. Клик открывает выбор срока.
//
// Раньше это была неинтерактивная пилюля-индикатор, ведущая в диалог настроек чата,
// а пилюли NewChatSetup доступны только в ПУСТОМ чате — то есть у начатого чата
// сменить или снять срок было негде, кроме того диалога.
export function ExpiryButton({ session, isMobile, onSessionUpdated }: {
  session: Session;
  isMobile?: boolean;
  onSessionUpdated?: (s: Session) => void;
}) {
  // Якорь поповера — rect кнопки, снятый в момент клика (общая идиома Menu в проекте):
  // fixed-режим не срезается overflow полосы бейджей, а ref во время рендера не читаем
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const [saving, setSaving] = useState(false);

  const left = formatTimeLeft(session);
  const temporary = session.expiresAfterMinutes != null;

  const pick = async (minutes: number | null) => {
    setMenu(null);
    if (minutes === (session.expiresAfterMinutes ?? null)) return;
    setSaving(true);
    try {
      onSessionUpdated?.(await updateChatFields(session, { expiresAfterMinutes: minutes }));
    } catch {
      showToast('Время жизни', 'Не удалось изменить срок жизни чата', 'info');
    } finally {
      setSaving(false);
    }
  };

  // Отсчёт идёт от последней активности, но не раньше момента установки срока
  // (expiryAnchor) — это уже учтено в expiresAt
  const at = expiresAt(session);

  return (
    <div style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        // rect снимаем ДО setMenu: в ленивом апдейтере currentTarget события уже null
        onClick={e => {
          const rect = e.currentTarget.getBoundingClientRect();
          setMenu(m => m ? null : rect);
        }}
        disabled={saving}
        title={temporary
          ? `Временный чат — удалится ${left ?? 'по истечении срока'}, если не будет активности. Нажмите, чтобы изменить.`
          : 'Чат хранится бессрочно. Нажмите, чтобы сделать временным.'}
        style={{
          display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
          background: temporary ? C.bgWhite : 'transparent',
          border: `1px solid ${temporary ? C.border : 'transparent'}`,
          borderRadius: R.lg, flexShrink: 0, cursor: saving ? 'default' : 'pointer',
          fontFamily: FONT.sans, fontSize: 11, fontWeight: 600,
          // Тот же цвет, что у icon-кнопок шапки (TB.iconColor): кнопка стоит с ними
          // в одном ряду, и приглушать её сильнее — читается как «неактивна»
          color: TB.iconColor, whiteSpace: 'nowrap',
          opacity: saving ? 0.6 : 1,
        }}
      >
        <Hourglass size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        {/* Остаток — только на десктопе у временного чата: в узкой мобильной шапке
            он распирает ряд, а метка временного чата есть в списке чатов */}
        {temporary && !isMobile && left}
      </button>
      {menu && (
        <Menu onClose={() => setMenu(null)} anchor={menu}
          minWidth={isMobile ? 260 : 300} maxHeight={190}>
          <div style={{ padding: '6px 8px 8px' }}>
            <ExpiryPicker value={session.expiresAfterMinutes} onChange={pick} columns={isMobile ? 2 : 3} />
            {at && (
              <p style={{ margin: '8px 0 0', fontSize: 11.5, color: C.textMuted, lineHeight: 1.4 }}>
                Удалится ~{formatExpiryDate(at)}, если не будет активности.
              </p>
            )}
          </div>
        </Menu>
      )}
    </div>
  );
}
