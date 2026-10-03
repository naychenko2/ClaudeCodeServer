// Подпись кнопки запуска двумя кусками (Р3): имя действия ужимается многоточием — на узком экране оно
// сокращается первым, — а «параметры · цена» не режутся никогда. Строит куски useActionRun, здесь — вёрстка.
import type { ActionRun } from '../../lib/chatContext/types';

export function RunLabel({ parts }: { parts: ActionRun['labelParts'] }) {
  return (
    <span data-run-label="" style={{ display: 'inline-flex', minWidth: 0, maxWidth: '100%' }}>
      <span data-run-label-name="" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{parts.name}</span>
      {parts.tail && <span data-run-label-tail="" style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>{parts.tail}</span>}
    </span>
  );
}
