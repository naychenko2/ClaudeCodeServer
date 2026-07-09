import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { Components } from 'react-markdown';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { MermaidDiagram } from './MermaidDiagram';
import { C, FONT, R, SHADOW, Z } from '../lib/design';

// Результат резолва вики-имени (для hover-preview и embed-вставок)
export interface ResolvedNote {
  title: string;
  content: string;   // фрагмент по якорю либо весь текст
}

interface Props {
  content: string;
  // Клик по вики-ссылке [[Заголовок]] — target = «сырой» текст цели (до | и #)
  onWikilink?: (target: string) => void;
  // Нормализованные (lower/trim) заголовки существующих заметок — чтобы отличить
  // «живую» ссылку (accent) от «призрачной» (серый пунктир, цели ещё нет)
  existingTitles?: Set<string>;
  // Резолв заметки по имени (+якорь) — включает hover-preview и embed ![[…]]
  resolveNote?: (name: string, anchor?: string) => Promise<ResolvedNote | null>;
  // Источник текущей заметки — для URL картинок-вложений ![[img.png]]
  embedSource?: string;
  // Глубина вложенности embed (защита от циклов: на глубине ≥1 embed → просто ссылка)
  embedDepth?: number;
}

const mono = FONT.mono;
const serif = FONT.serif;

// [[Заметка]] / [[Заметка|подпись]] → markdown-ссылка со схемой wikilink:
// (urlTransform ниже пропускает эту схему, иначе санитайзер react-markdown её вырежет).
function preprocessWikilinks(md: string): string {
  return md.replace(/\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g,
    (_m, target: string, label?: string) =>
      `[${(label ?? target).trim()}](wikilink:${encodeURIComponent(target.trim())})`);
}

const IMG_EXT = /\.(png|jpe?g|gif|webp|svg|bmp|avif)$/i;

// ![[Заметка]] → embed-вставка (схема noteembed:), ![[img.png]] → картинка-вложение
// (noteatt:). Вызывается ДО preprocessWikilinks — иначе [[…]]-часть съест общий regex.
function preprocessEmbeds(md: string, depth: number): string {
  return md.replace(/!\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g, (_m, target: string, label?: string) => {
    const t = target.trim();
    const text = (label ?? t).trim();
    if (IMG_EXT.test(t)) return `![${text}](noteatt:${encodeURIComponent(t)})`;
    if (depth >= 1) return `[${text}](wikilink:${encodeURIComponent(t)})`;
    return `[${text}](noteembed:${encodeURIComponent(t)})`;
  });
}

// Имя цели для проверки существования: последний сегмент пути, без якоря, lower/trim
function normTarget(target: string): string {
  return target.split('/').pop()!.split('#')[0].trim().toLowerCase();
}

// «Имя#Якорь» → [имя, якорь?]
function splitAnchor(name: string): [string, string | undefined] {
  const i = name.indexOf('#');
  return i < 0 ? [name, undefined] : [name.slice(0, i), name.slice(i + 1)];
}

// Кэш резолва на сессию (hover-preview дёргается часто)
const resolveCache = new Map<string, ResolvedNote | null>();

async function cachedResolve(
  resolveNote: NonNullable<Props['resolveNote']>, name: string,
): Promise<ResolvedNote | null> {
  if (resolveCache.has(name)) return resolveCache.get(name)!;
  const [n, anchor] = splitAnchor(name);
  const r = await resolveNote(n, anchor).catch(() => null);
  resolveCache.set(name, r);
  return r;
}

// Сбрасывается стором заметок при изменениях (см. lib/notes.ts)
export function clearResolveCache(): void {
  resolveCache.clear();
}

// Embed-вставка ![[Заметка]]: рамка-цитата с заголовком-ссылкой и содержимым
function NoteEmbed({ name, resolveNote, onWikilink, embedSource }: {
  name: string;
  resolveNote: NonNullable<Props['resolveNote']>;
  onWikilink?: (target: string) => void;
  embedSource?: string;
}) {
  const [data, setData] = useState<ResolvedNote | null | 'loading'>('loading');
  useEffect(() => {
    let alive = true;
    void cachedResolve(resolveNote, name).then(d => { if (alive) setData(d); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name]);

  if (data === 'loading')
    return <span style={{ color: C.textMuted, fontSize: 12 }}>…</span>;
  if (!data)
    return (
      <a role="link" tabIndex={0} onClick={() => onWikilink?.(name)}
        style={{ color: C.textMuted, borderBottom: `1px dashed ${C.textMuted}`, cursor: 'pointer' }}>
        {name}
      </a>
    );
  return (
    <span style={{
      display: 'block', margin: '10px 0', padding: '10px 14px',
      background: C.bgCard, border: `1px solid ${C.borderLight}`,
      borderLeft: `3px solid ${C.accent}`, borderRadius: `0 ${R.lg}px ${R.lg}px 0`,
    }}>
      <a role="link" tabIndex={0} onClick={() => onWikilink?.(name)}
        style={{ color: C.accent, fontWeight: 600, fontSize: 13, cursor: 'pointer', display: 'block', marginBottom: 6 }}>
        {data.title}
      </a>
      <span style={{ display: 'block' }}>
        <MarkdownViewer
          content={data.content.length > 1500 ? data.content.slice(0, 1500) + '…' : data.content}
          onWikilink={onWikilink} embedDepth={1} embedSource={embedSource}
        />
      </span>
    </span>
  );
}

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

// URL картинки-вложения из vault (JWT в query — <img> не шлёт заголовки)
function attachmentUrl(path: string, source?: string): string {
  const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token') || '';
  const qs = new URLSearchParams({ source: source ?? 'personal', path });
  if (token) qs.set('access_token', token);
  return `/api/notes/attachment?${qs}`;
}

const CUSTOM_SCHEMES = ['wikilink:', 'noteembed:', 'noteatt:'];

interface HoverState { x: number; y: number; data: ResolvedNote }

export function MarkdownViewer({ content, onWikilink, existingTitles, resolveNote, embedSource, embedDepth = 0 }: Props) {
  // Режим заметок: включаем рендер [[wikilinks]]/![[embeds]] и внешние ссылки синим
  // (info), чтобы три класса ссылок различались (живая accent / призрак / внешняя).
  const notesMode = onWikilink != null;
  const source = notesMode ? preprocessWikilinks(preprocessEmbeds(content, embedDepth)) : content;

  // Hover-preview вики-ссылок (только при resolveNote, не на тач-устройствах)
  const [hover, setHover] = useState<HoverState | null>(null);
  const hoverTimer = useRef<number | null>(null);
  const hoverEnabled = resolveNote != null && embedDepth === 0 &&
    typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

  const cancelHover = () => {
    if (hoverTimer.current) { window.clearTimeout(hoverTimer.current); hoverTimer.current = null; }
    setHover(null);
  };
  useEffect(() => cancelHover, []);

  const scheduleHover = (target: string, el: HTMLElement) => {
    if (!hoverEnabled) return;
    if (hoverTimer.current) window.clearTimeout(hoverTimer.current);
    hoverTimer.current = window.setTimeout(() => {
      void cachedResolve(resolveNote!, target).then(data => {
        if (!data) return;
        const rect = el.getBoundingClientRect();
        const x = Math.min(rect.left, window.innerWidth - 396);
        const y = rect.bottom + 6;
        setHover({ x: Math.max(8, x), y, data });
      });
    }, 350);
  };

  const merged: Components = notesMode
    ? {
        ...components,
        a: ({ href, children }) => {
          if (href?.startsWith('noteembed:') && resolveNote) {
            const name = decodeURIComponent(href.slice('noteembed:'.length));
            return <NoteEmbed name={name} resolveNote={resolveNote} onWikilink={onWikilink} embedSource={embedSource} />;
          }
          if (href?.startsWith('wikilink:') || href?.startsWith('noteembed:')) {
            const scheme = href.startsWith('wikilink:') ? 'wikilink:' : 'noteembed:';
            const target = decodeURIComponent(href.slice(scheme.length));
            const live = existingTitles ? existingTitles.has(normTarget(target)) : true;
            return (
              <a
                role="link"
                tabIndex={0}
                onClick={e => { e.preventDefault(); cancelHover(); onWikilink?.(target); }}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onWikilink?.(target); } }}
                onMouseEnter={live ? e => scheduleHover(target, e.currentTarget) : undefined}
                onMouseLeave={cancelHover}
                title={live ? undefined : `Создать заметку «${target}»`}
                style={{
                  color: live ? C.accent : C.textMuted,
                  textDecoration: 'none',
                  cursor: 'pointer',
                  fontWeight: 500,
                  borderBottom: live ? 'none' : `1px dashed ${C.textMuted}`,
                }}
              >{children}</a>
            );
          }
          // Внешняя ссылка в заметке — синим (info), чтобы отличать от вики-ссылок
          return <a href={href} target="_blank" rel="noopener noreferrer" style={{ color: C.info, textDecoration: 'underline' }}>{children}</a>;
        },
        img: ({ src, alt }) => {
          const url = typeof src === 'string' && src.startsWith('noteatt:')
            ? attachmentUrl(decodeURIComponent(src.slice('noteatt:'.length)), embedSource)
            : src;
          return <img src={url} alt={alt ?? ''} style={{ maxWidth: '100%', borderRadius: 8, margin: '8px 0' }} />;
        },
      }
    : components;

  return (
    <div style={{ fontFamily: FONT.sans, fontSize: 14, lineHeight: 1.7, color: C.textHeading, width: '100%' }}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        urlTransform={url => (CUSTOM_SCHEMES.some(s => url.startsWith(s)) ? url : defaultUrlTransform(url))}
        components={merged}
      >{source}</ReactMarkdown>
      {hover && createPortal(
        <div style={{
          position: 'fixed', left: hover.x, top: hover.y, zIndex: Z.modal,
          width: 380, maxHeight: 280, overflow: 'hidden', pointerEvents: 'none',
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: '12px 14px',
        }}>
          <div style={{ fontFamily: serif, fontSize: 15, fontWeight: 700, color: C.textHeading, marginBottom: 6 }}>
            {hover.data.title}
          </div>
          <MarkdownViewer
            content={hover.data.content.length > 900 ? hover.data.content.slice(0, 900) + '…' : hover.data.content}
            onWikilink={() => {}} embedDepth={1} embedSource={embedSource}
          />
        </div>,
        document.body,
      )}
    </div>
  );
}
