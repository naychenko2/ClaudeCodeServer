import { useEffect, useState } from 'react';
import type { ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, FS, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Modal } from '../../components/ui';
import { invalidateProjectsCache } from '../projects/useAllProjects';

interface Props {
  sphere: ProjectGroup;
  onDeleted: (id: string) => void;
  // Переход на страницу сферы (когда удалению мешают персоны)
  onOpenPage: (id: string) => void;
  onClose: () => void;
}

function plural(n: number, one: string, few: string, many: string) {
  const m10 = n % 10, m100 = n % 100;
  return m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? few : many;
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
      const err = e as Error & { status?: number; body?: { personas?: number } };
      // Персон успели добавить между открытием диалога и подтверждением
      if (err.status === 409) { setPersonas(err.body?.personas ?? 1); return; }
      setError(err.message || 'Не удалось удалить сферу');
    }
  };

  if (error) {
    return (
      <Modal title={`Сферу «${sphere.name}» не удалось удалить`} width={MODAL_W.confirm} onClose={onClose}
        footer={<Button variant="secondary" size="md" fullWidth onClick={onClose}>Закрыть</Button>}>
        <div style={{ color: C.danger, fontSize: FS.base }}>{error}</div>
      </Modal>
    );
  }
  if (memory === null) return null;

  if (personas > 0) {
    return (
      <Modal
        title={`Сферу «${sphere.name}» нельзя удалить`}
        width={MODAL_W.form}
        onClose={onClose}
        footer={
          <div style={{ display: 'flex', gap: 10, width: '100%' }}>
            <div style={{ flex: 1 }}><Button variant="secondary" size="md" fullWidth onClick={onClose}>Закрыть</Button></div>
            <div style={{ flex: 1.5 }}>
              <Button variant="primary" size="md" fullWidth onClick={() => onOpenPage(sphere.id)}>Открыть страницу сферы</Button>
            </div>
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
