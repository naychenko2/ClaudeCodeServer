// Кнопка-компаньон «Открыть рядом» у внешней ссылки в ленте чата (докс/ADR-005).
// Оборачивает обычную ссылку в inline-flex вместе со значком: клик по самой ссылке
// не меняется (новая вкладка), кнопка — отдельное действие.
import { useContext, type ReactNode } from 'react';
import { PanelRight } from 'lucide-react';
import { C, R } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ChatOpenReaderContext } from './contexts';
import { FLAGS, useFeature } from '../../lib/featureFlags';
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

export function ReaderLinkWrap({ href, children }: { href?: string; children: ReactNode }) {
  const onOpenReader = useContext(ChatOpenReaderContext);
  const enabled = useFeature(FLAGS.linkReader);
  const domain = enabled && onOpenReader && href ? readerEligibleDomain(href) : null;
  // Фича выключена, контекста нет (мобильный «тонкий» чат без ридера) или ссылка не
  // годится под кнопку — молчим, ссылка ведёт себя ровно как раньше
  if (!domain) return <>{children}</>;
  return (
    <span className="cc-reader-wrap" style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
      {children}
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
