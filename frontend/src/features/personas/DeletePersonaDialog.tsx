// Удаление персоны с обработкой дефолта: обычная персона удаляется через простое
// подтверждение; на 400 «нужен преемник»
// (удаляемая — текущая дефолт-персона) диалог переключается на выбор преемника
// той же зоны и повторяет DELETE с successorId. Остаться без дефолта нельзя.

import { useState } from 'react';
import type { Persona } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Modal, ModalActions, ConfirmDialog } from '../../components/ui';
import { showToast } from '../../lib/toast';
import { personaLabel, usePersonas } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';

export function DeletePersonaDialog({ persona, onDeleted, onCancel }: {
  persona: Persona;
  onDeleted: () => void;
  onCancel: () => void;
}) {
  const personas = usePersonas();
  // Режим выбора преемника — включается ответом бэка «выберите преемника»
  const [needSuccessor, setNeedSuccessor] = useState(false);
  const [successorId, setSuccessorId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Кандидаты той же зоны (бэк валидирует ещё раз): глобальной — глобальные,
  // проектной — команда её проекта. Сама удаляемая исключена.
  const candidates = personas.filter(p => p.id !== persona.id && p.scope === persona.scope
    && (persona.scope !== 'project' || p.projectId === persona.projectId));

  const doDelete = async (successor?: string) => {
    setBusy(true);
    try {
      await api.personas.remove(persona.id, successor);
      onDeleted();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Не удалось удалить персону.';
      // Дефолт-персона: бэк требует преемника — переключаемся на его выбор
      if (!successor && msg.includes('преемник')) setNeedSuccessor(true);
      else showToast('Персоны', msg);
    } finally {
      setBusy(false);
    }
  };

  if (!needSuccessor) {
    return (
      <ConfirmDialog
        title="Удалить персону?"
        subtitle={<>Персона «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{personaLabel(persona)}</strong>» будет удалена без возможности восстановления.</>}
        confirmLabel="Удалить"
        confirmVariant="danger"
        onConfirm={() => doDelete()}
        onCancel={onCancel}
      />
    );
  }

  return (
    <Modal
      title="Выберите преемника"
      subtitle={<>«{personaLabel(persona)}» — персона по умолчанию: прежде чем удалить её, назначьте, кто станет дефолтом вместо неё. Остаться без персоны по умолчанию нельзя.</>}
      onClose={onCancel}
      footer={
        <ModalActions
          confirmLabel="Назначить и удалить"
          confirmVariant="danger"
          confirmDisabled={!successorId}
          loading={busy}
          onConfirm={() => { if (successorId) void doDelete(successorId); }}
          onCancel={onCancel}
        />
      }
    >
      {candidates.length === 0 ? (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
          Подходящих преемников нет — сначала создайте другую персону
          {persona.scope === 'project' ? ' в этом проекте' : ''}.
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 320, overflowY: 'auto' }}>
          {candidates.map(p => {
            const active = p.id === successorId;
            return (
              <button
                key={p.id}
                onClick={() => setSuccessorId(p.id)}
                style={{
                  display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
                  padding: '7px 10px', borderRadius: R.lg, cursor: 'pointer', fontFamily: FONT.sans,
                  background: active ? C.accentLight : 'transparent',
                  border: `1px solid ${active ? C.accent : 'transparent'}`,
                }}
              >
                <PersonaAvatar persona={p} size={28} />
                <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personaLabel(p)}
                </span>
              </button>
            );
          })}
        </div>
      )}
    </Modal>
  );
}
