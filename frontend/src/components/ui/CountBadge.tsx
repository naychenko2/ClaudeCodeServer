import { C, FONT } from '../../lib/design';

// Тон кружка — РОЛЬ, а не цвет: accent (оранжевый) зовёт посмотреть немедленно,
// muted (серый) просто сообщает число, warning (жёлтый) предупреждает о состоянии,
// которое человек, скорее всего, забыл закрыть (доступ наружу к дев-серверу).
// Общий тип: те же значения носят точки-расшифровки в тултипе рельсы (RailFlyout),
// и разъехавшиеся списки молча теряли бы цвет на одной из сторон.
export type BadgeTone = 'accent' | 'muted' | 'warning';

// Кружок-счётчик на иконке: сколько всего чего-то есть за этой кнопкой.
// Жил внутри PanelRail (рельса панелей), теперь общий — тот же кружок нужен кнопке
// «Архив» в шапке списка чатов, и второй такой же, нарисованный руками, разъехался бы
// с первым на первой же правке габарита.
export function CountBadge({ value, inline, tone = 'accent', bottom }: {
  value: number; inline?: boolean; tone?: BadgeTone; bottom?: boolean;
}) {
  const muted = tone === 'muted';
  const warning = tone === 'warning';
  return (
    <span style={{
      // Второй индикатор стоит в нижнем углу — стопка под основным (top:-6/right:-7).
      // Оба на правом краю: это и есть «ниже серая точка» — прямо под оранжевой
      ...(inline ? null : bottom
        ? { position: 'absolute', bottom: -6, right: -7 }
        : { position: 'absolute', top: -6, right: -7 }),
      minWidth: 14, height: 14, padding: '0 3px', flexShrink: 0,
      borderRadius: 7,
      background: muted ? C.bgSelected : warning ? C.warning : C.accent,
      // Жёлтый светлый в обеих темах — цифра поверх него всегда тёмная (onWarning)
      color: muted ? C.textSecondary : warning ? C.onWarning : C.onAccent,
      // Тихий кружок сидит на подложке почти того же тона — без обводки он
      // расплывался бы в ней пятном
      ...(muted ? { boxShadow: `0 0 0 1px ${C.border}` } : null),
      fontFamily: FONT.sans, fontSize: 9, fontWeight: muted ? 600 : 700, lineHeight: '14px', textAlign: 'center',
    }}>
      {value > 99 ? '99+' : value}
    </span>
  );
}
