import { useEffect, useMemo, useState } from 'react';
import type { NoteSource, NoteTemplate } from '../../types';
import { api } from '../../lib/api';
import { useNotes, useNoteFolders } from '../../lib/notes';
import { Modal } from '../../components/ui';
import { C, FONT, R } from '../../lib/design';
import { OfflineError } from '../../lib/offline';
import { createNoteOffline } from '../../lib/notesOffline';
import { EXPIRY_PRESETS, expiryOptionLabel } from '../../lib/expiry';

// Диалог создания заметки: заголовок, источник, папка (с автодополнением по
// существующим — включая пустые физические папки), опционально шаблон и время жизни.
// Вынесен из NotesPage, чтобы переиспользоваться из раздела «Файлы».
export function NewNoteDialog({ defaults, onClose, onCreated }: {
  defaults?: { source?: string; folder?: string };
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const notes = useNotes();
  const noteFolders = useNoteFolders();
  const [title, setTitle] = useState('');
  const [source, setSource] = useState(defaults?.source ?? 'personal');
  const [folder, setFolder] = useState(defaults?.folder ?? '');
  const [sources, setSources] = useState<NoteSource[]>([{ key: 'personal', label: 'Личный' }]);
  const [templates, setTemplates] = useState<NoteTemplate[]>([]);
  const [templateId, setTemplateId] = useState('');
  const [temporary, setTemporary] = useState(false);
  const [ttl, setTtl] = useState<number>(1440);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.notes.sources().then(setSources).catch(() => {});
    api.notes.templates().then(setTemplates).catch(() => {});
  }, []);

  // Папки выбранного источника для автодополнения: из путей заметок + физические
  // (в т.ч. пустые); можно ввести и новую.
  const folders = useMemo(() => {
    const dirs = new Set<string>();
    notes.filter(n => n.source === source).forEach(n => {
      const i = n.path.lastIndexOf('/');
      if (i > 0) {
        const parts = n.path.slice(0, i).split('/');
        for (let k = 1; k <= parts.length; k++) dirs.add(parts.slice(0, k).join('/'));
      }
    });
    noteFolders.filter(f => f.source === source).forEach(f => dirs.add(f.path));
    return [...dirs].sort((a, b) => a.localeCompare(b, 'ru'));
  }, [notes, noteFolders, source]);

  const create = async () => {
    if (!title.trim()) return;
    setBusy(true);
    try {
      const note = await api.notes.create({
        title: title.trim(), source, templateId: templateId || undefined,
        folder: folder.trim() || undefined,
        expiresAfterMinutes: temporary ? ttl : null,
      });
      onCreated(note.id);
    } catch (e) {
      // Офлайн — создаём локально (шаблоны серверные, офлайн игнорируются)
      if (e instanceof OfflineError) {
        const localKey = await createNoteOffline({ title: title.trim(), source, folder: folder.trim() || undefined });
        onCreated(localKey);
      } else throw e;
    } finally { setBusy(false); }
  };

  return (
    <Modal width={440} title="Новая заметка" onClose={onClose}
      footer={
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button onClick={onClose} style={{ background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '8px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textSecondary }}>Отмена</button>
          <button onClick={create} disabled={busy || !title.trim()} style={{ background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg, padding: '8px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans, opacity: busy || !title.trim() ? 0.6 : 1 }}>Создать</button>
        </div>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div>
          <label style={fieldLabel}>Заголовок</label>
          <input autoFocus value={title} onChange={e => setTitle(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') create(); }}
            placeholder="Название заметки"
            style={fieldInput} />
        </div>
        <div>
          <label style={fieldLabel}>Куда</label>
          <select value={source} onChange={e => setSource(e.target.value)} style={{ ...fieldInput, cursor: 'pointer' }}>
            {sources.map(s => <option key={s.key} value={s.key}>{s.label}</option>)}
          </select>
        </div>
        <div>
          <label style={fieldLabel}>Папка</label>
          <input value={folder} onChange={e => setFolder(e.target.value)}
            list="note-folders" placeholder="Корень (или введи новую: Идеи/Черновики)"
            style={fieldInput} />
          <datalist id="note-folders">
            {folders.map(f => <option key={f} value={f} />)}
          </datalist>
        </div>
        {templates.length > 0 && (
          <div>
            <label style={fieldLabel}>Шаблон</label>
            <select value={templateId} onChange={e => setTemplateId(e.target.value)} style={{ ...fieldInput, cursor: 'pointer' }}>
              <option value="">Без шаблона</option>
              {templates.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}
            </select>
          </div>
        )}
        <div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
            <label style={fieldLabel}>Время жизни</label>
            <label style={{ fontSize: 11, color: C.textMuted, display: 'flex', alignItems: 'center', gap: 4, cursor: 'pointer' }}>
              <input type="checkbox" checked={temporary} onChange={e => setTemporary(e.target.checked)}
                style={{ accentColor: C.accent }} />
              только на время
            </label>
          </div>
          {temporary && (
            <select value={ttl} onChange={e => setTtl(Number(e.target.value))} style={{ ...fieldInput, cursor: 'pointer' }}>
              {EXPIRY_PRESETS.map(p => <option key={p.minutes} value={p.minutes}>{p.label}</option>)}
            </select>
          )}
          {!temporary && (
            <div style={{ padding: '9px 0', fontSize: 13, color: C.textMuted }}>{expiryOptionLabel(null)}</div>
          )}
        </div>
      </div>
    </Modal>
  );
}

const fieldLabel: React.CSSProperties = {
  display: 'block', fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '.04em',
  color: C.textMuted, marginBottom: 6,
};
const fieldInput: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '9px 12px', fontSize: 14, fontFamily: FONT.sans, color: C.textHeading, outline: 'none',
};
