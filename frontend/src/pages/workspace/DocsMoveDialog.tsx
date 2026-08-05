// Подтверждение переноса документа или раздела в другую папку — жест перетаскивания
// двигает файлы на диске, и промах мышью не должен уносить целую ветку молча.
//
// От переименования перенос отличается тем, что меняется ГЛУБИНА: ломаются не только
// чужие ссылки на переехавшее, но и его собственные ссылки на всё остальное
// («../vision.md» после переезда указывает не туда). Поэтому галка «обновить ссылки»
// здесь важнее, чем при переименовании, и включена по умолчанию.

import { useState } from 'react';
import type { DocEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, SP } from '../../lib/design';
import { Modal, ModalActions } from '../../components/ui';

interface Props {
  projectId: string;
  doc: DocEntry;
  // Куда переносим и как эта папка называется в панели (заголовок раздела либо путь)
  targetFolder: string;
  targetLabel: string;
  // Сколько документов уедет вместе с разделом — готовой фразой («ещё 3 документа»):
  // склонение считает панель, у неё для этого уже есть docCountWord
  subtreeLabel?: string;
  onClose: () => void;
  onMoved: (result: {
    path: string; moved: Record<string, string>;
    updatedDocs: number; brokenLinks: number; index: DocEntry[];
  }) => void;
}

export function DocsMoveDialog({
  projectId, doc, targetFolder, targetLabel, subtreeLabel, onClose, onMoved,
}: Props) {
  const [updateLinks, setUpdateLinks] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const section = !!doc.sectionFolder;

  const move = async () => {
    if (saving) return;
    setSaving(true);
    setError(null);
    try {
      onMoved(await api.docs.move(projectId, doc.path, targetFolder, updateLinks));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось перенести');
      setSaving(false);
    }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title={section ? 'Перенести раздел?' : 'Перенести документ?'}
      subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{doc.path}</span>}
      onClose={onClose}
      footer={
        <ModalActions confirmLabel="Перенести" onConfirm={move} loading={saving} onCancel={onClose} />
      }
    >
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
        В папку <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{targetFolder}/</span>
        {targetLabel && targetLabel !== targetFolder && <> — «{targetLabel}»</>}.
        {section && subtreeLabel && (
          <span style={{ display: 'block', marginTop: SP.xs }}>
            Папка раздела переедет со всем содержимым — внутри {subtreeLabel}.
          </span>
        )}
      </div>

      {/* Правка чужих файлов, поэтому решает пользователь. Но по умолчанию — да:
          после переезда битыми становятся и собственные ссылки документа */}
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
          Обновить ссылки
          <span style={{ display: 'block', color: C.textMuted, fontSize: FS.xs, marginTop: 2 }}>
            И чужие ссылки на переехавшее, и его собственные — при смене папки меняется
            глубина, и относительные пути перестают вести куда надо
          </span>
        </span>
      </label>

      {error && <div style={{ fontSize: FS.sm, color: C.danger }}>{error}</div>}
    </Modal>
  );
}
