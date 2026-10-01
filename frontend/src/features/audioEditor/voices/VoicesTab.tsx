// Вкладка «Голоса» панели «Звук» (ADR-021 §5, записка v2 «Вкладка «Голоса»»): библиотека
// voices/ проекта — список, «Где работает», прослушивание образцов, новый голос из записей,
// переименование, удаление, правка образцов и «Выбрать» для поля «Голос». Голоса живут только
// в проектах: в личном чате — честный пустой экран. Вход один — панель подставляет компонент
// вместо заглушки одной строкой; выбор приходит в onPick как slug, значение поля — voicePickValue.

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Check, ChevronDown, ChevronUp, Lock, Mic, Pencil, Plus, Trash2, X } from 'lucide-react';
import {
  Badge, Button, ConfirmDialog, EmptyState, Field, IconButton, TextArea, TextField, showToast, useFeature,
  FLAGS, C, FS, R, SP, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import { AudioPlayer } from '../player';
import { isPersonalScope } from '../scope';
import { voicesApi, MAX_VOICE_SAMPLE_MB, MAX_VOICE_SAMPLES, type AudioVoice, type VoicesList } from './api';
import {
  EMPTY_TEXT, EMPTY_TITLE, MINIMAX_TTL_TEXT, PERSONAL_TEXT, PERSONAL_TITLE,
  canAddSamples, isStale, newVoiceProblem, recreateAction, sampleRemoval, samplesProblem,
  voicesView, voiceSubtitle, whereWorks, type WhereStatus,
} from './model';
import { EMPTY_SAMPLES, SamplesPicker, toDraft, type SamplesValue } from './SamplesPicker';

export interface VoicesTabProps {
  // Ключ области звука: id проекта или personal
  scope: string;
  sessionId: string | null;
  // Выбранный в панели голос (slug) — подсвечивается в списке
  selected?: string | null;
  onPick: (slug: string) => void;
}

const ic = (I: typeof Mic, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;
const errText = (e: unknown) => (e as Error)?.message || 'Запрос не выполнен';

const STATUS: Record<WhereStatus, { label: string; tone: 'success' | 'warning' | 'neutral' }> = {
  ok: { label: 'работает', tone: 'success' },
  stale: { label: 'клон удалён', tone: 'warning' },
  none: { label: 'не создан', tone: 'neutral' },
  no: { label: 'не умеет', tone: 'neutral' },
};

export function VoicesTab(props: VoicesTabProps) {
  const enabled = useFeature(FLAGS.audioEditor);
  return enabled ? <VoicesTabBody {...props} /> : null;
}

function VoicesTabBody({ scope, sessionId, selected = null, onPick }: VoicesTabProps) {
  const personal = isPersonalScope(scope);
  const [list, setList] = useState<VoicesList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(() => {
    if (personal) return;
    setError(null);
    voicesApi.list(scope, sessionId)
      .then(setList)
      .catch(e => setError(errText(e)));
  }, [personal, scope, sessionId]);

  useEffect(() => { setList(null); load(); }, [load]);

  const put = (v: AudioVoice) => setList(l => {
    const voices = l?.voices ?? [];
    return { available: true, voices: voices.some(x => x.slug === v.slug) ? voices.map(x => (x.slug === v.slug ? v : x)) : [...voices, v] };
  });
  const drop = (slug: string) => setList(l => (l ? { ...l, voices: l.voices.filter(x => x.slug !== slug) } : l));

  const view = voicesView(personal, list, error);

  if (view.kind === 'personal') {
    return <EmptyState compact icon={ic(Lock, ICON_SIZE.sm)} title={PERSONAL_TITLE} subtitle={PERSONAL_TEXT} />;
  }
  if (view.kind === 'loading') {
    return <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.sm }}>Загружаем голоса…</div>;
  }
  if (view.kind === 'error') {
    return <EmptyState compact icon={ic(Mic, ICON_SIZE.sm)} title="Не удалось загрузить голоса" subtitle={view.message}
      action={<Button size="sm" variant="secondary" onClick={load}>Повторить</Button>} />;
  }

  const form = creating && (
    <NewVoiceForm scope={scope}
      onSaved={v => { put(v); setCreating(false); setOpen(v.slug); showToast(`Голос «${v.name}» добавлен в voices/`, '', 'info'); }}
      onClose={() => setCreating(false)} />
  );

  if (view.kind === 'empty') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {!creating && (
          <EmptyState compact inline icon={ic(Mic, ICON_SIZE.sm)} title={EMPTY_TITLE} subtitle={EMPTY_TEXT}
            action={<Button size="sm" variant="primary" leftIcon={ic(Plus)} onClick={() => setCreating(true)}>Голос</Button>} />
        )}
        {form}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      {view.voices.map(v => (
        <VoiceCard key={v.slug} scope={scope} voice={v} expanded={open === v.slug} picked={selected === v.slug}
          onToggle={() => setOpen(o => (o === v.slug ? null : v.slug))}
          onPick={() => onPick(v.slug)} onChanged={put}
          onDeleted={() => { drop(v.slug); if (open === v.slug) setOpen(null); }} />
      ))}
      {form || (
        <Button size="sm" variant="dashed" fullWidth leftIcon={ic(Plus)} onClick={() => setCreating(true)}>Голос</Button>
      )}
    </div>
  );
}

function VoiceCard({ scope, voice: v, expanded, picked, onToggle, onPick, onChanged, onDeleted }: {
  scope: string;
  voice: AudioVoice;
  expanded: boolean;
  picked: boolean;
  onToggle: () => void;
  onPick: () => void;
  onChanged: (v: AudioVoice) => void;
  onDeleted: () => void;
}) {
  const stale = isStale(v);
  return (
    <div data-voice={v.slug} style={{
      borderRadius: R.lg, border: `1px solid ${picked ? C.accent : C.borderLight}`,
      background: picked ? C.accentMuted : C.bgPanel, minWidth: 0,
    }}>
      <button type="button" onClick={onToggle} aria-expanded={expanded} style={{
        all: 'unset', boxSizing: 'border-box', width: '100%', cursor: 'pointer',
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.sm,
      }}>
        <span aria-hidden style={{
          width: 32, height: 32, flex: '0 0 32px', borderRadius: R.max, background: C.bgInset,
          border: `1px solid ${C.border}`, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          fontSize: FS.sm, fontWeight: 600, color: C.textSecondary,
        }}>{v.name.slice(0, 1).toUpperCase()}</span>
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{v.name}</span>
          <span style={{ fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{voiceSubtitle(v)}</span>
        </span>
        {stale && (
          <span title="У одного из поставщиков клон удалён" style={{ display: 'inline-flex', color: C.warningText }}>
            {ic(AlertTriangle, ICON_SIZE.sm)}
          </span>
        )}
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(expanded ? ChevronUp : ChevronDown, ICON_SIZE.sm)}</span>
      </button>
      {expanded && (
        <VoiceDetails scope={scope} voice={v} picked={picked} onPick={onPick} onChanged={onChanged} onDeleted={onDeleted} />
      )}
    </div>
  );
}

function VoiceDetails({ scope, voice: v, picked, onPick, onChanged, onDeleted }: {
  scope: string;
  voice: AudioVoice;
  picked: boolean;
  onPick: () => void;
  onChanged: (v: AudioVoice) => void;
  onDeleted: () => void;
}) {
  const [sample, setSample] = useState(v.samples[0]?.file ?? null);
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(v.name);
  const [transcript, setTranscript] = useState(v.transcript ?? '');
  const [adding, setAdding] = useState<SamplesValue | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const current = v.samples.some(s => s.file === sample) ? sample : v.samples[0]?.file ?? null;
  const removal = sampleRemoval(v);
  const rows = whereWorks(v);
  const stale = rows.find(r => r.status === 'stale');
  const recreate = recreateAction(null);

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const saveRename = () => run(async () => {
    const patch: { name?: string; transcript?: string } = {};
    if (name.trim() && name.trim() !== v.name) patch.name = name.trim();
    if (transcript.trim() !== (v.transcript ?? '')) patch.transcript = transcript.trim();
    if (patch.name !== undefined || patch.transcript !== undefined) onChanged(await voicesApi.update(scope, v.slug, patch));
    setRenaming(false);
  });

  const addProblem = adding ? samplesProblem(toDraft(adding), v.samples.length) : null;
  const saveSamples = () => run(async () => {
    if (!adding || addProblem) return;
    onChanged(await voicesApi.addSamples(scope, v.slug, adding));
    setAdding(null);
  });

  const lbl = (text: string) => (
    <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em' }}>{text}</div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `0 ${SP.sm}px ${SP.sm}px` }}>
      {v.kind === 'samples' && current && (
        <AudioPlayer compact sources={[{ key: current, label: current, url: voicesApi.fileUrl(scope, v.slug, current), title: current }]} />
      )}

      {v.kind === 'samples' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          {lbl(`Образцы · ${v.samples.length} из ${MAX_VOICE_SAMPLES}`)}
          {v.samples.map(s => (
            <div key={s.file} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
              <Button size="sm" variant={s.file === current ? 'ghostAccent' : 'ghost'} onClick={() => setSample(s.file)}
                style={{ flex: 1, minWidth: 0, justifyContent: 'flex-start' }}>
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.file}</span>
              </Button>
              <IconButton size="xs" ariaLabel={`Убрать образец ${s.file}`} disabled={busy || !removal.allowed}
                title={removal.allowed ? 'Убрать образец' : removal.reason ?? ''}
                onClick={() => run(async () => { onChanged(await voicesApi.removeSample(scope, v.slug, s.file)); })}>
                {ic(X)}
              </IconButton>
            </div>
          ))}
          {!removal.allowed && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{removal.reason}</div>}
          {adding ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, paddingTop: SP.xs }}>
              <SamplesPicker value={adding} onChange={setAdding} room={MAX_VOICE_SAMPLES - v.samples.length} disabled={busy} />
              {addProblem && draftHasAny(adding) && <div style={{ fontSize: FS.xs, color: C.warningText }}>{addProblem}</div>}
              <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'flex-end' }}>
                <Button size="sm" variant="ghost" disabled={busy} onClick={() => setAdding(null)}>Отмена</Button>
                <Button size="sm" variant="primary" loading={busy} disabled={busy || !!addProblem} onClick={saveSamples}>Добавить</Button>
              </div>
            </div>
          ) : canAddSamples(v) && (
            <Button size="sm" variant="dashed" leftIcon={ic(Plus)} disabled={busy} onClick={() => setAdding(EMPTY_SAMPLES)}>Образец</Button>
          )}
        </div>
      )}

      {v.kind === 'rvc' && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
          {v.path}/voice.pth · {v.path}/voice.index
        </div>
      )}

      {renaming ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <Field label="Имя"><TextField value={name} onChange={setName} autoFocus onEnter={saveRename} disabled={busy} /></Field>
          {v.kind === 'samples' && (
            <Field label="Расшифровка" hint="Точный текст записей — по нему клонируют поставщики">
              <TextArea value={transcript} onChange={setTranscript} autoGrow minHeight={56} maxHeight={160} disabled={busy} />
            </Field>
          )}
          <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'flex-end' }}>
            <Button size="sm" variant="ghost" disabled={busy}
              onClick={() => { setRenaming(false); setName(v.name); setTranscript(v.transcript ?? ''); }}>Отмена</Button>
            <Button size="sm" variant="primary" loading={busy} disabled={busy || !name.trim()} onClick={saveRename}>Сохранить</Button>
          </div>
        </div>
      ) : v.kind === 'samples' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          {lbl('Расшифровка')}
          <div style={{ fontSize: FS.sm, color: v.transcript ? C.textPrimary : C.textMuted, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
            {v.transcript || 'Нет — поставщики распознают запись сами'}
          </div>
        </div>
      )}

      {stale && (
        <div data-stale style={{
          display: 'flex', flexDirection: 'column', gap: SP.xs, fontSize: FS.sm, lineHeight: 1.45,
          color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
        }}>
          <span style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start' }}>
            <span style={{ display: 'inline-flex', marginTop: 2 }}>{ic(AlertTriangle)}</span>
            <span>{MINIMAX_TTL_TEXT}. {stale.note}.</span>
          </span>
          <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
            <Button size="sm" variant="secondary" disabled={recreate.disabled} title={recreate.hint ?? undefined}>{recreate.label}</Button>
            {recreate.hint && <span style={{ fontSize: FS.xs }}>{recreate.hint}</span>}
          </span>
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        {lbl('Где работает')}
        {rows.map(r => (
          <div key={r.key} data-where={r.key} style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start', fontSize: FS.sm, minWidth: 0 }}>
            <span style={{ flex: 1, minWidth: 0, color: C.textPrimary }}>
              {r.label}
              <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>{r.note}</span>
            </span>
            <Badge size="xs" tone={STATUS[r.status].tone}>{STATUS[r.status].label}</Badge>
          </div>
        ))}
      </div>

      {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}

      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>
        <Button size="sm" variant={picked ? 'secondary' : 'primary'} leftIcon={picked ? ic(Check) : undefined} disabled={picked} onClick={onPick}>
          {picked ? 'Выбран' : 'Выбрать'}
        </Button>
        <span style={{ flex: 1 }} />
        {!renaming && (
          <IconButton size="sm" title={v.kind === 'samples' ? 'Переименовать и расшифровка' : 'Переименовать'} ariaLabel={`Переименовать «${v.name}»`}
            disabled={busy} onClick={() => setRenaming(true)}>{ic(Pencil)}</IconButton>
        )}
        <IconButton size="sm" title="Удалить голос" ariaLabel={`Удалить «${v.name}»`} disabled={busy} onClick={() => setConfirmDelete(true)}>
          {ic(Trash2)}
        </IconButton>
      </div>

      {confirmDelete && (
        <ConfirmDialog title={`Удалить голос «${v.name}»?`}
          subtitle={`Папка ${v.path}/ с записями удалится из проекта. Клоны у поставщиков останутся до их срока.`}
          confirmLabel="Удалить" confirmVariant="danger"
          onConfirm={async () => {
            try {
              await voicesApi.remove(scope, v.slug);
              setConfirmDelete(false);
              onDeleted();
            } catch (e) {
              setError(errText(e));
              setConfirmDelete(false);
            }
          }}
          onCancel={() => setConfirmDelete(false)} />
      )}
    </div>
  );
}

const draftHasAny = (v: SamplesValue) => v.files.length + v.projectFiles.length > 0;

function NewVoiceForm({ scope, onSaved, onClose }: { scope: string; onSaved: (v: AudioVoice) => void; onClose: () => void }) {
  const [name, setName] = useState('');
  const [transcript, setTranscript] = useState('');
  const [samples, setSamples] = useState<SamplesValue>(EMPTY_SAMPLES);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problem = newVoiceProblem(name, toDraft(samples));

  const submit = async () => {
    if (problem) return;
    setBusy(true);
    setError(null);
    try {
      onSaved(await voicesApi.create(scope, { name, transcript, ...samples }));
    } catch (e) {
      setError(errText(e));
      setBusy(false);
    }
  };

  return (
    <div data-new-voice style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.sm,
      borderRadius: R.lg, border: `1px solid ${C.accent}`, background: C.bgPanel,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <span style={{ flex: 1, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>Новый голос</span>
        <IconButton size="xs" title="Закрыть" ariaLabel="Закрыть форму голоса" disabled={busy} onClick={onClose}>{ic(X)}</IconButton>
      </div>
      <Field label="Имя"><TextField value={name} onChange={setName} placeholder="Например, Аня" autoFocus disabled={busy} /></Field>
      <Field label={`Записи · от 1 до ${MAX_VOICE_SAMPLES}`} hint={`5–15 секунд чистой речи без музыки, до ${MAX_VOICE_SAMPLE_MB} МБ каждая`}>
        <SamplesPicker value={samples} onChange={setSamples} disabled={busy} />
      </Field>
      <Field label="Расшифровка" hint="Точный текст записей. Пусто — поставщики распознают запись сами">
        <TextArea value={transcript} onChange={setTranscript} autoGrow minHeight={56} maxHeight={160} disabled={busy}
          placeholder="Привет! Меня зовут Аня…" />
      </Field>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>
        Голос сразу работает у локальных моделей и Seed-VC. У fal и Higgsfield клон создаётся при первом запуске — цену покажем заранее.
      </div>
      {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' }}>
        {problem && (name.trim() || draftHasAny(samples)) && (
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textMuted }}>{problem}</span>
        )}
        <Button size="sm" variant="ghost" disabled={busy} onClick={onClose}>Отмена</Button>
        <Button size="sm" variant="primary" loading={busy} disabled={busy || !!problem} onClick={submit}>Сохранить в voices/</Button>
      </div>
    </div>
  );
}

