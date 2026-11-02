import { useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { NotesList } from './NotesList';
import { NewNoteDialog } from './NewNoteDialog';
import { useAllProjects } from '../projects/useAllProjects';
import { canWriteContent } from '../projects/members/contentRights';
import {
  useNotes, ensureNotesLoaded, bumpNotes, C, SP,
  Button, PanelHeaderSlot, useHasPanelHeader, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';

// Панель «Заметки» воркспейса: заметки ТЕКУЩЕГО проекта (физические .md в notes/
// репы). Пара к разделу хаба «Заметки» (все источники) — как knowledge/knowledgeList.
// Клик по заметке открывает её в центре обычным путём файлов (notes/**.md в
// FileViewer рендерится полноценным NoteView).
//
// Известное ограничение: переход по [[wikilink]] внутри открытой заметки резолвится
// по заголовку среди ВСЕХ источников — одноимённая заметка другого источника может
// перехватить переход. Это поведение резолва заметок в целом, панель его не меняет.
export function ProjectNotesPanel({ projectId, activeFilePath, onOpenFile }: {
  projectId: string;
  // Открытый в центре файл — для подсветки выбранной заметки в списке
  activeFilePath?: string | null;
  onOpenFile: (path: string) => void;
}) {
  const notes = useNotes();
  useEffect(() => { void ensureNotesLoaded(); }, []);
  const inHeader = useHasPanelHeader();
  // Проект — по prop id панели (S2c-F4h): право записи решается по его роли. Точки записи
  // панели: кнопка «Заметка», проп onCreateInFolder (плюс на папке) и диалог создания — все
  // под дверью 'note.create'. Закрытая ПРЯЧЕТ (0 узлов, пустой строки шапки тоже нет),
  // не отключена. Проекта нет в кэше (ещё не загружен) — как раньше: бэк ответит 403.
  const project = useAllProjects().find(p => p.id === projectId);
  const canCreate = canWriteContent(project, 'note.create');
  // Диалог создания: null — закрыт; folder — préfill из «+» на папке
  const [newDialog, setNewDialog] = useState<{ folder?: string } | null>(null);
  // Заметка, созданная из панели: открыть в центре, как только приедет в список
  const [pendingOpenId, setPendingOpenId] = useState<string | null>(null);

  const openNote = (id: string) => {
    const n = notes.find(x => x.id === id);
    if (n) onOpenFile(`notes/${n.path}`);
  };

  useEffect(() => {
    if (!pendingOpenId) return;
    const n = notes.find(x => x.id === pendingOpenId);
    if (n) { setPendingOpenId(null); onOpenFile(`notes/${n.path}`); }
  }, [pendingOpenId, notes, onOpenFile]);

  // Подсветка в списке: открытый в центре notes/**.md → id его заметки
  const selectedId = activeFilePath?.replace(/\\/g, '/').match(/^notes\/(.+)$/i)
    ? notes.find(n => n.source === projectId && `notes/${n.path}`.toLowerCase() === activeFilePath!.replace(/\\/g, '/').toLowerCase())?.id ?? null
    : null;

  const createBtn = (
    <Button variant="primary" size="xs" onClick={() => setNewDialog({})}
      title="Новая заметка проекта"
      leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
      Заметка
    </Button>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
      {canCreate && (inHeader
        ? <PanelHeaderSlot pinned>{createBtn}</PanelHeaderSlot>
        : <div style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end',
            padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
          }}>{createBtn}</div>)}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <NotesList
          notes={notes}
          sourceFilter={projectId}
          selectedId={selectedId}
          onSelect={openNote}
          onCreateInFolder={canCreate ? (_, folder) => setNewDialog({ folder }) : undefined}
          onOpenFileRef={onOpenFile}
        />
      </div>
      {/* Диалог достижим только из двух гейтнутых входов; гейт и у самого диалога —
          это точка записи (api.notes.create внутри NewNoteDialog), закрыта для читателя */}
      {newDialog && canCreate && (
        <NewNoteDialog
          defaults={{ source: projectId, folder: newDialog.folder }}
          onClose={() => setNewDialog(null)}
          onCreated={id => { setNewDialog(null); bumpNotes(); setPendingOpenId(id); }}
        />
      )}
    </div>
  );
}
