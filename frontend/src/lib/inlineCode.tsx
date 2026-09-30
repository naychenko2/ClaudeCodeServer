import type { CSSProperties, ReactNode } from 'react';
import { C, FONT, R } from './design';

// Подсветка `…` в обратных кавычках моноширинным — без тяжёлого markdown-парсера там, где
// текст почти простой: карточка онбординга (имя файла `CLAUDE.md`), пункты списка
// изменений claude CLI (флаги и команды `claude update`). Незакрытая кавычка остаётся текстом.
const codeStyle: CSSProperties = {
  fontFamily: FONT.mono, fontSize: '0.95em', background: C.bgPanel,
  padding: '0 4px', borderRadius: R.sm,
};

export function renderInlineCode(text: string): ReactNode[] {
  const parts: ReactNode[] = [];
  const re = /`([^`\n]+)`/g;
  let last = 0;
  let m: RegExpExecArray | null;
  let idx = 0;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push(text.slice(last, m.index));
    parts.push(<code key={idx++} style={codeStyle}>{m[1]}</code>);
    last = m.index + m[0].length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return parts;
}
