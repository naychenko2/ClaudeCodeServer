import { Info } from 'lucide-react';
import { Toggle } from '../ui';
import { C, FONT } from '../../lib/design';

// Строка настройки «Не сохранять решения из этого чата» (opt-out для истории
// решений, ADR-004 §6). Один и тот же блок нужен и в пустом чате (пилюли
// NewChatSetup), и в шапке начатого (кнопка-история), поэтому живёт отдельным
// компонентом — иначе подпись, подсказка и сноска расходятся по копиям.
// Тексты — дословно из согласованной заметки «Тексты — Паспорта изменений».
export function DossierOptOutRow({ value, onChange }: {
  value: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 12 }}>
        {/* Вся подпись кликабельна — на тах удобнее тапнуть по тексту, чем по тумблеру */}
        <div
          role="button"
          tabIndex={-1}
          onClick={() => onChange(!value)}
          style={{
            flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600, color: C.textHeading,
            lineHeight: 1.35, paddingTop: 2, cursor: 'pointer', fontFamily: FONT.sans,
          }}
        >
          Не сохранять решения из этого чата
        </div>
        <Toggle checked={value} onChange={onChange} focusable ariaLabel="Не сохранять решения из этого чата" />
      </div>
      <p style={{ margin: '8px 0 0', fontSize: 11.5, color: C.textMuted, lineHeight: 1.45 }}>
        Записи из этого чата не попадут в историю решений и не уедут в репозиторий.
      </p>
      {/* Сноска появляется только во включённом состоянии — снимает страх
          «а что со старыми записями?» в момент включения */}
      {value && (
        <p style={{
          margin: '10px 0 0', paddingTop: 8, borderTop: `1px solid ${C.borderLight}`,
          display: 'flex', gap: 6, alignItems: 'flex-start',
          fontSize: 11.5, color: C.textSecondary, lineHeight: 1.45,
        }}>
          <Info size={13} strokeWidth={2} style={{ color: C.textMuted, marginTop: 1, flexShrink: 0 }} />
          <span>Уже записанные решения останутся в истории — настройка действует на новые записи.</span>
        </p>
      )}
    </div>
  );
}
