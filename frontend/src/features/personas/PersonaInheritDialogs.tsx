import { Button, ConfirmDialog, Modal } from '../../components/ui';
import { MODAL_W, SP } from '../../lib/design';

// Диалоги перехода группы прав формы персоны (макет persona-inheritance.html, P17/P25/P26).
// Только оформление: решения и состояние — в PersonaForm.

// Возврат «Как у специальности» из «Своё»: перечень отличий, чтобы человек видел, что потеряет
export function ReturnInheritDialog({ specialtyTitle, diffs, onKeep, onReturn }: {
  specialtyTitle: string;
  diffs: string[];
  onKeep: () => void;
  onReturn: () => void;
}) {
  const where = diffs.length === 1 ? '1 месте' : `${diffs.length} местах`;
  return (
    <ConfirmDialog
      title="Вернуть права как у специальности?"
      subtitle={`Ваши права отличаются от «${specialtyTitle}» в ${where}: ${diffs.join(', ')}. После возврата персона получит права специальности и будет менять их вместе с ней.`}
      confirmLabel="Вернуть"
      cancelLabel="Оставить своё"
      onConfirm={onReturn}
      onCancel={onKeep}
    />
  );
}

// Смена специальности при наследовании: права просто станут как у новой роли
export function SwitchSpecialtyInheritDialog({ specialtyTitle, rights, onApply, onCancel }: {
  specialtyTitle: string;
  rights: string;
  onApply: () => void;
  onCancel: () => void;
}) {
  return (
    <ConfirmDialog
      title={`Специальность — «${specialtyTitle}»`}
      subtitle={`Права и модели наследуются — они станут как у «${specialtyTitle}»${rights ? `: ${rights}` : ''}. Умения специальности заменятся, ваши отключения и свои умения останутся.`}
      confirmLabel="Сменить специальность"
      cancelLabel="Отмена"
      onConfirm={onApply}
      onCancel={onCancel}
    />
  );
}

// Смена специальности при своих правах: три исхода — отмена, оставить свои, перейти на шаблон
export function SwitchSpecialtyOwnDialog({ specialtyTitle, rights, onKeep, onTemplate, onCancel }: {
  specialtyTitle: string;
  rights: string;
  onKeep: () => void;
  onTemplate: () => void;
  onCancel: () => void;
}) {
  return (
    <Modal
      title={`Специальность — «${specialtyTitle}»`}
      subtitle={`Ваши права отличаются от шаблона новой специальности (${rights}). Оставить свои или перейти на новые? Умения специальности заменятся, ваши отключения и свои умения останутся.`}
      width={MODAL_W.confirm}
      onClose={onCancel}
      footer={
        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
          <Button variant="ghost" onClick={onCancel}>Отмена</Button>
          <Button variant="secondary" onClick={onKeep}>Оставить свои</Button>
          <Button variant="primary" onClick={onTemplate}>Перейти на новые</Button>
        </div>
      }
    />
  );
}
