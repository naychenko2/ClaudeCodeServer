// Сохранение содержимого чата в заметку — общий хелпер кнопок «В заметку»
// (ответ ассистента, план). Создаёт .md через notes-API; куда — по контексту:
// чат в проекте → notes/ проекта, чат вне проекта → личный vault.

import { api } from '../../lib/api';
import { bumpNotes } from '../../lib/notes';
import type { NoteDetail } from '../../types';

// Папка, куда складываются заметки, сохранённые из чата
const CHAT_NOTES_FOLDER = 'Из чатов';

// Заголовок из первой содержательной строки текста: markdown-маркеры и символы,
// проблемные для имени файла/wikilink, отбрасываются; длинное режется по слову.
export function deriveNoteTitle(text: string, prefix = ''): string {
  const line = text
    .split('\n')
    .map(s => s.replace(/^[\s#>*\-+`~\d.)]+/, '').trim())
    .find(s => s.length > 0) ?? '';
  let title = line.replace(/[*_`[\]|#\\/:<>"?]/g, '').replace(/\s+/g, ' ').trim();
  if (title.length > 60) {
    const cut = title.slice(0, 60);
    const sp = cut.lastIndexOf(' ');
    title = (sp > 30 ? cut.slice(0, sp) : cut).trimEnd() + '…';
  }
  if (!title) {
    const now = new Date();
    title = `Ответ · ${now.toLocaleDateString('ru-RU')} ${now.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}`;
  }
  return prefix + title;
}

// Создать заметку из текста чата. Коллизии заголовков бэк решает сам (суффикс -2).
export async function saveChatNote(opts: {
  text: string;
  projectId?: string | null;
  titlePrefix?: string;
}): Promise<NoteDetail> {
  const note = await api.notes.create({
    title: deriveNoteTitle(opts.text, opts.titlePrefix),
    content: opts.text,
    source: opts.projectId ?? 'personal',
    folder: CHAT_NOTES_FOLDER,
  });
  bumpNotes();
  return note;
}

// Перейти к заметке из любого раздела (паттерн FileViewer → NotesPage)
export function openNoteById(id: string): void {
  sessionStorage.setItem('cc_pending_note_id', id);
  window.dispatchEvent(new Event('cc-open-note'));
}
