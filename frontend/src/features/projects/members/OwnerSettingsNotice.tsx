// Плашки режима участника над рабочей областью проекта (S1-F6, карта v2 п.4.2):
// «Проект <владельца> — ходы идут по его настройкам и за его счёт» и, когда доступ
// отозван, предупреждение с возвратом «На „Домой“». Владельцу и обычному проекту — ничего.

import { Info, UserX } from 'lucide-react';
import { Notice } from '../../../components/ui/Notice';
import { Button } from '../../../components/ui/Button';
import { SP } from '../../../lib/design';
import type { Project } from '../../../types';
import { isParticipant, ownerNameOf } from './participantGate';

interface Props {
  project: Project;
  // Доступ отозван, пока проект открыт
  revoked?: boolean;
  onGoHome: () => void;
}

export function OwnerSettingsNotice({ project, revoked, onGoHome }: Props) {
  if (!isParticipant(project)) return null;
  const owner = ownerNameOf(project);
  const style = { margin: `${SP.xs}px ${SP.md}px` };
  if (revoked) {
    return (
      <Notice
        tone="warning" icon={UserX} style={style} data-testid="access-revoked-notice"
        action={<Button size="sm" variant="secondary" onClick={onGoHome}>На «Домой»</Button>}
      >
        {owner} закрыл вам доступ к проекту.
      </Notice>
    );
  }
  return (
    <Notice tone="info" icon={Info} style={style} data-testid="owner-settings-notice">
      Проект {owner} — ходы идут по его настройкам и за его счёт.
    </Notice>
  );
}
