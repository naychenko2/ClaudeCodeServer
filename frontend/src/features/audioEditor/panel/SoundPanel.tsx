// Панель «Звук» рабочей области (ADR-021 §3, макет docs/mockups/audio-editor-v2.html и
// audio-editor-v2-proposal.md, «Панель «Звук»») на общем каркасе GenerationPanel. Живёт и в
// проекте, и в правой колонке личного чата (projectId = null → область personal).
//
// «Настройки»: режим → операция → поставщик → модель → поля операции → «Дополнительно» по схеме
// модели; закреплённый низ с «− N +», ценой из котировки и запуском. Настройки пишутся в нить
// (без нити — в префы режима), входы операции — туда же отдельной частью inputs, остальное — в браузер
// на ту же нить (panel/inputs.ts). «Голоса» — библиотека voices/ проекта; «Выбрать» ставит голос в
// поле операции.

import { useEffect, useMemo, useRef, useState } from 'react';
import { AudioLines, Combine, Cpu, Lock, Mic, Scissors, SlidersHorizontal, X } from 'lucide-react';
import {
  Badge, GenerationPanel, IconButton, SegmentedControl, C, FS, SP, REVEAL_PANEL_EVENT, ICON_SIZE,
  clearGenDraft, followPeeked, noteGenDraft, useAgentPick,
  type GenerationFoot, type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { audioApi, type AudioMode, type AudioOp, type AudioPrefs, type AudioQuote, type AudioThread, type AudioThreadSettings } from '../api';
import { MODE_LABEL, opInfo } from '../ops';
import { audioScope, isPersonalScope } from '../scope';
import { focusLabel, queueBadge } from '../strip/summary';
import { releaseFocus } from '../thread/actions';
import {
  focusThread, getCatalog, getComposerText, getJobsOf, getPrefs, getSelection, getShortcutMode, setPieceFieldOpen, setSelection,
  soundDraftKey, SOUND_PANEL, useAudioStoreVersion, useAudioThreads,
} from '../thread/threadStore';
import { hasRvcModel, voicePickValue, pickedSlug } from '../voices/model';
import { useLibraryVoice } from '../voices/useLibraryVoice';
import { VoicesTab } from '../voices/VoicesTab';
import { CloneRefusalNote } from './CloneRefusalNote';
import { ConcatFields } from './ConcatFields';
import {
  inputsKey, LIBRARY_VOICE_OPS, mergeInputs, migrateLocal, readInputs, saveSettings, serverPiece, toServerInputs, writeInputs,
  type PanelInputs,
} from './inputs';
import {
  isNoAi, modelOptions, nextSettings, panelOps, pillOf, priceLines, providerOptions, pruneFields, resolvePanel, runReason,
  splitSchema, voicePick, type PanelState, type SettingsPatch,
} from './model';
import { heavyWarning, licenseWarning } from './music';
import { MusicFields } from './MusicFields';
import { ProcessFields, trimReady, VoiceFields } from './OpFields';
import { pendingOperation, takeOperation } from './opRequest';
import { withFirstPiece } from '../thread/procMenu';
import { readPiece } from './piece';
import type { PieceBinding } from './PieceField';
import { AdvancedForm } from './ParamField';
import { Hint, ic, Label, Opt, Row } from './primitives';
import { dialogueText, musicDuration, quoteRequest, runPanel, type CloneRefusal } from './run';
import { useSchema } from './schema';

type Tab = 'settings' | 'voices';
const isTab = (t: unknown): t is Tab => t === 'settings' || t === 'voices';

// Вкладку из revealWorkspacePanel(sound, tab) ловим на уровне модуля: закрытая панель
// монтируется уже ПОСЛЕ события, и её собственный слушатель его бы пропустил
let wanted: Tab | null = null;
const subs = new Set<() => void>();
const takeWanted = () => { const t = wanted; wanted = null; return t; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
    if (d?.key !== SOUND_PANEL || !isTab(d.tab)) return;
    wanted = d.tab;
    subs.forEach(fn => fn());
  });
}

const MODES: { value: AudioMode; label: string }[] = (['voice', 'music', 'process'] as AudioMode[])
  .map(m => ({ value: m, label: MODE_LABEL[m] }));
const SAVE_DELAY = 500;
const QUOTE_DELAY = 600;

// Выбор человека, ещё не доехавший до сервера, — поверх нити (или префов режима без нити)
function overlay(thread: AudioThread | null, prefs: AudioPrefs, pending: AudioThreadSettings | null) {
  if (!pending) return { thread, prefs };
  if (thread) return { thread: { ...thread, settings: pending }, prefs };
  return {
    thread: null,
    prefs: {
      ...prefs,
      [pending.mode]: {
        operation: pending.operation, provider: pending.provider, model: pending.model, count: pending.count ?? null,
        fields: pending.fields, inputs: pending.inputs ?? null,
      },
    },
  };
}

export function SoundPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const { sessionId } = ctx;
  const scope = audioScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const threads = useAudioThreads(scope, sessionId);
  useAudioStoreVersion();
  const thread = threads.focus ? threads.threads.find(t => t.id === threads.focus) ?? null : null;
  const catalog = getCatalog(scope);
  const [tab, setTab] = useState<Tab>(() => takeWanted() ?? 'settings');
  // Опущенная шторка держит низ с ценой и запуском на любой вкладке: ради него её и опускают
  // Шторка, пришедшая на смену опущенной соседке по клику на карточку, тоже опущена
  const [peeked, setPeeked] = useState(() => followPeeked(SOUND_PANEL));
  const agentPick = useAgentPick(sessionId, SOUND_PANEL);

  useEffect(() => {
    const on = () => { const t = takeWanted(); if (t) setTab(t); };
    subs.add(on);
    return () => { subs.delete(on); };
  }, []);

  // ── Настройки: цепочка нить → префы режима → умолчание, плюс несохранённый выбор ──
  const [pending, setPending] = useState<AudioThreadSettings | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Отложенное сохранение правки: уходит сразу, если звук сменили или панель закрыли раньше таймера
  const flushLater = useRef<(() => void) | null>(null);
  const threadId = thread?.id ?? null;
  // Черновик элемента «Работаем с» (genDrafts): правка поля его ставит, запуск снимает
  const draftKey = threadId ? soundDraftKey(threadId) : null;
  useEffect(() => { setPending(null); }, [threadId, sessionId]);
  const eff = overlay(thread, getPrefs(scope), pending);
  const state: PanelState = resolvePanel(eff.thread, eff.prefs, catalog, pending?.mode ?? getShortcutMode(sessionId) ?? 'voice');

  const flush = (next: AudioThreadSettings) => {
    void saveSettings(scope, sessionId, thread, next).then(() => {
      setPending(p => (p === next ? null : p));
    });
  };
  const change = (patch: SettingsPatch, debounced = false) => {
    const next = nextSettings(state, patch);
    setPending(next);
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    flushLater.current = null;
    if (debounced) {
      flushLater.current = () => flush(next);
      timer.current = setTimeout(() => { timer.current = null; flushLater.current = null; flush(next); }, SAVE_DELAY);
    } else flush(next);
  };
  // Клик по другой карточке не сбрасывает правку: недосохранённое уходит в СВОЮ нить (flush
  // держит её в замыкании) до смены звука и при закрытии панели
  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    const f = flushLater.current;
    flushLater.current = null;
    f?.();
  }, [threadId, sessionId]);

  // ── Просьба карточки («Обрезать», «Перегенерировать кусок»): сперва её нить в работу, потом операция ──
  const opReq = pendingOperation(sessionId);
  const focusing = useRef(0);
  useEffect(() => {
    if (!sessionId || !opReq) return;
    if (opReq.threadId !== threadId) {
      if (focusing.current !== opReq.seq) {
        focusing.current = opReq.seq;
        void focusThread(scope, sessionId, opReq.threadId);
      }
      return;
    }
    const r = takeOperation(sessionId);
    if (!r) return;
    setTab('settings');
    const mode = opInfo(r.op)?.mode;
    // «Склеить с другим звуком»: эта версия — первым куском, одним сохранением с операцией
    if (mode && r.piece) {
      // Входы — из хранилища самой нити: local ещё может держать прежнюю, фокус только что сменился
      const cur = mergeInputs(readInputs(key), state.inputs, threads.threads);
      const next = { ...cur, concat: withFirstPiece(cur.concat, r.piece) };
      writeInputs(key, next);
      setLocal(next);
      change({ mode, operation: r.op, inputs: toServerInputs(r.op, next, pieceForServer, personal) });
    } else if (mode && (r.op !== state.op || mode !== state.mode)) change({ mode, operation: r.op });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- отрабатываем по новой просьбе или смене нити
  }, [opReq?.seq, threadId, sessionId]);

  // ── Кусок: выделение нити, общее с волной карточки ──
  const versionId = thread?.currentVersionId ?? null;
  const piece: PieceBinding | null = sessionId && threadId && versionId ? { sessionId, threadId, versionId } : null;
  const pieceSel = readPiece(sessionId, threadId);
  const pieceOpen = tab === 'settings' && (state.op === 'repaint' || state.op === 'trim') && !!piece;
  useEffect(() => {
    if (!sessionId) return;
    setPieceFieldOpen(sessionId, pieceOpen ? threadId : null);
    return () => setPieceFieldOpen(sessionId, null);
  }, [sessionId, pieceOpen, threadId]);

  // ── Входы: белый список — в настройках на сервере, остальное — на нить в браузере ──
  const key = inputsKey(scope, sessionId, threadId);
  const [local, setLocal] = useState<PanelInputs>(() => readInputs(key));
  useEffect(() => { setLocal(readInputs(key)); }, [key]);
  const inputs = mergeInputs(local, state.inputs, threads.threads);
  // Кусок для сервера: выделение нити, а пока его не подняли из настроек — сохранённый
  const pieceForServer = pieceSel ?? serverPiece(state.inputs);
  // Отправляем, только когда серверная часть правда изменилась
  const saveInputs = (next: PanelInputs, sel = pieceForServer) => {
    const raw = toServerInputs(state.op, next, sel, personal);
    if (JSON.stringify(raw) !== JSON.stringify(state.inputs ?? null)) change({ inputs: raw }, true);
  };
  const setInputs = (patch: Partial<PanelInputs>) => {
    const next = { ...inputs, ...patch };
    writeInputs(key, next);
    setLocal(next);
    saveInputs(next);
  };
  const [reference, setReference] = useState<File | null>(null);
  useEffect(() => { setReference(null); }, [key]);

  // Перенос старых входов из браузера: когда настройки нити (или префы) уже пришли — раз на нить и
  // операцию, чтобы входы чужой операции (куски склейки) переехали, когда её выберут
  const migrated = useRef<string | null>(null);
  const loaded = !!thread || !!catalog;
  useEffect(() => {
    const at = `${key}|${state.op}`;
    if (!sessionId || !loaded || migrated.current === at) return;
    migrated.current = at;
    const moved = migrateLocal(key, state.inputs, state.op, personal);
    setLocal(readInputs(key));
    if (moved) change({ inputs: moved });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- раз на нить и операцию, как только есть что сверить
  }, [key, loaded, sessionId, state.op]);

  // Кусок из настроек на сервере поднимаем в выделение (после перезагрузки его видит и волна)
  useEffect(() => {
    if (!sessionId || !threadId || !versionId || getSelection(sessionId, threadId)) return;
    const saved = serverPiece(state.inputs);
    if (saved) setSelection(sessionId, threadId, { ...saved, versionId });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по смене нити или версии
  }, [sessionId, threadId, versionId]);

  // Правка куска (поле или волна) — в настройки; первая встреча с нитью ничего не пишет
  const selSig = pieceSel ? `${pieceSel.start}|${pieceSel.end}` : '';
  const seenSel = useRef<string | null>(null);
  useEffect(() => {
    const prev = seenSel.current;
    seenSel.current = `${threadId}#${selSig}`;
    if (prev === null || !prev.startsWith(`${threadId}#`) || prev === seenSel.current) return;
    saveInputs(inputs, pieceSel);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по смене выделения
  }, [threadId, selSig]);

  // ── Голос из библиотеки: «Выбрать» на вкладке «Голоса» ──
  const pickVoice = (slug: string) => {
    const pick = voicePick(state, inputs, voicePickValue(slug), personal);
    if ('inputs' in pick) setInputs(pick.inputs);
    else change(pick.patch);
    setTab('settings');
  };

  // Отказ запуска «клон MiniMax протух или не создан» — до новой нити, операции или голоса
  const [refusal, setRefusal] = useState<CloneRefusal | null>(null);
  useEffect(() => { setRefusal(null); }, [threadId, state.op, inputs.voice]);

  // ── Схема модели: поля операции и «Дополнительно» ──
  const noAi = isNoAi(state.op);
  const schema = useSchema(noAi ? null : state.provider?.key ?? null, noAi ? null : state.model?.id ?? null, state.op);
  const { main, extra } = splitSchema(schema.schema, state.mode);
  // Поля, которых у модели нет, сервер отверг бы — чистим, как только схема пришла
  useEffect(() => {
    if (!schema.schema) return;
    const pruned = pruneFields(state.fields, schema.schema);
    if (Object.keys(pruned).length !== Object.keys(state.fields).length) change({ fields: pruned });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- сверяемся только с новой схемой
  }, [schema.schema]);
  const setField = (k: string, v: unknown) => {
    const fields = { ...state.fields };
    if (v === undefined) delete fields[k];
    else fields[k] = v;
    change({ fields }, true);
    noteGenDraft(draftKey);
  };
  // Правка полей операции человеком — черновик элемента
  const editInputs = (patch: Partial<PanelInputs>) => { setInputs(patch); noteGenDraft(draftKey); };

  // ── Котировка: цена, ETA и отказ поставщика ──
  const text = state.op === 'dialogue' ? dialogueText(inputs) : getComposerText(sessionId);
  const providers = providerOptions(catalog, state.op, personal);
  const providerOpt = state.provider ? providers.find(p => p.key === state.provider!.key) ?? null : null;
  const [quote, setQuote] = useState<AudioQuote | null>(null);
  const [quoteError, setQuoteError] = useState<string | null>(null);
  const fieldsKey = JSON.stringify(state.fields);
  const canQuote = !!sessionId && !noAi && !!providerOpt && !providerOpt.disabled && !!state.model;
  useEffect(() => {
    setQuote(null);
    setQuoteError(null);
    if (!canQuote || !sessionId) return;
    let alive = true;
    const t = setTimeout(() => {
      audioApi.quote(scope, sessionId, quoteRequest({
        scope, sessionId, thread, state, fields: state.fields, text, durationSec: musicDuration(state, inputs),
      })).then(
        q => { if (alive) setQuote(q); },
        (e: Error) => { if (alive) setQuoteError(e.message || 'Котировка не получилась'); },
      );
    }, QUOTE_DELAY);
    return () => { alive = false; clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- пересчёт по смыслу настроек, а не по ссылкам
  }, [canQuote, scope, sessionId, threadId, state.op, state.providerKey, state.modelId, state.count, fieldsKey, text.length, inputs.durationSec]);

  const [busy, setBusy] = useState(false);
  const libraryVoice = LIBRARY_VOICE_OPS.has(state.op) && !personal && !!inputs.voice;
  // Вид голоса из библиотеки нужен только смене голоса: модель RVC берёт лишь голос с парой .pth и .index
  const libVoice = useLibraryVoice(scope, sessionId, state.op === 'convertVoice' && libraryVoice ? pickedSlug(inputs.voice) : null, tab);
  const reason = runReason({
    sessionId, thread, state, provider: providerOpt, text,
    hasReference: !!reference || !!inputs.referencePath.trim() || libraryVoice,
    hasVoiceModel: !!inputs.voiceModelPath.trim() || libraryVoice,
    libraryRvc: libVoice ? hasRvcModel(libVoice) : null,
    clips: inputs.clipPaths.filter(p => p.trim()).length,
    replicas: inputs.replicas.filter(r => r.text.trim()).length,
    trimReady: trimReady(inputs.trim, pieceSel),
    pieces: inputs.concat.pieces.length,
    quoteError,
    music: inputs,
    piece: pieceSel,
  });
  const running = !!thread?.launches.some(l => l.status === 'running');
  const queue = noAi ? null : queueBadge(getJobsOf(sessionId, threadId), running, state);
  const single = noAi;
  const run = async () => {
    if (!sessionId || busy || reason) return;
    setBusy(true);
    setRefusal(null);
    const ok = await runPanel({
      scope, sessionId, thread, state, fields: pruneFields(state.fields, schema.schema), inputs, reference, text, piece: pieceSel,
    }, setRefusal);
    // Запуск забрал черновик — пометка «черновик» уходит
    if (ok) clearGenDraft(draftKey);
    setBusy(false);
  };
  const foot: GenerationFoot = {
    reason: reason ?? undefined,
    queue: queue ?? undefined,
    count: single ? 1 : state.count,
    price: priceLines({
      op: state.op, provider: state.provider, model: state.model, quote, count: state.count, textLength: text.length,
      pieces: inputs.concat.pieces.length,
    }),
    runLabel: busy ? 'Запускаем…' : opInfo(state.op)?.run ?? 'Запустить',
    // Правка без ИИ: значок самой правки вместо ✦ и без «− N +» — результат всегда один
    ...(noAi ? { runIcon: ic(state.op === 'concat' ? Combine : Scissors), noCount: true } : {}),
    onRun: () => { void run(); },
    ...(single
      ? { maxCount: 1 as const, maxCountHint: 'Правка без ИИ даёт один результат' }
      : { maxCount: catalog?.maxCount ?? 4, onCountChange: (count: number) => change({ count }) }),
  };

  // ── Разметка ──
  const opLabel = opInfo(state.op)?.label;
  const subtitle = [MODE_LABEL[state.mode], opLabel].filter(Boolean).join(' · ');
  const release = () => { if (sessionId) void releaseFocus(scope, sessionId, thread); };

  let context;
  if (tab === 'voices') {
    context = personal
      ? <span>Библиотека голосов — только в проекте</span>
      : <span>Папка <code>voices/</code> проекта · подключённый голос уходит в каждую озвучку</span>;
  } else if (state.op === 'concat') {
    context = <span>Куски — звук этого чата · результат — новый файл, исходники не меняются</span>;
  } else if (thread) {
    context = (
      <>
        <span data-sound-context="" style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Работаем с: <b style={{ color: C.textHeading }}>{focusLabel(thread)}</b> · операция возьмёт её на вход
        </span>
      </>
    );
  } else {
    context = <span>Новый звук · результат ляжет в ленту новой карточкой</span>;
  }

  const heavyOps = useMemo(() => new Set(catalog?.providers.flatMap(p => p.models.flatMap(m => m.caps.heavyOps ?? [])) ?? []), [catalog]);

  let body;
  if (tab === 'voices') {
    body = (
      <div style={{ paddingTop: SP.sm }}>
        <VoicesTab scope={scope} sessionId={sessionId} selected={pickedSlug(inputs.voice)} onPick={pickVoice} />
      </div>
    );
  } else if (!catalog) {
    body = <div style={{ fontSize: FS.sm, color: C.textMuted, paddingTop: SP.sm }}>Загружаем…</div>;
  } else {
    const pill = pillOf(state.op);
    const fieldsProps = {
      state, personal, main, values: state.fields, setField, inputs, setInputs: editInputs, reference, setReference,
      onOp: (op: AudioOp) => change(op === 'transcribe' && state.mode !== 'process' ? { mode: 'process' } : { operation: op }),
      isMobile: ctx.isMobile, piece, onOpenVoices: () => setTab('voices'),
    };
    const license = noAi ? null : licenseWarning(state.model);
    const heavy = noAi ? null : heavyWarning(state.op, state.model);
    body = (
      <div data-sound-settings="">
        <div style={{ height: SP.sm }} />
        <SegmentedControl<AudioMode> value={state.mode} options={MODES} onChange={mode => change({ mode })} />

        <Label>Операция</Label>
        <Row>
          {panelOps(state.mode).map(o => (
            <Opt key={o.op} dataKey={`op:${o.op}`} on={pill === o.op} name={o.label}
              badges={heavyOps.has(o.op) ? <span title="Тяжёлая задача: одна за раз, минуты" style={{ display: 'inline-flex', color: C.textMuted }}>{ic(Cpu)}</span> : undefined}
              onClick={() => change({ operation: o.op })} />
          ))}
        </Row>

        {noAi ? (
          state.op !== 'concat' && <div style={{ marginTop: SP.md }}><Badge tone="neutral">Правка без ИИ · бесплатно</Badge></div>
        ) : (
          <>
            <Label>Поставщик</Label>
            {providers.length === 0
              ? <Hint>Звучать нечем: поставщиков не настроил администратор</Hint>
              : (
                <Row>
                  <Opt dataKey="provider:auto" on={state.providerKey === null} name="Авто"
                    hint={state.provider && !state.providerKey ? `сейчас ${state.provider.label}` : 'первый, кто умеет'}
                    onClick={() => change({ provider: null })} />
                  {providers.map(p => (
                    <Opt key={p.key} dataKey={`provider:${p.key}`} on={state.providerKey === p.key} disabled={p.disabled}
                      title={p.reason ?? undefined}
                      name={<>{p.locked && ic(Lock)}{p.label}</>}
                      hint={p.reason ?? p.unit}
                      onClick={() => change({ provider: p.key })} />
                  ))}
                </Row>
              )}
            {state.provider && (
              <>
                <Label aside={state.provider.key === 'fal' ? 'отобранные · остальные — по запросу' : undefined}>Модель</Label>
                <Row>
                  {modelOptions(state.provider, state.op, catalog.autoModelId).map(m => (
                    <Opt key={m.id} dataKey={`model:${m.id}`} on={state.modelId === m.id} name={m.label}
                      badges={<>
                        {m.ru && <Badge size="xs" tone={m.ru === 'RU' ? 'success' : 'neutral'}>{m.ru}</Badge>}
                        {m.license && <Badge size="xs" tone="neutral">{m.license}</Badge>}
                        {m.heavy && <Badge size="xs" tone="warning" title="Тяжёлая задача: одна за раз, минуты">тяжёлая</Badge>}
                      </>}
                      hint={m.unit ?? undefined}
                      onClick={() => change({ model: m.id })} />
                  ))}
                </Row>
              </>
            )}
            {license && <div data-sound-license=""><Hint warn>{license}</Hint></div>}
            {heavy && <div data-sound-heavy=""><Hint warn>{heavy}</Hint></div>}
          </>
        )}

        {state.mode === 'voice' && <VoiceFields {...fieldsProps} />}
        {state.mode === 'process' && state.op !== 'concat' && <ProcessFields {...fieldsProps} />}
        {state.op === 'concat' && (
          <ConcatFields c={inputs.concat} set={patch => editInputs({ concat: { ...inputs.concat, ...patch } })}
            threads={threads.threads} personal={personal} />
        )}
        {state.mode === 'music' && <MusicFields {...fieldsProps} />}

        {refusal && <CloneRefusalNote scope={scope} refusal={refusal} onCleared={() => setRefusal(null)} />}

        {!noAi && state.model && (
          <AdvancedForm schema={schema.schema} fields={extra}
            values={state.fields} error={schema.error} loading={schema.loading}
            onChange={setField} onReset={() => change({ fields: {} })} />
        )}
      </div>
    );
  }

  return (
    <GenerationPanel<Tab>
      title="Звук"
      subtitle={subtitle || undefined}
      icon={ic(AudioLines, ICON_SIZE.sm)}
      tabs={[
        { value: 'settings', label: 'Настройки', icon: ic(SlidersHorizontal) },
        { value: 'voices', label: 'Голоса', icon: ic(Mic) },
      ]}
      tab={tab}
      onTabChange={setTab}
      context={context}
      contextAction={tab === 'settings' && state.op !== 'concat' && thread
        ? <IconButton size="xs" title="Снять выбор звука" ariaLabel="Снять выбор звука" onClick={release}>{ic(X)}</IconButton>
        : undefined}
      panelKey={SOUND_PANEL}
      agentPick={agentPick}
      draftKey={tab === 'settings' && state.op !== 'concat' ? draftKey : null}
      foot={tab === 'settings' || (peeked && ctx.isMobile) ? foot : undefined}
      peeked={peeked}
      onPeekedChange={setPeeked}
      // Подзаголовок уже в шапке опущенной шторки: в сводке — с чем работаем и чем
      peekSummary={[thread ? focusLabel(thread) : 'Новый звук', noAi ? null : state.provider?.label, noAi ? null : state.model?.label].filter(Boolean).join(' · ')}
      onClose={ctx.onClose}
      layout={ctx.isMobile ? 'sheet' : 'column'}
    >
      {body}
    </GenerationPanel>
  );
}
