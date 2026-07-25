import { FolderInput } from 'lucide-react';
import type { CSSProperties } from 'react';
import { C, FONT, R, SP } from '../lib/design';

// Чип «откуда пришло сообщение»: имя чужого проекта либо «Вне проектов». Рисуется только
// когда источник ОТЛИЧАЕТСЯ от текущего чата — сравнение делает сервер (Session.senderOrigin),
// клиент лишь отображает. Живёт в двух местах — карточке входящего сообщения и карточке
// сообщения, ждущего доставки, — поэтому вынесен сюда, к смысловому соседу ChatOriginBadge.
//
// Размер иконки 11 намеренно мимо ICON_SIZE (минимум шкалы 14): чип — микро-метка в одну
// строку с @handle, иконка 14 распирала бы её. Тот же приём в ChatOriginBadge.
export function MessageOriginChip({ origin, style }: { origin: string; style?: CSSProperties }) {
  return (
    <span
      title={`Источник: ${origin}`}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0, maxWidth: '100%',
        padding: '1px 6px', borderRadius: R.max,
        background: C.bgSelected, border: `1px solid ${C.border}`,
        fontFamily: FONT.sans, fontSize: 11, lineHeight: 1.5, color: C.textSecondary,
        ...style,
      }}
    >
      <FolderInput size={11} strokeWidth={2.2} style={{ flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{origin}</span>
    </span>
  );
}
