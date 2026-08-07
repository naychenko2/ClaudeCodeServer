import { useState, type ReactNode } from 'react';
import { C, R } from '../lib/design';

// Мигание при переходе к группе списка: одно короткое затухание по прозрачности — без
// подложки, чтобы не спорить с выделением строки. Класс экспортируется: вешать его можно и
// на всю секцию целиком (разделитель + её строки), а не только на подпись.
// Инжектим один раз, как focus-ring у IconButton.
export const LIST_FLASH_CLASS = 'cc-list-flash';
export const LIST_FLASH_MS = 520;   // один цикл 500 мс + запас на снятие класса

if (typeof document !== 'undefined' && !document.getElementById('cc-list-flash-style')) {
  const el = document.createElement('style');
  el.id = 'cc-list-flash-style';
  el.textContent =
    `@keyframes ccListFlash{0%,100%{opacity:1}50%{opacity:.2}}` +
    `.${LIST_FLASH_CLASS}{animation:ccListFlash .5s ease-in-out 1}`;
  document.head.appendChild(el);
}

/**
 * Разделитель групп в списках: тонкая черта с подписью.
 * В списках чатов заменяет дату на самих карточках — по разделителю видно, какие чаты
 * относятся к одному дню, и карточка не тратит на это место.
 *
 * align='left' + dense — вариант для плотных списков (панель «Документация»): подпись прижата
 * влево, левой черты нет вовсе (её огрызок только отодвигал подпись от колонки иконок,
 * а границу группы держит черта справа), отступы вдвое меньше.
 *
 * С onClick разделитель становится переключателем группы (свернуть/развернуть) — корень
 * тогда кнопка, а не div: у сворачивания есть клавиатура и фокус, самодельный кликабельный
 * div их теряет. leading/trailing — место под шеврон и счётчик скрытых строк.
 */
export function ListDateDivider({
  title, subtitle, align = 'center', dense = false, flash = false, onClick, leading, trailing, titleAttr,
  highlightOnHover = false, active = false, onLineClick, lineTitleAttr, onLineHover, beforeTitle,
}: {
  title: string;
  // Приписка сразу после подписи, приглушённо: у групп документации это родительский
  // раздел. Название группы при этом остаётся коротким — путь целиком в него не влезает
  // и читается хуже, чем «где я» одним словом
  subtitle?: string;
  align?: 'center' | 'left';
  dense?: boolean;
  // Кратко подсветить и мигнуть — «вот сюда прокрутили»
  flash?: boolean;
  onClick?: () => void;
  leading?: ReactNode;
  trailing?: ReactNode;
  // Подсказка при наведении: у кликабельного разделителя объясняет, что будет по клику
  titleAttr?: string;
  // Подсветить подложку под курсором. Включаем там, где клик ОТКРЫВАЕТ документ (страница
  // раздела), а не просто сворачивает группу: подложка обещает переход к контенту
  highlightOnHover?: boolean;
  // Разделитель-документ ПОКАЗАН сейчас (страница раздела открыта). Выделяется тем же
  // способом, что строка документа, — иначе открытого документа в списке не видно вовсе:
  // строкой страница раздела не рисуется, она и есть эта подпись
  active?: boolean;
  // Свой обработчик на правую линию: когда сама подпись открывает документ, черта справа
  // остаётся под сворачивание группы — как шеврон. Клик по ней не всплывает к onClick
  onLineClick?: () => void;
  lineTitleAttr?: string;
  // Наведение на правую линию: сообщаем наружу, чтобы подсветить тот шеврон, чьё действие
  // линия дублирует (одиночный у листового раздела, двойной у раздела с поддеревом)
  onLineHover?: (hovering: boolean) => void;
  // Слот между левой линией и заголовком — под бейдж/булавку строки-раздела
  beforeTitle?: ReactNode;
}) {
  const [hover, setHover] = useState(false);
  const lineColor = flash ? C.accent : C.divider;
  const line = { flex: 1, height: 1, background: lineColor };
  const body = (
    <>
      {leading}
      {/* Слева черты нет только в плотном варианте: там подпись стоит в колонке строк
          списка, и огрызок линии перед ней сбивал бы иконку раздела с колонки иконок
          документов */}
      {align === 'left' ? null : <div style={line} />}
      {beforeTitle}
      <span style={{
        fontSize: 11, fontWeight: 700, whiteSpace: 'nowrap',
        color: flash ? C.accent : active ? C.textHeading : C.textSecondary,
      }}>
        {title}
      </span>
      {subtitle && (
        // Родитель — та же мишень, что черта справа: приписка не самостоятельная строка,
        // а контекст группы, и клик по ней сворачивает раздел, а не открывает его страницу.
        // span, а не button: подпись сама кнопка, а button в button html не разрешает —
        // поэтому клик гасим stopPropagation'ом, как у линии
        <span
          onClick={onLineClick ? e => { e.stopPropagation(); onLineClick(); } : undefined}
          onMouseEnter={onLineHover ? () => onLineHover(true) : undefined}
          onMouseLeave={onLineHover ? () => onLineHover(false) : undefined}
          title={onLineClick ? lineTitleAttr : undefined}
          style={{
            display: 'flex', alignItems: 'center', gap: 8, minWidth: 0,
            cursor: onLineClick ? 'pointer' : undefined,
          }}
        >
          {/* Центральная точка между папкой и родителем: разделяет их отчётливее пробела,
              вертикально по центру строки. Декоративная — скринридеру не нужна */}
          <span aria-hidden style={{ fontSize: 10, color: C.textMuted, flexShrink: 0, margin: '0 -4px' }}>·</span>
          <span style={{
            fontSize: 10, fontWeight: 400, whiteSpace: 'nowrap',
            color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {subtitle}
          </span>
        </span>
      )}
      {onLineClick ? (
        // Зона клика во всю высоту строки, черта в 1px — по центру: попасть по линии
        // толщиной в пиксель мышью нельзя, поэтому кликабельна вся правая колонка.
        // stopPropagation держит жест отдельно от onClick подписи (та открывает документ)
        <div
          onClick={e => { e.stopPropagation(); onLineClick(); }}
          onMouseEnter={onLineHover ? () => onLineHover(true) : undefined}
          onMouseLeave={onLineHover ? () => onLineHover(false) : undefined}
          title={lineTitleAttr}
          style={{
            flex: 1, alignSelf: 'stretch', cursor: 'pointer',
            display: 'flex', alignItems: 'center',
          }}
        >
          <div style={{ flex: 1, height: 1, background: lineColor }} />
        </div>
      ) : (
        <div style={line} />
      )}
      {trailing}
    </>
  );
  const layout = {
    display: 'flex', alignItems: 'center', gap: 8,
    // Плотный вариант стоит в колонке строк списка (группы документации), поэтому
    // его высота считается по строке файла: 3 + бейдж 16 + 3 = 22, ровно ROW_H
    // соседних документов. С прежними 5/3 подпись была на два пикселя выше их и
    // читалась как более крупный элемент — особенно под заливкой выделения
    padding: dense ? '3px 4px' : '10px 4px 7px',
  };
  if (!onClick) return <div style={layout}>{body}</div>;
  return (
    <button
      onClick={onClick}
      onMouseEnter={highlightOnHover ? () => setHover(true) : undefined}
      onMouseLeave={highlightOnHover ? () => setHover(false) : undefined}
      title={titleAttr}
      style={{
        ...layout,
        width: '100%', border: 'none',
        // Порядок и цвета как у строки документа: выделение сильнее наведения — иначе
        // подсветка открытого раздела гасла под курсором, будто документ закрылся
        background: active ? C.accentMuted : highlightOnHover && hover ? C.bgSelected : 'transparent',
        borderRadius: highlightOnHover || active ? R.md : undefined,
        // Полоска выделения — та же и тем же способом, что у строки документа: по
        // левому краю СВОЕЙ заливки. Отдельным слоем по краю всей строки она уже
        // пробовалась и читалась чужеродно — раздел выделяется как документ, и точка
        boxShadow: active ? `inset 2px 0 0 ${C.accent}` : undefined,
        cursor: 'pointer', font: 'inherit', textAlign: 'left',
      }}
    >
      {body}
    </button>
  );
}
