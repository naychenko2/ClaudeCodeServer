import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react';
import type { KeyboardEvent } from 'react';
import { AlertTriangle, Download, Maximize2, Minus, Music, Pause, Play, Plus, SkipBack } from 'lucide-react';
import { C, FONT, FS, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Button } from '../ui/Button';
import { EmptyState } from '../ui/EmptyState';
import { IconButton } from '../ui/IconButton';
import { Notice } from '../ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { MidiParseError, parseMidi, type MidiDoc } from './midiModel';
import { getState, pause, play, seek, stop, subscribe } from './midiPlayer';
import { PianoRoll } from './PianoRoll';
import { MidiTracks } from './MidiTracks';
import { fitPxPerSec, formatTime, toggleMute, toggleSolo, zoomPxPerSec } from './midiViewerLogic';

// Просмотрщик .mid: панель воспроизведения, дорожки и нотная лента. Только просмотр.

export type MidiEditorProps = {
  load: () => Promise<ArrayBuffer>;
  fileName: string;
  variant: 'inline' | 'full';
  onExpand?: () => void;
  onDownload?: () => void;
  onRetry?: () => void;
};

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; doc: MidiDoc };

const INLINE_ROLL_H = 220;
const INLINE_ROLL_H_MOBILE = 180;
// Ширина клавиатуры ленты: вычитается при подгонке масштаба под ширину
const KEYS_W = 56;
const ICON = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE };

export function MidiEditor({ load, fileName, variant, onExpand, onDownload, onRetry }: MidiEditorProps) {
  const isMobile = useIsMobile();
  const ownerId = useId();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [muted, setMuted] = useState<Set<number>>(() => new Set());
  const [zoom, setZoom] = useState<number | null>(null);
  const [localPos, setLocalPos] = useState(0);
  const [audioFailed, setAudioFailed] = useState(false);
  const [width, setWidth] = useState(0);
  const [fullRollH, setFullRollH] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const rollBoxRef = useRef<HTMLDivElement>(null);
  const loadRef = useRef(load);
  loadRef.current = load;

  const player = useSyncExternalStore(subscribe, getState);
  const mine = player.ownerId === ownerId;
  const playing = mine && player.playing;
  const positionSec = mine ? player.positionSec : localPos;

  // Загрузка и разбор; ответ после размонтирования или повторного запуска отбрасывается
  useEffect(() => {
    let alive = true;
    setState({ kind: 'loading' });
    setMuted(new Set());
    setZoom(null);
    setLocalPos(0);
    Promise.resolve()
      .then(() => loadRef.current())
      .then(buf => {
        if (!alive) return;
        try {
          setState({ kind: 'ready', doc: parseMidi(buf) });
        } catch (e) {
          setState({ kind: 'error', message: e instanceof MidiParseError ? e.message : 'Файл MIDI повреждён' });
        }
      })
      .catch(() => {
        if (alive) setState({ kind: 'error', message: 'Не удалось загрузить файл' });
      });
    return () => {
      alive = false;
      // stop сам проверяет владельца, в том числе play, ждущий загрузки Tone
      stop(ownerId);
    };
  }, [attempt, ownerId]);

  useLayoutEffect(() => {
    const root = rootRef.current;
    if (!root || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => {
      setWidth(root.clientWidth);
      if (rollBoxRef.current) setFullRollH(rollBoxRef.current.clientHeight);
    });
    ro.observe(root);
    if (rollBoxRef.current) ro.observe(rollBoxRef.current);
    return () => ro.disconnect();
  }, [state.kind, variant]);

  const doc = state.kind === 'ready' ? state.doc : null;
  const pxPerSec = zoom ?? fitPxPerSec(width - KEYS_W, doc?.durationSec ?? 0);

  const startPlay = useCallback((d: MidiDoc, fromSec: number, nextMuted: Set<number>) => {
    setAudioFailed(false);
    play(ownerId, d, { fromSec, muted: nextMuted }).catch(() => setAudioFailed(true));
  }, [ownerId]);

  const togglePlay = useCallback(() => {
    if (!doc) return;
    if (playing) pause(ownerId);
    else startPlay(doc, positionSec >= doc.durationSec ? 0 : positionSec, muted);
  }, [doc, playing, ownerId, startPlay, positionSec, muted]);

  const handleSeek = useCallback((sec: number) => {
    setLocalPos(sec);
    if (mine) seek(ownerId, sec);
  }, [mine, ownerId]);

  // Смена заглушённых во время игры — перезапуск с текущей позиции
  const applyMuted = (next: Set<number>) => {
    setMuted(next);
    if (doc && playing) startPlay(doc, positionSec, next);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== ' ' || e.repeat) return;
    // На кнопке пробел нажимает её саму
    const target = e.target as HTMLElement;
    if (target.closest('button, input, textarea, select, [contenteditable="true"]')) return;
    e.preventDefault();
    togglePlay();
  };

  const retry = () => {
    if (onRetry) onRetry();
    else setAttempt(a => a + 1);
  };

  const full = variant === 'full';
  const rollH = full ? fullRollH : (isMobile ? INLINE_ROLL_H_MOBILE : INLINE_ROLL_H);

  return (
    <div
      ref={rootRef}
      tabIndex={-1}
      onKeyDown={onKeyDown}
      aria-label={`Ноты: ${fileName}`}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0, outline: 'none',
        height: full ? '100%' : undefined, fontFamily: FONT.sans,
      }}
    >
      {state.kind === 'loading' && (
        <div style={{
          height: full ? '100%' : rollH, display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: C.bgPanel, color: C.textSecondary, fontSize: FS.sm,
        }}>
          Читаем ноты…
        </div>
      )}

      {state.kind === 'error' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.sm }}>
          <Notice tone="danger" icon={AlertTriangle} title="Не получилось открыть ноты">
            {state.message}
          </Notice>
          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
            {onDownload && (
              <Button variant="ghost" size="sm" leftIcon={<Download {...ICON} />} onClick={onDownload}>
                Скачать как есть
              </Button>
            )}
            <Button variant="secondary" size="sm" onClick={retry}>Повторить</Button>
          </div>
        </div>
      )}

      {doc && doc.noteCount === 0 && (
        <EmptyState compact inline icon={<Music size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />} title="В файле нет нот" />
      )}

      {doc && doc.noteCount > 0 && (
        <>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
            <IconButton
              size="sm"
              tone="accent"
              onClick={togglePlay}
              title={playing ? 'Пауза' : 'Нет звука на iPhone — проверьте беззвучный режим'}
            >
              {playing ? <Pause {...ICON} /> : <Play {...ICON} />}
            </IconButton>
            <IconButton size="sm" title="В начало" onClick={() => handleSeek(0)}>
              <SkipBack {...ICON} />
            </IconButton>
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary,
              fontVariantNumeric: 'tabular-nums', padding: `0 ${SP.xs}px`, whiteSpace: 'nowrap',
            }}>
              {formatTime(positionSec)} / {formatTime(doc.durationSec)}
            </span>
            <span style={{ flex: 1 }} />
            <IconButton size="sm" title="Мельче" onClick={() => setZoom(zoomPxPerSec(pxPerSec, -1))}>
              <Minus {...ICON} />
            </IconButton>
            <IconButton size="sm" title="Крупнее" onClick={() => setZoom(zoomPxPerSec(pxPerSec, 1))}>
              <Plus {...ICON} />
            </IconButton>
            {onDownload && (
              <IconButton size="sm" title="Скачать" onClick={onDownload}>
                <Download {...ICON} />
              </IconButton>
            )}
            {onExpand && (
              <IconButton size="sm" title="Развернуть" onClick={onExpand}>
                <Maximize2 {...ICON} />
              </IconButton>
            )}
          </div>

          {audioFailed && (
            <Notice tone="warning" icon={AlertTriangle}>Не удалось включить звук</Notice>
          )}

          <MidiTracks
            tracks={doc.tracks}
            muted={muted}
            onToggleMute={i => applyMuted(toggleMute(muted, i))}
            onToggleSolo={i => applyMuted(toggleSolo(doc.tracks.map(t => t.index), muted, i))}
          />

          <div ref={rollBoxRef} style={{ flex: full ? 1 : undefined, minHeight: 0 }}>
            {rollH > 0 && (
              <PianoRoll
                doc={doc}
                positionSec={positionSec}
                muted={muted}
                pxPerSec={pxPerSec}
                height={rollH}
                onSeek={handleSeek}
              />
            )}
          </div>
        </>
      )}
    </div>
  );
}
