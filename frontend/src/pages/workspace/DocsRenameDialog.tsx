// Переименование документа или раздела в панели «Документы».
//
// Меняется ИМЯ ФАЙЛА, а не заголовок внутри документа: в code wiki имя файла и есть
// адрес страницы, а «# Заголовок» остаётся авторским — в корпусе Estium имена файлов
// латиницей, а заголовки с эмодзи, и связывать их насильно неправильно.
//
// Раздел переименовывается парой «страница + папка» вместе со всем поддеревом — об этом
// говорим прямо: жест выглядит как правка одной строки, а переезжает целая ветка.

import { useState } from 'react';
import type { DocEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Field, Modal, ModalActions, TextField } from '../../components/ui';

interface Props {
  projectId: string;
  // Путь переименовываемого документа и его подпись в списке (заголовок — для шапки)
  path: string;
  title: string;
  // Папка раздела, если документ — его страница: тогда переезжает и она со всем содержимым
  sectionFolder?: string | null;
  onClose: () => void;
  // Новый путь, свежий индекс и карта переезда: по ней панель чинит закреплённые и
  // открытый документ — они помнят старые пути
  onRenamed: (result: {
    path: string; moved: Record<string, string>;
    updatedDocs: number; brokenLinks: number; index: DocEntry[];
  }) => void;
}

// Имя файла без расширения — то, что правит пользователь
function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1).replace(/\.[^.]+$/, '');
}

function folderOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

export function DocsRenameDialog({ projectId, path, title, sectionFolder, onClose, onRenamed }: Props) {
  const [name, setName] = useState(baseName(path));
  // Ссылки чиним по умолчанию: битая ссылка — это молчаливая поломка, а её починка
  // видна в git и откатывается одной командой
  const [updateLinks, setUpdateLinks] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const folder = folderOf(path);
  const next = name.trim().replace(/ /g, '-');
  const unchanged = next === baseName(path);

  const rename = async () => {
    if (!next || unchanged || saving) return;
    setSaving(true);
    setError(null);
    try {
      onRenamed(await api.docs.rename(projectId, path, name, updateLinks));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось переименовать');
      setSaving(false);
    }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title={sectionFolder ? 'Переименовать раздел' : 'Переименовать документ'}
      subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{path}</span>}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Переименовать"
          onConfirm={rename}
          loading={saving}
          confirmDisabled={!next || unchanged}
          onCancel={onClose}
        />
      }
    >
      {/* Заголовок документа остаётся прежним — говорим об этом, иначе «переименовал,
          а в списке всё то же» читается как несработавшее действие */}
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
        Меняется имя файла. Заголовок «{title}» внутри документа останется прежним.
        {sectionFolder && ' Папка раздела переедет вместе со всем содержимым.'}
      </div>

      <Field label="Имя файла">
        <TextField value={name} onChange={setName} autoFocus mono onEnter={rename} />
      </Field>

      {next && !unchanged && (
        <div style={{
          padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, background: C.bgInset,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {folder && `${folder}/`}{next}.md{sectionFolder ? ` + ${next}/` : ''}
        </div>
      )}

      {/* Ссылки в остальных документах: правка чужих файлов, поэтому решает пользователь */}
      <label style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm, cursor: 'pointer',
        fontSize: FS.sm, color: C.textSecondary,
      }}>
        <input
          type="checkbox"
          checked={updateLinks}
          onChange={e => setUpdateLinks(e.target.checked)}
          style={{ marginTop: 3, accentColor: C.accent }}
        />
        <span>
          Обновить ссылки на этот документ в остальных
          <span style={{ display: 'block', color: C.textMuted, fontSize: FS.xs, marginTop: 2 }}>
            Чинится только то, что входит в область документации: ссылки из кода и файлов
            вне области останутся битыми — их число покажем после
          </span>
        </span>
      </label>

      {error && <div style={{ fontSize: FS.sm, color: C.danger }}>{error}</div>}
    </Modal>
  );
}
