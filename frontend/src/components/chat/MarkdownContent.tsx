import { useState, useEffect, useContext, useMemo, memo, type CSSProperties, type ReactNode } from 'react';
import ReactMarkdown, { defaultUrlTransform, type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { FileText } from 'lucide-react';
import { MermaidDiagram } from '../MermaidDiagram';
import { CodeBlockFrame } from './CodeCopyButton';
import { ReaderLinkWrap } from './ReaderLinkButton';
import { api } from '../../lib/api';
import { C, FONT, SP } from '../../lib/design';
import { readStoredToken } from '../../lib/offline';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { relPathTree } from '../../lib/paths';
import { useProjectFileIndex, lookupProjectFile, FILE_LIKE_MENTION } from '../../lib/projectFileIndex';
import { withImageLimit } from '../../lib/concurrency';
import { useInView } from '../../lib/useInView';
import { ChatProjectContext, ChatTreePathContext, ChatOpenFileContext } from './contexts';

// Абсолютный путь (Windows-диск или POSIX) — для A1: упоминание вне корня проекта тоже
// становится ссылкой (открытие само покажет ошибку, если файла на самом деле нет).
const ABS_PATH_RE = /^[A-Za-z]:[\\/]|^\//;
// Windows-диск отдельно: POSIX-ветка (голый `/...`) требует ещё и «похоже на файл»
// (FILE_LIKE_MENTION) — иначе роуты вида /api/mcp/calls, /hubs/session становятся
// ссылками на несуществующий файл и ведут в 404 хост-вьювера.
const WINDOWS_ABS_PATH_RE = /^[A-Za-z]:[\\/]/;

// A1: абсолютный путь становится ссылкой без проверки существования в индексе — но
// только если это Windows-путь (диск) либо POSIX-путь, похожий на файл (есть расширение).
// Голый POSIX-роут без расширения (/api/mcp/calls, /hubs/session) — обычный текст.
export function isFileLikeAbsPath(p: string): boolean {
  if (!ABS_PATH_RE.test(p)) return false;
  return WINDOWS_ABS_PATH_RE.test(p) || FILE_LIKE_MENTION.test(p);
}

// Кэш уже загруженных картинок проекта: «projectId|путь» → data-URL. Модульный, потому
// что переживает перемонтирование компонента: лента перерисовывается на каждом обновлении
// чата, и без кэша картинка каждый раз уходила бы в «Загрузка…» и заново тянула base64.
// Лимит — простой FIFO: base64 крупных скриншотов держать в памяти пачками ни к чему.
const IMAGE_CACHE_LIMIT = 24;
const _imageCache = new Map<string, string>();

function cacheImage(key: string, dataUrl: string): void {
  if (_imageCache.size >= IMAGE_CACHE_LIMIT) {
    const oldest = _imageCache.keys().next();
    if (!oldest.done) _imageCache.delete(oldest.value);
  }
  _imageCache.set(key, dataUrl);
}

// Дедуп идущих загрузок: пока base64 картинки в полёте, повторные рендеры (лента
// перерисовывается на каждом обновлении объекта проекта) переиспользуют один и тот же
// промис, а не плодят дублирующие запросы. Без этого на медленном канале десятки
// перезапросов одной картинки забивали пул соединений браузера (HTTP/1.1 ~6 на origin),
// из-за чего health-ping не пролезал и приложение мигало online/offline.
const _imageInflight = new Map<string, Promise<string | null>>();

// Загрузка картинки проекта как data-URL с дедупом и модульным кэшем.
// null — контент не картинка либо сбой. Реальный fetch к одному cacheKey — ровно один.
function loadProjectImage(projectId: string, rel: string, cacheKey: string): Promise<string | null> {
  const hit = _imageCache.get(cacheKey);
  if (hit) return Promise.resolve(hit);
  const flying = _imageInflight.get(cacheKey);
  if (flying) return flying;
  const p = withImageLimit(() => api.files.getContent(projectId, rel))
    .then(r => {
      if (r.isImage && r.base64) {
        const dataUrl = `data:${r.mimeType ?? 'image/png'};base64,${r.base64}`;
        cacheImage(cacheKey, dataUrl);
        return dataUrl;
      }
      return null;
    })
    .catch(() => null)
    .finally(() => { _imageInflight.delete(cacheKey); });
  _imageInflight.set(cacheKey, p);
  return p;
}

// Путь картинки относительно корня проекта (Claude мог дать абсолютный путь внутри проекта)
function toProjectRelative(src: string, rootPath: string): string {
  let rel = src.replace(/\\/g, '/');
  const root = rootPath.replace(/\\/g, '/');
  if (rel.toLowerCase().startsWith(root.toLowerCase())) rel = rel.slice(root.length);
  return rel.replace(/^\/+/, '');
}

// Картинка из markdown: внешние URL (http/https/data) — напрямую; локальный путь файла
// проекта (например, картинка, скачанная Claude) — грузим через API и показываем как data-URL.
function ChatImage({ src, alt }: { src?: string; alt?: string }) {
  const project = useContext(ChatProjectContext);
  // /api/proxy?... — уже проксированный URL (от urlTransform)
  const isRemote = !!src && /^(https?:|data:|\/api\/proxy)/i.test(src);
  // Примитивы (id/rel/ключ), а не объект project — в зависимостях эффекта: смена ССЫЛКИ
  // projectCtx при неизменных id/rootPath не должна дёргать перезагрузку картинки.
  const projectId = !isRemote ? project?.id ?? null : null;
  const rel = src && !isRemote && project ? toProjectRelative(src, project.rootPath) : null;
  const cacheKey = projectId && rel ? `${projectId}|${rel}` : null;
  // Кэш читаем на рендере, а не через setState в эффекте: после перемонтирования картинка
  // должна стоять сразу, без промежуточного «Загрузка…» — это и есть мигание.
  const cached = cacheKey ? _imageCache.get(cacheKey) ?? null : null;
  const [resolved, setResolved] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  // Не фетчим, пока картинка вне viewport: лента не виртуализирована, и без этого все картинки
  // разных сообщений фетчатся разом, забивая пул браузера. Remote (http/data/proxy) тут не нужен —
  // они идут через нативный <img loading="lazy">, а эффект для них делает ранний return по !projectId.
  const [wrapRef, inView] = useInView({ rootMargin: '300px' });

  useEffect(() => {
    if (!projectId || !rel || !cacheKey || cached || !inView) return;
    let cancelled = false;
    // Через loadProjectImage: параллельные рендеры дедуплицируются в один fetch, а withImageLimit
    // (внутри) ограничивает общую параллельность — слоты под health-ping остаются свободными.
    loadProjectImage(projectId, rel, cacheKey)
      .then(dataUrl => {
        if (cancelled) return;
        if (dataUrl) setResolved(dataUrl);
        else setFailed(true);
      });
    return () => { cancelled = true; };
  }, [projectId, rel, cacheKey, cached, inView]);

  const finalSrc = isRemote ? src : cached ?? resolved;

  if (failed) return <span style={{ fontSize: 13, color: C.textMuted }}>🖼 {alt || src}</span>;
  if (!finalSrc) return <span style={{ fontSize: 13, color: C.textMuted }}>Загрузка изображения…</span>;

  return (
    <a ref={wrapRef} href={finalSrc} target="_blank" rel="noopener noreferrer" style={{ display: 'block', margin: '6px 0' }}>
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
export function FileLink({ path, onOpen, mono, children }: {
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
// Парные маркеры ищем ПОЗИЦИОННО: [открывающий тег, закрывающий тег], обе границы — вне
// код-фрагментов, и вырезаем диапазон [openStart, closeEnd) из исходного текста целиком,
// вместе с вложенным кодом: fenced-блок внутри <team:work> не разрывает пару (рез по
// сегментам показывал код-фенс и хвост постановки как обычный текст). Та же семантика,
// что у бэкенда (SessionManager.FindPairedMarkerOutsideCode + StripTeamProtocolMarkers):
// зачистка и разбор не расходятся. Закрытие по имени (</team:work>, </escalate:check>)
// терпим — как и парсер бэкенда.
const PAIRED_MARKERS: ReadonlyArray<readonly [RegExp, RegExp]> = [
  [/<escalate:[a-z]+>/i, /<\/escalate(?::\w+)?>/i],
  [/<team:work>/i, /<\/team(?::work)?>/i],
  [/<promise>/i, /<\/promise>/i],
];
// Незакрытый открывающий тег в хвосте — ход ещё стримится, закрывающий тег придёт позже;
// без этого пользователь успевает прочитать «<team:work>Создать …» целиком. Хвост прячем
// до конца текста (включая код-блок внутри незакрытой постановки), а не до фенса.
const OPEN_TAGS = /<(?:promise|team:work|escalate:[a-z]+)>/gi;
// Осиротевший закрывающий тег без пары (повторное закрытие, цитата закрытия отдельно от
// открытия) — служебный синтаксис протокола, человеку не место ни в какой форме
// (симметрично OrphanCloserRegex бэкенда)
const ORPHAN_CLOSERS = /<\/team(?::work)?>|<\/escalate(?::\w+)?>|<\/promise>/gi;
// Код-фрагменты (```блок``` и `инлайн`) — зона, где маркер остаётся как есть: там его
// цитируют, объясняя протокол. Так же его игнорируют и детекторы бэкенда
// (SessionManager.ParseWorkMarker / ParseEscalationMarker / проверка promise).
// Незакрытый фенс тянем до конца текста — стрим ещё не дописал закрывающие ```.
const CODE_SPANS = /```[\s\S]*?(?:```|$)|`[^`\n]*`/g;

// matchAll требует флаг g; теги выше объявлены без него, чтобы не таскать lastIndex
function global(re: RegExp): RegExp {
  return new RegExp(re.source, re.flags + 'g');
}

function codeRanges(text: string): [number, number][] {
  return [...text.matchAll(CODE_SPANS)].map(m => [m.index, m.index + m[0].length] as [number, number]);
}

function outsideCode(ranges: [number, number][], start: number, end: number): boolean {
  return !ranges.some(([s, e]) => start < e && end > s);
}

// Первый парный маркер, у которого ОБА тега вне код-фрагментов: границы [openStart, closeEnd).
function findPairedOutsideCode(text: string, open: RegExp, close: RegExp): [number, number] | null {
  const ranges = codeRanges(text);
  for (const om of text.matchAll(global(open))) {
    const openStart = om.index;
    const openEnd = openStart + om[0].length;
    if (!outsideCode(ranges, openStart, openEnd)) continue;
    for (const cm of text.matchAll(global(close))) {
      if (cm.index < openEnd) continue;
      if (outsideCode(ranges, cm.index, cm.index + cm[0].length))
        return [openStart, cm.index + cm[0].length];
    }
  }
  return null;
}

function stripPairedMarkers(text: string): string {
  for (const [open, close] of PAIRED_MARKERS) {
    for (;;) {
      const found = findPairedOutsideCode(text, open, close);
      if (!found) break;
      text = text.slice(0, found[0]) + text.slice(found[1]);
    }
  }
  return text;
}

function stripOrphanClosers(text: string): string {
  const ranges = codeRanges(text);
  let out = '';
  let last = 0;
  for (const m of text.matchAll(ORPHAN_CLOSERS)) {
    if (!outsideCode(ranges, m.index, m.index + m[0].length)) continue;
    out += text.slice(last, m.index);
    last = m.index + m[0].length;
  }
  return out + text.slice(last);
}

// Пары уже вырезаны выше: оставшийся открывающий тег вне кода — маркер ещё не закрылся.
// Прячем с него и до конца текста: тело маркера — постановка, не для глаз.
function stripUnresolvedOpenTail(text: string): string {
  const ranges = codeRanges(text);
  for (const m of text.matchAll(OPEN_TAGS)) {
    if (outsideCode(ranges, m.index, m.index + m[0].length))
      return text.slice(0, m.index);
  }
  return text;
}

export function stripServiceMarkers(text: string): string {
  return stripUnresolvedOpenTail(stripOrphanClosers(stripPairedMarkers(text)))
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

const REMARK_PLUGINS = [remarkGfm];

// Рендер текста Claude с поддержкой Markdown.
// memo + мемоизированная карта components: react-markdown использует её функции как ТИПЫ
// элементов, поэтому новая ссылка на каждый рендер размонтировала бы поддерево целиком.
// Для картинок это выливалось в мигание — ChatImage терял загруженный data-URL и тянул
// файл заново (лента перерисовывается на каждом обновлении объекта проекта).
export const MarkdownContent = memo(function MarkdownContent({ text }: { text: string }) {
  const project = useContext(ChatProjectContext);
  const treePath = useContext(ChatTreePathContext);
  const onOpenFile = useContext(ChatOpenFileContext);
  const fileIndex = useProjectFileIndex(project?.id ?? null);

  const urlTransform = useMemo(() => (url: string, key: string) => {
    // Медиа-домены (fal/glif) src — блокируем: медиа уже показаны в MediaBlock из tool_result
    if (key === 'src' && matchesHosts(url, MEDIA_HOSTS)) return null;
    // Абсолютный путь (в т.ч. вне корня проекта, A1) — оставляем как есть: defaultUrlTransform
    // режет его, приняв «C:» за неизвестный протокол; существование не проверяем —
    // resolveFileMention сам решит, стал ли он ссылкой на файл
    if (project && ABS_PATH_RE.test(url)) return url;
    // остальные внешние URL (src и href) — через прокси если домен разрешён
    return isProxiable(url) ? proxyUrl(url) : defaultUrlTransform(url);
  }, [project]);

  const components = useMemo<Components>(() => {
    // Упоминание пути → файл проекта: точный/суффиксный путь из индекса (lookupProjectFile,
    // включая B1) либо, если индекс молчит, абсолютный путь — тоже ссылка без проверки
    // существования (A1; открытие само покажет ошибку, если файла на самом деле нет).
    // label — короткий текст для абсолютного пути внутри активного дерева/корня чата.
    // Вне проекта и без обработчика открытия фича молчит — текст как раньше.
    const resolveFileMention = (raw?: string | null): { path: string; label?: string } | null => {
      if (!project || !onOpenFile || !raw) return null;
      const known = lookupProjectFile(fileIndex, raw, project.rootPath);
      if (known) return { path: known };
      let p = raw.trim().split(/[#?]/)[0];
      if (!p) return null;
      try { p = decodeURIComponent(p); } catch { /* не URL-экранирован — как есть */ }
      if (!isFileLikeAbsPath(p)) return null;
      const label = relPathTree(p, project.rootPath, treePath);
      return { path: p, label: label !== p ? label : undefined };
    };
    return {
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
              <CodeBlockFrame text={text}>
                <SyntaxHighlighter
                  language={language}
                  style={oneDark}
                  customStyle={{ borderRadius: 8, fontSize: 12.5, margin: '6px 0', padding: '10px 14px', fontFamily: FONT.mono, overflowX: 'auto' }}
                >
                  {text}
                </SyntaxHighlighter>
              </CodeBlockFrame>
            );
          }
          if (text.includes('\n')) {
            // Код без указания языка — на светлой панели вывода (лёгкий тёплый фон вместо тёмного)
            return (
              <CodeBlockFrame text={text}>
                <pre style={{ background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: '6px 0', overflowX: 'auto' }}>
                  <code style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, lineHeight: 1.5 }} {...props}>{text}</code>
                </pre>
              </CodeBlockFrame>
            );
          }
          // Путь к файлу проекта в бэктиках — кликабельная ссылка на просмотр
          const mention = resolveFileMention(text);
          if (mention) return <FileLink path={mention.path} onOpen={onOpenFile!} mono>{mention.label ?? children}</FileLink>;
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
          // Ссылка на файл проекта ([гайд](docs/…)) открывает его на просмотр, а не наружу.
          // Текст ссылки — как написал автор markdown, label из resolveFileMention сюда не идёт.
          const mention = resolveFileMention(href);
          if (mention) return <FileLink path={mention.path} onOpen={onOpenFile!}>{children}</FileLink>;
          // Внешняя ссылка: сам <a>, перехват клика в панель «Чтение» и кнопка-компаньон
          // живут в ReaderLinkWrap (ADR-006 §2/§5)
          return <ReaderLinkWrap href={href}>{children}</ReaderLinkWrap>;
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
    };
    // treePath — в зависимостях: от него зависит короткий label абсолютного пути
  }, [project, onOpenFile, fileIndex, treePath]);

  return (
    <ReactMarkdown remarkPlugins={REMARK_PLUGINS} urlTransform={urlTransform} components={components}>
      {stripServiceMarkers(text)}
    </ReactMarkdown>
  );
});

// Оборачивает внешний URL через backend-прокси (/api/proxy) — поддерживает любой тип контента
export function proxyUrl(url: string): string {
  const token = readStoredToken();
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
