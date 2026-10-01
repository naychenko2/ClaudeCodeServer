import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Pause, Play, Scissors, Volume2, VolumeX } from 'lucide-react';
import { Button, IconButton, PillSwitch, C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { AudioWave } from './AudioWave';
import { swapSourceKeepingPosition } from './abSwitch';
import { fmtSelection, fmtTime, clampSelection, type AudioSelection } from './selection';
import { useBrowserPeaks } from './useBrowserPeaks';

export interface AudioSource {
  key: string;
  /** Подпись сегмента A/B: «A · в1» */
  label: string;
  url: string;
  /** Готовые пики; нет — посчитаем в браузере */
  peaks?: number[];
  duration?: number;
  title?: string;
}

export interface AudioPlayerProps {
  /** Одна версия — просто плеер; две — с переключателем A/B */
  sources: AudioSource[];
  /** Какая версия звучит; без пропа — первая, переключает сам плеер */
  activeKey?: string;
  onActiveChange?: (key: string) => void;
  selection?: AudioSelection | null;
  onSelectionChange?: (sel: AudioSelection | null) => void;
  /** Кнопки под выделением: «Обрезать», «Перегенерировать кусок» */
  selectionActions?: ReactNode;
  compact?: boolean;
}

export function AudioPlayer({
  sources, activeKey, onActiveChange, selection = null, onSelectionChange, selectionActions, compact,
}: AudioPlayerProps) {
  const [ownKey, setOwnKey] = useState(sources[0]?.key ?? '');
  const key = activeKey ?? ownKey;
  const src = sources.find(s => s.key === key) ?? sources[0];
  const url = src?.url ?? '';

  const audioRef = useRef<HTMLAudioElement>(null);
  const loadedUrl = useRef<string | null>(null);
  const swapping = useRef(false);
  const [playing, setPlaying] = useState(false);
  const [position, setPosition] = useState(0);
  const [mediaDuration, setMediaDuration] = useState(0);
  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);

  const decoded = useBrowserPeaks(src?.peaks ? null : url);
  const peaks = src?.peaks ?? decoded?.peaks ?? [];
  const duration = src?.duration ?? (mediaDuration || decoded?.duration || 0);

  // Источник ставим руками, а не атрибутом src: смена атрибута сбросила бы позицию
  useEffect(() => {
    const el = audioRef.current;
    if (!el || !url || loadedUrl.current === url) return;
    if (loadedUrl.current === null) {
      loadedUrl.current = url;
      el.src = url;
      return;
    }
    loadedUrl.current = url;
    swapping.current = true;
    const cancel = swapSourceKeepingPosition(el, url);
    return () => { cancel(); };
  }, [url]);

  useEffect(() => {
    const el = audioRef.current;
    if (!el) return;
    el.volume = volume;
    el.muted = muted;
  }, [volume, muted]);

  // Курсор ведём по кадрам, а не по timeupdate (тот приходит ~4 раза в секунду)
  useEffect(() => {
    if (!playing) return;
    let raf = 0;
    const tick = () => {
      const el = audioRef.current;
      if (el && !swapping.current) setPosition(el.currentTime);
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [playing]);

  // Длина версии сменилась — выделение обрезается по новой длине
  useEffect(() => {
    if (!selection || !onSelectionChange || !(duration > 0)) return;
    const c = clampSelection(selection, duration);
    if (!c || c.start !== selection.start || c.end !== selection.end) onSelectionChange(c);
  }, [duration, selection, onSelectionChange]);

  const toggle = () => {
    const el = audioRef.current;
    if (!el) return;
    if (el.paused) void el.play().catch(() => setPlaying(false));
    else el.pause();
  };
  const seek = (t: number) => {
    const el = audioRef.current;
    setPosition(t);
    if (el) el.currentTime = t;
  };
  const pick = (k: string) => {
    if (k === key) return;
    if (activeKey === undefined) setOwnKey(k);
    onActiveChange?.(k);
  };

  const iconSize = compact ? ICON_SIZE.xs : ICON_SIZE.sm;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <audio
        ref={audioRef}
        preload="metadata"
        onPlay={() => setPlaying(true)}
        onPause={() => setPlaying(false)}
        onEnded={() => setPlaying(false)}
        onLoadedMetadata={e => {
          swapping.current = false;
          setMediaDuration(e.currentTarget.duration || 0);
          setPosition(e.currentTarget.currentTime);
        }}
        onTimeUpdate={e => { if (!swapping.current && !playing) setPosition(e.currentTarget.currentTime); }}
      />
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
        <IconButton
          size={compact ? 'xs' : 'md'}
          title={playing ? 'Пауза' : 'Слушать'}
          onClick={toggle}
          disabled={!url}
          style={{ background: C.accent, color: C.onAccent, borderRadius: R.full }}
        >
          {playing
            ? <Pause size={iconSize} strokeWidth={ICON_STROKE} fill="currentColor" />
            : <Play size={iconSize} strokeWidth={ICON_STROKE} fill="currentColor" />}
        </IconButton>
        <AudioWave
          peaks={peaks}
          duration={duration}
          position={position}
          showCursor={playing}
          onSeek={seek}
          selection={selection}
          onSelectionChange={onSelectionChange}
          size={compact ? 'sm' : 'md'}
          ariaLabel={src?.title ? `Волна: ${src.title}` : 'Волна'}
        />
        <span data-player-time style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
          {fmtTime(position)} / {fmtTime(duration)}
        </span>
      </div>

      {onSelectionChange && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
          <Scissors size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          <span>{selection ? `Выделено ${fmtSelection(selection, duration)}` : 'Протяните по волне, чтобы выделить кусок'}</span>
          {selection && (
            <>
              {selectionActions}
              <Button size="xs" variant="ghost" onClick={() => onSelectionChange(null)}>Снять</Button>
            </>
          )}
        </div>
      )}

      {!compact && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          {sources.length > 1 && (
            <div style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
              <PillSwitch
                value={src.key}
                options={sources.map(s => ({ value: s.key, label: s.label, title: s.title }))}
                onChange={pick}
              />
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>переключение не сбивает позицию</span>
            </div>
          )}
          <div style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, marginLeft: 'auto' }}>
            <IconButton size="sm" title={muted ? 'Включить звук' : 'Выключить звук'} onClick={() => setMuted(m => !m)}>
              {muted || volume === 0
                ? <VolumeX size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                : <Volume2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            </IconButton>
            <input
              type="range" min={0} max={100} step={1} value={Math.round(volume * 100)} aria-label="Громкость"
              onChange={e => { setVolume(Number(e.target.value) / 100); setMuted(false); }}
              style={{ width: 96, accentColor: C.accent }}
            />
          </div>
        </div>
      )}
    </div>
  );
}
