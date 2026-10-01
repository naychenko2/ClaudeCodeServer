import { useEffect, useState } from 'react';
import { AudioLines } from 'lucide-react';
import { Island, IslandHeader, Button } from '../components/ui';
import { C, FONT, FS, ISLAND, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { showToast } from '../lib/toast';
import {
  AudioPlayer, AudioWave, StemMixer, TO_END, fmtSelection, type AudioSelection, type MixerState,
} from '../features/audioEditor/player';

// Демо-звук для витрины: синусы собираются прямо в браузере в WAV — без сети и файлов
const RATE = 22050;
const LEN = 10;

type Voice = { freq: number; amp: number; beat?: number };

function sineWav(voices: Voice[], seconds = LEN): string {
  const n = Math.floor(RATE * seconds);
  const buf = new ArrayBuffer(44 + n * 2);
  const v = new DataView(buf);
  const str = (o: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
  str(0, 'RIFF'); v.setUint32(4, 36 + n * 2, true); str(8, 'WAVE');
  str(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, RATE, true); v.setUint32(28, RATE * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  str(36, 'data'); v.setUint32(40, n * 2, true);
  for (let i = 0; i < n; i++) {
    const t = i / RATE;
    let s = 0;
    for (const vc of voices) {
      // Пульсация громкости — чтобы на волне было что рассмотреть
      const env = vc.beat ? 0.35 + 0.65 * Math.abs(Math.sin(Math.PI * vc.beat * t)) : 1;
      s += vc.amp * env * Math.sin(2 * Math.PI * vc.freq * t);
    }
    const fade = Math.min(1, t * 4, (seconds - t) * 4);
    v.setInt16(44 + i * 2, Math.max(-1, Math.min(1, s * fade)) * 0x7fff, true);
  }
  return URL.createObjectURL(new Blob([buf], { type: 'audio/wav' }));
}

const STEM_VOICES: { id: string; name: string; voices: Voice[] }[] = [
  { id: 'vocals', name: 'вокал', voices: [{ freq: 440, amp: 0.5, beat: 0.7 }] },
  { id: 'drums', name: 'барабаны', voices: [{ freq: 90, amp: 0.7, beat: 2 }] },
  { id: 'bass', name: 'бас', voices: [{ freq: 55, amp: 0.6, beat: 0.25 }] },
  { id: 'other', name: 'прочее', voices: [{ freq: 660, amp: 0.25, beat: 1.3 }] },
];

interface DemoUrls { a: string; b: string; short: string; stems: Record<string, string> }

function useDemoUrls(): DemoUrls | null {
  const [urls, setUrls] = useState<DemoUrls | null>(null);
  useEffect(() => {
    const made: DemoUrls = {
      a: sineWav([{ freq: 330, amp: 0.6, beat: 0.5 }]),
      b: sineWav([{ freq: 330, amp: 0.4, beat: 0.5 }, { freq: 495, amp: 0.3, beat: 1.5 }]),
      short: sineWav([{ freq: 523, amp: 0.5, beat: 1 }], 4),
      stems: Object.fromEntries(STEM_VOICES.map(s => [s.id, sineWav(s.voices)])),
    };
    setUrls(made);
    return () => {
      [made.a, made.b, made.short, ...Object.values(made.stems)].forEach(u => URL.revokeObjectURL(u));
    };
  }, []);
  return urls;
}

// Пики без звука — для волны «как с сервера»
const STATIC_PEAKS = Array.from({ length: 160 }, (_, i) => 0.25 + 0.75 * Math.abs(Math.sin(i / 9)) * (0.6 + 0.4 * Math.sin(i / 3.1)));

const ALL_MUTED: MixerState = { mute: Object.fromEntries(STEM_VOICES.map(s => [s.id, true])), solo: null, gain: {} };

export function AudioPlayerKitSection() {
  const urls = useDemoUrls();
  const [sel, setSel] = useState<AudioSelection | null>({ start: 2.4, end: 5.8 });
  const [ab, setAb] = useState('a');
  const [waveSel, setWaveSel] = useState<AudioSelection | null>(null);
  const [wavePos, setWavePos] = useState(3);

  return (
    <Island>
      <IslandHeader
        icon={<AudioLines size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Звук: плеер"
      />
      <div style={{ padding: ISLAND.pad, display: 'flex', flexDirection: 'column', gap: SP.xl }}>
        <Block label="AudioPlayer — две версии, A/B и выделение куска (протяните по волне; Shift+←/→, [ ], Shift+End — «до конца», Esc)">
          {urls && (
            <AudioPlayer
              sources={[
                { key: 'a', label: 'A · в1', url: urls.a, title: 'Версия 1' },
                { key: 'b', label: 'B · в2', url: urls.b, title: 'Версия 2' },
              ]}
              activeKey={ab}
              onActiveChange={setAb}
              selection={sel}
              onSelectionChange={setSel}
              selectionActions={<>
                <Button size="xs" variant="ghost" onClick={() => showToast('Обрезать', 'Подключим к данным в шаге 2.8б')}>Обрезать</Button>
                <Button size="xs" variant="ghost" onClick={() => setSel(s => (s ? { ...s, end: TO_END } : s))}>До конца</Button>
              </>}
            />
          )}
          <Mono>selection = {sel ? JSON.stringify(sel) : 'null'} · звучит {ab === 'a' ? 'A' : 'B'}</Mono>
        </Block>

        <Block label="AudioPlayer compact — одна версия 4 с, без выделения">
          {urls && <AudioPlayer compact sources={[{ key: 's', label: 'в1', url: urls.short }]} />}
        </Block>

        <Block label="AudioWave — готовые пики без звука, клик перематывает, выделение контролируемое">
          <AudioWave
            peaks={STATIC_PEAKS} duration={16} position={wavePos} onSeek={setWavePos}
            selection={waveSel} onSelectionChange={setWaveSel}
          />
          <Mono>позиция {wavePos.toFixed(1)} с · {waveSel ? fmtSelection(waveSel, 16) : 'кусок не выделен'}</Mono>
          <AudioWave peaks={[]} duration={16} size="sm" ariaLabel="Волна ещё считается" />
        </Block>

        <Block label="StemMixer — M заглушает, S солирует и перекрывает M, громкость −24…+6 дБ">
          {urls && (
            <StemMixer
              stems={STEM_VOICES.map(s => ({ id: s.id, name: s.name, url: urls.stems[s.id] }))}
              duration={LEN}
              folderName="podcast-intro.stems/"
              defaultValue={{ mute: {}, solo: null, gain: { vocals: -3 } }}
              onMix={plan => showToast(`Свели ${plan.stems.length} из ${plan.total}`, plan.description)}
              onDownload={id => showToast('Сохранить стем', id)}
            />
          )}
        </Block>

        <Block label="StemMixer — все стемы заглушены: кнопка серая">
          <StemMixer
            stems={STEM_VOICES.map(s => ({ id: s.id, name: s.name, peaks: STATIC_PEAKS }))}
            duration={LEN}
            defaultValue={ALL_MUTED}
            onMix={() => {}}
          />
        </Block>
      </div>
    </Island>
  );
}

function Block({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0, maxWidth: 560 }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary }}>{label}</div>
      {children}
    </div>
  );
}

function Mono({ children }: { children: React.ReactNode }) {
  return <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{children}</div>;
}
