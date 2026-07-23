import { useEffect, useState } from 'react';
import type { Project, Persona } from '../../../types';
import { api } from '../../../lib/api';
import { personaLabel } from '../../../lib/personas';
import { C, MODAL_W, R } from '../../../lib/design';
import { Modal, ModalActions } from '../../../components/ui';
import { invalidateProjectsCache } from '../useAllProjects';

interface Props {
  project: Project;
  onSuccess: () => void;
  onClose: () => void;
}

// Что заденет удаление проекта: персоны каскадно удаляются вместе с памятью,
// а чаты остаются на диске, но теряют привязку и пропадают из интерфейса.
interface Impact {
  personas: Persona[];
  chatCount: number;
}

export function DeleteDialog({ project, onSuccess, onClose }: Props) {
  const [error, setError] = useState('');
  const [impact, setImpact] = useState<Impact | null>(null);
  const [loadingImpact, setLoadingImpact] = useState(true);

  // Последствия считаем уже существующими эндпоинтами — отдельного API не заводим
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [personas, sessions] = await Promise.all([
          api.personas.list({ scope: 'project', projectId: project.id }),
          api.sessions.list(project.id),
        ]);
        if (!cancelled) setImpact({ personas, chatCount: sessions.length });
      } catch {
        // Не смогли посчитать — удаление не блокируем, просто не показываем блок
        if (!cancelled) setImpact(null);
      } finally {
        if (!cancelled) setLoadingImpact(false);
      }
    })();
    return () => { cancelled = true; };
  }, [project.id]);

  const handleConfirm = async () => {
    setError('');
    try {
      await api.projects.delete(project.id);
      invalidateProjectsCache(); // удаленный проект уходит из полки/палитры сразу
      onSuccess();
    } catch (e: any) {
      setError(e.message || 'Ошибка удаления');
    }
  };

  const personas = impact?.personas ?? [];
  const chatCount = impact?.chatCount ?? 0;

  return (
    <Modal
      title="Удалить проект?"
      width={MODAL_W.confirm}
      onClose={onClose}
      subtitle={
        <>
          Проект «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{project.name}</strong>» будет удалён без возможности восстановления. Файлы на диске не затрагиваются.
        </>
      }
      footer={
        <ModalActions
          confirmLabel="Удалить"
          confirmVariant="danger"
          confirmDisabled={loadingImpact}
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        {loadingImpact && (
          <div style={{ fontSize: 13, color: C.textMuted }}>Смотрю, что ещё затронет удаление…</div>
        )}

        {/* Каскад: персоны проекта уходят безвозвратно вместе с долгой памятью */}
        {!loadingImpact && personas.length > 0 && (
          <div style={{
            padding: '10px 12px',
            background: C.dangerBg,
            border: `1px solid ${C.dangerBorder}`,
            borderRadius: R.md,
            fontSize: 13,
            color: C.dangerText,
            lineHeight: 1.5,
          }}>
            <div style={{ fontWeight: 600, marginBottom: 4 }}>
              Вместе с проектом удалятся персоны ({personas.length}) и вся их долгая память:
            </div>
            <div>{personas.map(p => personaLabel(p)).join(', ')}</div>
          </div>
        )}

        {/* Чаты переживают удаление, но осиротевают — предупреждаем отдельно */}
        {!loadingImpact && chatCount > 0 && (
          <div style={{
            padding: '10px 12px',
            background: C.bgInset,
            border: `1px solid ${C.border}`,
            borderRadius: R.md,
            fontSize: 13,
            color: C.textSecondary,
            lineHeight: 1.5,
          }}>
            Чаты проекта ({chatCount}) не удаляются, но потеряют привязку и пропадут из интерфейса.
            Заново подключённая та же папка — уже другой проект, старые чаты в неё сами не вернутся.
          </div>
        )}

        {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      </div>
    </Modal>
  );
}
