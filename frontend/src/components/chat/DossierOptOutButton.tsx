import { useState } from 'react';
import { History } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, R, TB } from '../../lib/design';
import { updateChatFields } from '../../lib/chatUpdate';
import { showToast } from '../../lib/toast';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu } from '../ui';
import { DossierOptOutRow } from './DossierOptOutRow';

// Opt-out «не сохранять решения из этого чата» прямо в шапке (ADR-004 §6): пилюли
// NewChatSetup доступны только в ПУСТОМ чате, а изменить настройку у начатого чата
// нужно у самого чата — рядом со «Временем жизни» (ExpiryButton), тем же паттерном.
//
// Дефолт — решения сохраняются: прозрачная иконка истории в ряду кнопок шапки.
// Исключён — обведённая пилюля с иконкой accent и подписью «Не сохраняются»
// (на мобиле подпись пропадает, как остаток времени у временного чата).
export function DossierOptOutButton({ session, isMobile, onSessionUpdated }: {
  session: Session;
  isMobile?: boolean;
  onSessionUpdated?: (s: Session) => void;
}) {
  // Якорь поповера — rect кнопки, снятый в момент клика (общая идиома Menu в проекте)
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const [saving, setSaving] = useState(false);

  const excluded = !!session.excludeFromDossiers;

  const persist = async (next: boolean) => {
    if (next === excluded) return;
    setSaving(true);
    try {
      onSessionUpdated?.(await updateChatFields(session, { excludeFromDossiers: next }));
    } catch {
      showToast('История решений', 'Не удалось изменить настройку чата', 'info');
    } finally {
      setSaving(false);
    }
  };

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
        title={excluded
          ? 'Решения этого чата не сохраняются. Нажмите, чтобы изменить.'
          : 'Решения этого чата сохраняются в историю. Нажмите, чтобы изменить.'}
        style={{
          display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
          background: excluded ? C.bgWhite : 'transparent',
          border: `1px solid ${excluded ? C.border : 'transparent'}`,
          borderRadius: R.lg, flexShrink: 0, cursor: saving ? 'default' : 'pointer',
          fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
          // Дефолт — тот же цвет, что у icon-кнопок шапки; исключён — иконка accent,
          // режим считывается с места, не открывая поповер
          // Тон нейтральный: акцент читается как «включено», а горело бы
          // отрицательное состояние («решения не сохраняются»). Режим виден
          // обводкой и подписью — как у ExpiryButton
          color: TB.iconColor,
          opacity: saving ? 0.6 : 1,
        }}
      >
        <History size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        {/* Подпись — только на десктопе у исключённого чата: в узкой мобильной шапке
            она распирает ряд, а иконка accent уже сигнализирует режим */}
        {excluded && !isMobile && <span style={{ color: C.textMuted }}>Не сохраняются</span>}
      </button>
      {menu && (
        <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={isMobile ? 260 : 300} maxHeight={220}>
          {/* Меню не закрываем по переключению: включённое состояние показывает сноску
              про уже записанные решения — пусть человек её прочтёт */}
          <div style={{ padding: '8px 10px 10px' }}>
            <DossierOptOutRow value={!!session.excludeFromDossiers} onChange={persist} />
          </div>
        </Menu>
      )}
    </div>
  );
}
