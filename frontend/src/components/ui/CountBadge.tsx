import { C, FONT } from '../../lib/design';

// Кружок-счётчик на иконке: сколько всего чего-то есть за этой кнопкой.
// Жил внутри PanelRail (рельса панелей), теперь общий — тот же кружок нужен кнопке
// «Архив» в шапке списка чатов, и второй такой же, нарисованный руками, разъехался бы
// с первым на первой же правке габарита.
//
// Тон — РОЛЬ: accent (оранжевый) зовёт посмотреть немедленно, muted (серый) просто
// сообщает число. Архив всегда muted: лежащие в нём чаты ничего не требуют.
export function CountBadge({ value, inline, tone = 'accent', bottom }: {
  value: number; inline?: boolean; tone?: 'accent' | 'muted'; bottom?: boolean;
}) {
  const muted = tone === 'muted';
  return (
    <span style={{
      // Второй индикатор стоит в нижнем углу — стопка под основным (top:-6/right:-7).
      // Оба на правом краю: это и есть «ниже серая точка» — прямо под оранжевой
      ...(inline ? null : bottom
        ? { position: 'absolute', bottom: -6, right: -7 }
        : { position: 'absolute', top: -6, right: -7 }),
      minWidth: 14, height: 14, padding: '0 3px', flexShrink: 0,
      borderRadius: 7,
      background: muted ? C.bgSelected : C.accent,
      color: muted ? C.textSecondary : C.onAccent,
      // Тихий кружок сидит на подложке почти того же тона — без обводки он
      // расплывался бы в ней пятном
      ...(muted ? { boxShadow: `0 0 0 1px ${C.border}` } : null),
      fontFamily: FONT.sans, fontSize: 9, fontWeight: muted ? 600 : 700, lineHeight: '14px', textAlign: 'center',
    }}>
      {value > 99 ? '99+' : value}
    </span>
  );
}
