// Панель «Звук» рабочей области (ADR-021 §3, макет docs/mockups/audio-editor-v2.html и
// audio-editor-v2-proposal.md, «Панель «Звук»») на общем каркасе GenerationPanel. Живёт и в
// проекте, и в правой колонке личного чата (projectId = null → область personal).
//
// «Настройки» (вариант А, docs/mockups/audio-panel-v3-proposal.md): режим → операция одним списком
// с группами → поля операции → «Чем» со списком «Исполнитель» → «Ещё настройки» по схеме модели;
// закреплённый низ с «− N +», ценой из котировки и запуском. Настройки пишутся в нить
// (без нити — в префы режима), входы операции — туда же отдельной частью inputs, остальное — в браузер
// на ту же нить (panel/inputs.ts). «Голоса» — библиотека voices/ проекта; «Выбрать» ставит голос в
// поле операции.

import { useEffect, useRef, useState } from 'react';
import { AudioLines, Combine, Mic, Scissors, Send, SlidersHorizontal, X } from 'lucide-react';
import {
  GenerationPanel, IconButton, SegmentedControl, Select, C, FS, SP, REVEAL_PANEL_EVENT, ICON_SIZE,
  clearGenDraft, consumePreset, followPeeked, noteGenDraft, returnLabel, returnToOrigin, useAgentPick,
  usePanelReturnTo, usePendingPreset,
  type GenerationFoot, type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { audioApi, type AudioOp, type AudioQuote, type AudioStemSet } from '../api';
import { MODE_LABEL, opInfo } from '../ops';
import { audioScope, isPersonalScope } from '../scope';
import { focusLabel, queueBadge } from '../strip/summary';
import { SoundModeSwitch } from '../strip/SoundModeSwitch';
import { changeSoundSettings, flushSoundSettings, releaseFocus, soundPanelState } from '../thread/actions';
import {
  focusThread, getCatalog, getComposerText, getJobsOf, getSelection, setComposerText, setPieceFieldOpen, setSelection,
  soundDraftKey, SOUND_PANEL, useAudioStoreVersion, useAudioThreads,
} from '../thread/threadStore';
import { hasRvcModel, voicePickValue, pickedSlug } from '../voices/model';
import { useLibraryVoice } from '../voices/useLibraryVoice';
import { VoicesTab } from '../voices/VoicesTab';
import { CloneRefusalNote } from './CloneRefusalNote';
import { ConcatFields } from './ConcatFields';
import {
  inputsKey, LIBRARY_VOICE_OPS, mergeInputs, migrateLocal, readInputs, serverPiece, toServerInputs,
  writeInputs,
  type PanelInputs,
} from './inputs';
import {
  isNoAi, pillOf, priceLines, providerOptions, pruneFields, runReason,
  splitSchema, tuckSchema, voicePick, type PanelState, type SettingsPatch,
} from './model';
import { heavyWarning, licenseWarning, vocalLanguage } from './music';
import { MusicFields } from './MusicFields';
import { Language, ProcessFields, trimReady, VoiceFields } from './OpFields';
import { pendingOperation, takeOperation } from './opRequest';
import { parseSoundPreset, type SoundPreset } from './preset';
import { withFirstPiece } from '../thread/procMenu';
import { readPiece } from './piece';
import type { PieceBinding } from './PieceField';
import { AdvancedForm } from './ParamField';
import { ExecutorField } from './ExecutorField';
import { composerHintOf, opOptions } from './opGroups';
import { Hint, ic, Label } from './primitives';
import { stemChoices, stemPatch, stemValue } from './stems';
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

const QUOTE_DELAY = 600;

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

  // ── Настройки: цепочка нить → префы режима → умолчание, плюс несохранённый выбор (общий с полосой) ──
  const threadId = thread?.id ?? null;
  // Черновик элемента «Работаем с» (genDrafts): правка поля его ставит, запуск снимает
  const draftKey = threadId ? soundDraftKey(threadId) : null;
  const state: PanelState = soundPanelState(scope, sessionId, thread);
  const change = (patch: SettingsPatch, debounced = false) => { changeSoundSettings(scope, sessionId, patch, debounced); };
  // Клик по другой карточке не сбрасывает правку: недосохранённое уходит в СВОЮ нить до смены
  // звука и при закрытии панели
  useEffect(() => () => flushSoundSettings(sessionId), [threadId, sessionId]);

  // ── Заготовка из другой панели («Сочинить под фильм…» в «Видео») ──
  // Заготовка — это НОВЫЙ звук: прежний в работе снимается, затем режим и операция, и только
  // когда настройки перестроились под них — длительность, «Инструментал» и стиль (поле ввода)
  const returnTo = usePanelReturnTo(SOUND_PANEL);
  const pendingRaw = usePendingPreset(SOUND_PANEL);
  const preset = useRef<SoundPreset | null>(null);
  const presetStage = useRef(0);
  const [presetFrom, setPresetFrom] = useState<string | null>(null);
  // Черновик заготовки (preset.thread): пока он в работе, строка контекста помнит, откуда заготовка
  const [presetThread, setPresetThread] = useState<string | null>(null);
  if (pendingRaw && !preset.current) {
    preset.current = parseSoundPreset(pendingRaw);
    presetStage.current = 0;
  }
  useEffect(() => {
    const pr = preset.current;
    if (!pendingRaw) return;
    if (!pr || !sessionId) { consumePreset(SOUND_PANEL); preset.current = null; return; }
    setTab('settings');
    if (pr.threadId) {
      // Черновик вызвавшей панели: берём в работу его, прежний звук не снимаем отдельно
      if (thread?.id !== pr.threadId) {
        if (presetStage.current === 0) { presetStage.current = 1; void focusThread(scope, sessionId, pr.threadId); }
        return;
      }
    } else if (thread) { if (presetStage.current === 0) { presetStage.current = 1; void releaseFocus(scope, sessionId, thread); } return; }
    const wantOp = pr.op ?? state.op;
    if ((state.mode !== 'music' || state.op !== wantOp) && presetStage.current < 2) {
      presetStage.current = 2;
      change({ mode: 'music', operation: wantOp });
      return;
    }
    if (pr.durationSec !== undefined || pr.instrumental !== undefined) {
      setInputs({
        ...(pr.durationSec !== undefined ? { durationSec: pr.durationSec } : {}),
        ...(pr.instrumental !== undefined ? { instrumental: pr.instrumental } : {}),
      });
    }
    if (pr.style) setComposerText(sessionId, pr.style);
    setPresetFrom(pr.from ?? '');
    setPresetThread(pr.threadId ?? null);
    noteGenDraft(draftKey);
    preset.current = null;
    consumePreset(SOUND_PANEL);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- по приходу заготовки и перестройке настроек под неё
  }, [pendingRaw, threadId, state.mode, state.op, sessionId]);
  useEffect(() => { if (threadId && threadId !== presetThread) { setPresetFrom(null); setPresetThread(null); } }, [threadId, presetThread]);

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
  const { main, extra, labels } = tuckSchema(state.mode, splitSchema(schema.schema, state.mode));
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
  } else if (thread && presetFrom !== null && thread.id === presetThread) {
    context = <span>Новый звук · заготовка из «Видео»{presetFrom ? `: ${presetFrom}` : ''}</span>;
  } else if (thread) {
    context = (
      <>
        <span data-sound-context="" style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Работаем с: <b style={{ color: C.textHeading }}>{focusLabel(thread)}</b> · операция возьмёт её на вход
        </span>
      </>
    );
  } else {
    context = presetFrom !== null
      ? <span>Новый звук · заготовка из «Видео»{presetFrom ? `: ${presetFrom}` : ''}</span>
      : <span>Новый звук · результат ляжет в ленту новой карточкой</span>;
  }

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
    const fieldsProps = {
      state, personal, main, values: state.fields, setField, inputs, setInputs: editInputs, reference, setReference,
      onOp: (op: AudioOp) => change(op === 'transcribe' && state.mode !== 'process' ? { mode: 'process' } : { operation: op }),
      isMobile: ctx.isMobile, piece, onOpenVoices: () => setTab('voices'),
    };
    const license = noAi ? null : licenseWarning(state.model);
    const heavy = noAi ? null : heavyWarning(state.op, state.model);
    const composerHint = composerHintOf(state.op);
    body = (
      <div data-sound-settings="">
        <div style={{ height: SP.sm }} />
        {/* Зеркало переключателя полосы: выбор общий, «Обработка» без звука спрашивает, что обработать */}
        <SoundModeSwitch scope={scope} sessionId={sessionId} mode={state.mode} thread={thread} threads={threads.threads} isMobile={ctx.isMobile} />

        <Label>Операция</Label>
        <div data-sound-op="">
          <Select<AudioOp> value={pillOf(state.op)} options={opOptions(state.mode, !!thread?.currentVersionId, state.op)}
            onChange={op => { if (op) change({ operation: op }); }} />
        </div>
        {catalog.providers.length === 0 && !noAi && <Hint warn>Звучать нечем: поставщиков не настроил администратор</Hint>}

        {state.op === 'separate' && (
          <div data-field="stems">
            <Label>Что получить</Label>
            <SegmentedControl<AudioStemSet> value={stemValue(state.model) ?? ('' as AudioStemSet)} options={stemChoices(state.provider)}
              onChange={set => { const patch = stemPatch(state.provider, state.model, set); if (patch) change(patch); }} />
          </div>
        )}
        {state.mode === 'voice' && <VoiceFields {...fieldsProps} />}
        {state.mode === 'process' && state.op !== 'concat' && <ProcessFields {...fieldsProps} />}
        {state.op === 'concat' && (
          <ConcatFields c={inputs.concat} set={patch => editInputs({ concat: { ...inputs.concat, ...patch } })}
            threads={threads.threads} personal={personal} />
        )}
        {state.mode === 'music' && <MusicFields {...fieldsProps} />}
        {composerHint && <Hint>{ic(Send)} {composerHint} — в поле ввода чата</Hint>}

        {!noAi && catalog.providers.length > 0 && (
          <>
            <ExecutorField catalog={catalog} state={state} personal={personal} price={foot.price?.[0] ?? ''} onChange={change} isMobile={ctx.isMobile} />
            {license && <div data-sound-license=""><Hint warn>{license}</Hint></div>}
            {heavy && <div data-sound-heavy=""><Hint warn>{heavy}</Hint></div>}
          </>
        )}

        {refusal && <CloneRefusalNote scope={scope} refusal={refusal} onCleared={() => setRefusal(null)} />}

        {!noAi && state.model && (
          <AdvancedForm schema={schema.schema} fields={extra}
            values={state.fields} error={schema.error} loading={schema.loading}
            onChange={setField} onReset={() => change({ fields: {} })} labels={labels}
            lead={vocalLanguage(state.op, state.model) && state.model ? (
              <Language languages={state.model.caps.languages} value={inputs.language} onChange={v => editInputs({ language: v })}
                auto="Язык вокала — как определит модель" />
            ) : undefined}
            leadSet={vocalLanguage(state.op, state.model) && !!inputs.language} isMobile={ctx.isMobile} />
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
      returnLink={returnTo ? { label: returnLabel(returnTo), onClick: () => returnToOrigin(SOUND_PANEL, returnTo, sessionId ?? undefined) } : undefined}
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
