import { useState, useEffect, useContext, type CSSProperties, type ReactNode } from 'react';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { FileText } from 'lucide-react';
import { MermaidDiagram } from '../MermaidDiagram';
import { api } from '../../lib/api';
import { C, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { toRelative } from '../../lib/paths';
import { useProjectFileIndex, lookupProjectFile } from '../../lib/projectFileIndex';
import { ChatProjectContext, ChatOpenFileContext } from './contexts';

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

// Однострочный код в тексте (`путь` / `флаг`) — общий стиль для обычного кода и ссылки на файл
const INLINE_CODE: CSSProperties = {
  fontFamily: FONT.mono, background: C.bgInset, padding: '1px 5px',
  borderRadius: 4, fontSize: '0.88em', color: C.accent,
};

// Ссылка на файл проекта в тексте ассистента: открывает файл на просмотр там же, где
// дерево и карточки инструментов. Рендерится только для файлов, которые реально есть
// в проекте — битых ссылок и «открыл, а там 404» не бывает.
function FileLink({ path, onOpen, mono, children }: {
  path: string;
  onOpen: (path: string) => void;
  mono?: boolean;
  children: ReactNode;
}) {
  const [hover, setHover] = useState(false);
  return (
    <span
      onClick={() => onOpen(path)}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={`Открыть ${path}`}
      style={{
        ...(mono ? INLINE_CODE : null),
        display: 'inline-flex', alignItems: 'center', gap: SP.xxs,
        color: C.accent, cursor: 'pointer',
        textDecoration: hover ? 'underline' : 'none',
      }}
    >
      <FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {children}
    </span>
  );
}

// Служебные маркеры протоколов — не для глаз: завершение цикла «до готово»
// (<promise>ГОТОВО</promise>, слово-обещание конфигурируемо на бэкенде) и протокол
// «Командной реализации» — <team:work>постановка</team>, <escalate:вид>…</escalate>.
const SERVICE_MARKERS = /<promise>[\s\S]*?<\/promise>|<team:work>[\s\S]*?<\/team>|<escalate:[a-z]+>[\s\S]*?<\/escalate>/gi;
// Незакрытый маркер в хвосте — ход ещё стримится, закрывающий тег придёт позже;
// без этого пользователь успевает прочитать «<team:work>Создать …» целиком
const OPEN_MARKER = /<(?:promise|team:work|escalate:[a-z]+)>[\s\S]*$/i;
// Код-фрагменты (```блок``` и `инлайн`) — зона, где маркер остаётся как есть: там его
// цитируют, объясняя протокол. Так же его игнорируют и детекторы бэкенда
// (SessionManager.ParseWorkMarker / ParseEscalationMarker / проверка promise)
const CODE_SPANS = /```[\s\S]*?(?:```|$)|`[^`\n]*`/g;

function stripOutsideCode(chunk: string): string {
  return chunk.replace(SERVICE_MARKERS, '').replace(OPEN_MARKER, '');
}

export function stripServiceMarkers(text: string): string {
  let out = '';
  let last = 0;
  for (const m of text.matchAll(CODE_SPANS)) {
    out += stripOutsideCode(text.slice(last, m.index)) + m[0];
    last = m.index + m[0].length;
  }
  out += stripOutsideCode(text.slice(last));
  return out.replace(/\n{3,}/g, '\n\n').trim();
}

// Рендер текста Claude с поддержкой Markdown
export function MarkdownContent({ text }: { text: string }) {
  const project = useContext(ChatProjectContext);
  const onOpenFile = useContext(ChatOpenFileContext);
  const fileIndex = useProjectFileIndex(project?.id ?? null);
  // Упоминание пути → файл проекта, если он там реально есть (иначе ссылки не будет).
  // Вне проекта и без обработчика открытия фича молчит — текст как раньше.
  const resolveFile = (raw?: string | null): string | null =>
    project && onOpenFile && raw ? lookupProjectFile(fileIndex, raw, project.rootPath) : null;

  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      urlTransform={(url, key) => {
        // Медиа-домены (fal/glif) src — блокируем: медиа уже показаны в MediaBlock из tool_result
        if (key === 'src' && matchesHosts(url, MEDIA_HOSTS)) return null;
        // Абсолютный путь внутри проекта (Claude часто пишет полный путь) — оставляем как
        // есть: defaultUrlTransform режет его, приняв «C:» за неизвестный протокол
        if (project && /^[a-zA-Z]:[\\/]/.test(url) && toRelative(url, project.rootPath)) return url;
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
          // ```ui — служебная Gallery-разметка glif: показываем обычным код-блоком,
          // медиа рендерятся из resource_link/assets результата инструмента
          if (language === 'ui') {
            return (
              <pre style={{ background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: '6px 0', overflowX: 'auto' }}>
                <code style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, lineHeight: 1.5 }} {...props}>{text}</code>
              </pre>
            );
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
          // Путь к существующему файлу проекта в бэктиках — кликабельная ссылка на просмотр
          const filePath = resolveFile(text);
          if (filePath) return <FileLink path={filePath} onOpen={onOpenFile!} mono>{children}</FileLink>;
          return (
            <code style={INLINE_CODE} {...props}>
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
        a: ({ children, href }) => {
          // Ссылка на файл проекта ([гайд](docs/…)) открывает его на просмотр, а не наружу
          const filePath = resolveFile(href);
          if (filePath) return <FileLink path={filePath} onOpen={onOpenFile!}>{children}</FileLink>;
          return (
            <a href={href} style={{ color: C.accent, textDecoration: 'underline' }} target="_blank" rel="noopener noreferrer">
              {children}
            </a>
          );
        },
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
      {stripServiceMarkers(text)}
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

// Домены, которые разрешены прокси-контроллером на бэкенде (синхронизировать с AllowedHosts).
// glif-медиа — glifusercontent.com и res.cloudinary.com (их CDN); glif.app/glif.xyz в списке
// нет намеренно: бэкенд их не проксирует (там страницы, а не медиа) — projectUrl из ответа
// glif остаётся обычной ссылкой.
const PROXY_ALLOWED_HOSTS = [
  'fal.media', 'fal.run', 'queue.fal.run', 'cdn.fal.ai',
  'storage.googleapis.com', 'replicate.delivery', 'pbxt.replicate.delivery',
  'glifusercontent.com', 'res.cloudinary.com',
];

// Домены генераторов медиа — их src в markdown не проксируем: медиа уже показаны в MediaBlock.
// res.cloudinary.com здесь нет намеренно: это общий CDN, ссылку на него в markdown показываем.
const MEDIA_HOSTS = ['fal.media', 'fal.run', 'queue.fal.run', 'cdn.fal.ai', 'glifusercontent.com'];

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
