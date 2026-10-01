// Тело версии звука в карточке ленты: плеер с серверной волной и A/B, мини-микшер стемов со
// сведением без ИИ и список остальных файлов версии (.abc, .txt/.srt/.lrc, .mid, .pth/.index)
// со скачиванием. Общее у карточки нити и вариантов запуска.

import { useState } from 'react';
import { Download, FileText } from 'lucide-react';
import { Button, IconButton, C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE, useIsMobile } from 'aihome_shell/kit';
import { audioApi, type AudioThread, type AudioThreadVersion } from '../api';
import { AudioPlayer, StemMixer, type AudioSource, type Stem } from '../player';
import { normalizeJoint } from '../player/peaks';
import { isPersonalScope } from '../scope';
import { downloadFile, mixStems } from './actions';
import { abSides, extraFiles, hasMain, stemsFolder, versionLabel, versionStems } from './model';
import { useServerPeaks } from './serverPeaks';
import {
  getPieceFieldOpen, getSelection, requestOperation, setSelection, useAudioStoreVersion,
} from './threadStore';

// Точек волны: крупный плеер и строка стема (волна потом ресемплится под ширину)
const PEAK_POINTS = 240;
const STEM_POINTS = 100;

interface Props {
  scope: string;
  sessionId: string;
  thread: AudioThread;
  version: AudioThreadVersion;
  // Выделение куска — только у нити в работе на её текущей версии
  selectable: boolean;
  compact?: boolean;
}

export function VersionBody(p: Props) {
  const stems = versionStems(p.version);
  return (
    <>
      {hasMain(p.version) && <PlayerBlock {...p} />}
      {stems.length > 0 && <StemsBlock {...p} />}
      <ExtraFiles {...p} />
    </>
  );
}

function PlayerBlock({ scope, sessionId, thread, version, selectable, compact }: Props) {
  useAudioStoreVersion();
  const sides = abSides(thread, version);
  const peaks = useServerPeaks(sides.map(s => (
    { scope, sessionId, threadId: thread.id, versionId: s.versionId, role: null, points: PEAK_POINTS }
  )));
  const waves = normalizeJoint(peaks.map(x => x?.peaks));
  // Звучит смотримая версия (B); A — основа для сравнения на слух
  const [active, setActive] = useState(version.id);
  const sources: AudioSource[] = sides.map((s, i) => {
    const v = thread.versions.find(x => x.id === s.versionId);
    return {
      key: s.versionId,
      label: s.label,
      url: audioApi.versionFileUrl(scope, sessionId, thread.id, s.versionId),
      // Пусто, пока сервер считает: так плеер не декодирует файл в главном потоке
      peaks: waves[i] ?? [],
      duration: peaks[i]?.seconds,
      title: v ? versionLabel(v) : undefined,
    };
  });

  const sel = selectable ? getSelection(sessionId, thread.id) : null;
  const linked = selectable && getPieceFieldOpen(sessionId) === thread.id;
  return (
    <AudioPlayer
      sources={sources}
      activeKey={active}
      onActiveChange={setActive}
      selection={sel}
      onSelectionChange={selectable ? s => setSelection(sessionId, thread.id, s ? { ...s, versionId: active } : null) : undefined}
      selectionActions={selectable && (
        <>
          {linked && <span data-piece-linked="" style={{ color: C.accent, fontWeight: 600 }}>= «Кусок» в панели</span>}
          <Button size="xs" variant="ghost" onClick={() => requestOperation(sessionId, thread.id, 'trim')}>Обрезать</Button>
          <Button size="xs" variant="ghost" onClick={() => requestOperation(sessionId, thread.id, 'repaint')}>Перегенерировать кусок</Button>
        </>
      )}
      compact={compact}
    />
  );
}

function StemsBlock({ scope, sessionId, thread, version }: Props) {
  const stems = versionStems(version);
  const peaks = useServerPeaks(stems.map(s => (
    { scope, sessionId, threadId: thread.id, versionId: version.id, role: s.id, points: STEM_POINTS }
  )));
  const waves = normalizeJoint(peaks.map(x => x?.peaks));
  const [mixing, setMixing] = useState(false);
  const list: Stem[] = stems.map((s, i) => ({
    id: s.id,
    name: s.name,
    url: audioApi.versionFileUrl(scope, sessionId, thread.id, version.id, s.id),
    peaks: waves[i] ?? [],
  }));
  const duration = peaks.reduce((m, x) => Math.max(m, x?.seconds ?? 0), 0);
  return (
    <div data-audio-stems={stems.length}>
      <StemMixer
        key={version.id}
        stems={list}
        duration={duration}
        mixing={mixing}
        folderName={isPersonalScope(scope) ? undefined : stemsFolder(thread)}
        onDownload={role => downloadFile(scope, sessionId, thread, version.id, role)}
        onMix={plan => {
          setMixing(true);
          void mixStems(scope, sessionId, thread, version.id, plan).finally(() => setMixing(false));
        }}
      />
    </div>
  );
}

function ExtraFiles({ scope, sessionId, thread, version }: Props) {
  const mobile = useIsMobile();
  const files = extraFiles(version);
  if (!files.length) return null;
  return (
    <div data-audio-files={files.length} style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden' }}>
      {files.map((f, i) => (
        <div key={f.role} data-audio-file={f.role} style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${SP.sm}px`, minWidth: 0,
          borderTop: i ? `1px solid ${C.borderLight}` : 'none', fontSize: FS.sm,
        }}>
          <FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
          <span style={{ color: C.textPrimary, whiteSpace: 'nowrap' }}>{f.label}</span>
          <span title={f.name} style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, minWidth: 0, flex: 1,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{f.ext ? `.${f.ext}` : f.name}</span>
          <IconButton size={mobile ? 'md' : 'xs'} title={`Скачать: ${f.name}`} ariaLabel={`Скачать ${f.label}`}
            onClick={() => downloadFile(scope, sessionId, thread, version.id, f.role)}>
            <Download size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </div>
      ))}
    </div>
  );
}
