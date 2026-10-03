// «Поля операции» режимов «Голос» и «Обработка» (макет audio-editor-v2-proposal.md, таблицы «Голос» и
// «Обработка»). Поле, которого у модели нет в схеме, не рисуется вовсе. Входы запуска вне схемы
// (язык, образец, записи, реплики, кусок обрезки) — из PanelInputs нити.

import { useRef } from 'react';
import type { ReactNode } from 'react';
import { ArrowDown, ArrowUp, Mic, Plus, Upload, X } from 'lucide-react';
import {
  Button, Checkbox, IconButton, SegmentedControl, Select, TextArea, TextField, C, FS, SP,
} from 'aihome_shell/kit';
import type { AudioFileFormat, AudioOp, AudioParamField } from '../api';
import type { AudioSelection } from '../player/selection';
import { pickedSlug } from '../voices/model';
import { LIBRARY_VOICE_OPS, type PanelInputs, type Replica, type TrimInputs } from './inputs';
import { MUSIC_FIELD_LABELS, SOURCE_LABEL, SPEAK_SOURCES, type PanelState } from './model';
import { ParamField } from './ParamField';
import { PieceField, type PieceBinding } from './PieceField';
import { Hint, ic, Label } from './primitives';

const LANG: Record<string, string> = {
  ru: 'Русский', en: 'Английский', de: 'Немецкий', fr: 'Французский', es: 'Испанский', it: 'Итальянский',
  pt: 'Португальский', zh: 'Китайский', ja: 'Японский', ko: 'Корейский', uk: 'Украинский', pl: 'Польский',
  tr: 'Турецкий', ar: 'Арабский', nl: 'Нидерландский', hi: 'Хинди',
};
export const langLabel = (code: string) => LANG[code] ?? code.toUpperCase();

export interface OpFieldsProps {
  state: PanelState;
  personal: boolean;
  main: AudioParamField[];
  values: Record<string, unknown>;
  setField: (key: string, v: unknown) => void;
  inputs: PanelInputs;
  setInputs: (patch: Partial<PanelInputs>) => void;
  reference: File | null;
  setReference: (f: File | null) => void;
  onOp: (op: AudioOp) => void;
  isMobile: boolean;
  // Кусок нити (обрезка, «Перегенерировать кусок»); null — нити нет
  piece: PieceBinding | null;
  // Открыть вкладку «Голоса» — выбрать голос из библиотеки
  onOpenVoices?: () => void;
}

export function SchemaFields({ fields, values, setField, only }: {
  fields: AudioParamField[]; values: Record<string, unknown>; setField: (k: string, v: unknown) => void; only?: string[];
}) {
  const list = only ? fields.filter(f => only.includes(f.key)) : fields;
  // Ключи музыки сюда попадают только в режиме «Музыка» (splitSchema) — подпись берём оттуда
  return <>{list.map(f => (
    <ParamField key={f.key} field={f} value={values[f.key]} onChange={v => setField(f.key, v)} label={MUSIC_FIELD_LABELS[f.key]} />
  ))}</>;
}

export function Language({ languages, value, onChange, auto }: {
  languages: string[]; value: string; onChange: (v: string) => void; auto: string;
}) {
  if (!languages.length) return null;
  return (
    <div data-field="language" style={{ marginBottom: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>Язык</div>
      <Select value={value} onChange={onChange} placeholder={auto} options={languages.map(l => ({ value: l, label: langLabel(l) }))} />
    </div>
  );
}

// Образец: файл с диска (живёт до перезагрузки) или путь в проекте (в личном чате путей нет)
function Reference({ title, hint, file, setFile, path, setPath, personal }: {
  title: string; hint?: ReactNode; file: File | null; setFile: (f: File | null) => void;
  path: string; setPath: (p: string) => void; personal: boolean;
}) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <div data-field="reference" style={{ marginBottom: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>{title}</div>
      <input ref={input} type="file" accept="audio/*" hidden onChange={e => { setFile(e.target.files?.[0] ?? null); e.target.value = ''; }} />
      {file ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary }}>
          <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{file.name}</span>
          <IconButton size="xs" title="Убрать образец" ariaLabel="Убрать образец" onClick={() => setFile(null)}>{ic(X)}</IconButton>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', flexWrap: 'wrap' }}>
          <Button size="xs" variant="secondary" leftIcon={ic(Upload)} onClick={() => input.current?.click()}>Файл…</Button>
          {!personal && (
            <div style={{ flex: 1, minWidth: 140 }}>
              <TextField value={path} onChange={setPath} placeholder="или путь в проекте: voices/anya/1.wav" />
            </div>
          )}
        </div>
      )}
      {hint && <Hint>{hint}</Hint>}
    </div>
  );
}

// Голос из библиотеки «Голоса»: выбирается на вкладке, здесь — что выбрано и как снять
function LibraryVoice({ value, onClear, onOpen, isMobile }: { value: string; onClear: () => void; onOpen?: () => void; isMobile?: boolean }) {
  const slug = pickedSlug(value);
  return (
    <div data-field="library-voice" style={{ marginBottom: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>Голос из библиотеки</div>
      {slug ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary }}>
          {ic(Mic)}
          <span data-picked-voice={slug} style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{slug}</span>
          {onOpen && <Button size={isMobile ? 'md' : 'xs'} variant="ghost" onClick={onOpen}>Сменить</Button>}
          <IconButton size={isMobile ? 'lg' : 'xs'} title="Не брать голос из библиотеки" ariaLabel="Не брать голос из библиотеки" onClick={onClear}>{ic(X)}</IconButton>
        </div>
      ) : (
        onOpen && <Button size={isMobile ? 'md' : 'xs'} variant="secondary" leftIcon={ic(Mic)} onClick={onOpen}>Выбрать в «Голосах»</Button>
      )}
    </div>
  );
}

// Список путей проекта (записи для обучения RVC)
export function PathList({ paths, onChange }: { paths: string[]; onChange: (p: string[]) => void }) {
  return (
    <div data-field="clips" style={{ marginBottom: SP.sm }}>
      {paths.map((p, i) => (
        <div key={i} style={{ display: 'flex', gap: SP.xs, alignItems: 'center', marginBottom: SP.xxs }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={p} placeholder="путь в проекте: records/andrey-1.wav" onChange={v => onChange(paths.map((x, j) => (j === i ? v : x)))} />
          </div>
          <IconButton size="xs" title="Убрать запись" ariaLabel="Убрать запись" onClick={() => onChange(paths.filter((_, j) => j !== i))}>{ic(X)}</IconButton>
        </div>
      ))}
      <Button size="xs" variant="dashed" leftIcon={ic(Plus)} onClick={() => onChange([...paths, ''])} disabled={paths.length >= 20}>Запись</Button>
    </div>
  );
}

function Replicas({ list, onChange }: { list: Replica[]; onChange: (r: Replica[]) => void }) {
  const set = (i: number, patch: Partial<Replica>) => onChange(list.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const move = (i: number, d: number) => {
    const next = [...list];
    [next[i], next[i + d]] = [next[i + d], next[i]];
    onChange(next);
  };
  const chars = list.reduce((n, r) => n + r.text.length, 0);
  return (
    <div data-field="replicas">
      <Label aside={`${list.length} · ${chars} симв. из 10 000`}>Реплики · до 10 голосов</Label>
      {list.map((r, i) => (
        <div key={i} style={{ border: `1px solid ${C.borderLight}`, borderRadius: 8, padding: SP.xs, marginBottom: SP.xs }}>
          <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', marginBottom: SP.xxs }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>{i + 1}</span>
            <div style={{ flex: 1, minWidth: 0 }}>
              <TextField value={r.voice} placeholder="Голос: Аня" onChange={v => set(i, { voice: v })} />
            </div>
            <IconButton size="xs" title="Выше" ariaLabel="Выше" disabled={i === 0} onClick={() => move(i, -1)}>{ic(ArrowUp)}</IconButton>
            <IconButton size="xs" title="Ниже" ariaLabel="Ниже" disabled={i === list.length - 1} onClick={() => move(i, 1)}>{ic(ArrowDown)}</IconButton>
            <IconButton size="xs" title="Убрать реплику" ariaLabel="Убрать реплику" onClick={() => onChange(list.filter((_, j) => j !== i))}>{ic(X)}</IconButton>
          </div>
          <TextArea autoGrow minHeight={40} maxHeight={260} value={r.text} placeholder="Текст реплики" onChange={v => set(i, { text: v })} />
        </div>
      ))}
      <Button size="xs" variant="dashed" leftIcon={ic(Plus)} disabled={list.length >= 50}
                // Голоса чередуются по кругу: новая реплика — голосом позапрошлой
        onClick={() => onChange([...list, { voice: list.length >= 2 ? list[list.length - 2].voice : '', text: '' }])}>
        Реплика
      </Button>
    </div>
  );
}

// ── Голос ──

export function VoiceFields(p: OpFieldsProps) {
  const { state: s, inputs, setInputs } = p;
  const op = s.op;
  const caps = s.model?.caps ?? null;
  const library = LIBRARY_VOICE_OPS.has(op) && !p.personal;
  const picked = library && !!pickedSlug(inputs.voice);
  const ref = (title: string, hint?: ReactNode) => !picked && (
    <Reference title={title} hint={hint} file={p.reference} setFile={p.setReference} personal={p.personal}
      path={inputs.referencePath} setPath={v => setInputs({ referencePath: v })} />
  );
  return (
    <div data-op-fields={op}>
      {SPEAK_SOURCES.includes(op) && (
        <>
          <Label>Источник голоса</Label>
          <SegmentedControl<AudioOp> value={op} onChange={p.onOp} touch={p.isMobile}
            options={SPEAK_SOURCES.map(o => ({ value: o, label: SOURCE_LABEL[o]! }))} />
          <div style={{ height: SP.sm }} />
        </>
      )}
      {library && <LibraryVoice value={inputs.voice} onClear={() => setInputs({ voice: '' })} onOpen={p.onOpenVoices} isMobile={p.isMobile} />}
      {op === 'cloneVoice' && ref('Образец голоса', 'Чистая речь 5–60 с; длиннее обрежем до 15 с')}
      {op === 'dialogue' && <Replicas list={inputs.replicas} onChange={r => setInputs({ replicas: r })} />}
      {op === 'convertVoice' && caps?.voiceKinds.includes('clone') && ref('Чей голос', 'Образец 1–30 с')}
      {op === 'convertVoice' && caps?.voiceKinds.includes('rvc') && !p.personal && !picked && (
        <div data-field="voice-model" style={{ marginBottom: SP.sm }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>Модель голоса</div>
          <TextField value={inputs.voiceModelPath} placeholder="voices/andrey/voice.pth" onChange={v => setInputs({ voiceModelPath: v })} />
          <div style={{ height: SP.xxs }} />
          <TextField value={inputs.voiceIndexPath} placeholder="voices/andrey/voice.index (необязательно)" onChange={v => setInputs({ voiceIndexPath: v })} />
        </div>
      )}
      {op === 'trainVoice' && (
        <>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>Записи голоса · 1–20 файлов</div>
          {p.personal
            ? <Hint warn>Обучение берёт записи из проекта — в личном чате оно недоступно</Hint>
            : <PathList paths={inputs.clipPaths} onChange={c => setInputs({ clipPaths: c })} />}
          <Hint>2–30 минут чистой речи или пения без музыки. Музыку сначала уберите: Обработка → Стемы</Hint>
        </>
      )}
      {op !== 'trainVoice' && op !== 'convertVoice' && caps && (
        <Language languages={caps.languages} value={inputs.language} onChange={v => setInputs({ language: v })}
          auto="Как у модели по умолчанию" />
      )}
      <SchemaFields fields={p.main} values={p.values} setField={p.setField} />
      {op === 'convertVoice' && s.model?.id === 'seed-vc' && <Hint>В режиме «Речь» Seed-VC сдвиг высоты пока не применяет</Hint>}
      {op !== 'transcribe' && (
        <Button size={p.isMobile ? 'md' : 'xs'} variant="ghost" onClick={() => p.onOp('transcribe')} style={{ paddingLeft: 0, marginTop: SP.xs }}>В текст →</Button>
      )}
    </div>
  );
}

// ── Обработка ──

export const FORMATS: { value: AudioFileFormat; label: string }[] = [
  { value: 'wav', label: 'WAV' }, { value: 'mp3', label: 'MP3' },
  { value: 'flac', label: 'FLAC' }, { value: 'ogg', label: 'OGG' },
];

// Кусок обрезки — выделение нити, а не поле входов (panel/piece.ts)
export const trimReady = (t: TrimInputs, piece: AudioSelection | null) =>
  piece !== null || t.gainDb !== 0 || t.fadeIn > 0 || t.fadeOut > 0 || t.normalize;

function NumRow({ label, value, onChange, unit }: { label: string; value: string; onChange: (s: string) => void; unit?: string }) {
  return (
    <div style={{ flex: '1 1 120px', minWidth: 0 }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>{label}{unit ? `, ${unit}` : ''}</div>
      <TextField value={value} onChange={onChange} placeholder="—" />
    </div>
  );
}

export function TrimFields({ t, set, piece, inEditor }: { t: TrimInputs; set: (patch: Partial<TrimInputs>) => void; piece: PieceBinding | null; inEditor?: boolean }) {
  const n = (s: string) => Number(s.replace(',', '.')) || 0;
  return (
    <div data-op-fields="trim">
      {piece && <PieceField binding={piece} aside="без ИИ · каждая правка — новая версия" inEditor={inEditor} />}
      <Label>Громкость</Label>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <NumRow label="Громкость" unit="дБ" value={t.gainDb ? String(t.gainDb) : ''} onChange={s => set({ gainDb: n(s) })} />
        <NumRow label="Нарастание" unit="с" value={t.fadeIn ? String(t.fadeIn) : ''} onChange={s => set({ fadeIn: Math.max(0, n(s)) })} />
        <NumRow label="Затухание" unit="с" value={t.fadeOut ? String(t.fadeOut) : ''} onChange={s => set({ fadeOut: Math.max(0, n(s)) })} />
      </div>
      <label style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary, marginTop: SP.xs }}>
        <Checkbox checked={t.normalize} onChange={v => set({ normalize: v })} ariaLabel="Нормализовать до −14 LUFS" />
        Нормализовать до −14 LUFS
      </label>
      <Label>Формат</Label>
      <Select<AudioFileFormat> value={t.format} placeholder="Как у исходника" onChange={v => set({ format: v })} options={FORMATS} />
    </div>
  );
}

export function ProcessFields(p: OpFieldsProps) {
  const { state: s, inputs, setInputs } = p;
  const op = s.op;
  if (op === 'trim') return <TrimFields t={inputs.trim} piece={p.piece} set={patch => setInputs({ trim: { ...inputs.trim, ...patch } })} />;
  const caps = s.model?.caps ?? null;
  return (
    <div data-op-fields={op}>
      {op === 'master' && (
        <Reference title="Образец звучания" hint="Громкость и АЧХ возьмём как у этого трека" file={p.reference} setFile={p.setReference}
          personal={p.personal} path={inputs.referencePath} setPath={v => setInputs({ referencePath: v })} />
      )}
      {op === 'upsample' && <Hint>AudioSR берёт записи до 120 с — длиннее сначала обрежьте</Hint>}
      {op === 'transcribe' && caps && (
        <Language languages={caps.languages} value={inputs.language} onChange={v => setInputs({ language: v })} auto="Определить сам" />
      )}
      {op === 'transcribe' && <Hint>Получите .txt, .srt и .lrc</Hint>}
      {op === 'align' && <Hint>Вставьте точный текст записи в поле ввода — получите .srt и .lrc</Hint>}
      <SchemaFields fields={p.main} values={p.values} setField={p.setField} />
    </div>
  );
}
