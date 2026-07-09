import { useEffect, useMemo, useRef, useState } from 'react';
import type { NoteSummary } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes, useNoteFolders } from '../../lib/notes';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { CollapseGroup, SourceDot, IconFolder, IconFolderMove, IconPencil, IconPlus, IconTrash } from './shared';

// Иконка «Новая папка» для меню (папка с плюсом)
const IconFolderPlus = () => (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
    <path d="M12 11v4M10 13h4" />
  </svg>
);

interface Group { source: string; label: string; root: FolderNode }

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
export function NotesList({ notes, selectedId, onSelect, onMoved, onCreateInFolder, onDeleted, onIdsRemapped }: {
  notes: NoteSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
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
    setCreatingFolder(null);
    setNewFolderValue('');
    if (!name) return;
    const path = parent ? `${parent}/${name}` : name;
    try { await api.notes.createFolder(source, path); bumpNotes(); }
    catch { /* дубликат/конфликт — реалтайм покажет актуальное */ }
  };

  const groups = useMemo<Group[]>(() => {
    const notesBySrc = new Map<string, { label: string; notes: NoteSummary[] }>();
    for (const n of notes) {
      let g = notesBySrc.get(n.source);
      if (!g) { g = { label: n.sourceLabel, notes: [] }; notesBySrc.set(n.source, g); }
      g.notes.push(n);
    }
    const foldersBySrc = new Map<string, string[]>();
    for (const f of folders) {
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
        return { source, label, root: buildTree(gn?.notes ?? [], foldersBySrc.get(source) ?? []) };
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
    return (
      <button
        key={n.id}
        onClick={() => onSelect(n.id)}
        onContextMenu={e => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, kind: 'note', source: n.source, note: n }); }}
        {...longPress((x, y) => setCtxMenu({ x, y, kind: 'note', source: n.source, note: n }))}
        title={n.title}
        draggable
        onDragStart={e => {
          e.dataTransfer.setData('application/x-note-id', n.id);
          e.dataTransfer.setData('application/x-note-source', n.source);
          e.dataTransfer.effectAllowed = 'move';
        }}
        style={{
          width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
          padding: `5px 8px 5px ${24 + depth * 14}px`, borderRadius: R.sm, fontFamily: FONT.sans,
          fontSize: 12.5, marginBottom: 1,
          background: active ? C.accentMuted : 'transparent',
          color: active ? C.textHeading : C.textSecondary,
          fontWeight: active ? 500 : 400,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          WebkitTouchCallout: 'none', WebkitUserSelect: 'none', userSelect: 'none',
        }}
      >{n.title}</button>
    );
  };

  // Собрать все заметки папки (включая подпапки)
  const notesUnder = (node: FolderNode): NoteSummary[] =>
    [...node.notes, ...node.children.flatMap(notesUnder)];

  const deleteNote = async (n: NoteSummary) => {
    if (!window.confirm(`Удалить заметку «${n.title}»?`)) return;
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

  const deleteFolder = async (source: string, node: FolderNode) => {
    const all = notesUnder(node);
    const msg = all.length > 0
      ? `Удалить папку «${node.name}» и ${all.length} замет${all.length === 1 ? 'ку' : all.length < 5 ? 'ки' : 'ок'} в ней?`
      : `Удалить папку «${node.name}»?`;
    if (!window.confirm(msg)) return;
    try { await api.notes.deleteFolder(source, node.path); }   // рекурсивно (пустая или с заметками)
    catch { /* уже удалена — реалтайм обновит */ }
    bumpNotes();
    onDeleted?.(all.map(n => n.id));
  };

  const iconBtn = (title: string, onClick: () => void, children: React.ReactNode) => (
    <span role="button" tabIndex={0} title={title}
      onClick={e => { e.stopPropagation(); onClick(); }}
      onKeyDown={e => { if (e.key === 'Enter') { e.stopPropagation(); onClick(); } }}
      style={{ display: 'flex', alignItems: 'center', color: C.textMuted, cursor: 'pointer', padding: '0 2px' }}>
      {children}
    </span>
  );

  const renderFolder = (source: string, node: FolderNode, depth: number): React.ReactNode => {
    const key = `${source}|${node.path}`;
    const isCollapsed = collapsed.has(key);
    const isDrop = dropTarget === key;
    const isRenaming = renaming === key;
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
        <button
          onClick={() => setCollapsed(prev => { const next = new Set(prev); isCollapsed ? next.delete(key) : next.add(key); return next; })}
          onContextMenu={e => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, kind: 'folder', source, node }); }}
          {...longPress((x, y) => setCtxMenu({ x, y, kind: 'folder', source, node }))}
          draggable
          onDragStart={e => {
            e.dataTransfer.setData('application/x-folder-path', node.path);
            e.dataTransfer.setData('application/x-note-source', source);
            e.dataTransfer.effectAllowed = 'move';
          }}
          {...dropProps(source, node.path)}
          style={{
            width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
            display: 'flex', alignItems: 'center', gap: 6,
            padding: `4px 8px 4px ${10 + depth * 14}px`, borderRadius: R.sm, fontFamily: FONT.sans,
            fontSize: 12, fontWeight: 500, color: C.textSecondary,
            background: isDrop ? C.accentMuted : 'transparent',
            boxShadow: isDrop ? `inset 0 0 0 1.5px ${C.accent}` : 'none',
          }}
        >
          <span style={{ fontSize: 8, color: C.textMuted, width: 8 }}>{isCollapsed ? '▸' : '▾'}</span>
          <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
          <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{node.name}</span>
          {onCreateInFolder && iconBtn('Новая заметка в папке', () => onCreateInFolder(source, node.path),
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>)}
          {iconBtn('Переименовать/перенести папку', () => { setRenaming(key); setRenameValue(node.path); },
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>)}
          {iconBtn('Удалить папку', () => void deleteFolder(source, node),
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" /></svg>)}
          <span style={{ fontSize: 10, color: C.textMuted }}>{countNotes(node)}</span>
        </button>
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

  if (notes.length === 0 && folders.length === 0)
    return (
      <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.6 }}>
        Пока нет заметок. Создай первую или попроси Claude законспектировать что-нибудь.
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
            tail={<span style={{ fontSize: 11, color: C.textMuted }}>{countNotes(g.root)}</span>}
          >
            {creatingFolder === `${g.source}|` && renderCreateFolderInput(g.source, '', 0)}
            {g.root.children.map(c => renderFolder(g.source, c, 0))}
            {g.root.notes.map(n => renderNote(n, 0))}
          </CollapseGroup>
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
              {menuItem(<IconFolderPlus />, 'Новая папка', () => { setCreatingFolder(`${ctxMenu.source}|`); setNewFolderValue(''); })}
            </>
          ) : ctxMenu.kind === 'note' ? (
            <>
              {menuItem(<IconPencil />, 'Открыть', () => onSelect(ctxMenu.note.id))}
              {menuItem(<IconFolderMove />, 'Переместить в...', () => setCtxMenu({ ...ctxMenu, move: true }))}
              {menuDivider}
              {menuItem(<IconTrash />, 'Удалить', () => void deleteNote(ctxMenu.note), true)}
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
              {menuItem(<IconTrash />, 'Удалить папку', () => void deleteFolder(ctxMenu.source, ctxMenu.node), true)}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function countNotes(node: FolderNode): number {
  return node.notes.length + node.children.reduce((s, c) => s + countNotes(c), 0);
}
