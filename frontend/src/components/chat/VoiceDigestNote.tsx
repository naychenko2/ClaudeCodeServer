// Плашка «Коротко» — краткая выдержка длинного ответа.
//
// Показывается всегда, когда маркер есть в тексте, независимо от режима озвучки: выдержку
// просит отдельная секция промпта у ЛЮБОГО длинного ответа (человек написал вопрос руками
// и хочет видеть суть, не вычитывая простыню), а в голосовом режиме её же читают вслух.
// Маркер живёт в сохранённой истории (без него --resume вернул бы модели контекст без
// примера формата), поэтому плашка есть и у старых ответов.
//
// Тезис может начинаться с пометки типа ([+] сделано / [!] риск / [>] осталось) — её
// рисуем значком, а не текстом: список глазами сортируется раньше, чем прочитан. Пометки
// нет — пункт живёт с точкой, как до значков (старые ответы, забывчивость модели).
//
// Из markdown внутри плашки живёт только жирный — им модель помечает одну-две опоры в
// строке (промпт VoicePrompts.LongAnswerSectionText). Остальная разметка блоку запрещена,
// поэтому полноценный рендер markdown сюда не подключён.
//
// Кнопка-динамик — РАЗОВОЕ чтение: режим озвучки не включается, микрофон не трогается.
// Ровно тот случай «иногда, но не всегда», ради которого она и заведена.

import { useContext, useEffect, useState } from 'react';
import { AudioLines, Volume2, Square, Check, AlertTriangle, ArrowRight } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { IconButton } from '../ui';
import { speak, stopSpeaking, primeAudio } from '../../lib/tts';
import { splitVoiceDigest, splitBoldSpans, splitBulletKind, type BulletKind } from '../../lib/turnSpeechStream';
import { ChatSessionContext } from './contexts';

// Правило разбора ОДНО с озвучкой (extractVoiceDigest): что читают вслух, то и
// показываем. Два одинаковых regex разъехались бы на первой же правке, и лента с речью
// стали бы показывать разное.
export { extractVoiceDigest as parseVoiceDigest } from '../../lib/turnSpeechStream';

// Строка выжимки с жирными опорами: разбор общий с речью (splitBoldSpans), здесь только
// показ. Жирным модель помечает предмет и действие — то, за что глаз цепляется, пробегая
// плашку. Ключ по индексу безопасен: список сегментов не переставляется и не растёт, он
// целиком пересобирается из строки.
function DigestLine({ text }: { text: string }) {
  return (
    <>
      {splitBoldSpans(text).map((part, i) => part.bold
        ? <strong key={i} style={{ fontWeight: 600 }}>{part.text}</strong>
        : <span key={i}>{part.text}</span>)}
    </>
  );
}

// Значок типа тезиса. Цветом выделен только «риск»: accent-дисциплина продукта — акцент
// достаётся тому, на что и правда надо посмотреть, а «сделано» и «осталось» держат строй
// приглушёнными. Пометки нет (старый ответ, модель забыла) — точка, то есть ровно тот вид,
// что был у списка до значков.
function BulletIcon({ kind }: { kind: BulletKind | null }) {
  const common = { size: ICON_SIZE.xs, strokeWidth: 2.2, style: { flexShrink: 0, marginTop: 3 } } as const;
  if (kind === 'done') return <Check {...common} color={C.textMuted} />;
  if (kind === 'risk') return <AlertTriangle {...common} color={C.warning} />;
  if (kind === 'next') return <ArrowRight {...common} color={C.textMuted} />;
  return (
    <span aria-hidden style={{
      flexShrink: 0, width: ICON_SIZE.xs, textAlign: 'center', color: C.textMuted, lineHeight: 1.45,
    }}>•</span>
  );
}

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
          {/* Вывод — курсивом: он отделён от фактов под ним не отступом, а начертанием,
              и на глаз сразу читается как «итог», а не как ещё один пункт списка */}
          {lead && <div style={{ fontStyle: 'italic' }}><DigestLine text={lead} /></div>}
          {bullets.length > 0 && (
            <ul style={{
              margin: lead ? `${SP.xs}px 0 0` : 0,
              /* Дисковые маркеры сняты: их место занял значок типа, а два маркера подряд
                 (точка и галочка) читались бы как вложенность, которой тут нет */
              padding: 0, listStyle: 'none',
            }}>
              {bullets.map((b, i) => {
                const { kind, text: body } = splitBulletKind(b);
                return (
                  <li key={i} style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: i ? 3 : 0 }}>
                    <BulletIcon kind={kind} />
                    <span style={{ minWidth: 0 }}><DigestLine text={body} /></span>
                  </li>
                );
              })}
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
