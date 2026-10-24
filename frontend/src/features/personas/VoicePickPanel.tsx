import { useEffect, useRef, useState } from 'react';
import { AlertCircle, Info, Volume2, X } from 'lucide-react';
import { C, FS, SP, R } from '../../lib/design';
import { Badge, Button, Chip, IconButton, Notice, PressableArea, ProgressBar } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { api } from '../../lib/api';
import { previewVoice, isSpeaking, VoicePreviewError } from '../../lib/tts';
import type { PersonaVoice, TtsVoiceInfo, VoiceCandidate } from '../../types';
import { LIVE_TTS_PRICE, greetingSample, voiceName, voiceRoleLabel } from './voiceLabels';
import { PlayingBars } from './PlayingBars';

// Панель подбора голоса под персону: три обычных кандидата и отдельно живой (LiveTTS) с
// ценой. Панелью под полем, а не попапом — кандидатов сравнивают на слух, переключаясь
// между ними, и список не должен закрываться от каждого клика.
//
// Прослушка — фразой самой персоны (из приветствия), а не общей: так голос примеряют к
// тому, что персона действительно скажет. Синтезированное кешируется в previewVoice по
// голосу, амплуа и фразе — повторное ▶ того же кандидата денег не стоит.

export type PersonaDescription = { name?: string; role?: string; description?: string; character?: string; tone?: string };

type PickState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | {
    status: 'ready'; candidates: VoiceCandidate[]; live: VoiceCandidate | null; exhausted: boolean;
    noMore: boolean; more: boolean;
    // Сбой «Подобрать ещё» — у самой кнопки, а не в строке ошибок прослушки
    moreError: string | null;
  };

const roleLabel = (role?: string | null) => (role ? voiceRoleLabel(role) : 'обычное');

export function VoicePickPanel({ describe, greeting, catalog, value, configured, isMobile, onPick, onOpenList, onClose, onBusy }: {
  describe: () => PersonaDescription;
  greeting?: string;
  catalog: TtsVoiceInfo[];
  value: PersonaVoice | null;
  configured: boolean;
  isMobile: boolean;
  onPick: (voice: string, role: string | null) => void;
  onOpenList: () => void;
  onClose: () => void;
  // Идёт ли запрос — кнопка «Подобрать под персону» показывает это своим loading
  onBusy: (busy: boolean) => void;
}) {
  const [state, setState] = useState<PickState>({ status: 'loading' });
  const [playing, setPlaying] = useState<string | null>(null);
  const [listenError, setListenError] = useState<string | null>(null);
  // Уже показанные голоса: «Подобрать ещё» просит следующую тройку без них
  const shown = useRef<string[]>([]);
  const alive = useRef(true);
  const started = useRef(false);
  const pending = useRef(false);
  const seq = useRef(0);
  // Персона — снимком на момент нажатия: правки формы во время подбора его не дёргают
  const [persona] = useState(describe);

  const sample = greetingSample(greeting);

  // Состояние «идёт» выставляет нажатие (здесь — retry/more, первое — кнопка у поля),
  // сам запрос только снимает его по завершении
  const fetchCandidates = async (more: boolean) => {
    // Ответ устаревшего запроса (повтор нажат, пока шёл прежний) состояние не трогает:
    // иначе прежняя тройка затёрла бы новую и сбила перечень показанных
    const id = ++seq.current;
    pending.current = true;
    try {
      const res = await api.personas.suggestVoiceCandidates({ ...persona, exclude: shown.current });
      if (!alive.current || id !== seq.current) return;
      const found = [...res.candidates, ...(res.live ? [res.live] : [])];
      shown.current = [...shown.current, ...found.map(c => c.voice)];
      setState(s => {
        if (more && s.status === 'ready') {
          // «Подобрать ещё» ничего не нашло — прежняя тройка остаётся, кнопка уступает место
          // «больше вариантов нет»
          if (found.length === 0) return { ...s, more: false, noMore: true, moreError: null };
          // Нашёлся только живой — тройку не меняем на одну строку, обновляем лишь живой вариант
          if (res.candidates.length === 0) {
            return { ...s, live: res.live, exhausted: res.exhausted, more: false, moreError: null };
          }
        }
        return {
          status: 'ready', candidates: res.candidates, live: res.live, exhausted: res.exhausted,
          noMore: false, more: false, moreError: null,
        };
      });
    } catch (e) {
      if (!alive.current || id !== seq.current) return;
      const message = e instanceof Error ? e.message : 'Не удалось подобрать голоса';
      // Сбой «ещё» не стирает уже показанных кандидатов
      setState(s => (more && s.status === 'ready' ? { ...s, more: false, moreError: message } : { status: 'error', message }));
    } finally {
      if (id === seq.current) {
        pending.current = false;
        if (alive.current) onBusy(false);
      }
    }
  };

  const load = (more: boolean) => {
    if (more) setState(s => (s.status === 'ready' ? { ...s, more: true, moreError: null } : s));
    else { shown.current = []; setState({ status: 'loading' }); }
    onBusy(true);
    void fetchCandidates(more);
  };

  // Подбор — один раз на открытие панели; повтор — кнопками внутри неё. Флаг started
  // переживает пробное размонтирование StrictMode (рефы сохраняются): без него в dev
  // уходило два оплаченных запроса, и второй ответ подменял первый
  useEffect(() => {
    alive.current = true;
    if (!started.current) { started.current = true; void fetchCandidates(false); }
    else if (pending.current) onBusy(true);
    return () => { alive.current = false; onBusy(false); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- запрос строго один на монтирование
  }, []);

  const listen = async (c: VoiceCandidate) => {
    if (playing) return; // каждое нажатие без кеша — оплаченный запрос
    if (isSpeaking()) { setListenError('Идёт озвучка ответа — дождитесь конца или остановите её'); return; }
    setListenError(null);
    setPlaying(c.voice);
    try {
      // Темп не шлём: кандидата слушают в его обычной подаче
      await previewVoice(c.voice, c.role ?? undefined, undefined, sample ?? undefined);
    } catch (e) {
      if (alive.current) setListenError(e instanceof VoicePreviewError ? e.message : 'Не удалось послушать голос');
    } finally {
      if (alive.current) setPlaying(null);
    }
  };

  const name = persona.name?.trim();
  const frame = (children: React.ReactNode) => (
    <div style={{
      border: `1px solid ${C.border}`, borderRadius: R.xl, background: C.bgWhite,
      padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.xs }}>
        <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {name ? `Под характер «${name}»` : 'Под характер персоны'}
        </span>
        <IconButton title="Скрыть подбор" size="sm" onClick={onClose}>
          <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      {children}
    </div>
  );

  if (state.status === 'loading') {
    return frame(
      <>
        <ProgressBar value={0} indeterminate label="Подбираю голоса" />
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>Подбираю три голоса по описанию и характеру персоны…</span>
      </>,
    );
  }
  if (state.status === 'error') {
    return frame(
      <Notice tone="danger" icon={AlertCircle} title="Не удалось подобрать голоса"
        action={<Button variant="ghost" size="sm" onClick={() => load(false)}>Повторить</Button>}>
        {state.message.replace(/[.\s]+$/, '')}. Список голосов работает как обычно.
      </Notice>,
    );
  }
  if (state.candidates.length === 0 && !state.live) {
    return frame(
      <Notice tone="info" icon={Info} title="Подходящих голосов не нашлось"
        action={<Button variant="ghost" size="sm" onClick={onOpenList}>Открыть список</Button>}>
        Опишите характер подробнее или выберите голос в списке сами.
      </Notice>,
    );
  }

  const row = (c: VoiceCandidate, i: number, live: boolean) => {
    const v = catalog.find(x => x.voice === c.voice);
    const title = v ? voiceName(v) : c.voice;
    const on = value?.voice === c.voice && (value?.role ?? null) === (c.role ?? null);
    return (
      <div key={c.voice} style={{
        display: 'flex', alignItems: 'center', gap: SP.xs,
        borderTop: i ? `1px solid ${C.borderLight}` : undefined, paddingTop: i ? SP.xs : 0,
      }}>
        {/* Строка целиком — выбор кандидата; ▶ — отдельная кнопка, кнопка в кнопке невалидна */}
        <PressableArea
          feedback
          onPress={() => onPick(c.voice, c.role)}
          title={`Выбрать: ${title}, ${roleLabel(c.role)}`}
          style={{
            flex: 1, minWidth: 0, borderRadius: R.md, padding: `${SP.sm}px ${SP.xs}px`,
            display: 'flex', flexDirection: 'column', gap: SP.xxs,
            background: on ? C.accentLight : undefined,
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
            <span style={{ fontSize: FS.base, fontWeight: 600, color: on ? C.accent : C.textHeading }}>{title}</span>
            <Chip>{roleLabel(c.role)}</Chip>
            {live && <Badge tone="info" size="xs">живой вариант</Badge>}
          </span>
          {/* Цена живого — в строке пояснения: в бейдже на 320 px она уходила под многоточие */}
          {(c.why || live) && (
            <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
              {live ? [`LiveTTS, ${LIVE_TTS_PRICE}`, c.why].filter(Boolean).join(' — ') : c.why}
            </span>
          )}
        </PressableArea>
        <IconButton
          title={configured ? `Послушать: ${title}` : 'Озвучка не настроена'}
          size={isMobile ? 'lg' : 'md'}
          disabled={!configured || (!!playing && playing !== c.voice)}
          onClick={() => void listen(c)}
        >
          {playing === c.voice
            ? <PlayingBars height={ICON_SIZE.sm} />
            : <Volume2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        </IconButton>
      </div>
    );
  };

  const all = [...state.candidates.map(c => ({ c, live: false })), ...(state.live ? [{ c: state.live, live: true }] : [])];
  const noMore = state.noMore || state.exhausted;

  return frame(
    <>
      <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
        {sample
          ? <>Нажмите на голос, чтобы выбрать. Кнопка с динамиком — послушать фразой персоны: «{sample}»</>
          : 'Нажмите на голос, чтобы выбрать. Кнопка с динамиком — послушать.'}
      </span>
      {all.map((x, i) => row(x.c, i, x.live))}
      {listenError && <span style={{ fontSize: FS.xs, color: C.danger }}>{listenError}</span>}
      {state.moreError && <span style={{ fontSize: FS.xs, color: C.danger }}>{state.moreError}</span>}
      <div>
        {noMore
          ? <span style={{ fontSize: FS.xs, color: C.textMuted }}>Больше вариантов нет — остальные голоса в списке.</span>
          : <Button variant="ghost" size="sm" loading={state.more} onClick={() => load(true)}>Подобрать ещё</Button>}
      </div>
    </>,
  );
}
