import { useEffect, useMemo, useRef, useState } from 'react';
import { Volume2, ChevronDown } from 'lucide-react';
import { C, FS, SP, R, FIELD, FONT, SHADOW } from '../../lib/design';
import { Button, IconButton, Menu, MenuItem, InlineSegmented } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { api } from '../../lib/api';
import { previewVoice, isSpeaking, VoicePreviewError } from '../../lib/tts';
import type { PersonaVoice, TtsVoiceInfo } from '../../types';

// Выбор голоса персоны: список голосов с прослушиванием, настроение и скорость.
//
// Голоса выбирают УШАМИ, а не глазами: имя вроде madi_ru человеку не говорит ничего,
// поэтому у каждой строки своя кнопка прослушивания, а подписи описывают тембр.
//
// Прослушивание идёт мимо обычной озвучки (previewVoice): фолбэк на голос браузера здесь
// был бы прямой ложью — человек решил бы, что персона так и звучит. И оно запрещено, пока
// озвучивается ответ в чате: форма живёт в воркспейсе рядом, а общий стоп оборвал бы ход
// и подсунул петле разговора ложное «озвучка закончилась».

// Скорость: три ступени вместо ползунка 0.1–3.0 — разницу в сотых на слух не поймать
const SPEEDS = [
  { value: 'slow' as const, label: 'медленно', speed: 0.85 },
  { value: 'normal' as const, label: 'обычно', speed: 1.0 },
  { value: 'fast' as const, label: 'быстро', speed: 1.15 },
];

// Подписи амплуа: ключи API человеку не показываем
const ROLE_LABELS: Record<string, string> = {
  good: 'доброе',
  evil: 'злое',
  strict: 'строгое',
  friendly: 'дружелюбное',
  whisper: 'шёпот',
};

// Активный сегмент — нейтральной плашкой, а не акцентом: оранжевого в карточке персоны и
// так хватает (выбранная строка голоса плюс «Сохранить» в шапке)
const SEGMENT_TONE = { bg: C.bgWhite, fg: C.textPrimary };

// Ширина списка — под самую длинную подпись голоса; высота — чтобы список не занимал
// экран целиком и оставлял видимой строку выбора
const LIST_MIN_WIDTH = 280;
const LIST_MAX_HEIGHT = 320;

// «Звук идёт» — те же полоски-эквалайзер, которыми композер показывает запись голоса:
// знак в продукте уже знакомый, и он влезает в кнопку. WaitingIndicator сюда не годится
// вовсе — это индикатор ЛЕНТЫ, с логотипом, аватаром персоны и печатающимся текстом:
// внутри кнопки-иконки он раскрывался в целый блок не на своём месте.
function PlayingBars({ height }: { height: number }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2, height }} aria-hidden>
      {[0, 0.15, 0.3].map(delay => (
        <span key={delay} className="cc-wave-bar" style={{ height, animationDelay: `${delay}s` }} />
      ))}
    </span>
  );
}

// Голос не выбран — «обычная» скорость, а не «ничего не выбрано»: пустой ряд читается как
// сломанный контрол, хотя персона в этот момент говорит именно с обычной скоростью
function speedKey(speed?: number | null): 'slow' | 'normal' | 'fast' {
  if (speed == null) return 'normal';
  if (speed < 0.95) return 'slow';
  return speed > 1.05 ? 'fast' : 'normal';
}

export function PersonaVoicePicker({ value, onChange, describe }: {
  value: PersonaVoice | null;
  onChange: (v: PersonaVoice | null) => void;
  // Чем персона является ПРЯМО СЕЙЧАС в форме (несохранённое тоже) — для подбора голоса
  describe?: () => { name?: string; role?: string; description?: string; character?: string; tone?: string };
}) {
  const isMobile = useIsMobile();
  const [catalog, setCatalog] = useState<TtsVoiceInfo[] | null>(null);
  const [configured, setConfigured] = useState(true);
  const [failed, setFailed] = useState(false);
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const [focused, setFocused] = useState(false);
  const [playing, setPlaying] = useState<string | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // Флаг поднимается на КАЖДОМ монтировании, а не только на первом: в dev React прогоняет
  // эффекты дважды (mount → unmount → mount), и без этого второй экземпляр считал бы себя
  // мёртвым и молча выбрасывал уже полученный каталог
  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; };
  }, []);

  useEffect(() => {
    void (async () => {
      try {
        const res = await api.tts.voices();
        if (!alive.current) return;
        setCatalog(res.voices);
        setConfigured(res.configured);
      } catch {
        if (alive.current) { setCatalog([]); setFailed(true); }
      }
    })();
  }, []);

  const selected = useMemo(
    () => catalog?.find(v => v.voice === value?.voice) ?? null,
    [catalog, value?.voice],
  );
  const roles = selected?.roles ?? [];
  const speed = SPEEDS.find(s => s.value === speedKey(value?.speed))?.speed;

  // voice не задан — слушаем голос по умолчанию: сравнивают-то как раз с ним
  const listen = async (voice?: string, role?: string) => {
    if (playing) return; // каждое нажатие — оплаченный запрос, очередь тут ни к чему
    if (isSpeaking()) { setError('Идёт озвучка ответа — дождитесь конца или остановите её'); return; }
    setError(null);
    setPlaying(voice ?? 'default');
    try {
      await previewVoice(voice ?? '', role, voice ? speed : undefined);
    } catch (e) {
      if (alive.current) setError(e instanceof VoicePreviewError ? e.message : 'Не удалось послушать голос');
    } finally {
      if (alive.current) setPlaying(null);
    }
  };

  const suggest = async () => {
    if (!describe || suggesting) return;
    setError(null);
    setSuggesting(true);
    try {
      const res = await api.personas.suggestVoice(describe());
      if (!alive.current) return;
      // Модель могла честно не выбрать никого — это ответ, а не ошибка
      if (!res.voice) { setError('Модель не выбрала голос — выберите сами'); return; }
      onChange({ voice: res.voice, role: res.role ?? undefined, speed: value?.speed ?? null });
    } catch (e) {
      if (alive.current) setError(e instanceof Error ? e.message : 'Не удалось подобрать голос');
    } finally {
      if (alive.current) setSuggesting(false);
    }
  };

  const pick = (voice: TtsVoiceInfo) => {
    setAnchor(null);
    // Амплуа у каждого голоса своё: при смене голоса прежнее просто не существует
    onChange({ voice: voice.voice, role: undefined, speed: value?.speed ?? null });
  };

  const openList = () => {
    if (!catalog?.length) return;
    setAnchor(triggerRef.current?.getBoundingClientRect() ?? null);
  };

  const previewIcon = (key: string) => playing === key
    ? <PlayingBars height={ICON_SIZE.sm} />
    : <Volume2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      {/* Строка выбора — по шкале полей формы, иначе читается как чужой контрол */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <button
          ref={triggerRef}
          type="button"
          onClick={openList}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          disabled={!catalog?.length}
          style={{
            flex: 1, minWidth: 0, boxSizing: 'border-box',
            display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.xs,
            background: FIELD.background,
            border: `1px solid ${focused || anchor ? FIELD.borderFocus : C.border}`,
            borderRadius: R.xl, padding: '10px 13px',
            fontSize: FIELD.fontSize, fontFamily: FONT.sans,
            color: catalog?.length ? FIELD.color : C.textMuted,
            outline: 'none', cursor: catalog?.length ? 'pointer' : 'default', textAlign: 'left',
            boxShadow: focused ? SHADOW.focus : 'none',
            transition: 'border-color 0.15s, box-shadow 0.15s',
          }}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {selected ? selected.label : 'Голос по умолчанию'}
          </span>
          <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} />
        </button>

        <IconButton
          title="Послушать"
          ariaLabel={selected ? `Послушать голос: ${selected.label}` : 'Послушать голос по умолчанию'}
          size={isMobile ? 'lg' : 'md'}
          disabled={!!playing || !configured}
          onClick={() => void listen(selected?.voice, value?.role ?? undefined)}
        >
          {previewIcon(selected?.voice ?? 'default')}
        </IconButton>
      </div>

      {anchor && catalog && (
        <Menu onClose={() => setAnchor(null)} anchor={anchor} minWidth={LIST_MIN_WIDTH} maxHeight={LIST_MAX_HEIGHT}>
          <VoiceRows
            catalog={catalog}
            selected={value?.voice}
            playing={playing}
            configured={configured}
            isMobile={isMobile}
            onPick={pick}
            onListen={(v) => void listen(v)}
          />
        </Menu>
      )}

      {/* Настроение — только у голосов, которые его умеют */}
      {selected && roles.length > 0 && (
        <SegmentRow label="Настроение" isMobile={isMobile}>
          <InlineSegmented
            isMobile={isMobile}
            value={value?.role ?? ''}
            options={[
              { value: '', label: 'обычное', tone: SEGMENT_TONE },
              ...roles.map(r => ({ value: r, label: ROLE_LABELS[r] ?? r, tone: SEGMENT_TONE })),
            ]}
            onChange={(r) => onChange({ ...value, voice: value?.voice, role: r || undefined })}
          />
        </SegmentRow>
      )}

      {selected && (
        <SegmentRow label="Скорость" isMobile={isMobile}>
          <InlineSegmented
            isMobile={isMobile}
            value={speedKey(value?.speed)}
            options={SPEEDS.map(s => ({ value: s.value, label: s.label, tone: SEGMENT_TONE }))}
            onChange={(k) => onChange({
              ...value, voice: value?.voice,
              speed: SPEEDS.find(s => s.value === k)?.speed ?? null,
            })}
          />
        </SegmentRow>
      )}

      {/* Подбор и сброс — вторичные действия: главное в этой карточке «Сохранить» */}
      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
        {describe && (
          <Button variant="ghostAccent" size="sm" loading={suggesting} onClick={() => void suggest()}>
            ✨ Подобрать
          </Button>
        )}
        {selected && (
          <Button variant="ghost" size="sm" onClick={() => onChange(null)}>Сбросить</Button>
        )}
      </div>

      {failed && (
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>Не удалось загрузить список голосов.</div>
      )}
      {!configured && !failed && (
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>
          Озвучка на сервере не настроена — послушать голоса нельзя.
        </div>
      )}
      {error && <div style={{ fontSize: FS.xs, color: C.danger }}>{error}</div>}
    </div>
  );
}

// Подпись и переключатель. На узком экране подпись уходит наверх, а трек получает
// горизонтальную прокрутку: InlineSegmented не переносится и не сжимается, поэтому
// пять амплуа рядом с подписью вылезали бы за кромку 320px
function SegmentRow({ label, isMobile, children }: {
  label: string;
  isMobile: boolean;
  children: React.ReactNode;
}) {
  return (
    <div style={{
      display: 'flex', gap: isMobile ? SP.xxs : SP.xs,
      flexDirection: isMobile ? 'column' : 'row',
      alignItems: isMobile ? 'stretch' : 'center',
    }}>
      <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{label}</span>
      <div style={{ minWidth: 0, overflowX: 'auto' }}>{children}</div>
    </div>
  );
}

// Строки списка: группировка по полу — 15 голосов подряд читаются хуже, чем два блока
function VoiceRows({ catalog, selected, playing, configured, isMobile, onPick, onListen }: {
  catalog: TtsVoiceInfo[];
  selected?: string;
  playing: string | null;
  configured: boolean;
  isMobile: boolean;
  onPick: (v: TtsVoiceInfo) => void;
  onListen: (voice: string) => void;
}) {
  const groups = [
    { title: 'Женские', items: catalog.filter(v => v.gender === 'female') },
    { title: 'Мужские', items: catalog.filter(v => v.gender === 'male') },
  ];

  return (
    <>
      {groups.map(g => g.items.length > 0 && (
        <div key={g.title}>
          <div style={{ padding: `${SP.xs}px ${SP.sm}px ${SP.xxs}px`, fontSize: FS.xs, color: C.textMuted }}>
            {g.title}
          </div>
          {g.items.map(v => (
            <div
              key={v.voice}
              style={{ background: v.voice === selected ? C.accentLight : undefined, borderRadius: R.md }}
            >
              <MenuItem
                label={
                  <span style={{ color: v.voice === selected ? C.accent : undefined }}>{v.label}</span>
                }
                onClick={() => onPick(v)}
                // Прослушивание — второе действие строки: в списке сравнивают ЧИСТЫЙ тембр,
                // поэтому без амплуа (выбранное настроение проверяется кнопкой снаружи)
                action={{
                  icon: playing === v.voice
                    ? <PlayingBars height={isMobile ? ICON_SIZE.sm : ICON_SIZE.xs} />
                    : <Volume2 size={isMobile ? ICON_SIZE.sm : ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
                  title: configured ? `Послушать: ${v.label}` : 'Озвучка не настроена',
                  onClick: () => { if (!playing && configured) onListen(v.voice); },
                }}
              />
            </div>
          ))}
        </div>
      ))}
    </>
  );
}
