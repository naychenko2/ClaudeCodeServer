import { C, FONT, FS, R } from '../lib/design';

// Компактная карточка-опция «Локальная модель» / слот / пресет — строка-карточка
// (имя + подпись). Отдельный файл, чтобы группа «Пресеты» (PresetOptions) не тянула
// за собой весь слой секций выбора (циклический импорт).
export function QuickOptionCard({ title, subtitle, active, onClick }: {
  title: string; subtitle: string; active: boolean; onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        width: '100%', display: 'flex', flexDirection: 'column', gap: 2,
        padding: '8px 10px', borderRadius: R.md, cursor: 'pointer', textAlign: 'left',
        border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentLight : C.bgWhite,
      }}
    >
      <span style={{ fontSize: FS.md, fontWeight: 600, color: active ? C.textHeading : C.textPrimary, fontFamily: FONT.sans }}>
        {title}
      </span>
      <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.35 }}>
        {subtitle}
      </span>
    </button>
  );
}
