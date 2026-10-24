import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Volume2, ChevronDown, Search, Sparkles } from 'lucide-react';
import { C, FS, SP, R, FIELD, FONT, SHADOW } from '../../lib/design';
import { Button, Chip, IconButton, IconField, Menu, MenuItem, Modal, InlineSegmented } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { api } from '../../lib/api';
import { previewVoice, isSpeaking, VoicePreviewError } from '../../lib/tts';
import type { PersonaVoice, TtsVoiceInfo } from '../../types';
import {
  LIVE_TTS_GROUP, filterVoices, isLiveTtsVoice, voiceName, voiceRoleLabel, voiceSupportsSpeed, voiceTrait,
  type VoiceGenderFilter,
} from './voiceLabels';
import { VoicePickPanel, type PersonaDescription } from './VoicePickPanel';
import { PlayingBars } from './PlayingBars';

// Выбор голоса персоны: список голосов с прослушиванием, амплуа и скорость.
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

// Активный сегмент — нейтральной плашкой, а не акцентом: оранжевого в карточке персоны и
// так хватает (выбранная строка голоса плюс «Сохранить» в шапке)
const SEGMENT_TONE = { bg: C.bgWhite, fg: C.textPrimary };

// Ширина списка — ровно по полю (длинный характер переносится, а не раздвигает попап под
// кнопку ▶); высота — чтобы список не занимал экран целиком и оставлял видимой строку выбора
const LIST_MAX_HEIGHT = 440;

// Голос инстанса — персона без своего голоса
const DEFAULT_LABEL = 'Голос по умолчанию';

const GENDERS: readonly { key: VoiceGenderFilter; label: string }[] = [
  { key: 'all', label: 'Все' },
  { key: 'female', label: 'Женские' },
  { key: 'male', label: 'Мужские' },
];

// Амплуа в подписи: пустое — «обычное», а не пустота
const roleLabel = (role?: string | null) => (role ? voiceRoleLabel(role) : 'обычное');

// Голос не выбран — «обычная» скорость, а не «ничего не выбрано»: пустой ряд читается как
// сломанный контрол, хотя персона в этот момент говорит именно с обычной скоростью
function speedKey(speed?: number | null): 'slow' | 'normal' | 'fast' {
  if (speed == null) return 'normal';
  if (speed < 0.95) return 'slow';
  return speed > 1.05 ? 'fast' : 'normal';
}

export function PersonaVoicePicker({ value, onChange, emptyLabel, describe, greeting, onPickPanelChange }: {
  value: PersonaVoice | null;
  onChange: (v: PersonaVoice | null) => void;
  // Подпись строки, пока голос не выбран (по умолчанию — «Голос по умолчанию»)
  emptyLabel?: string;
  // Чем персона является ПРЯМО СЕЙЧАС в форме (несохранённое тоже) — для подбора голоса
  describe?: () => PersonaDescription;
  // Приветствие персоны из формы: кандидатов подбора слушают её же фразой
  greeting?: string;
  // Открыта ли панель подбора: форма прячет подсказку поля, иначе она уезжает под панель
  // и отрывается от самого поля
  onPickPanelChange?: (open: boolean) => void;
}) {
  const isMobile = useIsMobile();
  const [catalog, setCatalog] = useState<TtsVoiceInfo[] | null>(null);
  const [configured, setConfigured] = useState(true);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const [focused, setFocused] = useState(false);
  const [playing, setPlaying] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [gender, setGender] = useState<VoiceGenderFilter>('all');
  // Подбор под персону: ключ перезапускает панель (повторное нажатие — новый подбор)
  const [pickKey, setPickKey] = useState(0);
  const [pickBusy, setPickBusy] = useState(false);
  const alive = useRef(true);
  const pickOpen = !!describe && pickKey > 0;
  useEffect(() => { onPickPanelChange?.(pickOpen); }, [pickOpen, onPickPanelChange]);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

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

  // Открытый список сразу показывает выбранный голос с его амплуа, а не верх каталога.
  // В шторке не крутим: прокрутка увела бы заголовок, а выбранный и так недалеко
  useLayoutEffect(() => {
    if (!open || isMobile) return;
    listRef.current?.querySelector('[data-selected]')?.scrollIntoView({ block: 'center' });
  }, [open, isMobile]);

  const selected = useMemo(
    () => catalog?.find(v => v.voice === value?.voice) ?? null,
    [catalog, value?.voice],
  );
  const speed = SPEEDS.find(s => s.value === speedKey(value?.speed))?.speed;

  // voice не задан — слушаем голос по умолчанию: сравнивают-то как раз с ним
  const listen = async (voice?: string, role?: string) => {
    if (playing) return; // каждое нажатие — оплаченный запрос, очередь тут ни к чему
    if (isSpeaking()) { setError('Идёт озвучка ответа — дождитесь конца или остановите её'); return; }
    setError(null);
    setPlaying(voice ?? 'default');
    // Темп шлём только голосу, который его умеет: LiveTTS сервер всё равно его не отправит
    const withSpeed = voice && voiceSupportsSpeed(catalog?.find(v => v.voice === voice));
    try {
      await previewVoice(voice ?? '', role, withSpeed ? speed : undefined);
    } catch (e) {
      if (alive.current) setError(e instanceof VoicePreviewError ? e.message : 'Не удалось послушать голос');
    } finally {
      if (alive.current) setPlaying(null);
    }
  };

  const close = () => { setOpen(false); setAnchor(null); };

  const openList = () => {
    if (!catalog?.length) return;
    setAnchor(triggerRef.current?.getBoundingClientRect() ?? null);
    setOpen(true);
  };

  // Голос с амплуа выбирается с «обычным» и оставляет список открытым — амплуа тут же,
  // под строкой. Голос без амплуа выбирать больше нечего — список закрывается сразу.
  // Амплуа у каждого голоса своё: при смене голоса прежнее просто не существует
  const pickVoice = (v: TtsVoiceInfo) => {
    onChange({ voice: v.voice, role: undefined, speed: value?.speed ?? null });
    if (v.roles.length === 0) close();
  };
  const pickRole = (v: TtsVoiceInfo, role: string) => {
    onChange({ voice: v.voice, role: role || undefined, speed: value?.speed ?? null });
    close();
  };

  const previewIcon = (key: string) => playing === key
    ? <PlayingBars height={ICON_SIZE.sm} />
    : <Volume2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;

  // Вторичное действие: главное в этой карточке «Сохранить»
  const pickButton = (
    <Button
      variant="ghostAccent"
      size="sm"
      loading={pickBusy}
      disabled={!catalog?.length}
      leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      onClick={() => { if (pickBusy) return; setPickBusy(true); setPickKey(k => k + 1); }}
    >
      Подобрать под персону
    </Button>
  );

  const items = catalog ? filterVoices(catalog, query, gender) : [];
  const resetFilters = () => { setQuery(''); setGender('all'); };

  // Шапка списка — вне прокрутки: на десктопе слотом header у Menu, в шторке — у Modal
  const header = (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm,
      padding: isMobile ? 0 : `${SP.xs}px ${SP.xs}px ${SP.sm}px`,
    }}>
      <IconField
        icon={<Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        value={query}
        onChange={setQuery}
        // Коротко: попап шириной с поле (~270 px), длиннее — режется посреди слова
        placeholder="Имя или характер"
        height={isMobile ? 44 : 36}
        radius={R.lg}
        fontSize={FS.md}
      />
      <div style={{ display: 'flex', gap: SP.xs }}>
        {GENDERS.map(g => (
          <Chip key={g.key} variant="toggle" large={isMobile} selected={gender === g.key} onClick={() => setGender(g.key)}>
            {g.label}
          </Chip>
        ))}
      </div>
    </div>
  );

  const body = (
    <div ref={listRef}>
      {items.length === 0 ? (
        <div style={{
          padding: `${SP.lg}px ${SP.md}px`, textAlign: 'center',
          display: 'flex', flexDirection: 'column', gap: SP.sm, alignItems: 'center',
        }}>
          <span style={{ fontSize: FS.base, color: C.textSecondary }}>
            {query.trim() ? `Ничего не нашлось по «${query.trim()}»` : 'Голосов с таким фильтром нет'}
          </span>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Ищем по имени и характеру голоса</span>
          <Button variant="ghost" size="sm" onClick={resetFilters}>Сбросить поиск</Button>
        </div>
      ) : (
        <>
          {/* Явный выбор голоса инстанса: при создании он отменяет подбор голоса сервером.
              Подсветка — как у выбранной строки; пока голос лишь «подберётся», не горит */}
          {!query.trim() && gender === 'all' && (
            <div style={{ background: !value?.voice && !emptyLabel ? C.accentLight : undefined, borderRadius: R.md }}>
              <MenuItem
                isMobile={isMobile}
                label={<span style={{ color: !value?.voice && !emptyLabel ? C.accent : undefined }}>{DEFAULT_LABEL}</span>}
                hint="голос, который задан на сервере"
                hintWrap
                onClick={() => { close(); onChange(null); }}
              />
            </div>
          )}
          {renderGroup('Обычные', null, items.filter(v => !isLiveTtsVoice(v)))}
          {renderGroup(LIVE_TTS_GROUP, 'темп у этих голосов не настраивается', items.filter(isLiveTtsVoice))}
        </>
      )}
    </div>
  );

  // Группа по модели: цена LiveTTS — один раз в заголовке, а не бейджем в каждой строке.
  // Функция отрисовки, а не компонент: компонент, объявленный в теле, пересоздавался бы
  // на каждом рендере и сбрасывал наведение строк
  function renderGroup(title: string, hint: string | null, group: TtsVoiceInfo[]) {
    if (group.length === 0) return null;
    return (
      <div key={title}>
        <div style={{ padding: `${SP.md}px ${SP.md}px ${SP.xxs}px`, fontSize: FS.xs, color: C.textMuted, fontWeight: 600 }}>
          {title}
          {hint && <div style={{ fontWeight: 400 }}>{hint}</div>}
        </div>
        {group.map(v => (
          <VoiceRow
            key={v.voice}
            v={v}
            on={v.voice === value?.voice}
            role={value?.role}
            isMobile={isMobile}
            playing={playing}
            configured={configured}
            onPick={() => pickVoice(v)}
            onRole={(r) => pickRole(v, r)}
            onListen={() => void listen(v.voice)}
          />
        ))}
      </div>
    );
  }

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
            border: `1px solid ${focused || open ? FIELD.borderFocus : C.border}`,
            borderRadius: R.xl, padding: '10px 13px',
            fontSize: FIELD.fontSize, fontFamily: FONT.sans,
            color: catalog?.length ? FIELD.color : C.textMuted,
            outline: 'none', cursor: catalog?.length ? 'pointer' : 'default', textAlign: 'left',
            boxShadow: focused ? SHADOW.focus : 'none',
            transition: 'border-color 0.15s, box-shadow 0.15s',
          }}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {selected ? (
              <>
                {voiceName(selected)}
                {/* Выбранное амплуа — в самом поле: отдельного ряда «Настроение» больше нет */}
                {selected.roles.length > 0 && <span style={{ color: C.textMuted }}> · {roleLabel(value?.role)}</span>}
              </>
            ) : (emptyLabel ?? DEFAULT_LABEL)}
          </span>
          <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} />
        </button>

        <IconButton
          title="Послушать"
          ariaLabel={selected ? `Послушать голос: ${voiceName(selected)}` : 'Послушать голос по умолчанию'}
          size={isMobile ? 'lg' : 'md'}
          disabled={!!playing || !configured}
          onClick={() => void listen(selected?.voice, value?.role ?? undefined)}
        >
          {previewIcon(selected?.voice ?? 'default')}
        </IconButton>
        {describe && !isMobile && pickButton}
      </div>

      {/* На узком экране кнопке подбора рядом с полем не хватает места — под ним */}
      {describe && isMobile && <div>{pickButton}</div>}

      {open && catalog && !isMobile && anchor && (
        <Menu
          onClose={close}
          anchor={anchor}
          anchorAlign="start"
          minWidth={anchor.width}
          maxWidth={anchor.width}
          maxHeight={LIST_MAX_HEIGHT}
          header={header}
        >
          {body}
        </Menu>
      )}
      {open && catalog && isMobile && (
        <Modal title="Голос персоны" onClose={close} header={header}
          footer={<Button variant="primary" fullWidth onClick={close}>Готово</Button>}>
          {body}
        </Modal>
      )}

      {/* Темп — только у голосов, которые его умеют; сохранённый темп остаётся в персоне:
          вернётся обычный голос — вернётся и он */}
      {selected && voiceSupportsSpeed(selected) && (
        <div style={{
          display: 'flex', gap: isMobile ? SP.xxs : SP.xs,
          flexDirection: isMobile ? 'column' : 'row', alignItems: isMobile ? 'stretch' : 'center',
        }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>Скорость</span>
          <div style={{ minWidth: 0, overflowX: 'auto' }}>
            <InlineSegmented
              isMobile={isMobile}
              value={speedKey(value?.speed)}
              options={SPEEDS.map(s => ({ value: s.value, label: s.label, tone: SEGMENT_TONE }))}
              onChange={(k) => onChange({
                ...value, voice: value?.voice,
                speed: SPEEDS.find(s => s.value === k)?.speed ?? null,
              })}
            />
          </div>
        </div>
      )}

      {describe && pickKey > 0 && catalog && (
        <VoicePickPanel
          key={pickKey}
          describe={describe}
          greeting={greeting}
          catalog={catalog}
          value={value}
          configured={configured}
          isMobile={isMobile}
          // Темп переносим только на голос, который его умеет: у LiveTTS он был бы мёртвым
          // значением в персоне
          onPick={(voice, role) => onChange({
            voice, role: role ?? undefined,
            speed: voiceSupportsSpeed(catalog.find(v => v.voice === voice)) ? value?.speed ?? null : null,
          })}
          onOpenList={openList}
          onClose={() => setPickKey(0)}
          onBusy={setPickBusy}
        />
      )}

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

// Строка голоса в две строки: имя и характер. У выбранного — амплуа чипами прямо под
// строкой: голос и подача выбираются за один заход, и видно, что этот голос умеет
function VoiceRow({ v, on, role, isMobile, playing, configured, onPick, onRole, onListen }: {
  v: TtsVoiceInfo;
  on: boolean;
  role?: string;
  isMobile: boolean;
  playing: string | null;
  configured: boolean;
  onPick: () => void;
  onRole: (role: string) => void;
  onListen: () => void;
}): ReactNode {
  const iconSize = isMobile ? ICON_SIZE.sm : ICON_SIZE.xs;
  return (
    <div
      data-selected={on || undefined}
      style={{ background: on ? C.accentLight : undefined, borderRadius: R.md, paddingBottom: on && v.roles.length ? SP.sm : 0 }}
    >
      <MenuItem
        isMobile={isMobile}
        label={<span style={{ color: on ? C.accent : undefined, fontWeight: on ? 600 : undefined }}>{voiceName(v)}</span>}
        hint={voiceTrait(v)}
        // Попап шириной ровно с поле: характер переносится, а не режется многоточием
        hintWrap
        onClick={onPick}
        // Прослушивание — второе действие строки: в списке сравнивают ЧИСТЫЙ тембр,
        // поэтому без амплуа (выбранное амплуа проверяется кнопкой у поля)
        action={{
          icon: playing === v.voice
            ? <PlayingBars height={iconSize} />
            : <Volume2 size={iconSize} strokeWidth={ICON_STROKE} />,
          title: configured ? `Послушать: ${voiceName(v)}` : 'Озвучка не настроена',
          onClick: () => { if (!playing && configured) onListen(); },
        }}
      />
      {on && v.roles.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, padding: `0 ${SP.md}px` }}>
          {['', ...v.roles].map(r => (
            <Chip key={r} touch={isMobile} selected={(role ?? '') === r} onClick={() => onRole(r)}>
              {roleLabel(r)}
            </Chip>
          ))}
        </div>
      )}
    </div>
  );
}
