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
 * dense — вариант для плотных списков (панели «Документация» и «Сервисы»): отступы
 * вдвое меньше, высота подписи совпадает со строкой файла.
 *
 * С onClick разделитель становится переключателем группы (свернуть/развернуть) — корень
 * тогда кнопка, а не div: у сворачивания есть клавиатура и фокус, самодельный кликабельный
 * div их теряет. leading/trailing — место под шеврон и счётчик скрытых строк.
 */
export function ListDateDivider({
  title, dense = false, flash = false, onClick, leading, trailing, titleAttr,
  highlightOnHover = false, active = false, onLineClick, lineTitleAttr, onLineHover, beforeTitle, titleOverlay,
}: {
  title: string;
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
  // Слот ПОВЕРХ заголовка (absolute): булавка md-страницы раздела, у которой нет
  // колонки бейджа. Рисуется только при наведении либо у закреплённой — держать
  // постоянную пустую зону ради неё не нужно
  titleOverlay?: ReactNode;
}) {
  const [hover, setHover] = useState(false);
  const lineColor = flash ? C.accent : C.divider;
  // Линия рисуется границей, а не блоком высотой 1: блок центрируется по дробной
  // координате (высота строки нечётная), браузер размазывает его на два пикселя, и
  // черта то тонкая, то толстая — заметнее всего при сворачивании, когда высота
  // разделителя меняется. Граница же всегда ложится на целый пиксель
  const line = { flex: 1, height: 0, borderTop: `1px solid ${lineColor}` };
  const body = (
    <>
      {leading}
      <div style={line} />
      {beforeTitle}
      <span style={{
        position: 'relative',   // точка отсчёта для titleOverlay (булавка поверх текста)
        fontSize: 11, fontWeight: 700, whiteSpace: 'nowrap',
        color: flash ? C.accent : active ? C.textHeading : C.textSecondary,
      }}>
        {title}
        {titleOverlay}
      </span>
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
