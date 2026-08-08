// Внешняя http(s)-ссылка в ленте чата (докс/ADR-005, ADR-006 §2/§5).
// Клик по самой ссылке открывает панель «Чтение» — и в ответах ассистента, и в
// сообщениях пользователя; клик с модификатором (Ctrl/Cmd/Shift/Alt) и средняя
// кнопка мыши уводят в браузер, как раньше. Кнопка-компаньон «Открыть рядом» —
// отдельное видимое действие рядом со ссылкой. Какой ссылке положено открытие в
// панели — решает общий фильтр readerEligibility, единый для iframe- и MD-режима:
// второго списка не заводим. Автооткрытия панели по факту отправки ссылки нет.
import { useContext, type MouseEvent, type ReactNode } from 'react';
import { PanelRight } from 'lucide-react';
import { C, R } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ChatOpenReaderContext } from './contexts';
import { readerEligibleDomain } from '../../pages/workspace/reader/readerEligibility';

// Стили кнопки инжектим один раз (тот же приём, что у IconButton.FOCUS_CLASS):
// hover/focus-visible на псевдо-классах CSS не выразить через inline-style, а
// проявление «по наведению на ссылку ИЛИ на саму кнопку» без JS-состояния решает
// :hover обёртки — она включает и наведение на текст ссылки, и на кнопку внутри неё.
const STYLE_ID = 'cc-reader-link-style';
if (typeof document !== 'undefined' && !document.getElementById(STYLE_ID)) {
  const el = document.createElement('style');
  el.id = STYLE_ID;
  el.textContent = `
    .cc-reader-btn { opacity: 0; transition: opacity .12s ease-out, background .12s, color .12s; }
    .cc-reader-wrap:hover .cc-reader-btn, .cc-reader-btn:focus-visible {
      opacity: 1; background: ${C.accentLight}; color: ${C.accent};
    }
    @media (hover: none) {
      .cc-reader-btn { opacity: 1; background: transparent; color: ${C.textMuted}; position: relative; }
      .cc-reader-btn::after { content: ''; position: absolute; inset: -12px; }
    }
  `;
  document.head.appendChild(el);
}

// Стиль ссылки в ленте (раньше жил в a-компоненте MarkdownContent)
const LINK_STYLE = { color: C.accent, textDecoration: 'underline' } as const;

export function ReaderLinkWrap({ href, children }: { href?: string; children: ReactNode }) {
  const onOpenReader = useContext(ChatOpenReaderContext);
  const domain = onOpenReader && href ? readerEligibleDomain(href) : null;

  // Перехватываем только чистый клик левой кнопкой; любой модификатор или другая
  // кнопка — поведение браузера по умолчанию (новая вкладка/окно)
  const onClick = domain ? (e: MouseEvent<HTMLAnchorElement>) => {
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    onOpenReader!(href!);
  } : undefined;

  const anchor = (
    <a href={href} style={LINK_STYLE} target="_blank" rel="noopener noreferrer" onClick={onClick}>
      {children}
    </a>
  );

  // Контекста нет (мобильный «тонкий» чат без ридера) или ссылка не годится под панель —
  // молчим, ссылка ведёт себя ровно как раньше (новая вкладка)
  if (!domain) return anchor;

  return (
    <span className="cc-reader-wrap" style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
      {anchor}
      <button
        type="button"
        className="cc-reader-btn"
        aria-label={`Открыть рядом: ${domain}`}
        onClick={e => { e.preventDefault(); e.stopPropagation(); onOpenReader!(href!); }}
        style={{
          display: 'inline-grid', placeItems: 'center', width: 20, height: 20, flexShrink: 0,
          borderRadius: R.sm, border: 'none', background: 'transparent', color: C.textMuted,
          cursor: 'pointer', verticalAlign: -4,
        }}
      >
        <PanelRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </button>
    </span>
  );
}
