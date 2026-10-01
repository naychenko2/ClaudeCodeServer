import { useEffect, useRef, useState } from 'react';
import { AudioWaveform, Download, Pause, Play } from 'lucide-react';
import { Button, IconButton, C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { AudioWave } from './AudioWave';
import {
  EMPTY_MIXER, GAIN_MAX_DB, GAIN_MIN_DB, dbToGain, isAudible, mixPlan, setGain, toggleMute, toggleSolo,
  type MixPlan, type MixerState,
} from './mix';
import { fmtTime } from './selection';
import { useBrowserPeaks } from './useBrowserPeaks';

export interface Stem {
  id: string;
  name: string;
  /** Файл стема; нет — строка без звука (только сведение) */
  url?: string;
  peaks?: number[];
}

export interface StemMixerProps {
  stems: Stem[];
  /** Общая длина, с — у стемов одной версии она одна */
  duration: number;
  defaultValue?: MixerState;
  onChange?: (state: MixerState) => void;
  /** «Свести N из M в новую версию» — без ИИ, план того, что звучит */
  onMix: (plan: MixPlan, state: MixerState) => void;
  onDownload?: (stemId: string) => void;
  /** «podcast-intro.stems/» в подсказке */
  folderName?: string;
  mixing?: boolean;
}

export function StemMixer({ stems, duration, defaultValue, onChange, onMix, onDownload, folderName, mixing }: StemMixerProps) {
  const [st, setSt] = useState<MixerState>(defaultValue ?? EMPTY_MIXER);
  const update = (next: MixerState) => { setSt(next); onChange?.(next); };
  const plan = mixPlan(stems, st);

  const player = useStemPlayback(stems, st);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <IconButton
          size="xs"
          title={player.playing ? 'Пауза' : 'Слушать сведение'}
          disabled={!player.canPlay}
          onClick={player.toggle}
          style={{ background: player.canPlay ? C.accent : C.bgInset, color: player.canPlay ? C.onAccent : C.textMuted, borderRadius: R.full }}
        >
          {player.playing
            ? <Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} fill="currentColor" />
            : <Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} fill="currentColor" />}
        </IconButton>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          {fmtTime(player.position)} / {fmtTime(duration)}
        </span>
      </div>

      <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden' }}>
        {stems.map((s, i) => (
          <StemRow
            key={s.id}
            stem={s}
            first={i === 0}
            duration={duration}
            position={player.position}
            showCursor={player.playing}
            onSeek={player.seek}
            muted={!!st.mute[s.id]}
            solo={st.solo === s.id}
            audible={isAudible(s.id, st)}
            gain={st.gain[s.id] ?? 0}
            onMute={() => update(toggleMute(st, s.id))}
            onSolo={() => update(toggleSolo(st, s.id))}
            onGain={db => update(setGain(st, s.id, db))}
            onDownload={onDownload ? () => onDownload(s.id) : undefined}
          />
        ))}
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.xxs }}>
        <span style={{ flex: '1 1 180px', minWidth: 0, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          {folderName && <>Сохранятся папкой <code style={{ fontFamily: FONT.mono }}>{folderName}</code>. </>}
          M — заглушить, S — слушать одну, ползунок — громкость, дБ. Сводим то, что сейчас звучит:{' '}
          <span data-mix-desc>{plan.description || 'ничего'}</span>.
        </span>
        <Button
          size="xs"
          // Сводить нечего — кнопка серая, а не бледный акцент: это не «временно недоступно»
          variant={plan.canMix ? 'primary' : 'secondary'}
          disabled={!plan.canMix || mixing}
          loading={mixing}
          title={plan.canMix ? 'Без ИИ · мгновенно · бесплатно' : 'Все стемы заглушены'}
          leftIcon={<AudioWaveform size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => onMix(plan, st)}
        >
          {plan.canMix ? `Свести ${plan.stems.length} из ${plan.total} в новую версию` : 'Все стемы заглушены'}
        </Button>
      </div>
    </div>
  );
}

function StemRow({
  stem, first, duration, position, showCursor, onSeek, muted, solo, audible, gain, onMute, onSolo, onGain, onDownload,
}: {
  stem: Stem; first: boolean; duration: number; position: number; showCursor: boolean; onSeek: (t: number) => void;
  muted: boolean; solo: boolean; audible: boolean; gain: number;
  onMute: () => void; onSolo: () => void; onGain: (db: number) => void; onDownload?: () => void;
}) {
  const decoded = useBrowserPeaks(stem.peaks ? null : stem.url, 50);
  const peaks = stem.peaks ?? decoded?.peaks ?? [];
  const msStyle = { fontSize: FS.xs, fontWeight: 700, border: `1px solid ${C.border}` } as const;
  return (
    <div data-stem-row={stem.id} style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.xs + 1}px ${SP.sm}px`, flexWrap: 'wrap',
      borderTop: first ? 'none' : `1px solid ${C.borderLight}`, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flex: '1 1 200px', minWidth: 0 }}>
        <IconButton
          size="xs" title="Заглушить: в сведение не попадёт" active={muted} onClick={onMute}
          style={muted ? { ...msStyle, background: C.warningBg, color: C.warningText, borderColor: C.warning } : msStyle}
        >M</IconButton>
        <IconButton
          size="xs" title="Слушать одну: сводим только её" active={solo} onClick={onSolo}
          style={solo ? { ...msStyle, background: C.accent, color: C.onAccent, borderColor: C.accent } : msStyle}
        >S</IconButton>
        <span style={{
          width: 64, flex: '0 0 64px', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textPrimary,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', opacity: audible ? 1 : 0.35,
        }} title={stem.name}>{stem.name}</span>
        <AudioWave
          peaks={peaks} duration={duration} position={position} showCursor={showCursor} onSeek={onSeek}
          size="sm" dim={!audible} ariaLabel={`Волна стема ${stem.name}`}
        />
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginLeft: 'auto' }}>
        <input
          type="range" min={GAIN_MIN_DB} max={GAIN_MAX_DB} step={1} value={gain}
          aria-label={`Громкость стема ${stem.name}, дБ`} title="Громкость стема в сведении"
          onChange={e => onGain(Number(e.target.value))}
          style={{ width: 80, accentColor: C.accent }}
        />
        <span style={{ width: 26, textAlign: 'right', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary }}>
          {gain > 0 ? `+${gain}` : gain < 0 ? `−${-gain}` : '0'}
        </span>
        {onDownload && (
          <IconButton size="xs" title="Сохранить стем" onClick={onDownload}>
            <Download size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>
    </div>
  );
}

// Прослушка сведения: стемы играют синхронно, у каждого свой GainNode — так слышно
// и M/S, и громкость выше 0 дБ, которую HTMLAudioElement.volume дать не может.
function useStemPlayback(stems: Stem[], st: MixerState) {
  const ctxRef = useRef<AudioContext | null>(null);
  const nodes = useRef(new Map<string, { el: HTMLAudioElement; gain: GainNode }>());
  const [playing, setPlaying] = useState(false);
  const [position, setPosition] = useState(0);
  const playable = stems.filter(s => s.url);
  const canPlay = playable.length > 0;

  useEffect(() => () => {
    nodes.current.forEach(n => { n.el.pause(); n.el.src = ''; });
    nodes.current.clear();
    void ctxRef.current?.close();
    ctxRef.current = null;
  }, []);

  useEffect(() => {
    nodes.current.forEach((n, id) => { n.gain.gain.value = isAudible(id, st) ? dbToGain(st.gain[id] ?? 0) : 0; });
  }, [st]);

  useEffect(() => {
    if (!playing) return;
    let raf = 0;
    const tick = () => {
      const first = nodes.current.values().next().value;
      if (first) {
        setPosition(first.el.currentTime);
        if (first.el.ended) setPlaying(false);
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [playing]);

  const ensureGraph = () => {
    if (!ctxRef.current) ctxRef.current = new AudioContext();
    const ctx = ctxRef.current;
    for (const s of playable) {
      if (nodes.current.has(s.id)) continue;
      const el = new Audio(s.url);
      el.preload = 'auto';
      el.currentTime = position;
      const gain = ctx.createGain();
      ctx.createMediaElementSource(el).connect(gain).connect(ctx.destination);
      gain.gain.value = isAudible(s.id, st) ? dbToGain(st.gain[s.id] ?? 0) : 0;
      nodes.current.set(s.id, { el, gain });
    }
    return ctx;
  };

  const toggle = () => {
    if (!canPlay) return;
    if (playing) {
      nodes.current.forEach(n => n.el.pause());
      setPlaying(false);
      return;
    }
    const ctx = ensureGraph();
    void ctx.resume();
    nodes.current.forEach(n => { n.el.currentTime = position; void n.el.play().catch(() => setPlaying(false)); });
    setPlaying(true);
  };

  const seek = (t: number) => {
    setPosition(t);
    nodes.current.forEach(n => { n.el.currentTime = t; });
  };

  return { playing, position, canPlay, toggle, seek };
}
