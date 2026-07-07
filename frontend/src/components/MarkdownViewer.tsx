import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { Components } from 'react-markdown';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { MermaidDiagram } from './MermaidDiagram';
import { C, FONT } from '../lib/design';

interface Props {
  content: string;
}

const mono = FONT.mono;
const serif = FONT.serif;

const components: Components = {
  h1: ({ children }) => (
    <h1 style={{ fontFamily: serif, fontSize: 26, fontWeight: 700, margin: '0 0 16px', color: C.textHeading, letterSpacing: '-0.02em', lineHeight: 1.25 }}>{children}</h1>
  ),
  h2: ({ children }) => (
    <h2 style={{ fontFamily: serif, fontSize: 21, fontWeight: 700, margin: '24px 0 12px', color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1.3 }}>{children}</h2>
  ),
  h3: ({ children }) => (
    <h3 style={{ fontFamily: serif, fontSize: 17, fontWeight: 700, margin: '20px 0 8px', color: C.textHeading, lineHeight: 1.35 }}>{children}</h3>
  ),
  h4: ({ children }) => (
    <h4 style={{ fontFamily: serif, fontSize: 15, fontWeight: 700, margin: '16px 0 6px', color: C.textHeading }}>{children}</h4>
  ),
  h5: ({ children }) => (
    <h5 style={{ fontFamily: serif, fontSize: 13.5, fontWeight: 700, margin: '14px 0 5px', color: C.textHeading }}>{children}</h5>
  ),
  h6: ({ children }) => (
    <h6 style={{ fontFamily: serif, fontSize: 12.5, fontWeight: 700, margin: '12px 0 4px', color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.03em' }}>{children}</h6>
  ),
  p: ({ children }) => (
    <p style={{ margin: '0 0 14px', lineHeight: 1.7, color: C.textHeading }}>{children}</p>
  ),
  a: ({ href, children }) => (
    <a href={href} style={{ color: C.accent, textDecoration: 'underline' }}>{children}</a>
  ),
  strong: ({ children }) => (
    <strong style={{ fontWeight: 700, color: C.textHeading }}>{children}</strong>
  ),
  em: ({ children }) => (
    <em style={{ fontStyle: 'italic' }}>{children}</em>
  ),
  // pre не оборачиваем — блоки кода целиком собираются в компоненте code ниже
  pre: ({ children }) => <>{children}</>,
  code: ({ children, className }) => {
    const language = /language-(\w+)/.exec(className || '')?.[1];
    const text = String(children).replace(/\n$/, '');
    if (language === 'mermaid') {
      return <MermaidDiagram code={text} />;
    }
    if (language) {
      return (
        <SyntaxHighlighter
          language={language}
          style={oneDark}
          customStyle={{ borderRadius: 10, fontSize: 12.5, margin: '0 0 16px', padding: '14px 16px', fontFamily: mono, overflowX: 'auto' }}
        >
          {text}
        </SyntaxHighlighter>
      );
    }
    // Многострочный блок без указания языка (например ASCII-схема) — моноширинный на светлом фоне
    if (text.includes('\n')) {
      return (
        <pre style={{ margin: '0 0 16px', background: C.bgInset, borderRadius: 10, padding: '14px 16px', overflowX: 'auto', fontFamily: mono, fontSize: 12.5, lineHeight: 1.6, color: C.textHeading }}>
          <code style={{ fontFamily: mono }}>{text}</code>
        </pre>
      );
    }
    return (
      <code style={{ fontFamily: mono, fontSize: 12.5, background: C.bgInset, padding: '1px 5px', borderRadius: 4, color: C.textHeading }}>
        {children}
      </code>
    );
  },
  ul: ({ children }) => (
    <ul style={{ margin: '0 0 14px', paddingLeft: 22, lineHeight: 1.7 }}>{children}</ul>
  ),
  ol: ({ children }) => (
    <ol style={{ margin: '0 0 14px', paddingLeft: 22, lineHeight: 1.7 }}>{children}</ol>
  ),
  li: ({ children }) => (
    <li style={{ margin: '3px 0', color: C.textHeading }}>{children}</li>
  ),
  blockquote: ({ children }) => (
    <blockquote style={{ margin: '0 0 14px', padding: '10px 16px', borderLeft: `3px solid ${C.accent}`, background: C.bgMain, borderRadius: '0 8px 8px 0', color: C.textSecondary }}>
      {children}
    </blockquote>
  ),
  hr: () => (
    <hr style={{ margin: '20px 0', border: 'none', borderTop: `1px solid ${C.border}` }} />
  ),
  table: ({ children }) => (
    <div style={{ overflowX: 'auto', marginBottom: 14 }}>
      <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 13 }}>{children}</table>
    </div>
  ),
  th: ({ children }) => (
    <th style={{ padding: '7px 12px', background: C.bgInset, fontWeight: 700, textAlign: 'left', borderBottom: `2px solid ${C.border}`, color: C.textHeading }}>{children}</th>
  ),
  td: ({ children }) => (
    <td style={{ padding: '7px 12px', borderBottom: `1px solid ${C.border}`, color: C.textHeading }}>{children}</td>
  ),
  img: ({ src, alt }) => (
    <img src={src} alt={alt ?? ''} style={{ maxWidth: '100%', borderRadius: 8, margin: '8px 0' }} />
  ),
};

export function MarkdownViewer({ content }: Props) {
  return (
    <div style={{ fontFamily: FONT.sans, fontSize: 14, lineHeight: 1.7, color: C.textHeading, width: '100%' }}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>{content}</ReactMarkdown>
    </div>
  );
}
