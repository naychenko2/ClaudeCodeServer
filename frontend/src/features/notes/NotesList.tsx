import { useEffect, useMemo, useRef, useState } from 'react';
import { Check, FileText, FolderPlus, MessageCircle, Timer, X } from 'lucide-react';
import type { NoteSummary } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes, useNoteFolders } from '../../lib/notes';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { ICON_SIZE } from '../../components/ui/icons';
import { ConfirmDialog, IconButton } from '../../components/ui';
import { CollapseGroup, SourceDot, IconFolder, IconFolderMove, IconPencil, IconPlus, IconTrash } from './shared';
// Форматирует остаток времени от ISO-строки expiresAt
const expiryTimeLeft = (expiresAt?: string): { label: string; urgent: boolean } | null => {
  if (!expiresAt) return null;
  const left = new Date(expiresAt).getTime() - Date.now();
  if (left <= 0) return { label: 'скоро', urgent: true };
  const min = Math.round(left / 60_000);
  if (min < 60) return { label: `${Math.max(min, 1)} мин`, urgent: true };
  const hours = Math.round(min / 60);
  if (hours < 24) return { label: `${hours} ч`, urgent: false };
  return { label: `${Math.round(hours / 24)} дн`, urgent: false };
};

// Иконка «Новая папка» для меню (lucide folder-plus)
const IconFolderPlus = () => (
  <FolderPlus size={ICON_SIZE.sm} strokeWidth={2} style={{ flexShrink: 0 }} />
);

interface Group { source: string; label: string; root: FolderNode; docGroups: DocGroup[] }

// Комментарии к документу (флаг doc-annotations): узел «документ → его комментарии»
interface DocGroup { docPath: string; notes: NoteSummary[] }

// Дерево папок источника: notes — заметки этого уровня, children — подпапки
interface FolderNode {
  name: string;
  path: string;          // полный путь папки ("Идеи/Черновики"); корень = ""
  children: FolderNode[];
  notes: NoteSummary[];
}

function buildTree(notes: NoteSummary[], folderPaths: string[] = []): FolderNode {
  const root: FolderNode = { name: '', path: '', children: [], notes: [] };
  const byPath = new Map<string, FolderNode>([['', root]]);
  const dirOf = (p: string) => { const i = p.lastIndexOf('/'); return i < 0 ? '' : p.slice(0, i); };

  const ensureFolder = (path: string): FolderNode => {
    const existing = byPath.get(path);
    if (existing) return existing;
    const parent = ensureFolder(dirOf(path));
    const node: FolderNode = { name: path.split('/').pop()!, path, children: [], notes: [] };
    parent.children.push(node);
    byPath.set(path, node);
    return node;
  };

  for (const n of notes) ensureFolder(dirOf(n.path)).notes.push(n);
  // Пустые физические папки — иначе они «исчезли» бы из дерева
  for (const p of folderPaths) if (p) ensureFolder(p);
  const sortRec = (node: FolderNode) => {
    node.children.sort((a, b) => a.name.localeCompare(b.name, 'ru'));
    node.children.forEach(sortRec);
  };
  sortRec(root);
  return root;
}

// Список заметок: источники → дерево папок → заметки. Перенос — drag&drop
// заметки на папку/заголовок источника (в пределах источника).
export function NotesList({ notes, selectedId, onSelect, onMoved, onCreateInFolder, onDeleted, onIdsRemapped, isMobile }: {
  notes: NoteSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  // Мобайл: инлайн-иконки скрыты, действия — через long-press контекстное меню (как в файлах)
  isMobile?: boolean;
  // Заметка перенесена (id сменился) — вызывающий обновляет выбор
  onMoved?: (oldId: string, newId: string) => void;
  // «+» на папке: создать заметку сразу в этой папке источника
  onCreateInFolder?: (source: string, folder: string) => void;
  // Папка удалена вместе с заметками — вызывающий сбрасывает выбор при необходимости
  onDeleted?: (ids: string[]) => void;
  // Папка переименована/перенесена — маппинг id заметок для обновления выбора
  onIdsRemapped?: (map: { oldId: string; newId: string }[]) => void;
}) {
  // Физические папки (в т.ч. пустые) и лейблы источников (для источников без заметок)
  const folders = useNoteFolders();
  const [srcLabels, setSrcLabels] = useState<Record<string, string>>({});
  useEffect(() => {
    api.notes.sources().then(ss => setSrcLabels(Object.fromEntries(ss.map(s => [s.key, s.label])))).catch(() => {});
  }, []);

  // Свёрнутые папки (ключ source|path); по умолчанию всё раскрыто
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  // Наведённая строка (ключ folder=source|path, note=note.id) — иконки только при ховере
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);   // source|path
  // Переименование папки: ключ source|path + редактируемый полный путь
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  // Создание новой папки: ключ source|parentPath ('' = корень источника) + имя
  const [creatingFolder, setCreatingFolder] = useState<string | null>(null);
  const [newFolderValue, setNewFolderValue] = useState('');
  // Контекстное меню (right-click / long-press): источник, заметка или папка
  const [ctxMenu, setCtxMenu] = useState<
    | { x: number; y: number; kind: 'source'; source: string; move?: undefined }
    | { x: number; y: number; kind: 'note'; source: string; note: NoteSummary; move?: boolean }
    | { x: number; y: number; kind: 'folder'; source: string; node: FolderNode; move?: boolean }
    | null
  >(null);
  useEffect(() => {
    if (!ctxMenu) return;
    const close = () => setCtxMenu(null);
    const t = setTimeout(() => {
      window.addEventListener('mousedown', close);
      window.addEventListener('touchstart', close);
    }, 0);
    return () => {
      clearTimeout(t);
      window.removeEventListener('mousedown', close);
      window.removeEventListener('touchstart', close);
    };
  }, [ctxMenu]);

  // Long-press на тач-устройствах → то же контекстное меню (iOS не шлёт contextmenu)
  const pressTimer = useRef<number | null>(null);
  const clearPress = () => { if (pressTimer.current) { clearTimeout(pressTimer.current); pressTimer.current = null; } };
  const longPress = (open: (x: number, y: number) => void) => ({
    onTouchStart: (e: React.TouchEvent) => {
      const t = e.touches[0];
      pressTimer.current = window.setTimeout(() => { navigator.vibrate?.(10); open(t.clientX, t.clientY); }, 500);
    },
    onTouchMove: clearPress,
    onTouchEnd: clearPress,
    onTouchCancel: clearPress,
  });

  const commitFolderRename = async (source: string, oldPath: string) => {
    const newPath = renameValue.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    setRenaming(null);
    if (!newPath || newPath === oldPath) return;
    try {
      const r = await api.notes.moveFolder(source, oldPath, newPath);
      bumpNotes();
      onIdsRemapped?.(r.notes);
    } catch { /* конфликт имени/вложение в себя — состояние покажет реалтайм */ }
  };

  const moveFolderTo = async (source: string, folderPath: string, targetFolder: string) => {
    const name = folderPath.split('/').pop()!;
    const newPath = targetFolder ? `${targetFolder}/${name}` : name;
    if (newPath === folderPath) return;
    if (targetFolder === folderPath || targetFolder.startsWith(folderPath + '/')) return;  // внутрь себя
    try {
      const r = await api.notes.moveFolder(source, folderPath, newPath);
      bumpNotes();
      onIdsRemapped?.(r.notes);
    } catch { /* конфликт имени */ }
  };

  const commitCreateFolder = async (source: string, parent: string) => {
    const name = newFolderValue.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    if (!name) return;  // пустой ввод (blur без текста) — не закрываем инпут
    setCreatingFolder(null);
    setNewFolderValue('');
    const path = parent ? `${parent}/${name}` : name;
    try { await api.notes.createFolder(source, path); bumpNotes(); }
    catch { /* дубликат/конфликт — реалтайм покажет актуальное */ }
  };

  const groups = useMemo<Group[]>(() => {
    const notesBySrc = new Map<string, { label: string; notes: NoteSummary[] }>();
    // Комментарии к документам — не в дерево папок, а в группы «документ → комментарии»
    const docsBySrc = new Map<string, Map<string, NoteSummary[]>>();
    for (const n of notes) {
      let g = notesBySrc.get(n.source);
      if (!g) { g = { label: n.sourceLabel, notes: [] }; notesBySrc.set(n.source, g); }
      if (n.annotation) {
        // Ответы тредов в дерево документов не попадают — вложатся под корневым
        if (n.annotation.isReply) continue;
        let docs = docsBySrc.get(n.source);
        if (!docs) { docs = new Map(); docsBySrc.set(n.source, docs); }
        const arr = docs.get(n.annotation.docPath) ?? [];
        arr.push(n);
        docs.set(n.annotation.docPath, arr);
      } else {
        g.notes.push(n);
      }
    }
    const foldersBySrc = new Map<string, string[]>();
    for (const f of folders) {
      // Физическая папка «Комментарии» служебная: её содержимое показано группами
      // «документ → комментарии», пустой узел папки в дереве — только шум
      if (/^Комментарии(\/|$)/i.test(f.path)) continue;
      const arr = foldersBySrc.get(f.source) ?? [];
      arr.push(f.path);
      foldersBySrc.set(f.source, arr);
    }
    // Источник может иметь только пустые папки (без заметок) — показываем и его
    const keys = new Set<string>([...notesBySrc.keys(), ...foldersBySrc.keys()]);
    return [...keys]
      .map(source => {
        const gn = notesBySrc.get(source);
        const label = gn?.label ?? srcLabels[source] ?? (source === 'personal' ? 'Личный' : source);
        const docGroups = [...(docsBySrc.get(source) ?? new Map<string, NoteSummary[]>())]
          .map(([docPath, ns]) => ({ docPath, notes: ns }))
          .sort((a, b) => a.docPath.localeCompare(b.docPath, 'ru'));
        return { source, label, root: buildTree(gn?.notes ?? [], foldersBySrc.get(source) ?? []), docGroups };
      })
      .sort((a, b) => a.source === 'personal' ? -1 : b.source === 'personal' ? 1 : a.label.localeCompare(b.label, 'ru'));
  }, [notes, folders, srcLabels]);

  const doMove = async (noteId: string, source: string, folder: string) => {
    const note = notes.find(n => n.id === noteId);
    if (!note || note.source !== source) return;   // перенос только внутри источника
    const dir = note.path.includes('/') ? note.path.slice(0, note.path.lastIndexOf('/')) : '';
    if (dir === folder) return;
    try {
      const updated = await api.notes.move(noteId, folder || null);
      bumpNotes();
      onMoved?.(noteId, updated.id);
    } catch { /* конфликт имени — реалтайм/повтор покажет актуальное */ }
  };

  const dropProps = (source: string, folder: string) => ({
    onDragOver: (e: React.DragEvent) => {
      if (e.dataTransfer.types.includes('application/x-note-id') ||
          e.dataTransfer.types.includes('application/x-folder-path')) {
        e.preventDefault();
        setDropTarget(`${source}|${folder}`);
      }
    },
    onDragLeave: () => setDropTarget(prev => prev === `${source}|${folder}` ? null : prev),
    onDrop: (e: React.DragEvent) => {
      e.preventDefault();
      setDropTarget(null);
      const dragSource = e.dataTransfer.getData('application/x-note-source');
      if (dragSource !== source) return;   // перенос только внутри источника
      const noteId = e.dataTransfer.getData('application/x-note-id');
      if (noteId) { void doMove(noteId, source, folder); return; }
      const folderPath = e.dataTransfer.getData('application/x-folder-path');
      if (folderPath) void moveFolderTo(source, folderPath, folder);
    },
  });

  // Инлайн-ввод имени новой папки (в корне источника или внутри папки)
  const renderCreateFolderInput = (source: string, parent: string, depth: number) => (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: `3px 8px 3px ${10 + depth * 14}px` }}>
      <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
      <input
        autoFocus
        value={newFolderValue}
        onChange={e => setNewFolderValue(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'Enter') void commitCreateFolder(source, parent);
          if (e.key === 'Escape') { setCreatingFolder(null); setNewFolderValue(''); }
        }}
        onBlur={() => void commitCreateFolder(source, parent)}
        placeholder="Имя папки"
        style={{
          flex: 1, minWidth: 0, fontSize: 12, fontFamily: FONT.sans, color: C.textHeading,
          background: C.bgWhite, border: `1px solid ${C.accent}`, borderRadius: R.sm,
          padding: '2px 6px', outline: 'none',
        }}
      />
    </div>
  );

  const renderNote = (n: NoteSummary, depth: number) => {
    const active = n.id === selectedId;
    const hovered = hoveredKey === n.id;
    const exp = expiryTimeLeft(n.expiresAt);
    return (
      <div
        key={n.id}
        onClick={() => onSelect(n.id)}
        onContextMenu={e => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, kind: 'note', source: n.source, note: n }); }}
        {...longPress((x, y) => setCtxMenu({ x, y, kind: 'note', source: n.source, note: n }))}
        onMouseEnter={() => setHoveredKey(n.id)}
        onMouseLeave={() => setHoveredKey(prev => prev === n.id ? null : prev)}
        title={n.title}
        draggable
        onDragStart={e => {
          e.dataTransfer.setData('application/x-note-id', n.id);
          e.dataTransfer.setData('application/x-note-source', n.source);
          e.dataTransfer.effectAllowed = 'move';
        }}
        style={{
          display: 'flex', alignItems: 'center', gap: 4, cursor: 'pointer',
          minHeight: 26, boxSizing: 'border-box',
          padding: `0 6px 0 ${24 + depth * 14}px`, borderRadius: R.sm, marginBottom: 1,
          background: active ? C.accentMuted : (hovered && !isMobile ? C.bgSelected : 'transparent'),
          WebkitTouchCallout: 'none', WebkitUserSelect: 'none', userSelect: 'none',
        }}
      >
        <span style={{
          flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 12.5,
          color: active ? C.textHeading : C.textSecondary, fontWeight: active ? 500 : 400,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{n.title}</span>
        {n.annotation && !n.annotation.isReply && (
          <span title={n.annotation.status === 'open' ? 'Комментарий открыт' : 'Комментарий решён'} style={{
            display: 'flex', alignItems: 'center', flexShrink: 0, marginRight: 2,
            color: n.annotation.status === 'open' ? C.warning : C.success,
          }}>
            {n.annotation.status === 'open' ? <MessageCircle size={11} strokeWidth={2.5} /> : <Check size={12} strokeWidth={2.5} />}
          </span>
        )}
        {exp && (
          <span style={{
            fontSize: 10, color: exp.urgent ? C.warning : C.textMuted, flexShrink: 0,
            display: 'flex', alignItems: 'center', gap: 3, marginRight: 4,
          }}>
            <Timer size={11} strokeWidth={2} />
            {exp.label}
          </span>
        )}
        {/* Действия — только при ховере на десктопе (на мобиле — long-press меню) */}
        {!isMobile && hovered && (
          <IconButton size="xs" tone="danger" title="Удалить заметку"
            onClick={e => { e.stopPropagation(); deleteNote(n); }}>
            <IconTrash />
          </IconButton>
        )}
      </div>
    );
  };

  // Собрать все заметки папки (включая подпапки)
  const notesUnder = (node: FolderNode): NoteSummary[] =>
    [...node.notes, ...node.children.flatMap(notesUnder)];

  // Удаление (заметка/папка) в два шага: запрос подтверждения (диалог) → само удаление
  const [confirmTarget, setConfirmTarget] = useState<
    | { kind: 'note'; note: NoteSummary }
    | { kind: 'folder'; source: string; node: FolderNode }
    | null
  >(null);

  const deleteNote = (n: NoteSummary) => setConfirmTarget({ kind: 'note', note: n });
  const doDeleteNote = async (n: NoteSummary) => {
    setConfirmTarget(null);
    try {
      await api.notes.delete(n.id);
      bumpNotes();
      onDeleted?.([n.id]);
    } catch { /* уже удалена — реалтайм обновит список */ }
  };

  // Папки источника (включая промежуточные уровни + физические пустые) —
  // для подменю «Переместить в…»
  const foldersOf = (source: string) => [...new Set([
    ...notes.filter(n => n.source === source && n.path.includes('/'))
      .flatMap(n => {
        const parts = n.path.slice(0, n.path.lastIndexOf('/')).split('/');
        return parts.map((_, i) => parts.slice(0, i + 1).join('/'));
      }),
    ...folders.filter(f => f.source === source).map(f => f.path),
  ])].sort((a, b) => a.localeCompare(b, 'ru'));

  const deleteFolder = (source: string, node: FolderNode) => setConfirmTarget({ kind: 'folder', source, node });
  const doDeleteFolder = async (source: string, node: FolderNode) => {
    setConfirmTarget(null);
    const all = notesUnder(node);
    try { await api.notes.deleteFolder(source, node.path); }   // рекурсивно (пустая или с заметками)
    catch { /* уже удалена — реалтайм обновит */ }
    bumpNotes();
    onDeleted?.(all.map(n => n.id));
  };

  const renderFolder = (source: string, node: FolderNode, depth: number): React.ReactNode => {
    const key = `${source}|${node.path}`;
    const isCollapsed = collapsed.has(key);
    const isDrop = dropTarget === key;
    const isRenaming = renaming === key;
    const hovered = hoveredKey === key;
    return (
      <div key={key}>
        {isRenaming ? (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: `3px 8px 3px ${10 + depth * 14}px` }}>
            <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
            <input
              autoFocus
              value={renameValue}
              onChange={e => setRenameValue(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter') void commitFolderRename(source, node.path);
                if (e.key === 'Escape') setRenaming(null);
              }}
              onBlur={() => void commitFolderRename(source, node.path)}
              title="Полный путь папки: новое имя или «Другая/Папка» для переноса"
              style={{
                flex: 1, minWidth: 0, fontSize: 12, fontFamily: FONT.sans, color: C.textHeading,
                background: C.bgWhite, border: `1px solid ${C.accent}`, borderRadius: R.sm,
                padding: '2px 6px', outline: 'none',
              }}
            />
          </div>
        ) : (
        <div
          onClick={() => setCollapsed(prev => { const next = new Set(prev); isCollapsed ? next.delete(key) : next.add(key); return next; })}
          onContextMenu={e => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, kind: 'folder', source, node }); }}
          {...longPress((x, y) => setCtxMenu({ x, y, kind: 'folder', source, node }))}
          onMouseEnter={() => setHoveredKey(key)}
          onMouseLeave={() => setHoveredKey(prev => prev === key ? null : prev)}
          draggable
          onDragStart={e => {
            e.dataTransfer.setData('application/x-folder-path', node.path);
            e.dataTransfer.setData('application/x-note-source', source);
            e.dataTransfer.effectAllowed = 'move';
          }}
          {...dropProps(source, node.path)}
          style={{
            cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6,
            minHeight: 26, boxSizing: 'border-box',
            padding: `0 6px 0 ${10 + depth * 14}px`, borderRadius: R.sm, fontFamily: FONT.sans,
            fontSize: 12, fontWeight: 500, color: C.textSecondary,
            background: isDrop ? C.accentMuted : (hovered && !isMobile ? C.bgSelected : 'transparent'),
            boxShadow: isDrop ? `inset 0 0 0 1.5px ${C.accent}` : 'none',
            WebkitTouchCallout: 'none', WebkitUserSelect: 'none', userSelect: 'none',
          }}
        >
          <span style={{ fontSize: 8, color: C.textMuted, width: 8 }}>{isCollapsed ? '▸' : '▾'}</span>
          <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
          <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{node.name}</span>
          {/* Действия — только при ховере на десктопе; иначе счётчик (на мобиле — long-press) */}
          {!isMobile && hovered ? (
            <span style={{ display: 'flex', alignItems: 'center', flexShrink: 0 }}>
              {onCreateInFolder && (
                <IconButton size="xs" tone="accent" title="Новая заметка в папке"
                  onClick={e => { e.stopPropagation(); onCreateInFolder(source, node.path); }}><IconPlus /></IconButton>
              )}
              <IconButton size="xs" title="Переименовать/перенести папку"
                onClick={e => { e.stopPropagation(); setRenaming(key); setRenameValue(node.path); }}><IconPencil /></IconButton>
              <IconButton size="xs" tone="danger" title="Удалить папку"
                onClick={e => { e.stopPropagation(); void deleteFolder(source, node); }}><IconTrash /></IconButton>
            </span>
          ) : (
            <span style={{ fontSize: 10, color: C.textMuted }}>{countNotes(node)}</span>
          )}
        </div>
        )}
        {!isCollapsed && (
          <>
            {creatingFolder === key && renderCreateFolderInput(source, node.path, depth + 1)}
            {node.children.map(c => renderFolder(source, c, depth + 1))}
            {node.notes.map(n => renderNote(n, depth + 1))}
          </>
        )}
      </div>
    );
  };

  // Ответы тредов по цели (source|путь корневой заметки-комментария) — вложатся под корнем
  const repliesIndex = useMemo(() => {
    const m = new Map<string, NoteSummary[]>();
    for (const n of notes) {
      const a = n.annotation;
      if (!a?.isReply) continue;
      const key = `${a.docScope}|${a.docPath.toLowerCase()}`;
      const arr = m.get(key) ?? [];
      arr.push(n);
      m.set(key, arr);
    }
    return m;
  }, [notes]);

  // Узел «документ → комментарии» (флаг doc-annotations): сворачиваемый, со счётчиком;
  // удалённый документ — ghost (зачёркнутый путь). Ответы — под корневым комментарием.
  const renderDocGroup = (source: string, dg: DocGroup) => {
    const key = `${source}|doc:${dg.docPath}`;
    const isCollapsed = collapsed.has(key);
    const openCount = dg.notes.filter(n => n.annotation?.status === 'open').length;
    const ghost = dg.notes.length > 0 && dg.notes.every(n => n.annotation?.docMissing);
    return (
      <div key={key}>
        <div
          onClick={() => setCollapsed(prev => { const next = new Set(prev); isCollapsed ? next.delete(key) : next.add(key); return next; })}
          title={ghost ? `${dg.docPath} — документ удалён` : dg.docPath}
          style={{
            cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6,
            minHeight: 26, boxSizing: 'border-box', padding: '0 6px 0 10px',
            borderRadius: R.sm, fontFamily: FONT.sans, fontSize: 12, fontWeight: 500,
            color: C.textSecondary, userSelect: 'none',
          }}
        >
          <span style={{ fontSize: 8, color: C.textMuted, width: 8 }}>{isCollapsed ? '▸' : '▾'}</span>
          <FileText size={ICON_SIZE.sm} strokeWidth={2} style={{ color: C.textMuted, flexShrink: 0 }} />
          <span style={{
            flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            fontFamily: FONT.mono, fontSize: 11.5,
            color: ghost ? C.textMuted : undefined,
            textDecoration: ghost ? 'line-through' : undefined,
            textDecorationThickness: ghost ? '1px' : undefined,
          }}>
            {dg.docPath}
          </span>
          {ghost && <span style={{ fontSize: 10, color: C.textMuted, flexShrink: 0 }}>удалён</span>}
          <span style={{
            display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: 10.5, fontWeight: 600,
            color: ghost ? C.textMuted : openCount > 0 ? C.warningText : C.successText,
            background: ghost ? C.bgInset : openCount > 0 ? C.warningBg : C.successBg,
            borderRadius: 9, padding: '0 7px', flexShrink: 0,
          }}>
            {ghost ? <X size={10} strokeWidth={2.5} /> : openCount > 0 ? <MessageCircle size={10} strokeWidth={2.5} /> : <Check size={10} strokeWidth={2.5} />}
            {dg.notes.length}
          </span>
        </div>
        {!isCollapsed && (
          <div style={{ marginLeft: 8, paddingLeft: 6, borderLeft: `1px solid ${C.border}` }}>
            {dg.notes.map(n => {
              // Канонический докпуть корня (у проектов — notes/…) + легаси без префикса
              const keys = n.source === 'personal'
                ? [`${n.source}|${n.path.toLowerCase()}`]
                : [`${n.source}|notes/${n.path.toLowerCase()}`, `${n.source}|${n.path.toLowerCase()}`];
              const replies = keys.flatMap(k => repliesIndex.get(k) ?? []);
              return (
                <div key={n.id}>
                  {renderNote(n, 0)}
                  {replies.map(r => renderNote(r, 1))}
                </div>
              );
            })}
          </div>
        )}
      </div>
    );
  };

  if (notes.length === 0 && folders.length === 0)
    return (
      <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.6 }}>
        Пока нет заметок. Создай первую или попроси ассистента законспектировать что-нибудь.
      </div>
    );

  // Пункт контекстного меню — стиль как у FileExplorer (десктоп-popup)
  const menuItem = (icon: React.ReactNode, label: string, action: () => void, danger = false) => (
    <button
      key={label}
      onPointerDown={e => { e.stopPropagation(); setCtxMenu(null); action(); }}
      style={{
        display: 'flex', alignItems: 'center', width: '100%', padding: '8px 12px',
        background: 'none', border: 'none', cursor: 'pointer', textAlign: 'left',
        fontFamily: FONT.sans, fontSize: 13, color: danger ? C.danger : C.textPrimary,
        borderRadius: 6, gap: 10,
      }}
      onMouseEnter={e => { (e.currentTarget as HTMLButtonElement).style.background = C.bgInset; }}
      onMouseLeave={e => { (e.currentTarget as HTMLButtonElement).style.background = 'none'; }}
    >
      <span style={{ display: 'flex', alignItems: 'center', flexShrink: 0, opacity: 0.8 }}>{icon}</span>
      {label}
    </button>
  );
  const menuDivider = <div style={{ height: 1, background: C.border, margin: '4px 0' }} />;

  return (
    <div style={{ padding: '8px 8px 20px' }}>
      {groups.map(g => (
        <div key={g.source} {...dropProps(g.source, '')}
          style={dropTarget === `${g.source}|` ? { borderRadius: R.md, boxShadow: `inset 0 0 0 1.5px ${C.accent}` } : undefined}>
          <CollapseGroup
            defaultOpen
            title={
              <span
                onContextMenu={e => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, kind: 'source', source: g.source }); }}
                {...longPress((x, y) => setCtxMenu({ x, y, kind: 'source', source: g.source }))}
                style={{ display: 'flex', alignItems: 'center', gap: 7, WebkitTouchCallout: 'none', userSelect: 'none' }}
              >
                <SourceDot source={g.source} />
                <span style={{ fontSize: 12.5, fontWeight: 500, color: C.textPrimary }}>{g.label}</span>
              </span>
            }
            tail={<span style={{ fontSize: 11, color: C.textMuted }}>{countNotes(g.root) + g.docGroups.reduce((s, d) => s + d.notes.length, 0)}</span>}
          >
            {g.root.children.map(c => renderFolder(g.source, c, 0))}
            {g.root.notes.map(n => renderNote(n, 0))}
            {g.docGroups.map(dg => renderDocGroup(g.source, dg))}
          </CollapseGroup>
          {creatingFolder === `${g.source}|` && <div style={{ padding: '4px 8px 4px 28px' }}>{renderCreateFolderInput(g.source, '', 1)}</div>}
        </div>
      ))}

      {/* Контекстное меню (right-click): заметка / папка; move — выбор целевой папки */}
      {ctxMenu && (
        <div
          onMouseDown={e => e.stopPropagation()}
          onContextMenu={e => e.preventDefault()}
          style={{
            position: 'fixed', top: Math.min(ctxMenu.y, window.innerHeight - 240), left: Math.min(ctxMenu.x, window.innerWidth - 210),
            zIndex: 1000, background: C.bgWhite, border: `1px solid ${C.border}`,
            borderRadius: R.lg, boxShadow: SHADOW.dropdown, padding: 4, minWidth: 190,
            maxHeight: 320, overflowY: 'auto',
          }}
        >
          {ctxMenu.move ? (
            <>
              <div style={{ padding: '6px 12px 4px', fontSize: 10.5, fontWeight: 600, letterSpacing: '.04em', textTransform: 'uppercase', color: C.textMuted, fontFamily: FONT.sans }}>
                Переместить в
              </div>
              {[{ path: '', label: 'Корень' }, ...foldersOf(ctxMenu.source).map(f => ({ path: f, label: f }))]
                .filter(d => ctxMenu.kind !== 'folder' || (d.path !== ctxMenu.node.path && !d.path.startsWith(ctxMenu.node.path + '/')))
                .map(d => menuItem(<IconFolder />, d.label, () => {
                  if (ctxMenu.kind === 'note') void doMove(ctxMenu.note.id, ctxMenu.source, d.path);
                  else if (ctxMenu.kind === 'folder') void moveFolderTo(ctxMenu.source, ctxMenu.node.path, d.path);
                }))}
            </>
          ) : ctxMenu.kind === 'source' ? (
            <>
              {onCreateInFolder && menuItem(<IconPlus />, 'Новая заметка', () => onCreateInFolder(ctxMenu.source, ''))}
              {menuItem(<IconFolderPlus />, 'Новая папка', () => {
                const k = `${ctxMenu.source}|`;
                setCollapsed(prev => { const n = new Set(prev); n.delete(k); return n; });
                setCreatingFolder(k); setNewFolderValue('');
              })}
            </>
          ) : ctxMenu.kind === 'note' ? (
            <>
              {menuItem(<IconPencil />, 'Открыть', () => onSelect(ctxMenu.note.id))}
              {menuItem(<IconFolderMove />, 'Переместить в...', () => setCtxMenu({ ...ctxMenu, move: true }))}
              {menuDivider}
              {menuItem(<IconTrash />, 'Удалить', () => deleteNote(ctxMenu.note), true)}
            </>
          ) : (
            <>
              {onCreateInFolder && menuItem(<IconPlus />, 'Новая заметка', () => onCreateInFolder(ctxMenu.source, ctxMenu.node.path))}
              {menuItem(<IconFolderPlus />, 'Новая папка', () => {
                const k = `${ctxMenu.source}|${ctxMenu.node.path}`;
                setCollapsed(prev => { const n = new Set(prev); n.delete(k); return n; });   // раскрыть, чтобы ввод был виден
                setCreatingFolder(k); setNewFolderValue('');
              })}
              {menuItem(<IconPencil />, 'Переименовать', () => { setRenaming(`${ctxMenu.source}|${ctxMenu.node.path}`); setRenameValue(ctxMenu.node.path); })}
              {menuItem(<IconFolderMove />, 'Переместить в...', () => setCtxMenu({ ...ctxMenu, move: true }))}
              {menuDivider}
              {menuItem(<IconTrash />, 'Удалить папку', () => deleteFolder(ctxMenu.source, ctxMenu.node), true)}
            </>
          )}
        </div>
      )}

      {/* Подтверждение удаления заметки/папки */}
      {confirmTarget && (confirmTarget.kind === 'note' ? (
        <ConfirmDialog
          title="Удалить заметку?"
          subtitle={<>Заметка «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{confirmTarget.note.title}</strong>» будет удалена без возможности восстановления.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => doDeleteNote(confirmTarget.note)}
          onCancel={() => setConfirmTarget(null)}
        />
      ) : (
        <ConfirmDialog
          title="Удалить папку?"
          subtitle={(() => {
            const count = notesUnder(confirmTarget.node).length;
            const name = <strong style={{ color: C.textPrimary, fontWeight: 600 }}>{confirmTarget.node.name}</strong>;
            return count > 0
              ? <>Папка «{name}» и {count} замет{count === 1 ? 'ка' : count < 5 ? 'ки' : 'ок'} в ней будут удалены без возможности восстановления.</>
              : <>Папка «{name}» будет удалена.</>;
          })()}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => doDeleteFolder(confirmTarget.source, confirmTarget.node)}
          onCancel={() => setConfirmTarget(null)}
        />
      ))}
    </div>
  );
}

function countNotes(node: FolderNode): number {
  return node.notes.length + node.children.reduce((s, c) => s + countNotes(c), 0);
}
