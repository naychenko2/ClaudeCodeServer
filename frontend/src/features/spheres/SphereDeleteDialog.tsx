import { useEffect, useState } from 'react';
import type { ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, FS, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Modal } from '../../components/ui';
import { invalidateProjectsCache } from '../projects/useAllProjects';
import { plural } from '../../lib/plural';
import { deleteDialogMode, deleteFailure } from './sphereLogic';

interface Props {
  sphere: ProjectGroup;
  onDeleted: (id: string) => void;
  // Переход на страницу сферы (когда удалению мешают персоны); на самой странице сферы не нужен — кнопки нет
  onOpenPage?: (id: string) => void;
  onClose: () => void;
}

// Удаление сферы. Память удалению не мешает: пустая от персон сфера удаляется одним
// подтверждением «вместе с N записями памяти». Мешают только персоны сферы — тогда
// модалка объясняет, что сделать, и ведёт на страницу сферы.
export function SphereDeleteDialog({ sphere, onDeleted, onOpenPage, onClose }: Props) {
  const [memory, setMemory] = useState<number | null>(null);
  const [personas, setPersonas] = useState(0);
  const [error, setError] = useState('');

  useEffect(() => {
    let alive = true;
    api.spheres.overview(sphere.id)
      .then(o => { if (alive) { setMemory(o.memory.count); setPersonas(o.team.length); } })
      .catch(e => { if (alive) setError(e instanceof Error ? e.message : 'Не удалось загрузить сферу'); });
    return () => { alive = false; };
  }, [sphere.id]);

  const remove = async () => {
    try {
      await api.projectGroups.delete(sphere.id);
      invalidateProjectsCache();
      onDeleted(sphere.id);
    } catch (e: unknown) {
      // Персон успели добавить между открытием диалога и подтверждением
      const f = deleteFailure(e);
      if ('personas' in f) setPersonas(f.personas);
      else setError(f.error);
    }
  };

  const mode = deleteDialogMode({ memory, personas, error });
  if (mode === 'error') {
    return (
      <Modal title={`Сферу «${sphere.name}» не удалось удалить`} width={MODAL_W.confirm} onClose={onClose}
        footer={<Button variant="secondary" size="md" fullWidth onClick={onClose}>Закрыть</Button>}>
        <div role="alert" style={{ color: C.dangerText, fontSize: FS.base }}>{error}</div>
      </Modal>
    );
  }
  if (mode === 'loading' || memory === null) {
    return (
      <Modal title={`Сфера «${sphere.name}»`} width={MODAL_W.confirm} onClose={onClose}
        footer={<Button variant="secondary" size="md" fullWidth onClick={onClose}>Отмена</Button>}>
        <div style={{ color: C.textMuted, fontSize: FS.base }}>Проверяем, что есть в сфере…</div>
      </Modal>
    );
  }

  if (mode === 'blocked') {
    return (
      <Modal
        title={`Сферу «${sphere.name}» нельзя удалить`}
        width={MODAL_W.form}
        onClose={onClose}
        footer={
          <div style={{ display: 'flex', gap: 10, width: '100%' }}>
            <div style={{ flex: 1 }}><Button variant="secondary" size="md" fullWidth onClick={onClose}>Закрыть</Button></div>
            {onOpenPage && (
              <div style={{ flex: 1.5 }}>
                <Button variant="primary" size="md" fullWidth onClick={() => onOpenPage(sphere.id)}>Открыть страницу сферы</Button>
              </div>
            )}
          </div>
        }
      >
        <div style={{ fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5 }}>
          В ней {personas} {plural(personas, 'персона', 'персоны', 'персон')}. Они живут только в этой сфере —
          удаление унесло бы их без следа.
        </div>
        <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Что сделать
        </div>
        <ol style={{ margin: 0, paddingLeft: 20, fontSize: FS.base, color: C.textPrimary, lineHeight: 1.6 }}>
          <li>Персонам сферы смените зону — «Во всех моих данных», другая сфера или проект — или удалите их.</li>
          <li>Проекты сферы удалению не мешают: они вернутся в «Без сферы».</li>
        </ol>
      </Modal>
    );
  }

  return (
    <ConfirmDialog
      title={memory > 0
        ? `Удалить сферу «${sphere.name}» и ${memory} ${plural(memory, 'запись', 'записи', 'записей')} памяти?`
        : `Удалить сферу «${sphere.name}»?`}
      subtitle="Проекты сферы вернутся в «Без сферы». Сами проекты, их чаты и память проектов не пострадают."
      confirmLabel="Удалить сферу"
      confirmVariant="danger"
      onConfirm={remove}
      onCancel={onClose}
    />
  );
}
