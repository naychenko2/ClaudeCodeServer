// Поле по схеме модели и автоформа «Ещё настройки» (макет audio-editor-v2-proposal.md, пункт 6
// «Вкладки «Настройки»»; вариант А audio-panel-v3-proposal.md): тип поля → ползунок, список, флажок или строка; у каждого поля русская
// подпись, код параметра и умолчание. Не тронутое поле в params не уходит — модель берёт своё.

import { useState } from 'react';
import type { ReactNode } from 'react';
import { ChevronDown, ChevronRight, RotateCcw } from 'lucide-react';
import { Button, Checkbox, C, FONT, FS, SP, Select, TextArea, TextField } from 'aihome_shell/kit';
import type { AudioParamField, AudioParamSchema } from '../api';
import { OP_FIELD_LABELS, widgetOf } from './model';
import { ic, Label } from './primitives';

const SOURCE_TITLE: Record<string, (s: AudioParamSchema) => ReactNode> = {
  'fal-openapi': s => <>по схеме модели <code>{s.model}</code></>,
  'local-catalog': () => 'параметры локального движка',
};

const show = (v: unknown) => (v === undefined || v === null ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v));

const num = (s: string, integer: boolean): number | undefined => {
  const n = integer ? parseInt(s, 10) : parseFloat(s.replace(',', '.'));
  return Number.isFinite(n) ? n : undefined;
};

export function ParamField({ field, value, onChange, label }: {
  field: AudioParamField; value: unknown; onChange: (v: unknown) => void; label?: string;
}) {
  const off = field.passed === false;
  const title = label ?? OP_FIELD_LABELS[field.key] ?? field.title ?? field.key;
  const def = field.default;
  const w = widgetOf(field);
  let control: ReactNode;
  if (w === 'checkbox') {
    control = <Checkbox checked={value === undefined ? def === true : value === true} disabled={off} ariaLabel={title} onChange={onChange} />;
  } else if (w === 'select') {
    const opts = (field.enum ?? []).map(v => ({ value: String(v), label: String(v) }));
    control = (
      <Select value={value === undefined ? '' : String(value)} disabled={off} title={title}
        placeholder={def !== undefined && def !== null ? `По умолчанию: ${show(def)}` : 'По умолчанию'}
        options={opts}
        onChange={v => {
          if (v === '') return onChange(undefined);
          const raw = field.enum!.find(x => String(x) === v);
          onChange(raw ?? v);
        }} />
    );
  } else if (w === 'slider') {
    const integer = field.type === 'integer';
    const cur = typeof value === 'number' ? value : typeof def === 'number' ? def : field.min!;
    const step = integer ? 1 : Math.max((field.max! - field.min!) / 100, 0.01);
    control = (
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <input type="range" min={field.min!} max={field.max!} step={step} value={cur} disabled={off} aria-label={title}
          onChange={e => onChange(integer ? Math.round(Number(e.target.value)) : Number(e.target.value))}
          style={{ flex: 1, minWidth: 0, accentColor: C.textSecondary }} />
        <span style={{ fontSize: FS.sm, color: value === undefined ? C.textMuted : C.textPrimary, minWidth: 36, textAlign: 'right' }}>
          {Math.round(cur * 100) / 100}
        </span>
      </div>
    );
  } else if (w === 'number') {
    control = (
      <TextField value={show(value)} disabled={off} placeholder={def != null ? show(def) : 'по умолчанию'}
        onChange={s => onChange(s.trim() === '' ? undefined : num(s, field.type === 'integer'))} />
    );
  } else if (w === 'textarea') {
    control = <TextArea autoGrow minHeight={56} maxHeight={200} value={show(value)} disabled={off}
      placeholder={def != null ? show(def) : undefined} onChange={s => onChange(s === '' ? undefined : s)} />;
  } else if (w === 'text') {
    control = <TextField value={show(value)} disabled={off} placeholder={def != null ? show(def) : undefined}
      onChange={s => onChange(s === '' ? undefined : s)} />;
  } else {
    // Объект или массив — JSON строкой; невалидный JSON держим текстом, сервер скажет, что не так
    control = <TextField mono value={show(value)} disabled={off} placeholder={def != null ? show(def) : 'JSON'}
      onChange={s => {
        if (s.trim() === '') return onChange(undefined);
        try { onChange(JSON.parse(s)); } catch { onChange(s); }
      }} />;
  }
  return (
    <div data-param={field.key} style={{ marginBottom: SP.sm, opacity: off ? 0.6 : 1 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, marginBottom: SP.xxs, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{title}</span>
        <code style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{field.key}</code>
        {def !== undefined && def !== null && <span style={{ fontSize: FS.xs, color: C.textMuted }}>· по умолчанию {show(def)}</span>}
      </div>
      {control}
      {off && <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>Пока не передаётся{field.notPassed ? `: ${field.notPassed}` : ''}</div>}
      {!off && field.description && <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>{field.description}</div>}
    </div>
  );
}

// «› Ещё настройки · N» со сводкой справа — свёрнуто по умолчанию; «Сбросить» снимает все поля формы
// Сводка справа от «Ещё настройки»: «всё по умолчанию» или сколько полей тронуто
export function advancedSummary(fields: AudioParamField[], values: Record<string, unknown>, leadSet = false): string {
  const n = fields.filter(f => values[f.key] !== undefined && values[f.key] !== '').length + (leadSet ? 1 : 0);
  return n ? `изменено: ${n}` : 'всё по умолчанию';
}

export function AdvancedForm({ schema, fields, values, error, loading, onChange, onReset, lead, leadSet, labels }: {
  schema: AudioParamSchema | null;
  fields: AudioParamField[];
  error: string | null;
  loading: boolean;
  values: Record<string, unknown>;
  onChange: (key: string, v: unknown) => void;
  onReset: () => void;
  // Поле вне схемы первой строкой (язык вокала) и задано ли оно; подписи полей, унесённых с виду
  lead?: ReactNode;
  leadSet?: boolean;
  labels?: Record<string, string>;
}) {
  const count = fields.length + (lead ? 1 : 0);
  const [open, setOpen] = useState(false);
  return (
    <div data-sound-advanced={open ? 'open' : 'closed'}>
      <Button size="sm" variant="ghost" fullWidth onClick={() => setOpen(!open)} leftIcon={ic(open ? ChevronDown : ChevronRight)}
        style={{ marginTop: SP.xs, justifyContent: 'flex-start', fontWeight: 400 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flex: 1, minWidth: 0 }}>
          <span style={{ flexShrink: 0 }}>Ещё настройки{count ? ` · ${count}` : ''}</span>
          <span data-sound-advanced-summary="" style={{
            flex: 1, minWidth: 0, textAlign: 'right', color: C.textMuted, fontSize: FS.xs,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {advancedSummary(fields, values, leadSet)}
          </span>
        </span>
      </Button>
      {open && (
        <div style={{ paddingTop: SP.xs }}>
          {loading && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем схему…</div>}
          {error && <div style={{ fontSize: FS.sm, color: C.warningText }}>{error}</div>}
          {lead}
          {schema && (
            <>
              <Label>{SOURCE_TITLE[schema.source]?.(schema) ?? `по схеме ${schema.provider}`}{schema.stale ? ' · из кеша' : ''}</Label>
              {fields.length === 0 && !lead
                ? <div style={{ fontSize: FS.sm, color: C.textMuted }}>У модели нет дополнительных параметров</div>
                : fields.map(f => <ParamField key={f.key} field={f} value={values[f.key]} onChange={v => onChange(f.key, v)} label={labels?.[f.key]} />)}
              {fields.length > 0 && (
                <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs }}>
                  <span style={{ flex: 1, fontSize: FS.xs, color: C.textMuted }}>Поле не тронуто — модель берёт своё значение по умолчанию</span>
                  <Button size="xs" variant="secondary" leftIcon={ic(RotateCcw)} onClick={onReset}>Сбросить</Button>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}
