import { useState, useEffect, useContext } from 'react';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { MermaidDiagram } from '../MermaidDiagram';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { ChatProjectContext } from './contexts';

// Картинка из markdown: внешние URL (http/https/data) — напрямую; локальный путь файла
// проекта (например, картинка, скачанная Claude) — грузим через API и показываем как data-URL.
function ChatImage({ src, alt }: { src?: string; alt?: string }) {
  const project = useContext(ChatProjectContext);
  // /api/proxy?... — уже проксированный URL (от urlTransform)
  const isRemote = !!src && /^(https?:|data:|\/api\/proxy)/i.test(src);
  const [resolved, setResolved] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!src || isRemote || !project) return;
    let cancelled = false;
    // Путь относительно корня проекта (Claude мог дать абсолютный путь внутри проекта)
    let rel = src.replace(/\\/g, '/');
    const root = project.rootPath.replace(/\\/g, '/');
    if (rel.toLowerCase().startsWith(root.toLowerCase())) rel = rel.slice(root.length);
    rel = rel.replace(/^\/+/, '');
    api.files.getContent(project.id, rel)
      .then(r => {
        if (cancelled) return;
        if (r.isImage && r.base64) setResolved(`data:${r.mimeType ?? 'image/png'};base64,${r.base64}`);
        else setFailed(true);
      })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [src, isRemote, project]);

  const finalSrc = isRemote ? src : resolved;

  if (failed) return <span style={{ fontSize: 13, color: C.textMuted }}>🖼 {alt || src}</span>;
  if (!finalSrc) return <span style={{ fontSize: 13, color: C.textMuted }}>Загрузка изображения…</span>;

  return (
    <a href={finalSrc} target="_blank" rel="noopener noreferrer" style={{ display: 'block', margin: '6px 0' }}>
      <img src={finalSrc} alt={alt ?? ''} loading="lazy" onError={() => setFailed(true)}
        style={{ maxWidth: '100%', height: 'auto', display: 'block', borderRadius: 8, border: `1px solid ${C.border}` }} />
    </a>
  );
}

// Рендер текста Claude с поддержкой Markdown
export function MarkdownContent({ text }: { text: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      urlTransform={(url, key) => {
        // fal.media src — блокируем: картинки уже показаны в MediaBlock из tool_result
        if (key === 'src' && matchesHosts(url, FAL_HOSTS)) return null;
        // остальные внешние URL (src и href) — через прокси если домен разрешён
        return isProxiable(url) ? proxyUrl(url) : defaultUrlTransform(url);
      }}
      components={{
        p: ({ children }) => (
          <p style={{ margin: '0 0 8px 0', lineHeight: 1.6 }}>{children}</p>
        ),
        h1: ({ children }) => (
          <h1 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 20, fontWeight: 600, margin: '10px 0 6px', color: C.textHeading, letterSpacing: '-0.01em' }}>{children}</h1>
        ),
        h2: ({ children }) => (
          <h2 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 17, fontWeight: 600, margin: '8px 0 5px', color: C.textHeading, letterSpacing: '-0.01em' }}>{children}</h2>
        ),
        h3: ({ children }) => (
          <h3 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 15, fontWeight: 600, margin: '6px 0 4px', color: C.textHeading }}>{children}</h3>
        ),
        pre: ({ children }) => <>{children}</>,
        code: ({ className, children, ...props }) => {
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
                customStyle={{ borderRadius: 8, fontSize: 12.5, margin: '6px 0', padding: '10px 14px', fontFamily: FONT.mono, overflowX: 'auto' }}
              >
                {text}
              </SyntaxHighlighter>
            );
          }
          if (text.includes('\n')) {
            // Код без указания языка — на светлой панели вывода (лёгкий тёплый фон вместо тёмного)
            return (
              <pre style={{ background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: '6px 0', overflowX: 'auto' }}>
                <code style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, lineHeight: 1.5 }} {...props}>{text}</code>
              </pre>
            );
          }
          return (
            <code style={{ fontFamily: FONT.mono, background: C.bgInset, padding: '1px 5px', borderRadius: 4, fontSize: '0.88em', color: C.accent }} {...props}>
              {children}
            </code>
          );
        },
        ul: ({ children }) => <ul style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ul>,
        ol: ({ children }) => <ol style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ol>,
        li: ({ children }) => <li style={{ marginBottom: 3, lineHeight: 1.6 }}>{children}</li>,
        blockquote: ({ children }) => (
          <blockquote style={{ borderLeft: `3px solid ${C.accent}`, paddingLeft: 12, margin: '6px 0', color: C.textSecondary, fontStyle: 'italic' }}>
            {children}
          </blockquote>
        ),
        a: ({ children, href }) => (
          <a href={href} style={{ color: C.accent, textDecoration: 'underline' }} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        ),
        // Картинки из markdown: внешние URL — напрямую, локальные пути файлов проекта — через API
        img: ({ src, alt }) => {
          if (!src) return null;
          return <ChatImage src={src} alt={alt ?? ''} />;
        },
        strong: ({ children }) => <strong style={{ fontWeight: 600 }}>{children}</strong>,
        hr: () => <hr style={{ border: 'none', borderTop: `1px solid ${C.border}`, margin: '10px 0' }} />,
        table: ({ children }) => (
          <div style={{ overflowX: 'auto', margin: '6px 0' }}>
            <table style={{ borderCollapse: 'collapse', minWidth: '100%', fontSize: 13 }}>{children}</table>
          </div>
        ),
        th: ({ children }) => (
          <th style={{ border: `1px solid ${C.border}`, padding: '6px 10px', background: C.bgInset, fontWeight: 600, textAlign: 'left' }}>{children}</th>
        ),
        td: ({ children }) => (
          <td style={{ border: `1px solid ${C.border}`, padding: '6px 10px' }}>{children}</td>
        ),
      }}
    >
      {text}
    </ReactMarkdown>
  );
}

// Оборачивает внешний URL через backend-прокси (/api/proxy) — поддерживает любой тип контента
export function proxyUrl(url: string): string {
  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;
  const params = new URLSearchParams({ url });
  if (token) params.set('access_token', token);
  return `/api/proxy?${params}`;
}

// Домены, которые разрешены прокси-контроллером на бэкенде (синхронизировать с AllowedHosts)
const PROXY_ALLOWED_HOSTS = [
  'fal.media', 'fal.run', 'queue.fal.run', 'cdn.fal.ai',
  'storage.googleapis.com', 'replicate.delivery', 'pbxt.replicate.delivery',
];

// Домены fal.ai — их src в markdown не проксируем: картинки уже показаны в MediaBlock
const FAL_HOSTS = ['fal.media', 'fal.run', 'queue.fal.run', 'cdn.fal.ai'];

function matchesHosts(url: string, hosts: string[]): boolean {
  try {
    const u = new URL(url);
    return hosts.some(h => u.hostname === h || u.hostname.endsWith('.' + h));
  } catch { return false; }
}

function isProxiable(url: string): boolean {
  try {
    const u = new URL(url);
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return false;
    return matchesHosts(url, PROXY_ALLOWED_HOSTS);
  } catch { return false; }
}
