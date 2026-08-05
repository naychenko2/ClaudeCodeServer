// Строка оглавления markdown-документа: «прыгнуть к разделу».
//
// Общая на всех, кто показывает оглавление, — панель «Документация» (поповер над превью)
// и панель «Оглавление» (документ, открытый в центре). Раньше жила внутри DocsPanel;
// вынесена сюда, когда оглавление понадобилось второму месту: две копии строки разошлись
// бы в отступах и жестах при первой же правке, а роль у них одна.
//
// Вид намеренно совпадает со строкой папки в списке переходов (DocsPanel.FolderRow):
// это одно и то же действие — перейти к месту, — и разной плотностью они бы спорили.
import { useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';

// Отступ на уровень вложенности заголовка: плоский список не показывает структуру
const TOC_INDENT = SP.md;

// Высота строки: оглавление длинное (десятки заголовков), поэтому плотная — как
// строки документов и папок в «Документации»
const ROW_H = 22;

export interface TocRowProps {
  text: string;
  // Уровень заголовка (h1 → 1, h2 → 2, …): переводится в отступ
  level: number;
  onJump: () => void;
  // Раздел в чат цитатой. Не задан — правый клик оставляем браузеру
  onQuote?: () => void;
  // Раздел, который читают сейчас (или к которому только что перешли). Выделяется тем
  // же языком, что выбранный документ и текущая папка в «Документации»: подложка плюс
  // насыщенность — одной жирности мало на списке, который постоянно на виду
  active?: boolean;
}

export function TocRow({ text, level, onJump, onQuote, active }: TocRowProps) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onJump}
      // Раздел в чат — правым кликом. Раньше для этого у каждой строки висела стрелка,
      // но в узкой колонке ряд иконок забирал больше внимания, чем сами заголовки
      onContextMenu={onQuote ? e => { e.preventDefault(); onQuote(); } : undefined}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={onQuote ? `${text}\nПравый клик — раздел в чат цитатой` : text}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
        padding: `1px ${SP.sm}px`, border: 'none', borderRadius: R.md,
        cursor: 'pointer', textAlign: 'left', minWidth: 0,
        fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.35,
        minHeight: ROW_H,
        paddingLeft: SP.sm + (level - 1) * TOC_INDENT,
        // Текущий раздел держит выделение и под курсором: наведение мягче, чтобы эти
        // два состояния не спорили между собой
        background: active ? C.bgSelected : hover ? C.bgInset : 'transparent',
        color: active || hover ? C.textHeading : C.textSecondary,
        fontWeight: active ? 600 : 400,
      }}
    >
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{text}</span>
    </button>
  );
}
