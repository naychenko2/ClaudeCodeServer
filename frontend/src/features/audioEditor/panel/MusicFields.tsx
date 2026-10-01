// «Поля операции» режима «Музыка» (макет audio-editor-v2-proposal.md, таблица «Музыка»): слова с
// секциями и «Инструментал», длительность, язык вокала, кусок, дорожки; темп, тональность, сила
// кавера и партитура ABC — поля схемы модели. Чего у модели нет, то не рисуется.

import { Button, TextArea, TextField, C, FS, SP } from 'aihome_shell/kit';
import type { AudioParamField } from '../api';
import { opInfo } from '../ops';
import {
  durationRange, insertSection, instrumentalBlocked, lyricsField, lyricsRequired, SECTIONS, trackLabel, vocalLanguage, YUE2,
} from './music';
import { Language, SchemaFields, type OpFieldsProps } from './OpFields';
import { PieceField } from './PieceField';
import { Hint, Label, Opt, Row } from './primitives';

// Дорожки рисуем пилюлями, а не списком схемы
const TRACK_KEYS = ['track', 'tracks'];

const trackValues = (f: AudioParamField | undefined): string[] =>
  ((f?.enum ?? f?.items?.enum ?? []) as unknown[]).map(String);

function Lyrics({ p }: { p: OpFieldsProps }) {
  const { state: s, inputs, setInputs } = p;
  const op = s.op;
  const required = lyricsRequired(op, s.model);
  const blocked = instrumentalBlocked(op, s.model);
  const instrumental = op === 'song' && inputs.instrumental && !blocked;
  const max = s.model?.caps.maxTextChars ?? null;
  let title = 'Слова';
  if (op === 'cover') title = required ? 'Слова · поются на мелодию исходника' : 'Слова (необязательно)';
  if (op === 'repaint') title = 'Слова для куска (необязательно)';
  return (
    <div data-field="lyrics">
      {op === 'song' && (
        <Row>
          <Opt dataKey="lyrics:on" on={!instrumental} name="Со словами" onClick={() => setInputs({ instrumental: false })} />
          <Opt dataKey="lyrics:off" on={instrumental} name="Инструментал" disabled={!!blocked} title={blocked ?? undefined}
            hint={blocked ?? undefined} onClick={() => setInputs({ instrumental: true })} />
        </Row>
      )}
      {!instrumental && (
        <>
          <Label aside={max ? `${inputs.lyrics.length} / ${max}` : `${inputs.lyrics.length} симв.`}>{title}</Label>
          <TextArea autoGrow minHeight={96} maxHeight={360} value={inputs.lyrics} onChange={v => setInputs({ lyrics: v })}
            placeholder={'[Verse]\nПервый куплет…\n\n[Chorus]\nПрипев…'} />
          <div style={{ display: 'flex', gap: SP.xxs, flexWrap: 'wrap', marginTop: SP.xxs }}>
            {SECTIONS.map(sec => (
              <Button key={sec} size="xs" variant="ghost" onClick={() => setInputs({ lyrics: insertSection(inputs.lyrics, sec) })}>{sec}</Button>
            ))}
          </div>
          {op === 'song' && !required && <Hint>Пустые слова — инструментал</Hint>}
        </>
      )}
    </div>
  );
}

function Duration({ p, min, max }: { p: OpFieldsProps; min: number; max: number }) {
  const v = p.inputs.durationSec;
  return (
    <div data-field="duration" style={{ marginTop: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>Длительность, с</div>
      <TextField value={v === null ? '' : String(v)} placeholder={`как у модели · ${min}–${max}`}
        onChange={t => {
          const n = Math.round(Number(t.replace(',', '.')));
          p.setInputs({ durationSec: t.trim() && Number.isFinite(n) ? n : null });
        }} />
      <Hint>От {min} до {max} с</Hint>
    </div>
  );
}

function Tracks({ field, value, multi, onChange }: {
  field: AudioParamField; value: unknown; multi: boolean; onChange: (v: unknown) => void;
}) {
  const all = trackValues(field);
  const def = field.default;
  if (multi) {
    const cur = Array.isArray(value) ? value.map(String) : Array.isArray(def) ? def.map(String) : [];
    return (
      <div data-field="tracks">
        <Label aside={`${cur.length} из ${all.length}`}>Инструменты</Label>
        <Row>
          {all.map(t => (
            <Opt key={t} dataKey={`track:${t}`} on={cur.includes(t)} name={trackLabel(t)}
              onClick={() => onChange(cur.includes(t) ? cur.filter(x => x !== t) : [...cur, t])} />
          ))}
        </Row>
      </div>
    );
  }
  const cur = typeof value === 'string' ? value : typeof def === 'string' ? def : all[0];
  return (
    <div data-field="track">
      <Label>Дорожка</Label>
      <Row>
        {all.map(t => <Opt key={t} dataKey={`track:${t}`} on={cur === t} name={trackLabel(t)} onClick={() => onChange(t)} />)}
      </Row>
    </div>
  );
}

export function MusicFields(p: OpFieldsProps) {
  const { state: s, inputs, setInputs } = p;
  const op = s.op;
  const range = durationRange(op, s.model);
  const track = p.main.find(f => f.key === 'track');
  const tracks = p.main.find(f => f.key === 'tracks');
  const rest = p.main.filter(f => !TRACK_KEYS.includes(f.key));
  return (
    <div data-op-fields={op}>
      <Label>Поля операции · {opInfo(op)?.label}</Label>
      {op === 'repaint' && (p.piece
        ? <PieceField binding={p.piece} aside="что перегенерировать" />
        : <Hint>Выберите звук в ленте — кусок выделяется на его волне</Hint>)}
      {lyricsField(op, s.model) && <Lyrics p={p} />}
      {vocalLanguage(op, s.model) && s.model && (
        <div style={{ marginTop: SP.sm }}>
          <Language languages={s.model.caps.languages} value={inputs.language} onChange={v => setInputs({ language: v })}
            auto="Язык вокала — как определит модель" />
        </div>
      )}
      {range && <Duration p={p} min={range.min} max={range.max} />}
      {track && <Tracks field={track} value={p.values.track} multi={false} onChange={v => p.setField('track', v)} />}
      {tracks && <Tracks field={tracks} value={p.values.tracks} multi onChange={v => p.setField('tracks', v)} />}
      {op === 'extract' && (
        <Hint>Нужны вокал и минус или 4–6 стемов разом — быстрее через «Обработка → Стемы»</Hint>
      )}
      <div style={{ marginTop: SP.sm }}>
        <SchemaFields fields={rest} values={p.values} setField={p.setField} />
      </div>
      {s.model?.id === YUE2 && op === 'song' && (
        <Hint>YuE2 вместе с песней кладёт партитуру .abc: поправьте её и вставьте в «Партитуру ABC» — мелодию и аккорды возьмём оттуда</Hint>
      )}
    </div>
  );
}
