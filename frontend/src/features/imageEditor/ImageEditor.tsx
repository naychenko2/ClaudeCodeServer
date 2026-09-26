// Экран редактора картинок, раскладка v2 (макет docs/mockups/image-editor-v2.html):
// слева шесть секций инструментов, в центре холст или генерация / варианты, справа
// сверху поле промпта, под ним чат картинки (ядро отдаёт его компонентом ImageChat).
// На телефоне холст на весь экран, промпт внизу, «Инструменты» и «Чат» — шторки.

import { useCallback, useEffect, useMemo, useRef, useState, type ComponentType, type ReactNode } from 'react';
import { ArrowLeft, Brush, Image as ImageIcon, MessageSquare, Upload, Wrench } from 'lucide-react';
import {
  Button, Dot, EmptyState, Field, IconButton, Island, Modal, ModalActions, SegmentedControl, TextField, Toggle, ICON_SIZE, ICON_STROKE,
  C, FS, ISLAND, R, SHADOW, SP, useIsMobile, api as appApi, showToast, useMe, ModelsSpendModal, getNav, navPush, type NavSnapshot,
} from 'aihome_shell/kit';
import {
  AUTO_MODEL, imageEditorApi, type ImageChatState, type ImageChatStateEvent, type ImageEditCatalog, type ImageEditInitiator, type ImageEditCatalogReason, type ImageEditQuoteRequest, type ImageEditSaveRequest,
  type ImageEncodeFormat, type ImageEncodeSpec, type ImageFractionRect, type ImageTransformBase, type ImageTransformOp,
} from './api';
import { EditorCanvas } from './EditorCanvas';
import { exportAnnotated, exportMask, hasAnnotationMark, hasMaskMark, marksToJson, type Mark, type Tool } from './marks';
import { effectiveProvider, isRemovalPrompt, modelBlockReason, money, pickOp, plural, priceSum, priceText, splitPath, variantsWord, type ProviderChoice } from './format';
import { currentModel, ProviderModelPicker } from './ProviderModelPicker';
import { EditorSections, MarksTools, MobileToolbar, SectionHint, useEditorSections, type EditorSection } from './EditorSections';
import { HistorySteps, ProjectImagePicker, QuickActions, SamplesSection, SaveButtons } from './PanelSections';
import {
  actionTitle, currentSrc, dropStepsFrom, EMPTY_HISTORY, goToStep, maxSamples, panelJobInput, patchStep, pushStep, quickBlockReason, quickPlan,
  stepSaveSource, type History, type HistoryStep, type LaunchAction, type LaunchPlan, type OutpaintRatio, type QuickAction, type Sample, type SaveSource,
} from './editorInputs';
import { AdjustPanel } from './AdjustPanel';
import {
  chainTransform, CROP_RATIOS, fitCropRatio, formatOf, initialCrop, isFullCrop, opTitle, renderPreview, transformedSize, type CropRatio,
} from './transforms';
import { PromptCard } from './PromptCard';
import { useQuote } from './useQuote';
import { useImageEditJob } from './useImageEditJob';
import { ErrorView, GenerationView, VariantsView } from './ResultViews';
import { SaveAsDialog } from './SaveAsDialog';
import { defaultStem, nameStem } from './saveAs';
import { CharacterChip, CharacterSection, useCharacterDialogs, useCharacters } from './characters/CharacterPicker';
import { useImageChat } from './chat/useImageChat';
import { useChatStateSync } from './chat/useChatStateSync';
import { applyAgentChanges, NO_AGENT_MARKS, providerFromState, type AgentMarks, type AgentApplied, type EditorSettings } from './chat/stateSync';
import { ImageEditorBridge, type ImageEditorBridgeApi } from './chat/bridge';
import { AgentTag } from './PromptCard';
import type { ImageChatSlotProps } from '../../lib/subsystems/registryCore';

export type ImageEditorTarget =
  | { kind: 'edit'; path: string }       // «Редактировать» у картинки проекта
  | { kind: 'create'; folder: string };  // «Нарисовать картинку» у папки

const ic = (I: typeof Brush, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export function ImageEditor({ projectId, projectName, target, sessionId: openSessionId, initialPrompt, showJob, ImageChat, onClose, onOpenPath, onShowInFiles, onDirtyChange }: {
  projectId: string;
  projectName: string;
  target: ImageEditorTarget;
  // Чат картинки, с которым открыли редактор (карточка чата)
  sessionId?: string | null;
  // Из ленты полного чата: промпт карточки «✦ Промпт» и задача карточки запуска
  initialPrompt?: string;
  showJob?: { jobId: string; count: number };
  // Чат картинки ядра (контекст слота app-overlay): своей копии ChatPanel у модуля нет
  ImageChat: ComponentType<ImageChatSlotProps>;
  onClose: () => void;
  // Открыть в редакторе другой файл проекта («Разговор продолжился на …»)
  onOpenPath: (path: string) => void;
  // Есть варианты, которые пропадут при закрытии (рисуются или не сохранены)
  onDirtyChange?: (dirty: boolean) => void;
  // «Показать в файлах» в тосте после сохранения
  onShowInFiles?: (path: string) => void;
}) {
  const mobile = useIsMobile();
  const api = useMemo(() => imageEditorApi(), []);

  const initial = target.kind === 'edit' ? splitPath(target.path) : { folder: target.folder, name: 'новая-картинка.png' };
  const [sourcePath, setSourcePath] = useState<string | null>(target.kind === 'edit' ? target.path : null);
  // История шагов: оригинал и каждое «Взять за основу». Холст показывает текущий шаг,
  // его же байты уходят в генерацию. Нарисованное с нуля до первого шага не с чем сравнивать
  const [history, setHistoryState] = useState<History>(() => target.kind === 'edit'
    ? { steps: [originalStep('original', appApi.files.fileUrl(projectId, target.path), { path: target.path })], cur: 0 }
    : EMPTY_HISTORY);
  // Правки без ИИ идут цепочкой быстрее рендера: вторая читает историю сразу после первой
  const histRef = useRef(history);
  const updateHistory = useCallback((fn: (h: History) => History) => {
    histRef.current = fn(histRef.current);
    setHistoryState(histRef.current);
  }, []);
  const setHistory = updateHistory;
  const curStep: HistoryStep | undefined = history.steps[history.cur];
  const src = currentSrc(history);
  const stepSeq = useRef(0);
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const imgRef = useRef<HTMLImageElement | null>(null);

  const [catalog, setCatalog] = useState<ImageEditCatalog | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [provider, setProvider] = useState<ProviderChoice>('settings');
  const [model, setModel] = useState(AUTO_MODEL);

  const [marks, setMarks] = useState<Mark[]>([]);
  const [tool, setTool] = useState<Tool>('mask');
  const [labelAt, setLabelAt] = useState<{ x: number; y: number; text: string } | null>(null);
  const [prompt, setPrompt] = useState(initialPrompt ?? '');
  const [count, setCount] = useState(3);
  // Кто поставил промпт в поле и какие настройки поменял агент — метки «✦ … Claude»
  const [promptAuthor, setPromptAuthor] = useState<ImageEditInitiator>(initialPrompt ? 'agent' : 'human');
  const [agentMarks, setAgentMarks] = useState<AgentMarks>(initialPrompt ? { ...NO_AGENT_MARKS, prompt: true } : NO_AGENT_MARKS);
  const [promptDraft, setPromptDraft] = useState<string | null>(null);
  const [promptFlash, setPromptFlash] = useState(initialPrompt ? 1 : 0);
  const [selected, setSelected] = useState(0);
  // Открытый «Сохранить как…»: источник и формат результата (от него расширение)
  const [saveAs, setSaveAs] = useState<{ from: SaveSource; format: ImageEncodeFormat } | null>(null);
  const [compress, setCompress] = useState(false);
  const [heavy, setHeavy] = useState<{ bytes: number; webpBytes: number | null } | null>(null);
  // «Вернуть размер оригинала» (ADR-018 §9): по умолчанию включён
  const [matchSize, setMatchSize] = useState(true);
  const [crop, setCrop] = useState<{ rect: ImageFractionRect; ratio: CropRatio } | null>(null);
  const [savedPath, setSavedPath] = useState<string | null>(null);
  const [savedJobId, setSavedJobId] = useState<string | null>(null);

  const [samples, setSamples] = useState<Sample[]>([]);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [ratio, setRatio] = useState<OutpaintRatio>('16:9');
  // Что запускали последним: «Ещё варианты», повтор после ошибки и заголовок шага истории
  const lastAction = useRef<LaunchAction>({ kind: 'prompt', prompt: '' });

  const chars = useCharacters(api, projectId);
  const charDialogs = useCharacterDialogs(api, projectId, chars);
  const character = chars.active;

  const me = useMe();
  const [providersOpen, setProvidersOpen] = useState(false);
  const sectionsState = useEditorSections(me.userId);
  // Телефон: шторки «Инструменты» и «Чат»
  const [sheet, setSheet] = useState<'tools' | 'chat' | null>(null);

  const job = useImageEditJob(api, projectId);
  const busy = job.phase === 'starting' || job.phase === 'running';
  // Шаги правки без ИИ, которые сервер ещё не записал: генерировать по предпросмотру нельзя
  const transforming = history.steps.some(x => x.pending);
  // Номера вариантов даёт сервер: выбранный по умолчанию — первый готовый
  const sel = job.variants.includes(selected) ? selected : job.variants[0] ?? 0;
  const dirty = busy || (job.phase === 'variants' && !!job.jobId && job.jobId !== savedJobId);
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);

  useEffect(() => {
    let alive = true;
    api.catalog(projectId)
      .then(c => {
        if (!alive) return;
        setCatalog(c);
        // Умолчание — поставщик и модель из настройки места image-editor
        // Выбор мог уже прийти из состояния чата — его не затираем
        if (c.default.provider) setModel(m => (m === AUTO_MODEL ? c.default.model : m));
        else setProvider(p => (p === 'settings' ? c.providers[0]?.key ?? 'settings' : p));
      })
      .catch((e: Error) => { if (alive) setCatalogError(e.message); });
    return () => { alive = false; };
  }, [api, projectId]);

  const hasImage = !!src;
  const hasMask = hasImage && hasMaskMark(marks);
  const hasAnnotations = hasImage && hasAnnotationMark(marks);
  const removal = hasMask && isRemovalPrompt(prompt);
  const pv = catalog ? effectiveProvider(catalog, provider) : null;
  const m = currentModel(pv, model);
  const notConfigured = !!catalog && !catalog.providers.length;
  const explicitModel = m && m.id !== AUTO_MODEL ? m : null;
  const samplesMax = maxSamples(catalog?.limits.maxReferences ?? 6, explicitModel?.caps?.maxReferences);
  const blocked = (m ? modelBlockReason(m, hasImage, hasMask) : '')
    || (samples.length > samplesMax ? `Модель берёт не больше ${samplesMax} ${plural(samplesMax, 'образца', 'образцов', 'образцов')} — уберите лишние` : '');

  const quoteReq: ImageEditQuoteRequest | null = pv && m && !blocked ? {
    provider: pv.key, model: m.id, mode: 'auto', op: pickOp(hasImage, hasMask), count,
    hasMask, hasAnnotations, removal, references: samples.length, hasCharacter: !!character, width: size?.w ?? null, height: size?.h ?? null,
  } : null;
  const { quote, error: quoteError, loading: quoteLoading } = useQuote(api, projectId, quoteReq);

  const unit = quote?.estimate.unit ?? pv?.priceUnit ?? 'usd';
  const priceLabel = quote
    ? priceText(quote.estimate.amount, unit, quote.estimate.approx, count)
    // Пока котировка едет — ориентир из каталога, чтобы цена не мигала
    : m?.priceHint ? priceText(m.priceHint.amount * count, m.priceHint.unit, true, count) : `… · ${variantsWord(count)}`;
  const priceSumLabel = quote
    ? priceSum(quote.estimate.amount, unit, quote.estimate.approx)
    : m?.priceHint ? priceSum(m.priceHint.amount * count, m.priceHint.unit, true) : '…';

  const onProvider = (p: ProviderChoice) => {
    // Сменился поставщик — модель сбрасывается на «Авто»: список моделей у него свой
    if (p !== provider) setModel(p === 'settings' && catalog?.default.provider ? catalog.default.model : AUTO_MODEL);
    setProvider(p);
    setAgentMarks(x => ({ ...x, model: false }));
  };
  // Ручная правка снимает метку агента и пишет author = human
  const onModel = (id: string) => { setModel(id); setAgentMarks(x => ({ ...x, model: false })); };
  const onCount = (n: number) => { setCount(n); setAgentMarks(x => ({ ...x, count: false })); };
  const onPrompt = (v: string) => {
    setPrompt(v);
    setPromptAuthor('human');
    setAgentMarks(x => ({ ...x, prompt: false }));
  };

  const canGenerate = !!quote && !quoteLoading && !blocked && !busy && !transforming && (hasImage || !!prompt.trim());

  const planOf = useCallback((a: LaunchAction): LaunchPlan => (a.kind === 'prompt'
    ? { op: pickOp(hasImage, hasMask), prompt: a.prompt, useMask: true, removal }
    : quickPlan(a.kind, a.kind === 'outpaint' ? a.ratio ?? ratio : ratio)), [hasImage, hasMask, removal, ratio]);

  // Запуск по промпту или быстрым действием. Быстрое действие — своя операция, поэтому
  // котировка у него всегда свежая
  const launch = useCallback(async (action: LaunchAction, n: number = count) => {
    if (!pv || !m) return;
    const plan = planOf(action);
    const withMask = plan.useMask && hasMask;
    // Стрелки и подписи поясняют правку по промпту; фону, качеству и краям они ни к чему
    const withMarks = action.kind === 'prompt' || action.kind === 'removeMarked';
    let q = action.kind === 'prompt' ? quote : null;
    // Другое число вариантов («Нарисовать 1 вариант») или протухшая котировка — берём свежую
    if (!q || n !== count || Date.parse(q.expiresAt) - Date.now() < 30_000) {
      q = await api.quote(projectId, {
        provider: pv.key, model: m.id, mode: 'auto', op: plan.op, count: n,
        hasMask: withMask, hasAnnotations: withMarks && hasAnnotations, removal: plan.removal, references: samples.length,
        hasCharacter: !!character, width: size?.w ?? null, height: size?.h ?? null,
      }).catch((e: Error) => { showToast(e.message, '', 'error'); return null; });
      if (!q) return;
    }
    let source: Blob | undefined;
    let mask: Blob | undefined;
    let annotated: Blob | undefined;
    if (src && size) {
      source = await fetch(src).then(r => r.blob()).catch(() => undefined);
      if (withMask) mask = (await exportMask(marks, size.w, size.h)) ?? undefined;
      if (withMarks && imgRef.current) annotated = (await exportAnnotated(imgRef.current, marks, size.w, size.h).catch(() => null)) ?? undefined;
    }
    lastAction.current = action;
    setSelected(-1);
    await job.start({
      quoteId: q.quoteId, prompt: plan.prompt.trim(),
      marks: withMarks && marks.length && size ? marksToJson(marks, size.w, size.h) : undefined,
      sourcePath: sourcePath ?? undefined, source, mask, annotated, characterSlug: character?.slug,
      matchSourceSize: matchSize,
      ...panelJobInput(samples, plan),
    }, n, q.expectedSeconds);
  }, [api, projectId, pv, m, quote, count, planOf, hasMask, hasAnnotations, size, src, marks, sourcePath, character, samples, job, matchSize]);

  const generate = () => launch({ kind: 'prompt', prompt });
  const runQuick = (a: QuickAction) => { setSheet(null); void launch(a === 'outpaint' ? { kind: a, ratio } : { kind: a }); };

  const quickBlock = (a: QuickAction) => {
    if (busy) return 'Идёт генерация';
    if (transforming) return 'Правка ещё сохраняется';
    if (!pv || !m || notConfigured) return 'Рисовать нечем — см. «Чем рисовать»';
    return quickBlockReason(a, hasImage && !!size, hasMask, explicitModel?.caps?.ops ?? null)
      || (samples.length > samplesMax ? blocked : '');
  };

  // ── Образцы ──
  const samplesRef = useRef(samples);
  samplesRef.current = samples;
  // Уходя, отпускаем object URL образцов с компьютера
  useEffect(() => () => samplesRef.current.forEach(s => { if (s.source === 'upload') URL.revokeObjectURL(s.url); }), []);

  const addSampleFiles = (files: File[]) => {
    const maxBytes = (catalog?.limits.maxFileMb ?? 20) * 1024 * 1024;
    const ok = files.filter(f => /^image\/(png|jpeg|webp)$/.test(f.type) && f.size <= maxBytes);
    if (ok.length < files.length) showToast('Образец — PNG, JPG или WebP до 20 МБ', '', 'error');
    const room = Math.max(0, samplesMax - samples.length);
    setSamples(list => [...list, ...ok.slice(0, room).map((f): Sample => ({
      id: `u${++stepSeq.current}`, source: 'upload', name: f.name, role: 'object', file: f, url: URL.createObjectURL(f),
    }))]);
  };
  const addSamplePaths = (paths: string[]) => {
    const room = Math.max(0, samplesMax - samples.length);
    const fresh = paths.filter(p => !samples.some(s => s.source === 'project' && s.path === p)).slice(0, room);
    setSamples(list => [...list, ...fresh.map((p): Sample => ({
      id: `p${++stepSeq.current}`, source: 'project', name: splitPath(p).name, role: 'object', path: p, url: appApi.files.fileUrl(projectId, p),
    }))]);
  };
  const removeSample = (id: string) => setSamples(list => list.filter(s => {
    if (s.id !== id) return true;
    if (s.source === 'upload') URL.revokeObjectURL(s.url);
    return false;
  }));

  // ── История шагов ──
  // Картинка с компьютера на сервере не лежит: править без ИИ её нельзя, пока не сохранена
  const startFrom = (url: string) => {
    setHistory(() => ({ steps: [originalStep(`o${++stepSeq.current}`, url, null)], cur: 0 }));
    setSize(null);
    setMarks([]);
  };
  const goStep = (i: number) => {
    if (i === history.cur) return;
    setHistory(h => goToStep(h, i));
    setCrop(null);
    // Размер подхватит холст, когда шаг загрузится
    setSize(null);
    setSheet(null);
  };

  // ── Правки без ИИ (ADR-018 §9) ──
  // Предпросмотр каждого шага в полёте: следующая правка рисуется поверх него, а не
  // поверх того, что успело загрузиться на холст
  const previews = useRef(new Map<string, Promise<HTMLImageElement | null>>());
  const previewUrls = useRef(new Set<string>());
  useEffect(() => () => previewUrls.current.forEach(u => URL.revokeObjectURL(u)), []);

  const imageOf = (step: HistoryStep): Promise<HTMLImageElement | null> => {
    const shown = imgRef.current;
    if (shown && shown.getAttribute('src') === step.src && shown.naturalWidth) return Promise.resolve(shown);
    return loadImage(step.src).catch(() => null);
  };

  // Размер берём у шага, если он известен: у предпросмотра натуральный размер экранный
  const onImageLoad = (img: HTMLImageElement) => {
    imgRef.current = img;
    const h = histRef.current;
    const cur = h.steps[h.cur];
    if (cur && cur.src !== img.getAttribute('src')) return;
    if (cur?.w && cur.h) { setSize({ w: cur.w, h: cur.h }); return; }
    const natural = { w: img.naturalWidth, h: img.naturalHeight };
    setSize(natural);
    if (cur) updateHistory(x => patchStep(x, cur.id, natural));
  };

  // Правка — сразу шагом истории с предпросмотром; сервер записывает шаг цепочкой за
  // предыдущими. Не записался — шаг и всё поверх него убираются
  const runTransform = (ops: ImageTransformOp[], encode: ImageEncodeSpec | null) => {
    const h = histRef.current;
    const cur = h.steps[h.cur];
    if (!cur?.ready || !cur.w || !cur.h || !ops.length && !encode) return;
    const id = `t${++stepSeq.current}`;
    const dims = transformedSize({ w: cur.w, h: cur.h }, ops);
    const { result, ready } = chainTransform(cur.ready, ops, encode, req => api.transform(projectId, req));
    const baseImg = previews.current.get(cur.id) ?? imageOf(cur);
    previews.current.set(id, baseImg.then(async img => {
      const url = img && ops.length === 1 ? await renderPreview(img, ops[0]).catch(() => null) : null;
      if (!url) return img;
      previewUrls.current.add(url);
      const shown = await loadImage(url).catch(() => null);
      if (shown) updateHistory(x => (x.steps.some(st => st.id === id && st.pending) ? patchStep(x, id, { src: url }) : x));
      return shown ?? img;
    }));
    updateHistory(x => pushStep(x, {
      id, original: false, title: opTitle(ops[0] ?? { type: 'resize' }, encode), src: cur.src, ready, pending: true, w: dims.w, h: dims.h,
    }));
    // Обрезка, поворот и отражение меняют размер, когда загрузится предпросмотр: иначе
    // прежняя картинка на миг растянется в новые пропорции. Ресайз выглядит как было
    if (!ops.some(o => o.type === 'crop' || o.type === 'rotate' || o.type === 'flip')) setSize(dims);
    setMarks([]);
    setCrop(null);
    result.then(async r => {
      const url = api.stepUrl(projectId, r.stepId);
      // Грузим заранее: предпросмотр сменится готовой картинкой без мигания
      await loadImage(url).catch(() => null);
      previews.current.delete(id);
      updateHistory(x => patchStep(x, id, { src: url, pending: false, base: { stepId: r.stepId }, w: r.width, h: r.height, bytes: r.bytes }));
    }).catch((e: Error) => {
      previews.current.delete(id);
      if (!histRef.current.steps.some(st => st.id === id)) return;
      updateHistory(x => dropStepsFrom(x, id));
      setSize(null);
      showToast(`Правка не выполнена: ${e.message}`, '', 'error');
    });
  };

  // Вес и формат текущей картинки — для «2,4 МБ → 310 КБ» и формата по умолчанию
  const [fileInfo, setFileInfo] = useState<{ src: string; bytes: number; format: ImageEncodeFormat | null } | null>(null);
  const infoSrc = curStep && !curStep.pending ? curStep.src : null;
  useEffect(() => {
    if (!infoSrc) return;
    let alive = true;
    fetch(infoSrc).then(r => r.blob())
      .then(b => { if (alive) setFileInfo({ src: infoSrc, bytes: b.size, format: formatOf(b.type) }); })
      .catch(() => {});
    return () => { alive = false; };
  }, [infoSrc]);

  // ── Чат картинки ──
  const chat = useImageChat({
    api, projectId, sourcePath, openSessionId, chatVisible: !mobile || sheet === 'chat',
    canvas: { imgRef, marks, size, stepId: curStep && !curStep.pending ? curStep.id : null },
    onClose, onOpenPath,
  });

  // ── Состояние редактора на сервере и правки агента (ADR-018 §2) ──
  const marksJson = useMemo(() => (marks.length && size ? JSON.parse(marksToJson(marks, size.w, size.h)) as unknown : null), [marks, size]);
  const stepBase = curStep?.base;
  const settings: EditorSettings = {
    prompt, promptAuthor, provider, model, count,
    references: samples.flatMap(x => (x.source === 'project' ? [{ path: x.path, role: x.role }] : [])),
    characterSlug: character?.slug ?? null, matchSourceSize: matchSize, marks: marksJson,
    canvasRevision: chat.revision, currentStepId: stepBase && 'stepId' in stepBase && stepBase.stepId ? stepBase.stepId : null,
  };

  const applyPatch = (patch: AgentApplied['patch']) => {
    if (patch.prompt !== undefined) setPrompt(patch.prompt);
    if (patch.promptAuthor) setPromptAuthor(patch.promptAuthor);
    if (patch.provider !== undefined) setProvider(patch.provider);
    if (patch.model !== undefined) setModel(patch.model);
    if (patch.count !== undefined) setCount(Math.min(4, Math.max(1, patch.count)));
    if (patch.matchSourceSize !== undefined) setMatchSize(patch.matchSourceSize);
    if (patch.characterSlug !== undefined) chars.setActive(patch.characterSlug);
    if (patch.references) {
      const refs = patch.references;
      setSamples(list => [
        ...list.filter(x => x.source === 'upload'),
        ...refs.map((r): Sample => {
          const had = list.find(x => x.source === 'project' && x.path === r.path);
          return had ? { ...had, role: r.role } : {
            id: `p${++stepSeq.current}`, source: 'project', name: splitPath(r.path).name, role: r.role, path: r.path, url: appApi.files.fileUrl(projectId, r.path),
          };
        }),
      ]);
    }
  };

  // Открыли чат: редактор забирает сохранённое состояние. Промпт, который уже в поле
  // (пришёл из карточки полного чата или набран до ответа сервера), не затираем
  const onStateLoad = (st: ImageChatState) => {
    if (!st.revision) return;
    const keepPrompt = !!prompt.trim();
    applyPatch({
      ...(keepPrompt ? null : { prompt: st.prompt, promptAuthor: st.promptAuthor }),
      provider: providerFromState(st.provider), model: st.model || AUTO_MODEL, count: st.count,
      matchSourceSize: st.matchSourceSize, ...(st.references.length ? { references: st.references } : null),
    });
    if (!keepPrompt && st.promptAuthor === 'agent' && st.prompt) setAgentMarks(x => ({ ...x, prompt: true }));
  };

  const onAgentState = (e: ImageChatStateEvent) => {
    const applied = applyAgentChanges({ prompt, promptAuthor }, agentMarks, e.state, e.changes);
    applyPatch(applied.patch);
    setAgentMarks(applied.marks);
    if (applied.draft) setPromptDraft(applied.draft);
    if (applied.patch.prompt !== undefined) setPromptFlash(n => n + 1);
  };

  useChatStateSync({
    api, projectId, sessionId: chat.sessionId, settings,
    maskFor: async () => (size && hasMaskMark(marks) ? exportMask(marks, size.w, size.h) : null),
    onLoad: onStateLoad, onAgent: onAgentState,
  });

  // Задача агента этого чата: свободный редактор показывает её в центре сам
  const attachJob = job.attach;
  const [agentJob, setAgentJob] = useState<string | null>(null);
  const idle = job.phase === 'idle' && !crop;
  const idleRef = useRef(idle);
  useEffect(() => { idleRef.current = idle; });
  const trackAgentJob = useCallback((jobId: string, n: number, expected?: number | null) => {
    if (!idleRef.current) return;
    idleRef.current = false;
    setAgentJob(jobId);
    attachJob(jobId, n, expected);
  }, [attachJob]);
  const chatSessionId = chat.sessionId;
  useEffect(() => api.subscribe(e => {
    if (!chatSessionId || e.chatSessionId !== chatSessionId || e.initiator !== 'agent' || e.type !== 'image_edit_progress') return;
    trackAgentJob(e.jobId, count);
  }), [api, chatSessionId, trackAgentJob, count]);

  // Открыли из карточки запуска в полном чате — сразу на этой задаче
  const shownJob = useRef(false);
  useEffect(() => {
    if (!showJob || shownJob.current) return;
    shownJob.current = true;
    setAgentJob(showJob.jobId);
    attachJob(showJob.jobId, showJob.count);
  }, [showJob, attachJob]);

  // Агент (или карточка) ставит промпт в поле: метка, подсветка, текст человека — в черновик
  const putAgentPrompt = (p: string, n?: number | null) => {
    if (promptAuthor === 'human' && prompt.trim() && prompt !== p) setPromptDraft(prompt);
    setPrompt(p);
    setPromptAuthor('agent');
    setAgentMarks(x => ({ ...x, prompt: true, count: n ? true : x.count }));
    setPromptFlash(k => k + 1);
    if (n) setCount(Math.min(4, Math.max(1, n)));
    setSheet(null);
  };

  const bridge: ImageEditorBridgeApi = {
    sessionId: chat.sessionId, busy: busy || transforming, priceSum: notConfigured ? null : priceSumLabel,
    insertPrompt: (p, o) => putAgentPrompt(p, o?.count),
    generate: (p, o) => {
      putAgentPrompt(p, o?.count);
      void launch({ kind: 'prompt', prompt: p }, o?.count ? Math.min(4, Math.max(1, o.count)) : count);
    },
    showJob: (jobId, n, expected) => {
      if (busy && job.jobId !== jobId) { showToast('Дождитесь текущей генерации', '', 'info'); return; }
      setSheet(null);
      setCrop(null);
      setAgentJob(jobId);
      job.attach(jobId, n, expected);
    },
    trackJob: trackAgentJob,
  };

  // Вариант становится шагом истории: «Взять за основу» и сохранение варианта
  const pushVariantStep = (jobId: string, variant: number) => {
    const url = api.variantUrl(projectId, jobId, variant);
    const base: ImageTransformBase = { jobId, variant };
    setHistory(h => pushStep(h, {
      id: `s${++stepSeq.current}`, original: false, title: actionTitle(lastAction.current), src: url, base, ready: Promise.resolve(base),
    }));
    setSize(null);
    setMarks([]);
    job.reset();
  };

  const takeAsBase = () => {
    if (!job.jobId) return;
    pushVariantStep(job.jobId, sel);
    onPrompt('');
  };

  // «Сохранить как…»: вариант задачи или шаг истории. Тяжёлый файл (больше HeavyFileMb) —
  // предупреждение с оценкой WebP; блокировки нет (ADR-018 §9)
  const openSaveAs = (from: SaveSource, known?: { bytes?: number; format?: ImageEncodeFormat | null }) => {
    setSaveAs({ from, format: known?.format ?? 'png' });
    setCompress(false);
    setHeavy(null);
    const limit = (catalog?.limits.heavyFileMb ?? 5) * 1024 * 1024;
    const info = 'jobId' in from
      ? fetch(api.variantUrl(projectId, from.jobId, from.variant)).then(r => r.blob()).then(b => ({ bytes: b.size, format: formatOf(b.type) }))
      : Promise.resolve({ bytes: known?.bytes ?? null, format: null });
    void info.then(async ({ bytes, format }) => {
      if (format) setSaveAs(v => (v && v.from === from ? { ...v, format } : v));
      if (bytes == null || bytes <= limit) return;
      setHeavy(h => h ?? { bytes, webpBytes: null });
      const r = await api.transform(projectId, { base: from, ops: [], encode: HEAVY_ENCODE }, { dryRun: true }).catch(() => null);
      if (r) setHeavy(h => (h ? { ...h, webpBytes: r.bytes } : h));
    }).catch(() => {});
  };

  const saveSource = (from: SaveSource) => ('jobId' in from ? { jobId: from.jobId, variant: from.variant } : { stepId: from.stepId, variant: 0 });

  // Редактор переходит на сохранённый файл: следующая версия ляжет уже рядом с ним.
  // Вариант с экрана вариантов становится шагом истории; шаг из шапки уже в истории
  const afterSave = (from: SaveSource, path: string) => {
    setSavedPath(path);
    setSourcePath(path);
    if ('jobId' in from) setSavedJobId(from.jobId);
    if ('jobId' in from && job.phase === 'variants' && job.jobId === from.jobId) pushVariantStep(from.jobId, from.variant);
    showToast(`Сохранено в проект: ${path}`, '', 'info', { label: 'Показать в дереве', onClick: () => showInTree(path) });
  };

  // «Применить» / «Сохранить» — сразу, без диалога, следующей версией рядом
  const saveNext = async (from: SaveSource) => {
    // Чат картинки переезжает на новый файл вместе с редактором (ADR-018 §1)
    const req: ImageEditSaveRequest = sourcePath
      ? { ...saveSource(from), mode: 'next-version', sourcePath, chatSessionId: chat.sessionId }
      : { ...saveSource(from), mode: 'next-version', folder: initial.folder || undefined, fileName: initial.name };
    try {
      afterSave(from, (await api.save(projectId, req)).path);
    } catch (e) {
      showToast(`Не сохранено: ${(e as Error).message}`, '', 'error');
    }
  };

  const saveAsFormat: ImageEncodeFormat = compress ? 'webp' : saveAs?.format ?? 'png';
  const checkName = useCallback((folder: string, name: string) =>
    api.saveCheck(projectId, { folder: folder || undefined, name, format: saveAsFormat }), [api, projectId, saveAsFormat]);

  // Ошибка (в том числе 409 name_taken) уходит в диалог: он покажет подсказку свободного имени
  const saveAsFile = async ({ folder, fileName }: { folder: string; fileName: string }) => {
    if (!saveAs) return;
    const res = await api.save(projectId, {
      ...saveSource(saveAs.from), mode: 'as', folder: folder || undefined, fileName, encode: compress ? HEAVY_ENCODE : undefined,
      chatSessionId: chat.sessionId,
    });
    setSaveAs(null);
    afterSave(saveAs.from, res.path);
  };

  // «Показать в дереве»: экран проекта открывает файл и панель файлов так же, как по
  // «назад/вперёд», а дерево раскрывает папки до него и подсвечивает строку
  const showInTree = (path: string) => {
    const nav = getNav();
    if (nav?.screen !== 'project' || nav.project?.id !== projectId) { onShowInFiles?.(path); return; }
    const snap: NavSnapshot = { ...nav, view: 'sidebar', file: path, task: null, board: false, revealInTree: true };
    onClose();
    navPush(snap);
    window.dispatchEvent(new PopStateEvent('popstate', { state: snap }));
  };

  const title = !hasImage && target.kind === 'create' ? 'Новая картинка' : splitPath(sourcePath ?? initial.name).name;
  const folder = sourcePath ? splitPath(sourcePath).folder : initial.folder;

  // ── Центр ──
  let center: ReactNode;
  if (job.phase === 'running' || job.phase === 'starting') {
    center = <GenerationView count={job.count} progress={job.progress} onCancel={job.cancel} mobile={mobile}
      byLabel={agentJob && agentJob === job.jobId ? (chat.hasPersona ? `запуск: ${chat.who}` : 'запустил Claude') : null} />;
  } else if (job.phase === 'variants' && job.jobId) {
    const jobId = job.jobId;
    center = (
      <VariantsView variants={job.variants} variantUrl={n => api.variantUrl(projectId, jobId, n)}
        before={src} cost={job.cost} selected={sel}
        onSelect={setSelected} onApply={() => { void saveNext({ jobId, variant: sel }); }}
        onSaveAs={() => openSaveAs({ jobId, variant: sel })} onBase={takeAsBase}
        onMore={() => { void launch(lastAction.current); }} onBack={job.reset} mobile={mobile} />
    );
  } else if (job.phase === 'error' && job.failure) {
    const perOne = quote?.estimate.amount != null ? quote.estimate.amount / Math.max(1, count) : null;
    center = (
      <ErrorView failure={job.failure} providerLabel={pv?.label ?? ''} priceUnit={unit}
        needText={quote?.estimate.amount != null ? priceLabel.replace(' · ', ', ') : null}
        oneVariantPrice={perOne != null ? `≈ ${money(perOne, unit)}` : null}
        onRetry={() => { void launch(lastAction.current); }} onRetryOne={() => { setCount(1); void launch(lastAction.current, 1); }}
        onEdit={job.reset} mobile={mobile} />
    );
  } else if (src) {
    center = (
      <EditorCanvas src={src} size={size} marks={marks} onMarksChange={setMarks} tool={tool}
        onTextAt={(x, y) => setLabelAt({ x, y, text: '' })}
        onImageLoad={onImageLoad}
        crop={crop} onCropChange={rect => setCrop(c => (c ? { ...c, rect } : c))}
        hint={crop ? 'Тяните рамку или её углы' : transforming ? 'Сохраняем правку…' : undefined}
        overlay={crop && size && (
          <CropBar ratio={crop.ratio} mobile={mobile}
            onRatio={ratio => setCrop(c => (c ? { ratio, rect: ratio === 'free' ? c.rect : fitCropRatio(c.rect, ratio, size) } : c))}
            onCancel={() => setCrop(null)}
            onApply={() => {
              if (isFullCrop(crop.rect)) { setCrop(null); return; }
              runTransform([{ type: 'crop', rect: crop.rect }], null);
            }} />
        )} />
    );
  } else {
    center = <DropZone folder={folder} mobile={mobile} onFile={f => startFrom(URL.createObjectURL(f))} />;
  }

  const marksOn = hasImage && job.phase === 'idle' && !crop;
  const adjustBlock = !hasImage ? 'Сначала загрузите картинку'
    : busy ? 'Идёт генерация'
      : job.phase !== 'idle' ? 'Вернитесь к картинке, чтобы править её'
        : !curStep?.ready ? 'Картинка с компьютера ещё не в проекте: сохраните её, тогда можно править'
          : '';
  const saveFromStep = job.phase === 'idle' ? stepSaveSource(curStep) : null;
  const doneSteps = history.steps.filter(x => !x.original).length;

  // ── Левая панель: шесть секций ──
  const sections: EditorSection[] = [
    {
      id: 'marks', title: 'Пометки',
      meta: marks.length ? `${marks.length} ${plural(marks.length, 'пометка', 'пометки', 'пометок')}` : 'нет',
      body: <MarksTools tool={tool} onTool={setTool} marksCount={marks.length} onClear={() => setMarks([])} disabled={!marksOn} />,
    },
    {
      id: 'samples', title: 'Образцы', meta: samples.length ? String(samples.length) : 'нет',
      body: (
        <SamplesSection samples={samples} max={samplesMax} disabled={busy}
          onAddFiles={addSampleFiles} onAddPaths={addSamplePaths} onRemove={removeSample}
          onRole={(id, role) => setSamples(list => list.map(x => (x.id === id ? { ...x, role } : x)))}
          onPickProject={() => setPickerOpen(true)} />
      ),
    },
    {
      id: 'chars', title: 'Персонажи',
      meta: character ? `в генерации: ${character.name}` : chars.list.length ? String(chars.list.length) : 'нет',
      body: (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <CharacterSection api={api} projectId={projectId} chars={chars} disabled={busy} bare
            onNew={charDialogs.openNew} onCard={charDialogs.openCard} />
          <SectionHint>Персонаж — набор фото лица в characters/. Подключённый узнаваем во всех вариантах.</SectionHint>
        </div>
      ),
    },
    {
      id: 'quick', title: 'Быстрые действия',
      body: <QuickActions blockReason={quickBlock} ratio={ratio} onRatio={setRatio} onRun={runQuick} />,
    },
    {
      id: 'adjust', title: 'Правка без ИИ',
      meta: transforming ? 'сохраняем…' : size ? `${size.w}×${size.h}` : undefined,
      body: (
        <AdjustPanel api={api} projectId={projectId} stepId={curStep?.id ?? ''} size={size} base={curStep?.ready ?? null}
          sourceFormat={fileInfo?.src === src ? fileInfo.format : undefined} beforeBytes={curStep?.bytes ?? (fileInfo?.src === src ? fileInfo.bytes : null)}
          blockReason={adjustBlock} cropping={!!crop}
          onOp={op => runTransform([op], null)}
          onCrop={() => {
            if (!size) return;
            setSheet(null);
            setCrop(c => (c ? null : { rect: initialCrop('free', size), ratio: 'free' }));
          }}
          onApply={(ops, encode) => runTransform(ops, encode)} />
      ),
    },
    {
      id: 'model', title: 'Чем рисовать',
      meta: pv && m ? `${agentMarks.model ? '✦ ' : ''}${pv.label} · ${m.label}` : undefined,
      body: (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {agentMarks.model && <span style={{ alignSelf: 'flex-start' }}><AgentTag>{chat.hasPersona ? `модель: ${chat.who}` : 'модель сменил Claude'}</AgentTag></span>}
          {catalogError && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{catalogError}</div>}
          {notConfigured && catalog && (
            <NotConfigured reason={catalogReason(catalog)} isAdmin={me.role === 'admin'} onSetup={() => setProvidersOpen(true)} />
          )}
          {catalog && !notConfigured && (
            <ProviderModelPicker catalog={catalog} provider={provider} model={m?.id ?? model}
              onProvider={onProvider} onModel={onModel} hasImage={hasImage} hasMask={hasMask}
              priceLabel={priceLabel} mobile={mobile} />
          )}
          <label style={{ display: 'flex', alignItems: 'center', gap: SP.sm, cursor: 'pointer' }}>
            <Toggle checked={matchSize} onChange={setMatchSize} ariaLabel="Вернуть размер оригинала" width={34} height={20} />
            <span style={{ fontSize: FS.sm, color: C.textPrimary }}>Вернуть размер оригинала</span>
          </label>
          <SectionHint>Варианты приводятся к размеру исходника, если пропорции совпадают.</SectionHint>
        </div>
      ),
    },
    {
      id: 'hist', title: 'История шагов',
      meta: doneSteps ? `${doneSteps} ${plural(doneSteps, 'шаг', 'шага', 'шагов')}` : undefined,
      body: <HistorySteps history={history} disabled={busy} onStep={goStep} />,
    },
  ];
  const toolPanel = <EditorSections sections={sections} open={sectionsState.open} onToggle={sectionsState.toggle} />;

  // ── Поле промпта ──
  const promptCard = (
    <PromptCard prompt={prompt} onPrompt={onPrompt} busy={busy} busyCount={job.count} onCancel={job.cancel}
      count={count} onCount={onCount}
      agentLabel={agentMarks.prompt ? (chat.hasPersona ? chat.who : 'написал Claude') : null} flash={promptFlash}
      draft={promptDraft} onRestoreDraft={() => { onPrompt(promptDraft ?? ''); setPromptDraft(null); }} countByAgent={agentMarks.count} priceSum={notConfigured ? null : priceSumLabel}
      canGenerate={canGenerate} blockedReason={blocked} onGenerate={() => { void generate(); }} mobile={mobile}
      placeholder={character
        ? `Где и что делает ${character.name}? Например, «${character.name} сидит в кафе у окна»`
        : hasImage
          ? 'Что изменить? Отметьте место на картинке или просто опишите'
          : 'Опишите, что нарисовать: например, «светлая гостиная, синий диван, торшер в углу, утро»'}
      above={character && (
        <CharacterChip api={api} projectId={projectId} character={character} disabled={busy}
          onOpen={() => charDialogs.openCard(character.slug)} onOff={() => chars.setActive(null)} />
      )}
      below={(
        <>
          {notConfigured && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Рисовать нечем — см. «Чем рисовать» слева</div>}
          {quoteError && !blocked && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{quoteError}</div>}
        </>
      )} />
  );

  // ── Чат картинки: только у картинки, которая лежит в проекте ──
  const chatArea = chat.props ? (
    <div data-image-chat="" style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <ImageEditorBridge.Provider value={bridge}>
        <ImageChat {...chat.props} />
      </ImageEditorBridge.Provider>
    </div>
  ) : (
    <div style={{ flex: 1, minHeight: 0, padding: SP.md, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, textAlign: 'center' }}>
      Чат картинки появится, когда картинка будет в проекте: сохраните её, и можно будет обсудить её с Claude.
    </div>
  );

  const body = mobile ? (
    <>
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative' }}>
        {center}
        {marksOn && src && <MobileToolbar tool={tool} onTool={setTool} />}
      </div>
      <div style={{ flex: '0 0 auto', display: 'flex', gap: SP.xs, padding: `${SP.xs}px ${SP.md}px 0`, background: C.bgPanel, borderTop: `1px solid ${C.borderLight}` }}>
        <div style={{ flex: 1 }}>
          <Button variant="ghostFilled" size="sm" fullWidth leftIcon={ic(Wrench, ICON_SIZE.xs)} onClick={() => setSheet('tools')}>Инструменты</Button>
        </div>
        <div style={{ flex: 1 }}>
          <Button variant="ghostFilled" size="sm" fullWidth leftIcon={ic(MessageSquare, ICON_SIZE.xs)} onClick={() => { chat.markRead(); setSheet('chat'); }}>
            Чат{chat.unread && <span data-chat-unread="" title="Новое в чате" style={{ display: 'inline-flex', marginLeft: SP.xs }}><Dot color={C.accent} /></span>}
          </Button>
        </div>
      </div>
      {promptCard}
    </>
  ) : (
    <>
      <div data-editor-left="" style={{ width: 272, flex: '0 0 272px', borderRight: `1px solid ${C.borderLight}`, overflow: 'auto', background: C.bgPanel }}>
        {toolPanel}
      </div>
      <div style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {center}
      </div>
      <div data-editor-right="" style={{ width: 384, flex: '0 0 384px', borderLeft: `1px solid ${C.borderLight}`, display: 'flex', flexDirection: 'column', minHeight: 0, background: C.bgPanel }}>
        {promptCard}
        {chatArea}
      </div>
    </>
  );

  return (
    <Island style={{ height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.sm}px`, minHeight: ISLAND.headerH,
        background: ISLAND.headerBg, borderBottom: `1px solid ${C.borderLight}`,
      }}>
        <IconButton title="К файлам" ariaLabel="К файлам" onClick={onClose}>{ic(ArrowLeft)}</IconButton>
        {!mobile && <span style={{ color: C.textMuted, display: 'inline-flex' }}>{ic(ImageIcon)}</span>}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{title}</div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {folder ? `${folder}/ · ` : ''}{projectName}
          </div>
        </div>
        {saveFromStep && !crop && (
          <SaveButtons mobile={mobile} disabled={transforming}
            onSave={() => { void saveNext(saveFromStep); }}
            onSaveAs={() => openSaveAs(saveFromStep, { bytes: curStep?.bytes, format: fileInfo?.src === src ? fileInfo.format : null })} />
        )}
        {savedPath && !mobile && (
          <span style={{ fontSize: FS.sm, color: C.successText, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 280 }}>
            Сохранено в проект: {savedPath}
          </span>
        )}
      </div>
      <div style={{ flex: 1, minHeight: 0, display: 'flex', ...(mobile ? { flexDirection: 'column' } : null) }}>
        {body}
      </div>

      {mobile && sheet && (
        <Modal title={sheet === 'tools' ? 'Инструменты' : 'Чат картинки'} onClose={() => setSheet(null)}
          cardStyle={sheet === 'chat' ? { height: '86vh', maxHeight: '86vh', background: C.bgPanel } : { maxHeight: '86vh', background: C.bgPanel }}>
          {sheet === 'tools' ? toolPanel : <div style={{ height: '100%', minHeight: 0, display: 'flex', flexDirection: 'column' }}>{chatArea}</div>}
        </Modal>
      )}
      {labelAt && (
        <Modal title="Подпись на картинке" width={380} onClose={() => setLabelAt(null)}
          footer={<ModalActions confirmLabel="Добавить" confirmDisabled={!labelAt.text.trim()} onCancel={() => setLabelAt(null)}
            onConfirm={() => { setMarks(ms => [...ms, { type: 'text', x: labelAt.x, y: labelAt.y, text: labelAt.text.trim() }]); setLabelAt(null); }} />}>
          <Field>
            <TextField value={labelAt.text} autoFocus placeholder="сюда лампу" onChange={text => setLabelAt({ ...labelAt, text })}
              onEnter={() => { if (labelAt.text.trim()) { setMarks(ms => [...ms, { type: 'text', x: labelAt.x, y: labelAt.y, text: labelAt.text.trim() }]); setLabelAt(null); } }} />
          </Field>
        </Modal>
      )}
      {charDialogs.dialog}
      {pickerOpen && (
        <ProjectImagePicker projectId={projectId}
          taken={samples.flatMap(x => (x.source === 'project' ? [x.path] : []))}
          onPick={p => { addSamplePaths([p]); setPickerOpen(false); }} onClose={() => setPickerOpen(false)} />
      )}
      {providersOpen && <ModelsSpendModal initialTab="apply" onClose={() => setProvidersOpen(false)} />}
      {saveAs && (
        <SaveAsDialog projectId={projectId} sourcePath={sourcePath}
          defaultName={sourcePath ? defaultStem(splitPath(sourcePath).name) : nameStem(initial.name)}
          folder={folder} format={saveAsFormat} onCheck={checkName} onSave={saveAsFile} onClose={() => setSaveAs(null)}
          heavy={heavy && { ...heavy, compress, onCompress: setCompress }} />
      )}
    </Island>
  );
}

const HEAVY_ENCODE: ImageEncodeSpec = { format: 'webp', quality: 80 };

function originalStep(id: string, src: string, base: ImageTransformBase | null): HistoryStep {
  return { id, original: true, title: 'Оригинал', src, base, ready: base ? Promise.resolve(base) : null };
}

function loadImage(url: string): Promise<HTMLImageElement> {
  const img = new Image();
  img.src = url;
  return img.decode().then(() => img);
}

const CROP_LABEL: Record<CropRatio, string> = { free: 'Свободно', '1:1': '1:1', '16:9': '16:9', '9:16': '9:16' };

// Плашка обрезки над холстом: пропорции, «Отмена», «Обрезать»
function CropBar({ ratio, mobile, onRatio, onCancel, onApply }: {
  ratio: CropRatio;
  mobile: boolean;
  onRatio: (r: CropRatio) => void;
  onCancel: () => void;
  onApply: () => void;
}) {
  return (
    <div data-crop-bar="true" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center',
      padding: SP.sm, background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, boxShadow: SHADOW.card,
    }}>
      <div style={{ width: mobile ? 280 : 300, maxWidth: '100%' }}>
        <SegmentedControl value={ratio} onChange={onRatio} options={CROP_RATIOS.map(r => ({ value: r, label: CROP_LABEL[r] }))} />
      </div>
      <div style={{ display: 'flex', gap: SP.xs }}>
        <Button size="sm" variant="ghost" onClick={onCancel}>Отмена</Button>
        <Button size="sm" variant="primary" onClick={onApply}>Обрезать</Button>
      </div>
    </div>
  );
}

// Причина пустого каталога. Старый бэкенд поля reason не присылает: пустой список = не настроено
function catalogReason(c: ImageEditCatalog): ImageEditCatalogReason | null {
  return c.reason ?? (c.providers.length ? null : 'no_provider_configured');
}

// Рисовать нечем: админу — путь к настройке, остальным — к кому идти
function NotConfigured({ reason, isAdmin, onSetup }: { reason: ImageEditCatalogReason | null; isAdmin: boolean; onSetup: () => void }) {
  const disabled = reason === 'subsystem_disabled';
  return (
    <EmptyState compact inline icon={ic(ImageIcon, ICON_SIZE.lg)}
      title={disabled ? 'Рисование выключено' : 'Рисование не настроено'}
      subtitle={isAdmin
        ? disabled ? 'Подсистема картинок выключена в настройках сервера' : 'Подключите fal.ai или Higgsfield'
        : 'Попросите администратора подключить сервис рисования'}
      action={isAdmin && !disabled
        ? <Button size="sm" variant="secondary" onClick={onSetup}>Настроить поставщиков</Button>
        : undefined} />
  );
}

// Пустой редактор (экран 2): перетащить картинку или загрузить с компьютера
function DropZone({ folder, onFile, mobile }: { folder: string; onFile: (f: File) => void; mobile: boolean }) {
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const take = (f: File | undefined) => { if (f && f.type.startsWith('image/')) onFile(f); };
  return (
    <div style={{ flex: 1, minHeight: 0, padding: mobile ? SP.md : SP.lg, display: 'flex', background: C.bgInset }}>
      <div
        onDragOver={e => { e.preventDefault(); setOver(true); }}
        onDragLeave={() => setOver(false)}
        onDrop={e => { e.preventDefault(); setOver(false); take(e.dataTransfer.files[0]); }}
        style={{
          flex: 1, border: `2px dashed ${over ? C.accent : C.dashed}`, borderRadius: R.xl,
          background: over ? C.accentLight : 'transparent', display: 'flex', flexDirection: 'column',
          alignItems: 'center', justifyContent: 'center', gap: SP.sm, padding: SP.lg, textAlign: 'center',
        }}
      >
        <span style={{ color: C.textMuted }}>{ic(ImageIcon, ICON_SIZE.xl)}</span>
        <div style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>Опишите, что нарисовать, или перетащите сюда картинку</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>
          PNG, JPG или WebP до 20 МБ. Картинка останется в проекте, в папке {folder || '(корень проекта)'}/
        </div>
        <Button variant="secondary" size="sm" leftIcon={ic(Upload)} onClick={() => input.current?.click()}>Загрузить</Button>
        <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" hidden onChange={e => take(e.target.files?.[0])} />
      </div>
    </div>
  );
}
