// Подпись LoRA (спека §8.4): имя файла и дата раздельно. Имя режется многоточием, дата видна всегда.
// Общая разметка для карточки персонажа и шторки «Тонкая настройка».
import { C, FS, SP } from 'aihome_shell/kit';

export function LoraLabel({ parts }: { parts: { name: string; rest: string } }) {
  return (
    <div data-lora-label style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, minWidth: 0, fontSize: FS.sm }}>
      <span title={`${parts.name} ${parts.rest}`.trim()} style={{
        color: C.textPrimary, fontWeight: 600, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{parts.name}</span>
      {parts.rest && <span style={{ color: C.textMuted, flex: '0 0 auto', whiteSpace: 'nowrap' }}>{parts.rest}</span>}
    </div>
  );
}
