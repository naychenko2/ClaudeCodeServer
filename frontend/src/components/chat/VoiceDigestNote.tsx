// Плашка «Коротко» — выжимка ответа, которую читает вслух озвучка (стиль digest).
//
// Показывается всегда, когда маркер есть в тексте, независимо от того, включена ли
// озвучка сейчас: маркер живёт в сохранённой истории (без него --resume вернул бы
// модели контекст без примера формата), а значит встречается и в старых ответах.
// Глазами это тоже полезно — вывод, не требующий вычитывать простыню.

import { AudioLines } from 'lucide-react';
import { C, FS, R } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Правило разбора ОДНО с озвучкой (extractVoiceDigest): что читают вслух, то и
// показываем. Два одинаковых regex разъехались бы на первой же правке, и лента с речью
// стали бы показывать разное.
export { extractVoiceDigest as parseVoiceDigest } from '../../lib/turnSpeechStream';

export function VoiceDigestNote({ text }: { text: string }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: 8,
      padding: '8px 10px', borderRadius: R.lg,
      // Тон легче пузыря ответа, а не плотнее: рамка без заливки. Второстепенный по
      // смыслу блок не должен выглядеть весомее главного
      border: `1px solid ${C.borderLight}`,
    }}>
      <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textMuted}
        style={{ flexShrink: 0, marginTop: 3 }} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 600, marginBottom: 2 }}>
          Коротко
        </div>
        {/* Размер и цвет — как у тела ответа: смысл плашки в том, чтобы длинный ответ
            можно было НЕ читать, а мелкий приглушённый текст к этому не располагает.
            wordBreak — против длинной ссылки в пересказе на узком экране (360 CSS) */}
        <div style={{ fontSize: FS.md, color: C.textPrimary, lineHeight: 1.45, wordBreak: 'break-word' }}>
          {text}
        </div>
      </div>
    </div>
  );
}
