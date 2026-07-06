import { C, R, FONT, MODAL_W } from '../lib/design';
import { Modal, ModalActions, useIsMobileModal } from './ui';
import { type Mode, MODE_META, ModeIcon } from '../lib/modes';

interface DangerModeConfirmProps {
  mode: Mode;            // опасный режим, который пытаются включить (сейчас — только bypass)
  assistantName?: string; // имя ассистента сессии (Claude | DeepSeek | GLM | …)
  onConfirm: () => void;
  onCancel: () => void;
}

// Что станет доступно без подтверждения в режиме «Без ограничений»
const BYPASS_BULLETS = [
  'изменение и удаление файлов',
  'выполнение команд в терминале',
  'установка пакетов и операции с git',
];

// Подтверждение включения опасного режима прав — замена системного window.confirm.
// Сдержанное предупреждение в стиле дизайн-системы: danger дозированно (иконка-бейдж + кнопка).
export function DangerModeConfirm({ mode, assistantName = 'Ассистент', onConfirm, onCancel }: DangerModeConfirmProps) {
  const isMobile = useIsMobileModal();
  const label = MODE_META[mode].label;

  return (
    <Modal
      width={MODAL_W.confirm}
      onClose={onCancel}
      closeOnBackdrop
      footer={
        <ModalActions
          confirmLabel="Включить режим"
          confirmVariant="danger"
          onConfirm={onConfirm}
          cancelLabel="Отмена"
          onCancel={onCancel}
        />
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {/* Иконка-бейдж + заголовок */}
        <div style={{ display: 'flex', gap: 14, alignItems: 'center' }}>
          <div style={{
            width: 44, height: 44, borderRadius: R.full, background: C.dangerBg,
            display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, color: C.danger,
          }}>
            <ModeIcon mode={mode} size={22} />
          </div>
          <h2 style={{
            margin: 0, fontFamily: FONT.serif, fontWeight: 500,
            fontSize: isMobile ? 21 : 22, color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1.25,
          }}>
            Включить режим «{label}»?
          </h2>
        </div>

        {/* Описание */}
        <div style={{ fontSize: 14, lineHeight: 1.5, color: C.textSecondary }}>
          {assistantName} будет выполнять любые действия без запроса разрешения. Проверки прав полностью отключаются.
        </div>

        {/* Что разрешается без подтверждения */}
        <div style={{
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.xl, padding: '12px 14px',
        }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, color: C.dangerText, marginBottom: 8 }}>
            Без подтверждения станут доступны:
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {BYPASS_BULLETS.map(b => (
              <div key={b} style={{ display: 'flex', gap: 8, fontSize: 13, color: C.textSecondary, lineHeight: 1.4 }}>
                <span style={{ color: C.danger, flexShrink: 0 }}>•</span>
                <span>{b}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </Modal>
  );
}
