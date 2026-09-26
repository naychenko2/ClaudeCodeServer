// Подсказки пустой ленты чата картинки (макет image-editor-v2): «Например:» и чипы,
// тап отправляет сообщение сразу

import { Sparkles } from 'lucide-react';
import { C, FS, SP } from '../../../lib/design';
import { Button } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';

export function ImageChatSuggestions({ items, disabled, onPick }: {
  items: string[];
  disabled?: boolean;
  onPick: (text: string) => void;
}) {
  return (
    <div data-image-chat-suggestions="" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs }}>
      <span style={{ fontSize: FS.xs, color: C.textMuted }}>Например:</span>
      {items.map(text => (
        <Button key={text} variant="ghostFilled" size="sm" pill disabled={disabled}
          leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => onPick(text)}>
          {text}
        </Button>
      ))}
    </div>
  );
}
