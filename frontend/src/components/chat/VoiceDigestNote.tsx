// Плашка «Коротко» — краткая выдержка длинного ответа.
//
// Показывается всегда, когда маркер есть в тексте, независимо от режима озвучки: выдержку
// просит отдельная секция промпта у ЛЮБОГО длинного ответа (человек написал вопрос руками
// и хочет видеть суть, не вычитывая простыню), а в голосовом режиме её же читают вслух.
// Маркер живёт в сохранённой истории (без него --resume вернул бы модели контекст без
// примера формата), поэтому плашка есть и у старых ответов.
//
// Кнопка-динамик — РАЗОВОЕ чтение: режим озвучки не включается, микрофон не трогается.
// Ровно тот случай «иногда, но не всегда», ради которого она и заведена.

import { useContext, useEffect, useState } from 'react';
import { AudioLines, Volume2, Square } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { IconButton } from '../ui';
import { speak, stopSpeaking, primeAudio } from '../../lib/tts';
import { splitVoiceDigest } from '../../lib/turnSpeechStream';
import { ChatSessionContext } from './contexts';

// Правило разбора ОДНО с озвучкой (extractVoiceDigest): что читают вслух, то и
// показываем. Два одинаковых regex разъехались бы на первой же правке, и лента с речью
// стали бы показывать разное.
export { extractVoiceDigest as parseVoiceDigest } from '../../lib/turnSpeechStream';

export function VoiceDigestNote({ text, personaId }: { text: string; personaId?: string }) {
  const [playing, setPlaying] = useState(false);
  // Вывод и тезисы. Вслух при этом читается СЫРОЙ text (санитайзер речи сам срежет
  // дефисы в начале строк): разбор — дело показа, а не произношения
  const { lead, bullets } = splitVoiceDigest(text);
  // Чат берём из контекста ленты: разовое чтение — такие же деньги, как озвучка хода,
  // и в аналитике трат оно должно лечь на свой чат
  const sessionId = useContext(ChatSessionContext);

  // Звук переживает размонтирование (лента перерисовывается на каждой дельте соседнего
  // хода), поэтому гасим его явно при уходе — но только СВОЁ чтение
  useEffect(() => () => { if (playing) stopSpeaking(); }, [playing]);

  const toggle = () => {
    if (playing) { stopSpeaking(); setPlaying(false); return; }
    // Политика autoplay: разбудить аудио надо внутри самого жеста, до любого await
    primeAudio();
    setPlaying(true);
    void speak(text, personaId, sessionId ?? undefined).finally(() => setPlaying(false));
  };

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
          {lead && <div>{lead}</div>}
          {bullets.length > 0 && (
            /* Отступ списка небольшой: плашка стоит под ответом и вложенности не изображает */
            <ul style={{ margin: lead ? `${SP.xs}px 0 0` : 0, paddingLeft: SP.lg }}>
              {bullets.map((b, i) => <li key={i}>{b}</li>)}
            </ul>
          )}
        </div>
      </div>
      <IconButton
        onClick={toggle}
        title={playing ? 'Остановить чтение' : 'Прочитать вслух'}
        active={playing}
        size="sm"
        style={{ flexShrink: 0 }}
      >
        {playing
          ? <Square size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <Volume2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      </IconButton>
    </div>
  );
}
