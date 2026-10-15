import { useCallback, useState } from 'react';
import { ArrowLeft, ArrowRight, Gauge } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Button, Field, Modal, ModalActions, Notice, ProgressBar, TextArea } from '../ui';
import { formatContextK, submitContinue } from '../../lib/chatContinue';

// «Продолжить в новом чате» — три общих куска UI: диалог подтверждения, подсказка при
// большом контексте и плашки связи старого/нового чата. Логика запроса — в ChatPanel,
// сюда приходят готовые колбэки (компоненты показываются и в витрине UiKitPage).

interface DialogProps {
  // Выполняет запрос и переход; бросает Error — текст уйдёт в тост снаружи, диалог при этом
  // остаётся открытым, чтобы человек мог повторить
  onConfirm: (note: string) => Promise<void>;
  onClose: () => void;
}

export function ContinueChatDialog({ onConfirm, onClose }: DialogProps) {
  const [note, setNote] = useState('');
  const [running, setRunning] = useState(false);

  const handleConfirm = useCallback(async () => {
    if (running) return;
    setRunning(true);
    // Успех: владелец закрывает диалог и уходит в новый чат; отказ — снимаем блокировку
    if (!(await submitContinue(onConfirm, note))) setRunning(false);
  }, [running, onConfirm, note]);

  return (
    <Modal
      title="Продолжить в новом чате"
      width={MODAL_W.form}
      // Пока агент пишет записку, закрытие не отменяет запрос — не даём закрыть по ошибке
      onClose={running ? () => {} : onClose}
      closeOnBackdrop={!running}
      hideCloseButton={running}
      footer={
        <ModalActions
          confirmLabel={running ? 'Агент пишет записку…' : 'Продолжить'}
          onConfirm={handleConfirm}
          loading={running}
          confirmDisabled={running}
          cancelLabel="Отмена"
          onCancel={running ? () => {} : onClose}
        />
      }
    >
      <div style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
        Агент напишет записку-передачу — это один ход. Затем откроется новый чат той же персоны
        с этой запиской, а старый останется на месте со ссылкой на продолжение.
      </div>
      <Field label="Что делать дальше" hint="Необязательно. Попадёт в записку как задание новому чату.">
        <TextArea value={note} onChange={setNote} disabled={running} minHeight={72} autoFocus
          placeholder="Например: довести фронт до сборки и показать скриншоты" />
      </Field>
      {running && (
        <div role="status" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <ProgressBar value={0} indeterminate label="Агент пишет записку" />
          <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
            Ход длится десятки секунд, а иногда и дольше. Не закрывайте окно.
          </span>
        </div>
      )}
    </Modal>
  );
}

// Ненавязчивая плашка у поля ввода: контекст вырос выше порога
export function ContinueHint({ tokens, onOpen, onSnooze }: {
  tokens: number; onOpen: () => void; onSnooze: () => void;
}) {
  return (
    <Notice tone="info" icon={Gauge} data-testid="continue-hint"
      action={
        <div style={{ display: 'flex', gap: SP.xs, flexShrink: 0 }}>
          <Button size="sm" onClick={onOpen}>Продолжить</Button>
          <Button size="sm" variant="ghost" onClick={onSnooze}>Не сейчас</Button>
        </div>
      }>
      Контекст {formatContextK(tokens)}. Продолжить в новом чате? Это дешевле, и агент меньше путается.
    </Notice>
  );
}

// Плашка связи. into — внизу старого чата («Продолжено в новом чате →»),
// from — вверху нового («Продолжение чата <имя> ←»). Клик ведёт в связанный чат.
export function ContinuationBanner({ kind, name, onOpen }: {
  kind: 'into' | 'from'; name?: string; onOpen: () => void;
}) {
  const Arrow = kind === 'into' ? ArrowRight : ArrowLeft;
  const label = kind === 'into'
    ? 'Продолжено в новом чате'
    : `Продолжение чата${name ? ` «${name}»` : ''}`;
  return (
    <button type="button" onClick={onOpen} data-testid={`continuation-${kind}`}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, alignSelf: 'center',
        padding: `${SP.xs}px ${SP.md}px`, borderRadius: R.max,
        border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textSecondary,
        fontFamily: FONT.sans, fontSize: FS.sm, cursor: 'pointer',
      }}>
      {kind === 'from' && <Arrow size={13} strokeWidth={2} aria-hidden />}
      {label}
      {kind === 'into' && <Arrow size={13} strokeWidth={2} aria-hidden />}
    </button>
  );
}
