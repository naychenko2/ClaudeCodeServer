// Панель «Документы» рельсы проекта: документация (README.md + docs/**) как связный корпус —
// дерево документов, превью с оглавлением, поиск, переходы по ссылкам и обратные ссылки.
//
// Разграничение с соседями: «Файлы» — дерево репозитория для работы с кодом, «Заметки» —
// личный vault вне репы, «Знания» — семантический поиск через Dify. Здесь — структура и
// связность репозиторной документации.
//
// Колонка узкая, поэтому превью тут для чтения «по месту», а крупное чтение — кнопкой
// «развернуть» в центральной области (тот же FileViewer, что и для остальных файлов).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, CornerUpRight, Link2, List, Maximize2, MessageSquarePlus, PanelBottom, ScrollText, Search, X } from 'lucide-react';
import type { Project, DocEntry, DocDetail, DocSearchHit } from '../../types';
import { api } from '../../lib/api';
import { onFilesChanged } from '../../lib/signalr';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton, TextField } from '../../components/ui';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { ListDateDivider } from '../../components/ListDateDivider';
import { useHeadings, scrollToHeading } from '../../hooks/useHeadings';
import { resolveDocLink, sliceSection, slugify } from '../../lib/docsLinks';

interface Props {
  project: Project;
  // Открыть файл в центральной области: «развернуть» документ и переходы на код
  onOpenFile: (path: string) => void;
  // Прикрепить путь к сообщению чата (документ целиком — вложением, не текстом)
  onAttachToChat: (path: string) => void;
}

// Высота зоны дерева документов: тянется хендлом, переживает перезагрузку.
// Приём тот же, что у зоны скоупов в «Изменениях» (GitChangesRail) — одинаковое
// поведение ресайза в панелях рельсы.
// Высота строки списка: список длинный (десятки документов), поэтому плотный
const ROW_H = 22;

// Порог, в пределах которого второй клик считается двойным (и отменяет одиночный)
const DOUBLE_CLICK_MS = 220;

// Тумблер нижней зоны. По умолчанию выключена: панель открывают ради списка, а превью —
// осознанный режим. Решение пользователя, поэтому переживает перезагрузку
const PREVIEW_KEY = 'cc_docs_preview';

const TREE_H_KEY = 'cc_docs_tree_h';
const TREE_H_DEFAULT = 220;
const TREE_H_MIN = 80;
const TREE_H_MAX = 700;

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
  const [previewEnabled, setPreviewEnabled] = useState<boolean>(() => {
    try { return localStorage.getItem(PREVIEW_KEY) === '1'; } catch { return false; }
  });
  const [treeH, setTreeH] = useState<number>(() => {
    try {
      const n = Number(localStorage.getItem(TREE_H_KEY));
      return Number.isFinite(n) && n >= TREE_H_MIN ? n : TREE_H_DEFAULT;
    } catch { return TREE_H_DEFAULT; }
  });
  const [backlinksOpen, setBacklinksOpen] = useState(false);
  const [tocOpen, setTocOpen] = useState(false);
  // Якорь, к которому нужно проскроллить после перехода по ссылке или из поиска.
  // Хранится ВМЕСТЕ с путём документа: между сменой документа и пересбором оглавления
  // есть кадр, где doc уже новый, а headings ещё от прежнего — без привязки к пути
  // якорь искался в чужом оглавлении, не находился и терялся.
  // В ref, а не в состоянии: значение нужно эффекту скролла, а не рендеру.
  const pendingAnchorRef = useRef<{ path: string; anchor: string } | null>(null);
  // Поиск активен от двух символов — результаты замещают список, пока запрос набран
  const searching = query.trim().length >= 2;

  const contentRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(contentRef, doc?.content);

  // Пути документов нижним регистром — по ним отличаем переход внутри панели
  // от открытия файла кода в центре
  const knownDocs = useMemo(
    () => new Set((index ?? []).map(d => d.path.toLowerCase())),
    [index]);

  // Документы по папкам: README и прочий корень — в безымянной группе сверху,
  // дальше подписанные группы («docs», «docs/adr», …) в алфавитном порядке
  const groups = useMemo<[string, DocEntry[]][]>(() => {
    const byFolder = new Map<string, DocEntry[]>();
    for (const d of index ?? []) {
      const slash = d.path.lastIndexOf('/');
      const folder = slash < 0 ? '' : d.path.slice(0, slash);
      const list = byFolder.get(folder);
      if (list) list.push(d); else byFolder.set(folder, [d]);
    }
    return [...byFolder.entries()].sort(([a], [b]) =>
      a === '' ? -1 : b === '' ? 1 : a.localeCompare(b));
  }, [index]);


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

  // Документ сам не открывается: панель начинается со списка на всю высоту, превью
  // появляется по клику и закрывается крестиком — так список виден целиком, пока он и нужен

  // Содержимое выбранного документа. Сброс doc делает closeDoc — здесь только загрузка,
  // чтобы не дёргать setState синхронно в эффекте
  useEffect(() => {
    if (!selected) return;
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
    const pending = pendingAnchorRef.current;
    if (!pending || !doc || doc.path !== pending.path) return;
    const target = headings.find(h => slugify(h.text) === pending.anchor);
    if (!target) return;
    scrollToHeading(target);
    pendingAnchorRef.current = null;
  }, [doc, headings]);

  // Поиск с задержкой: панель узкая, дёргать сервер на каждый символ незачем
  useEffect(() => {
    if (!searching) return;
    const timer = window.setTimeout(() => {
      api.docs.search(project.id, query.trim()).then(setHits).catch(() => setHits([]));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [project.id, query, searching]);

  const openDoc = (path: string, anchor: string | null = null) => {
    setSelected(path);
    pendingAnchorRef.current = anchor ? { path, anchor } : null;
    setQuery('');   // выход из поиска: список возвращается на место результатов
  };

  // Клик по строке списка откладывается на порог двойного: иначе двойной клик успевал
  // открыть превью до того, как документ уходил в центр, и панель дёргалась зря
  const clickTimer = useRef<number | null>(null);
  useEffect(() => () => { if (clickTimer.current) window.clearTimeout(clickTimer.current); }, []);

  const handleRowClick = (path: string) => {
    // Выделение — сразу: откладывается загрузка документа, а не отклик на клик,
    // иначе строка подсвечивалась через порог двойного клика и это выглядело поломкой
    setSelected(path);
    if (clickTimer.current) window.clearTimeout(clickTimer.current);
    clickTimer.current = window.setTimeout(() => {
      clickTimer.current = null;
      // Без нижней зоны показывать документ негде — открываем сразу в центре
      if (previewEnabled) openDoc(path);
      else onOpenFile(path);
    }, DOUBLE_CLICK_MS);
  };

  const handleRowDoubleClick = (path: string) => {
    if (clickTimer.current) { window.clearTimeout(clickTimer.current); clickTimer.current = null; }
    onOpenFile(path);
  };

  // Клик по ссылке внутри превью: документ области — переход в панели,
  // файл проекта — открытие в центре, внешняя — ушла в новую вкладку без нас
  const handleDocLink = useCallback((href: string) => {
    if (!doc) return;
    const link = resolveDocLink(doc.path, href, knownDocs);
    if (!link) return;
    if (link.kind === 'doc') openDoc(link.target, link.anchor);
    else if (link.kind === 'repo') onOpenFile(link.target);
  }, [doc, knownDocs, onOpenFile]);

  // Ресайз границы «дерево / превью»: тянем хендл вниз — дерево выше, превью ниже
  const handleTreeResize = (e: React.PointerEvent) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = treeH;
    let latest = startH;
    const onMove = (ev: PointerEvent) => {
      latest = Math.max(TREE_H_MIN, Math.min(TREE_H_MAX, startH + (ev.clientY - startY)));
      setTreeH(latest);
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try { localStorage.setItem(TREE_H_KEY, String(Math.round(latest))); } catch { /* квота */ }
    };
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

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
        <ScrollText size={20} strokeWidth={ICON_STROKE} style={{ opacity: 0.5, marginBottom: SP.sm }} />
        <div>В проекте нет README.md и папки docs/</div>
      </div>
    );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Поиск по документации + тумблер нижней зоны */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs,
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.border}`,
      }}>
        <div style={{ position: 'relative', flex: 1, minWidth: 0 }}>
          <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ position: 'absolute', left: SP.sm, top: '50%', transform: 'translateY(-50%)', color: C.textMuted, pointerEvents: 'none' }} />
          <TextField value={query} onChange={setQuery} placeholder="Поиск по документам"
            style={{ height: 30, fontSize: FS.sm, paddingLeft: 28 }} />
        </div>
        {/* Открытый документ в чат — вложением. Дубль кнопки из шапки превью: до неё
            нужно доводить взгляд вниз, а действие частое */}
        <IconButton
          title={doc ? `«${doc.title}» в чат — вложением` : 'Откройте документ, чтобы отправить его в чат'}
          disabled={!doc}
          onClick={() => doc && onAttachToChat(doc.path)}
          size="sm"
        >
          <MessageSquarePlus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        {/* Режим работы панели: со встроенным превью или только список (тогда документ
            открывается сразу в центральной области) */}
        <IconButton
          title={previewEnabled ? 'Превью снизу включено — выключить' : 'Превью снизу выключено — включить'}
          active={previewEnabled}
          onClick={() => setPreviewEnabled(v => {
            const next = !v;
            try { localStorage.setItem(PREVIEW_KEY, next ? '1' : '0'); } catch { /* квота */ }
            return next;
          })}
          size="sm"
        >
          <PanelBottom size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>

      {/* Результаты поиска замещают дерево, пока запрос активен */}
      {searching ? (
        <div style={{ flex: 1, overflowY: 'auto', padding: `${SP.xs}px 0` }}>
          {/* null — ответ ещё не пришёл (запрос уходит через 250 мс после ввода) */}
          {hits === null && <div style={emptyStyle}>Ищем…</div>}
          {hits?.length === 0 && <div style={emptyStyle}>Ничего не найдено</div>}
          {(hits ?? []).map((h, i) => (
            <button key={`${h.path}-${i}`} onClick={() => openDoc(h.path, h.slug)} style={hitStyle}>
              <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{h.title}</div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>{h.path}</div>
              <div style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs, lineHeight: 1.5 }}>{h.snippet}</div>
            </button>
          ))}
        </div>
      ) : (
        <>
          {/* Дерево документов. С выключенной нижней зоной занимает всю панель;
              с включённой — высоту, заданную хендлом ресайза */}
          <div style={previewEnabled
            ? { flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0, height: treeH }
            : { flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }
          }>
            <div style={{ overflowY: 'auto', padding: `${SP.xs}px ${SP.xs}px` }}>
                {groups.map(([folder, docs]) => (
                  <div key={folder}>
                    {/* Подпись папки тем же разделителем, что группирует чаты по дням:
                        общий приём для «границы группы» в списках — и никакой подложки,
                        которая спорила бы с выделением строки */}
                    {folder && <ListDateDivider title={folder} />}
                    {docs.map(d => (
                      <div
                        key={d.path}
                        style={{
                          display: 'flex', alignItems: 'center', borderRadius: R.md,
                          background: d.path === selected ? C.bgSelected : 'transparent',
                          minHeight: ROW_H,
                        }}
                      >
                        <button
                          onClick={() => handleRowClick(d.path)}
                          onDoubleClick={() => handleRowDoubleClick(d.path)}
                          title={`${d.path}\nДвойной клик — открыть в центре`}
                          style={{
                            ...rowStyle,
                            flex: 1, minWidth: 0,
                            paddingLeft: folder ? SP.md : SP.sm,
                            color: d.path === selected ? C.textHeading : C.textSecondary,
                            fontWeight: d.path === selected ? 600 : 400,
                          }}>
                          {/* Без иконки: у всех строк она была бы одинаковой и не различала бы
                              документы — папка и подпись группы несут больше смысла */}
                          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{d.title}</span>
                        </button>
                      </div>
                    ))}
                  </div>
                ))}
            </div>
          </div>

          {/* Хендл ресайза границы «список / превью».
              Фон как у шапки панели: полоса читается частью её оформления, а не швом */}
          {previewEnabled && (
            <div
              onPointerDown={handleTreeResize}
              title="Потяните, чтобы изменить высоту списка"
              style={{
                flexShrink: 0, height: 9, cursor: 'row-resize', background: C.bgMain,
                borderTop: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              <div style={{ width: 28, height: 2, borderRadius: R.max, background: C.border }} />
            </div>
          )}

          {/* Нижняя зона: живёт постоянно, пока включён тумблер. Без выбранного документа
              показывает подсказку — так граница зон не скачет при каждом открытии */}
          {previewEnabled && (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
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
              {!doc && !error && <div style={emptyStyle}>Выберите документ в списке</div>}
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
          )}
        </>
      )}
    </div>
  );
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
  display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
  padding: `1px ${SP.sm}px`, border: 'none', background: 'transparent',
  borderRadius: R.md, cursor: 'pointer', textAlign: 'left' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.35, minWidth: 0,
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
