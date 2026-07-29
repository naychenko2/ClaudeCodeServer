// Панель «Доки» рельсы проекта: документация (README.md + docs/**) как связный корпус —
// дерево документов, превью с оглавлением, поиск, переходы по ссылкам и обратные ссылки.
//
// Разграничение с соседями: «Файлы» — дерево репозитория для работы с кодом, «Заметки» —
// личный vault вне репы, «Знания» — семантический поиск через Dify. Здесь — структура и
// связность репозиторной документации.
//
// Колонка узкая, поэтому превью тут для чтения «по месту», а крупное чтение — кнопкой
// «развернуть» в центральной области (тот же FileViewer, что и для остальных файлов).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { BookOpen, ChevronDown, ChevronRight, CornerUpRight, Link2, List, Maximize2, MessageSquarePlus, Search, X } from 'lucide-react';
import type { Project, DocEntry, DocDetail, DocSearchHit } from '../../types';
import { api } from '../../lib/api';
import { onFilesChanged } from '../../lib/signalr';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton, TextField } from '../../components/ui';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { useHeadings, scrollToHeading } from '../../hooks/useHeadings';
import { resolveDocLink, sliceSection, slugify } from '../../lib/docsLinks';

interface Props {
  project: Project;
  // Открыть файл в центральной области: «развернуть» документ и переходы на код
  onOpenFile: (path: string) => void;
  // Прикрепить путь к сообщению чата (документ целиком — вложением, не текстом)
  onAttachToChat: (path: string) => void;
}

// Пути области, по которым решаем, надо ли перечитывать индекс после правок на диске
function isDocPath(path: string): boolean {
  const p = path.replace(/\\/g, '/');
  return p === 'README.md' || p.startsWith('docs/');
}

// Цитата раздела в композер: тем же механизмом, что «Про файл …» в FileViewer —
// текст ложится в ПУСТОЕ поле композера, набранный черновик важнее
function prefillComposer(text: string): void {
  sessionStorage.setItem('cc_pending_chat_prompt', text);
  window.dispatchEvent(new Event('cc-compose-prefill'));
}

export function DocsPanel({ project, onOpenFile, onAttachToChat }: Props) {
  const [index, setIndex] = useState<DocEntry[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [doc, setDoc] = useState<DocDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<DocSearchHit[] | null>(null);
  const [treeOpen, setTreeOpen] = useState(true);
  const [backlinksOpen, setBacklinksOpen] = useState(false);
  const [tocOpen, setTocOpen] = useState(false);
  // Якорь, к которому нужно проскроллить после перехода по ссылке или из поиска.
  // Хранится ВМЕСТЕ с путём документа: между сменой документа и пересбором оглавления
  // есть кадр, где doc уже новый, а headings ещё от прежнего — без привязки к пути
  // якорь искался в чужом оглавлении, не находился и терялся.
  const [pendingAnchor, setPendingAnchor] = useState<{ path: string; anchor: string } | null>(null);

  const contentRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(contentRef, doc?.content);

  // Пути документов нижним регистром — по ним отличаем переход внутри панели
  // от открытия файла кода в центре
  const knownDocs = useMemo(
    () => new Set((index ?? []).map(d => d.path.toLowerCase())),
    [index]);

  const loadIndex = useCallback(() => {
    api.docs.index(project.id)
      .then(list => { setIndex(list); setError(null); })
      .catch(() => setError('Не удалось загрузить документацию'));
  }, [project.id]);

  useEffect(() => { loadIndex(); }, [loadIndex]);

  // Основной сценарий правок — Claude меняет docs/ прямо в чате; без подписки корпус
  // (дерево, превью, обратные ссылки) устаревал бы до перезагрузки страницы
  useEffect(() => onFilesChanged(({ projectId, paths }) => {
    if (projectId !== project.id || !paths.some(isDocPath)) return;
    // Достаточно перечитать индекс: открытый документ висит на нём зависимостью
    // эффекта ниже и перезагрузится следом
    loadIndex();
  }), [project.id, loadIndex]);

  // Первый показ — README, иначе первый документ списка
  useEffect(() => {
    if (!index || index.length === 0 || selected) return;
    setSelected(index.find(d => d.path === 'README.md')?.path ?? index[0].path);
  }, [index, selected]);

  // Содержимое выбранного документа
  useEffect(() => {
    if (!selected) { setDoc(null); return; }
    let alive = true;
    api.docs.doc(project.id, selected)
      .then(d => { if (alive) { setDoc(d); setError(null); } })
      .catch(() => { if (alive) setError('Документ не открывается'); });
    return () => { alive = false; };
  }, [project.id, selected, index]);

  // Скролл к разделу после того, как документ отрисован и оглавление собрано.
  // Пока цель не найдена — ждём следующего прохода (оглавление ещё пересобирается);
  // «висящий» якорь безопасен: следующий переход перезапишет его своим.
  useEffect(() => {
    if (!pendingAnchor || !doc || doc.path !== pendingAnchor.path) return;
    const target = headings.find(h => slugify(h.text) === pendingAnchor.anchor);
    if (!target) return;
    scrollToHeading(target);
    setPendingAnchor(null);
  }, [pendingAnchor, doc, headings]);

  // Поиск с задержкой: панель узкая, дёргать сервер на каждый символ незачем
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { setHits(null); return; }
    const timer = window.setTimeout(() => {
      api.docs.search(project.id, q).then(setHits).catch(() => setHits([]));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [project.id, query]);

  const openDoc = (path: string, anchor: string | null = null) => {
    setSelected(path);
    setPendingAnchor(anchor ? { path, anchor } : null);
    setHits(null);
    setQuery('');
  };

  // Клик по ссылке внутри превью: документ области — переход в панели,
  // файл проекта — открытие в центре, внешняя — ушла в новую вкладку без нас
  const handleDocLink = useCallback((href: string) => {
    if (!doc) return;
    const link = resolveDocLink(doc.path, href, knownDocs);
    if (!link) return;
    if (link.kind === 'doc') openDoc(link.target, link.anchor);
    else if (link.kind === 'repo') onOpenFile(link.target);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc, knownDocs, onOpenFile]);

  const quoteSection = (slug: string, title: string) => {
    if (!doc) return;
    const section = sliceSection(doc.content, slug);
    if (!section) return;
    prefillComposer(`Вопрос по разделу «${title}» документа ${doc.path}:\n\n${section}\n\n`);
    setTocOpen(false);
  };

  if (error && !index)
    return <div style={emptyStyle}>{error}</div>;

  if (index && index.length === 0)
    return (
      <div style={emptyStyle}>
        <BookOpen size={20} strokeWidth={ICON_STROKE} style={{ opacity: 0.5, marginBottom: SP.sm }} />
        <div>В проекте нет README.md и папки docs/</div>
      </div>
    );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Поиск по документации */}
      <div style={{ flexShrink: 0, padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.border}` }}>
        <div style={{ position: 'relative' }}>
          <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ position: 'absolute', left: SP.sm, top: '50%', transform: 'translateY(-50%)', color: C.textMuted, pointerEvents: 'none' }} />
          <TextField value={query} onChange={setQuery} placeholder="Поиск по докам"
            style={{ height: 30, fontSize: FS.sm, paddingLeft: 28 }} />
        </div>
      </div>

      {/* Результаты поиска замещают дерево, пока запрос активен */}
      {hits !== null ? (
        <div style={{ flex: 1, overflowY: 'auto', padding: `${SP.xs}px 0` }}>
          {hits.length === 0 && <div style={emptyStyle}>Ничего не найдено</div>}
          {hits.map((h, i) => (
            <button key={`${h.path}-${i}`} onClick={() => openDoc(h.path, h.slug)} style={hitStyle}>
              <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{h.title}</div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>{h.path}</div>
              <div style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs, lineHeight: 1.5 }}>{h.snippet}</div>
            </button>
          ))}
        </div>
      ) : (
        <>
          {/* Дерево документов */}
          <div style={{ flexShrink: 0, maxHeight: '38%', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <button onClick={() => setTreeOpen(v => !v)} style={sectionHeadStyle}>
              {treeOpen
                ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              Документы
              <span style={{ marginLeft: 'auto', color: C.textMuted, fontWeight: 400 }}>{index?.length ?? ''}</span>
            </button>
            {treeOpen && (
              <div style={{ overflowY: 'auto', padding: `0 ${SP.xs}px ${SP.xs}px` }}>
                {(index ?? []).map(d => (
                  <button key={d.path} onClick={() => openDoc(d.path)}
                    title={d.path}
                    style={{
                      ...rowStyle,
                      paddingLeft: SP.sm + depthOf(d.path) * SP.md,
                      background: d.path === selected ? C.bgSelected : 'transparent',
                      color: d.path === selected ? C.textHeading : C.textSecondary,
                      fontWeight: d.path === selected ? 600 : 400,
                    }}>
                    <BookOpen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.6 }} />
                    <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{d.title}</span>
                  </button>
                ))}
              </div>
            )}
          </div>

          {/* Превью документа */}
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, borderTop: `1px solid ${C.border}` }}>
            {doc && (
              <div style={{
                flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: SP.xs,
                padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.border}`,
              }}>
                <span style={{
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{doc.title}</span>
                <div style={{ flex: 1 }} />
                {headings.length > 0 && (
                  <IconButton title="Оглавление" onClick={() => setTocOpen(v => !v)} active={tocOpen} size="sm">
                    <List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                )}
                <IconButton title="Документ в чат — вложением" onClick={() => onAttachToChat(doc.path)} size="sm">
                  <MessageSquarePlus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
                <IconButton title="Развернуть в центре" onClick={() => onOpenFile(doc.path)} size="sm">
                  <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>

                {/* Оглавление: у каждого пункта — переход и отправка раздела в чат цитатой */}
                {tocOpen && headings.length > 0 && (
                  <div style={tocPopoverStyle}>
                    <div style={{ display: 'flex', alignItems: 'center', padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.border}` }}>
                      <span style={{ fontSize: FS.xs, fontWeight: 700, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.03em' }}>
                        Оглавление
                      </span>
                      <div style={{ flex: 1 }} />
                      <IconButton title="Закрыть" onClick={() => setTocOpen(false)} size="sm">
                        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                    </div>
                    {headings.map((h, i) => (
                      <div key={i} style={{ display: 'flex', alignItems: 'center' }}>
                        <button
                          onClick={() => { scrollToHeading(h); setTocOpen(false); }}
                          style={{ ...rowStyle, flex: 1, paddingLeft: SP.sm + (h.level - 1) * SP.md, color: C.textSecondary }}>
                          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{h.text}</span>
                        </button>
                        <IconButton title="Раздел в чат — цитатой" size="sm"
                          onClick={() => quoteSection(slugify(h.text), h.text)}>
                          <CornerUpRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                        </IconButton>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}

            <div ref={contentRef} style={{ flex: 1, overflowY: 'auto', padding: `${SP.md}px ${SP.md}px ${SP.xl}px` }}>
              {error && <div style={emptyStyle}>{error}</div>}
              {doc && <MarkdownViewer content={doc.content} onDocLink={handleDocLink} />}
            </div>

            {/* Обратные ссылки: кто в документации ведёт на этот документ */}
            {doc && doc.backlinks.length > 0 && (
              <div style={{ flexShrink: 0, borderTop: `1px solid ${C.border}` }}>
                <button onClick={() => setBacklinksOpen(v => !v)} style={sectionHeadStyle}>
                  {backlinksOpen
                    ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  Ссылаются сюда
                  <span style={{ marginLeft: 'auto', color: C.textMuted, fontWeight: 400 }}>{doc.backlinks.length}</span>
                </button>
                {backlinksOpen && (
                  <div style={{ maxHeight: 140, overflowY: 'auto', padding: `0 ${SP.xs}px ${SP.xs}px` }}>
                    {doc.backlinks.map((b, i) => (
                      <button key={`${b.path}-${i}`} onClick={() => openDoc(b.path, b.anchor)}
                        title={b.path} style={{ ...rowStyle, color: C.textSecondary }}>
                        <Link2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.6 }} />
                        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{b.title}</span>
                      </button>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// Уровень вложенности документа в дереве: docs/adr/0001.md — второй уровень
function depthOf(path: string): number {
  return Math.max(0, path.split('/').length - 1);
}

const emptyStyle = {
  padding: `${SP.xl}px ${SP.md}px`, textAlign: 'center' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted,
};

const sectionHeadStyle = {
  display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
  padding: `${SP.sm}px ${SP.md}px`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
  textTransform: 'uppercase' as const, letterSpacing: '0.03em',
};

const rowStyle = {
  display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
  padding: `${SP.xs}px ${SP.sm}px`, border: 'none', background: 'transparent',
  borderRadius: R.md, cursor: 'pointer', textAlign: 'left' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, minWidth: 0,
};

const hitStyle = {
  display: 'block', width: '100%', textAlign: 'left' as const,
  padding: `${SP.sm}px ${SP.md}px`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans,
};

const tocPopoverStyle = {
  position: 'absolute' as const, top: '100%', right: SP.sm, zIndex: 5,
  width: 260, maxHeight: 320, overflowY: 'auto' as const,
  background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.lg,
  boxShadow: SHADOW.dropdown,
};
