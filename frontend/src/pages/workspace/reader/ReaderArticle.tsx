// Рендерер статьи ридера — СВОЙ экземпляр react-markdown, а не переиспользование
// MarkdownContent целиком (ADR-005): текст в панели недоверенный (чужой сайт), поэтому
// здесь НЕТ resolveFileMention (превращения путей в ссылки на файлы проекта — в чужом
// тексте это фишинговый примитив) и урезанный urlTransform — только defaultUrlTransform,
// без прокси-послаблений MarkdownContent.tsx. rehype-raw не подключается нигде в проекте,
// поэтому сырой HTML физически не исполняется.
import { useEffect, useMemo, useRef, type CSSProperties } from 'react';
import ReactMarkdown, { defaultUrlTransform, type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { C, FONT } from '../../../lib/design';
import { slugify } from '../../../lib/docsLinks';
import { useHeadings, scrollToHeading } from '../../../hooks/useHeadings';

const REMARK_PLUGINS = [remarkGfm];

interface Props {
  markdown: string;
  // #fragment из адреса статьи — «дешёвый якорь» ADR §7: совпадение по слагу заголовка,
  // не совпало — молча открываем сверху, без ошибки
  anchor: string | null;
  // Переход по ссылке ВНУТРИ статьи — грузит следующую страницу в тот же ридер
  onFollow: (url: string) => void;
}

export function ReaderArticle({ markdown, anchor, onFollow }: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(rootRef, markdown);

  useEffect(() => {
    if (!anchor) { rootRef.current?.scrollTo({ top: 0 }); return; }
    const target = headings.find(h => slugify(h.text) === anchor);
    if (target) scrollToHeading(target);
    else rootRef.current?.scrollTo({ top: 0 });
    // headings пересобираются на каждый рендер статьи — привязка нужна только к смене markdown/anchor
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [markdown, anchor]);

  const components = useMemo<Components>(() => ({
    h1: ({ children }) => <h1 style={ART.h1}>{children}</h1>,
    h2: ({ children }) => <h2 style={ART.h2}>{children}</h2>,
    h3: ({ children }) => <h3 style={ART.h3}>{children}</h3>,
    p: ({ children }) => <p style={ART.p}>{children}</p>,
    ul: ({ children }) => <ul style={ART.list}>{children}</ul>,
    ol: ({ children }) => <ol style={ART.list}>{children}</ol>,
    li: ({ children }) => <li style={ART.li}>{children}</li>,
    blockquote: ({ children }) => <blockquote style={ART.blockquote}>{children}</blockquote>,
    a: ({ children, href }) => {
      if (!href) return <span>{children}</span>;
      // mailto:/tel: и прочие нестандартные схемы — обычная ссылка вовне, как в чате
      const isHttp = /^https?:\/\//i.test(href);
      if (!isHttp) return <a href={href} style={ART.a} target="_blank" rel="noopener noreferrer">{children}</a>;
      return (
        <a
          href={href}
          style={ART.a}
          onClick={e => { e.preventDefault(); onFollow(href); }}
        >
          {children}
        </a>
      );
    },
    code: ({ className, children }) => {
      const language = /language-(\w+)/.exec(className || '')?.[1];
      const text = String(children).replace(/\n$/, '');
      if (text.includes('\n') || language) {
        return (
          <pre style={ART.pre}><code style={ART.preCode}>{text}</code></pre>
        );
      }
      return <code style={ART.code}>{children}</code>;
    },
    pre: ({ children }) => <>{children}</>,
    img: ({ src, alt }) => !src ? null : (
      <figure style={ART.figure}>
        {/* referrerpolicy=no-referrer — сайт не узнаёт, откуда пришли (ADR §1); lazy — картинки
            ниже экрана вообще не запрашиваются */}
        <img src={src} alt={alt ?? ''} referrerPolicy="no-referrer" loading="lazy" style={ART.img} />
        {alt && <figcaption style={ART.figcaption}>{alt}</figcaption>}
      </figure>
    ),
    strong: ({ children }) => <strong style={{ fontWeight: 600 }}>{children}</strong>,
    hr: () => <hr style={{ border: 'none', borderTop: `1px solid ${C.border}`, margin: '16px 0' }} />,
    table: ({ children }) => <div style={ART.tableWrap}><table style={ART.table}>{children}</table></div>,
    th: ({ children }) => <th style={ART.th}>{children}</th>,
    td: ({ children }) => <td style={ART.td}>{children}</td>,
  }), [onFollow]);

  return (
    <div ref={rootRef} style={ART.root}>
      <ReactMarkdown remarkPlugins={REMARK_PLUGINS} urlTransform={defaultUrlTransform} components={components}>
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

// Типографика колонки чтения — см. постановку и макет (docs/mockups/link-reader-v1.html §6)
const ART: Record<string, CSSProperties> = {
  root: { color: C.textPrimary, fontSize: 14, lineHeight: 1.7, overflowWrap: 'anywhere' },
  h1: { fontFamily: FONT.serif, fontSize: 22, lineHeight: 1.3, color: C.textHeading, margin: '0 0 4px' },
  h2: { fontFamily: FONT.serif, fontSize: 18, color: C.textHeading, margin: '24px 0 8px' },
  h3: { fontSize: 14, fontWeight: 600, color: C.textHeading, margin: '18px 0 6px' },
  p: { margin: '0 0 12px' },
  list: { margin: '0 0 12px', paddingLeft: 20 },
  li: { marginBottom: 4 },
  blockquote: { margin: '12px 0', padding: '2px 0 2px 12px', borderLeft: `3px solid ${C.accent}`, color: C.textSecondary, fontStyle: 'italic' },
  a: { color: C.info, textDecoration: 'underline', cursor: 'pointer' },
  pre: { background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: '0 0 12px', overflowX: 'auto' },
  preCode: { fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, whiteSpace: 'pre' },
  code: { fontFamily: FONT.mono, fontSize: 12.5, background: C.bgSelected, padding: '1px 5px', borderRadius: 6, color: C.textHeading },
  figure: { margin: '0 0 14px' },
  img: { maxWidth: '100%', height: 'auto', borderRadius: 8, border: `1px solid ${C.border}`, display: 'block' },
  figcaption: { fontSize: 11, color: C.textMuted, marginTop: 5 },
  tableWrap: { overflowX: 'auto', margin: '0 0 14px' },
  table: { borderCollapse: 'collapse', minWidth: '100%', fontSize: 13 },
  th: { border: `1px solid ${C.border}`, padding: '6px 10px', background: C.bgInset, fontWeight: 600, textAlign: 'left', whiteSpace: 'nowrap' },
  td: { border: `1px solid ${C.border}`, padding: '6px 10px', whiteSpace: 'nowrap' },
};
