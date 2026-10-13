// Попап «Редактор» (записка v3, решение Андрея 1): Modal size="fullscreen" вместо окна
// v2. Слева холст с зумом, панорамой и пометками, справа колонка 310 px: быстрые
// действия, «Без ИИ», переход между версиями (у старой нити — варианты и шаги с откатом).
// На 390 — на весь экран, секции ниже холста прокруткой. Открытая не текущая версия
// становится текущей при первой правке: следующая правка всегда идёт от версии в работе. Закрытие (✕, «Готово», Esc) выбор картинки не снимает; пометки
// остаются в сторе нити и уходят чипом со следующим сообщением.

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Check, Download, Eye, Save } from 'lucide-react';
import {
  Button, Chip, ConfirmDialog, Field, Modal, ModalActions, TextField, C, FS, R, SP, ICON_SIZE, ICON_STROKE, showToast, useIsMobile,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageTransformBase, type ImageTransformOp, type ImageEncodeSpec, type ImageEncodeFormat } from '../api';
import { AdjustPanel } from '../AdjustPanel';
import { CropBar } from '../CropBar';
import { EditorCanvas } from '../EditorCanvas';
import { FaceOverlay, FaceSearchScrim, type FaceLabel } from '../FaceOverlay';
import { FaceSwapBar } from '../FaceSwapBar';
import { FaceSwapPanel } from '../FaceSwapPanel';
import { faceSwapView, launchFailure, clearFailures, clearFaceChange, parseFacesChangedBody, hasAssignments, faceTargetFromAssignments, faceQuoteChip, overlayTap, refusalFaces, FS_TEXT, type NoticeSpec, type FaceUi } from '../faceSwapView';
import { MarksTools, MobileToolbar } from '../EditorSections';
import { QuickActions } from '../PanelSections';
import { isPersonalScope } from '../scope';
import { useCharacters } from '../characters/useCharacters';
import { SaveAsDialog } from '../SaveAsDialog';
import { QUICK_ACTIONS, quickOffered, quickUsesOwnModel, type OutpaintRatio, type QuickAction } from '../editorInputs';
import { hasMaskMark, type Mark, type Tool } from '../marks';
import { defaultStem, nameStem } from '../saveAs';
import { splitPath } from '../format';
import { fitCropRatio, formatOf, initialCrop, isFullCrop, type CropRatio } from '../transforms';
import type { ImageFractionRect } from '../api';
import {
  applyStep, continueFrom, rollbackTo, saveAsInThread, saveToProject, savedStepOf, versionSaved,
} from '../thread/actions';
import { JobBlock } from '../thread/ThreadCard';
import { download, PERSONAL_DOWNLOAD_HINT } from '../thread/download';
import {
  chainOf, currentIndex, currentStack, currentVersion, downloadName, findVersion, isLegacyThread, ORIGIN, originFile, saveFolder, stepOf,
  threadName, versionHasImage, versionLabel, versionName, versionShort, versionsOf, versionStep,
} from '../thread/model';
import { closeEditor, getThreadMarks, getThreadsState, setThreadMarks, showEditorVersion, useThreads, type EditorTool } from '../thread/threadStore';
import { faceSwapQuoteBody, imageSrc, launchFaceSwap, launchThread, quickAvailabilityFor, resolveModel, threadHasImage, versionSrc } from '../thread/useThreadLaunch';
import { useCatalog } from '../thread/catalog';
import { modeSettings, usePrefs } from '../thread/prefs';
import { useJobStatus } from '../thread/useJobStatus';
import { useFaceSwap } from '../thread/faceSwap';
import { useQuote } from '../useQuote';

const ic = (I: typeof Check, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

function Section({ title, meta, children }: { title: string; meta?: string; children: ReactNode }) {
  return (
    <section style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
        {title}{meta && <span style={{ fontWeight: 400, color: C.textMuted }}> · {meta}</span>}
      </div>
      {children}
    </section>
  );
}

// Формат и вес показанной картинки — из её ответа (браузер берёт его из кэша холста).
// undefined — ещё не узнали: форма «Размер и сжатие» ждёт, иначе считала бы от PNG
function useSourceInfo(src: string | null) {
  const [info, setInfo] = useState<{ src: string; format: ImageEncodeFormat | null; bytes: number | null } | null>(null);
  useEffect(() => {
    if (!src) return;
    let alive = true;
    fetch(src).then(r => r.blob())
      .then(b => { if (alive) setInfo({ src, format: formatOf(b.type), bytes: b.size }); })
      .catch(() => { if (alive) setInfo({ src, format: null, bytes: null }); });
    return () => { alive = false; };
  }, [src]);
  return info && info.src === src ? info : undefined;
}

// Инструменты холста; остальные инструменты старта — правки без ИИ в колонке справа
const CANVAS_TOOLS: readonly EditorTool[] = ['hand', 'mask', 'arrow', 'rect', 'text', 'eraser'];

export function EditorModal({ projectId, sessionId, threadId, versionId = null, startTool }: {
  projectId: string; sessionId: string; threadId: string; versionId?: string | null;
  // С чем открыть: инструмент холста, «Обрезать» (сразу рамка) или раздел «Без ИИ»
  startTool?: EditorTool;
}) {
  const mobile = useIsMobile();
  const api = useMemo(() => imageEditorApi(), []);
  // Личный чат вне проекта: сохранять некуда — вместо «Сохранить» в футере «Скачать»
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.threads.find(t => t.id === threadId) ?? null;
  const [tool, setTool] = useState<Tool>(() => (startTool && CANVAS_TOOLS.includes(startTool) ? startTool as Tool : 'mask'));
  // «Обрезать» из панели ставит рамку один раз, как только известен размер картинки
  const [cropPending, setCropPending] = useState(startTool === 'crop');
  const noAiRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState<{ src: string; w: number; h: number } | null>(null);
  const [crop, setCrop] = useState<{ rect: ImageFractionRect; ratio: CropRatio } | null>(null);
  const [labelAt, setLabelAt] = useState<{ x: number; y: number; text: string } | null>(null);
  const [ratio, setRatio] = useState<OutpaintRatio>('16:9');
  const [preview, setPreview] = useState<{ jobId: string; variant: number } | null>(null);
  const [before, setBefore] = useState(false);
  const [saveAs, setSaveAs] = useState(false);
  const [saving, setSaving] = useState(false);
  const [transforming, setTransforming] = useState(false);
  const { status } = useJobStatus(projectId, thread?.pendingJobId ?? null);
  const catalog = useCatalog(projectId);
  const prefs = usePrefs(projectId);
  // «Подставить персонажа» (этап 2 · шаг 10b): режим поверх холста, как обрезка.
  // versionId версии, на которой ищем лица; provider — исполнитель текущего запуска
  const viewedVersionId = thread && !isLegacyThread(thread)
    ? (findVersion(thread, versionId) ?? currentVersion(thread))?.id ?? null : null;
  const editSettings = modeSettings('edit', prefs, thread?.settings);
  const faceProvider = resolveModel(catalog, editSettings).pv?.key ?? null;
  const faceSwap = useFaceSwap(projectId, sessionId, threadId, viewedVersionId, faceProvider);
  const [faceSwapMode, setFaceSwapMode] = useState(startTool === 'faceSwap');
  // Подтверждение «Кисть работает с одним лицом — назначения сбросятся»
  const [brushConfirm, setBrushConfirm] = useState(false);
  const { list: faceCharacters } = useCharacters(projectId);
  // Локальное состояние экрана: мелкое лицо (тап), отказы запуска, «локальные на этот запуск», запуск
  const [faceUi, setFaceUi] = useState<FaceUi>({
    tappedSmall: null, tooClose: false, faceOutOfFrame: false, busy: false,
    providerUnavailable: false, facesChanged: false, useLocalOnce: false, submitting: false, refusedFace: null, refusedText: null,
  });
  // Вход в режим / смена версии-нити: новый поиск лиц + сброс переходных состояний (устаревший ответ не применяется)
  useEffect(() => {
    if (!faceSwapMode) return;
    void faceSwap.search();
    setFaceUi(u => ({ ...u, tappedSmall: null, tooClose: false, faceOutOfFrame: false, busy: false, providerUnavailable: false, facesChanged: false, refusedFace: null, refusedText: null }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [faceSwapMode, threadId, viewedVersionId]);
  const viewedSrc = useMemo(() => {
    if (!thread) return null;
    const v = isLegacyThread(thread) ? null : findVersion(thread, versionId) ?? currentVersion(thread);
    return v ? versionSrc(projectId, thread, v) : imageSrc(projectId, thread, thread.currentStepId);
  }, [projectId, thread, versionId]);
  const sourceInfo = useSourceInfo(viewedSrc);
  // Котировка подстановки до запуска (ADR-028, шаг 16б): чип подвала берёт из неё число проходов
  // и время. Тело — то же, что уйдёт в запуск (faceSwapQuoteBody); хук — до раннего return
  const faceTargetNow = faceSwapMode && faceSwap.state.phase === 'ready'
    ? faceTargetFromAssignments(faceSwap.state.assignments, viewedVersionId ?? '', faceSwap.state.facesKey ?? '') : null;
  const faceDims = size && size.src === viewedSrc ? { w: size.w, h: size.h } : null;
  const { quote: faceQuoteResp } = useQuote(api, projectId, faceTargetNow ? faceSwapQuoteBody(catalog, faceTargetNow, faceDims) : null);
  // «Повернуть и отразить», «Размер и формат» из панели: раздел «Без ИИ» сразу на виду
  useEffect(() => {
    if (startTool !== 'rotate' && startTool !== 'resize') return;
    const box = noAiRef.current;
    const el = startTool === 'resize' ? box?.querySelector('[data-size-compress]') ?? box : box;
    el?.scrollIntoView?.({ block: 'start' });
  }, [startTool, sourceInfo]);

  if (!thread) return null;
  const stack = currentStack(thread);
  const chain = chainOf(thread, stack);
  const at = currentIndex(thread, chain, stack);
  const cur = chain[at];
  // Нить с версиями: показана открытая версия (по умолчанию — текущая)
  const legacy = isLegacyThread(thread);
  const viewed = legacy ? null : findVersion(thread, versionId) ?? currentVersion(thread);
  const viewedStep = viewed ? versionStep(thread, viewed) : thread.currentStepId;
  const isCurrent = !viewed || viewed.id === thread.currentVersionId;
  const src = viewed ? versionSrc(projectId, thread, viewed) : imageSrc(projectId, thread, thread.currentStepId);
  const shownSrc = preview && !before ? api.variantUrl(projectId, preview.jobId, preview.variant) : src;
  const dims = size && size.src === src ? { w: size.w, h: size.h } : null;
  const { marks } = getThreadMarks(thread.id);
  const running = !!thread.pendingJobId && (status?.phase ?? 'run') === 'run';
  const hasImage = viewed ? versionHasImage(thread, viewed) : threadHasImage(thread);
  const saved = savedStepOf(thread.id);
  const inProject = viewed ? versionSaved(thread, viewed) : !!thread.currentStepId && thread.currentStepId === saved;
  const canSave = !!viewedStep && !transforming;
  // «Подставить персонажа»: состояние экрана (faceSwapView) — плашка, плашки, canSubmit, список
  const localAvailable = !!catalog && (catalog.providers.find(p => p.key === 'local')?.available ?? true);
  // Этап 3: число проходов = число назначений (0, 1 или 2)
  const facePasses = faceSwapMode ? faceSwap.state.assignments.assignments.size : 0;
  const faceView = faceSwapMode ? faceSwapView({
    phase: faceSwap.state.phase,
    faces: faceSwap.state.faces,
    selectedFace: faceSwap.state.selectedFace,
    selectedCharacter: faceSwap.state.selectedCharacter,
    error: faceSwap.state.error,
    provider: faceProvider,
    localAvailable,
    useLocalOnce: faceUi.useLocalOnce,
    tappedSmall: faceUi.tappedSmall,
    over8: faceSwap.state.faces.length >= 8,
    facesTooClose: faceUi.tooClose,
    faceOutOfFrame: faceUi.faceOutOfFrame,
    busy: faceUi.busy,
    providerUnavailable: faceUi.providerUnavailable,
    facesChanged: faceUi.facesChanged,
    hasMask: hasMaskMark(marks),
    assignments: faceSwap.state.assignments,
    searchFailure: faceSwap.state.searchFailure,
    refusedFace: faceUi.refusedFace,
    refusedText: faceUi.refusedText,
  }) : null;

  // Правка открытой не текущей версии: сначала она становится текущей
  const ensureCurrent = async () => (isCurrent || !viewed ? true : continueFrom(projectId, sessionId, thread, viewed.id));
  const fresh = () => getThreadsState(sessionId).threads.find(t => t.id === thread.id) ?? thread;

  const setMarks = (ms: Mark[]) => {
    if (ms.length && !isCurrent) void ensureCurrent();
    setThreadMarks(thread.id, ms, dims);
  };
  const onImageLoad = (img: HTMLImageElement) => {
    if (img.getAttribute('src') !== src) return;
    const d = { w: img.naturalWidth, h: img.naturalHeight };
    setSize({ src: src!, ...d });
    if (cropPending) {
      setCropPending(false);
      setCrop({ rect: initialCrop('free', d), ratio: 'free' });
    }
  };

  // Правка без ИИ: сервер пишет шаг, шаг ложится в текущую версию (у старой нити — take)
  const baseFile = viewed ? (viewed.id === ORIGIN ? originFile(thread) : null) : thread.file;
  const transformBase: ImageTransformBase | null = viewedStep
    ? { stepId: viewedStep } : baseFile ? { path: baseFile } : null;
  const runTransform = async (ops: ImageTransformOp[], encode: ImageEncodeSpec | null) => {
    if (!transformBase) return;
    setTransforming(true);
    try {
      if (!(await ensureCurrent())) return;
      const r = await api.transform(projectId, { base: transformBase, ops, encode });
      if (r.stepId) await applyStep(projectId, sessionId, fresh(), r.stepId);
      setThreadMarks(thread.id, [], null);
      setCrop(null);
    } catch (e) {
      showToast(`Правка не выполнена: ${(e as Error).message}`, '', 'error');
    } finally {
      setTransforming(false);
    }
  };

  // Умеет ли поставщик полосы действие — по каталогу, до котировки и запуска
  // Быстрые действия редактора — правка: идут выбором «Править»
  const settings = modeSettings('edit', prefs, thread.settings);
  const quickActions = QUICK_ACTIONS.filter(a => quickOffered(a, catalog));
  const availability = (a: QuickAction) => (catalog ? quickAvailabilityFor(catalog, settings, a) : null);
  const quickBlock = (a: QuickAction) => {
    if (running) return 'Идёт генерация';
    if (transforming) return 'Правка ещё сохраняется';
    if (!hasImage) return 'Сначала нарисуйте картинку';
    if ((a === 'removeMarked') && !hasMaskMark(marks)) return 'Закрасьте кистью, что убрать';
    return availability(a)?.reason ?? '';
  };
  // «Взять fal» предлагаем, только когда мешает одно умение поставщика
  const quickFallback = (a: QuickAction) => {
    const r = availability(a);
    return r && !r.route && quickBlock(a) === r.reason ? r.fallback : null;
  };
  // Быстрое действие запускает генерацию и закрывает попап: версии лягут в ленту внизу
  const runQuick = async (a: QuickAction, provider?: string) => {
    if (!(await ensureCurrent())) return;
    const ok = await launchThread(projectId, sessionId, fresh(), a === 'outpaint' ? { kind: a, ratio } : { kind: a }, provider ? { provider } : undefined);
    if (ok && quickUsesOwnModel(a)) showToast('Улучшаем лица — один вариант', '', 'info');
    if (ok && !legacy) closeEditor();
  };

  const save = async () => { setSaving(true); await saveToProject(projectId, sessionId, thread, viewedStep); setSaving(false); };
  const folder = saveFolder(thread);
  const baseName = thread.file ? defaultStem(splitPath(thread.file).name) : 'kartinka';
  const format: ImageEncodeFormat = thread.file && /\.jpe?g$/i.test(thread.file) ? 'jpeg' : thread.file && /\.webp$/i.test(thread.file) ? 'webp' : 'png';

  // Ярлыки назначенных лиц на холсте: имя персонажа (удалён — slug) и первое фото
  const faceLabels = new Map<number, FaceLabel>();
  for (const [n, slug] of faceSwap.state.assignments.assignments) {
    const c = faceCharacters?.find(x => x.slug === slug);
    const file = c?.photos[0]?.file;
    faceLabels.set(n, { name: c?.name?.trim() || slug, avatar: file ? api.characterPhotoUrl(projectId, slug, file) : null });
  }
  // «Подставить персонажа»: оверлей поверх картинки — поиск (scrim) или рамки лиц
  const faceOverlayNode = faceSwapMode && dims ? (
    faceSwap.state.phase === 'loading'
      ? <FaceSearchScrim busy={faceSwap.state.error === 'busy'} text={faceView?.bar ?? FS_TEXT.search} />
      : faceSwap.state.phase === 'ready' && faceSwap.state.faces.length > 0
        ? <FaceOverlay
            faces={faceSwap.state.faces} width={faceSwap.state.width} height={faceSwap.state.height}
            selectedFace={faceSwap.state.assignments.active} tappedSmall={faceUi.tappedSmall}
            errorFaces={refusalFaces(faceUi, faceSwap.state.faces)}
            labels={faceLabels} limitFace={faceSwap.state.assignments.limit}
            mobile={mobile}
            onSelect={n => overlayTap(n, faceSwap.state.faces, {
              small: m => setFaceUi(u => ({ ...u, tappedSmall: m })),
              tap: m => { faceSwap.tap(m); setFaceUi(u => ({ ...clearFaceChange(u), tappedSmall: null })); },
            })} />
        : null
  ) : null;

  // Запуск подстановки: тем же путём (quote + startJob), faceTarget из назначений (ADR-028, этап 3)
  const launchFaceSwapNow = async () => {
    const target = faceTargetFromAssignments(faceSwap.state.assignments, viewedVersionId ?? '', faceSwap.state.facesKey ?? '');
    if (!target) return;
    // Новый запуск: сброс ВСЕХ флагов отказов (иначе старый отказ висит поверх нового)
    setFaceUi(u => ({ ...clearFailures(u), submitting: true }));
    try {
      const ok = await launchFaceSwap(projectId, sessionId, thread, target, viewedVersionId, dims);
      if (ok) setFaceSwapMode(false); // новая версия ляжет в ленту; режим закрывается
    } catch (e) {
      const err = e as { status?: number; body?: { code?: string; error?: string; face?: unknown; facesKey?: string; faces?: unknown } } | null;
      // Номер лица в теле отказа «близко»/«вне кадра» (ADR-028); нет — отказ без привязки к лицу
      const refusedFace = typeof err?.body?.face === 'number' ? err.body.face : null;
      const code = typeof err?.body?.code === 'string' ? err.body.code : null;
      // Бэкенд шлёт { error, code }; поля body.message нет (всегда undefined)
      const msg = typeof err?.body?.error === 'string' ? err.body.error : (e as Error).message;
      // 409 faces_changed: тело — свежий ImageThreadFacesResponse (facesKey + faces), не {code, error}.
      // Обновляем facesKey/список и сбрасываем ВСЕ назначения — иначе повторный запуск уходит со
      // старым facesKey → вечный цикл 409 (дефект high приёмки этапа 2)
      if (err?.status === 409 && typeof err.body?.facesKey === 'string' && Array.isArray(err.body?.faces)) {
        const fresh = parseFacesChangedBody(err.body);
        if (fresh) {
          faceSwap.applyFacesChanged(fresh);
          setFaceUi(u => ({ ...u, facesChanged: true }));
        } else {
          showToast(`Запуск не удался: ${msg}`, '', 'error');
        }
      } else {
        // Реакция на прочие отказы запуска — по коду (launchFailure), не по тексту сообщения
        const f = launchFailure(code, msg, err?.body?.face);
        switch (f.kind) {
        case 'facesTooClose': setFaceUi(u => ({ ...u, tooClose: true, refusedFace })); break;
        case 'faceOutOfFrame': setFaceUi(u => ({ ...u, faceOutOfFrame: true, refusedFace })); break;
        case 'faceRefused': setFaceUi(u => ({ ...u, refusedFace: f.face, refusedText: f.message })); break;
        case 'busy': setFaceUi(u => ({ ...u, busy: true })); break;
        case 'providerUnavailable': setFaceUi(u => ({ ...u, providerUnavailable: true })); break;
        case 'facesChanged': setFaceUi(u => ({ ...u, facesChanged: true })); break;
        case 'featureDisabled':
          // Флаг выключили, пока экран был открыт: режим закрывается с коротким сообщением
          showToast(FS_TEXT.featureDisabled, '', 'info');
          setFaceSwapMode(false);
          break;
        case 'generic': showToast(`Запуск не удался: ${msg}`, '', 'error'); break;
        default: {
          // Исчерпывающесь: все виды LaunchFailure обработаны выше; f сужен до never
          const _exhaustive: never = f;
          void _exhaustive;
          break;
        }
        }
      }
    } finally {
      setFaceUi(u => ({ ...u, submitting: false }));
    }
  };

  const canvas = shownSrc ? (
    <div style={{ position: 'relative', flex: 1, minHeight: 0, display: 'flex' }}>
      <EditorCanvas src={shownSrc} size={preview && !before ? null : dims} marks={preview && !before ? [] : marks}
        onMarksChange={setMarks} tool={preview || faceSwapMode ? 'hand' : tool}
        onTextAt={(x, y) => setLabelAt({ x, y, text: '' })}
        onImageLoad={onImageLoad}
        crop={crop} onCropChange={rect => setCrop(c => (c ? { ...c, rect } : c))}
        faceOverlay={faceOverlayNode}
        hint={faceSwapMode ? (faceView?.bar ?? FS_TEXT.search) : preview ? (before ? 'До: текущий шаг' : 'После: выбранный вариант') : crop ? 'Тяните рамку или её углы' : transforming ? 'Сохраняем правку…' : undefined}
        overlay={faceSwapMode && faceView ? (
          <FaceSwapBar text={faceView.bar} icon={faceView.barIcon} mobile={mobile} onCancel={() => setFaceSwapMode(false)} />
        ) : crop && dims ? (
          <CropBar ratio={crop.ratio} mobile={mobile}
            onRatio={r => setCrop(c => (c ? { ratio: r, rect: r === 'free' ? c.rect : fitCropRatio(c.rect, r, dims) } : c))}
            onCancel={() => setCrop(null)}
            onApply={() => {
              if (isFullCrop(crop.rect)) { setCrop(null); return; }
              void runTransform([{ type: 'crop', rect: crop.rect }], null);
            }} />
        ) : preview ? (
          <Button size="sm" variant={before ? 'ghostAccent' : 'secondary'} leftIcon={ic(Eye)} onClick={() => setBefore(b => !b)}>
            {before ? 'Показать после' : 'Показать до'}
          </Button>
        ) : undefined} />
      {mobile && !preview && !crop && !faceSwapMode && <MobileToolbar tool={tool} onTool={setTool} />}
    </div>
  ) : (
    <div style={{
      flex: 1, minHeight: 240, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: SP.lg,
      color: C.textMuted, fontSize: FS.sm, textAlign: 'center', background: C.bgInset,
    }}>
      Картинки ещё нет. Опишите её в композере в режиме «Картинка» — варианты появятся в карточке и здесь.
    </div>
  );

  // «Подставить персонажа»: панель назначений + подвал вместо обычного контента
  // Выход в кисть: режим закрывается, кисть включается (назначения при этом уже сброшены/пусты)
  const startBrush = () => { setFaceSwapMode(false); setTool('mask'); };
  const onFaceNoticeAction = (n: NoticeSpec) => {
    if (!n.action) return;
    if (n.action.kind === 'ghostAccent') {
      // «Закрасить кистью»: при существующих назначениях — подтверждение (этап 3)
      if (hasAssignments(faceSwap.state.assignments)) setBrushConfirm(true);
      else startBrush();
    } else {
      // «Использовать локальные для этого запуска» (состояние 9): явное действие, тихой подмены нет
      setFaceUi(u => ({ ...u, useLocalOnce: true }));
    }
  };
  // Чип котировки: «N прохода · бесплатно · ~M мин» из котировки; пока она едет — чипа нет
  const faceQuote = faceQuoteChip(faceQuoteResp);
  const faceSide = faceSwapMode && faceView ? (
    <div style={{
      display: 'flex', flexDirection: 'column', width: mobile ? '100%' : 310, flexShrink: 0,
      boxSizing: 'border-box', borderLeft: mobile ? undefined : `1px solid ${C.borderLight}`,
      background: C.bgPanel, minHeight: 0,
    }}>
      <FaceSwapPanel
        api={api} projectId={projectId}
        characters={faceCharacters ?? []}
        assignments={faceSwap.state.assignments}
        facePasses={facePasses > 0 ? facePasses : null}
        canSubmit={faceView.canSubmit}
        quote={faceView.canSubmit ? faceQuote : null}
        localOnce={faceView.localOnce}
        submitting={faceUi.submitting}
        notices={faceView.notices}
        onPick={slug => {
          const active = faceSwap.state.assignments.active;
          if (active == null) return;
          faceSwap.assign(active, slug);
          setFaceUi(u => ({ ...clearFaceChange(u), tappedSmall: null }));
        }}
        onRemove={face => { faceSwap.remove(face); setFaceUi(clearFailures); }}
        onTapFace={face => faceSwap.tap(face)}
        onNoticeAction={onFaceNoticeAction}
        onSubmit={() => { void launchFaceSwapNow(); }} />
    </div>
  ) : null;

  const side = faceSide ?? (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.lg, padding: mobile ? SP.md : SP.lg,
      width: mobile ? '100%' : 310, flexShrink: 0, boxSizing: 'border-box',
      overflowY: mobile ? undefined : 'auto', borderLeft: mobile ? undefined : `1px solid ${C.borderLight}`,
    }}>
      {thread.pendingJobId && (
        <Section title="Варианты">
          <JobBlock projectId={projectId} sessionId={sessionId} thread={thread} inEditor
            onPreview={v => { setPreview(v); setBefore(false); }} />
        </Section>
      )}
      {!mobile && hasImage && (
        <Section title="Пометки" meta={personal ? 'уйдут со следующим запуском' : 'уйдут со следующим сообщением'}>
          <MarksTools tool={tool} onTool={setTool} marksCount={marks.length} onClear={() => setMarks([])} disabled={!!preview || !!crop} />
        </Section>
      )}
      <Section title="Быстрые действия" meta="сразу, без промпта">
        <QuickActions actions={quickActions} blockReason={quickBlock} fallback={quickFallback} ratio={ratio} onRatio={setRatio}
          onRun={(a, provider) => { void runQuick(a, provider); }} />
      </Section>
      {hasImage && (
        <div ref={noAiRef} data-editor-no-ai="">
        <Section title="Без ИИ" meta={legacy ? 'бесплатно, мгновенно' : 'бесплатно, мгновенно, шагом этой версии'}>
          <AdjustPanel api={api} projectId={projectId} stepId={viewedStep ?? baseFile ?? ''} size={dims}
            base={transformBase ? Promise.resolve(transformBase) : null}
            sourceFormat={sourceInfo?.format} beforeBytes={sourceInfo?.bytes ?? null}
            blockReason={running ? 'Идёт генерация' : transforming ? 'Правка ещё сохраняется' : preview ? 'Сначала возьмите вариант или вернитесь к картинке' : ''}
            cropping={!!crop}
            onOp={op => { void runTransform([op], null); }}
            onCrop={() => { if (dims) setCrop(c => (c ? null : { rect: initialCrop('free', dims), ratio: 'free' })); }}
            onApply={(ops, encode) => { void runTransform(ops, encode); }} />
        </Section>
        </div>
      )}
      {viewed && (
        <Section title="Версии" meta="в ленте — карточка на каждую">
          <div data-editor-versions="" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
            {versionsOf(thread).filter(v => versionHasImage(thread, v)).map(v => (
              <Chip key={v.id} selected={v.id === viewed.id} onClick={() => showEditorVersion(v.id)}
                title={`${versionName(v)}${v.id === thread.currentVersionId ? ' · в работе' : ''}`}>
                {versionShort(v)}{versionSaved(thread, v) ? ' ✓' : ''}{v.id === thread.currentVersionId ? ' ●' : ''}
              </Chip>
            ))}
          </div>
        </Section>
      )}
      {legacy && chain.length > 0 && (
        <Section title="Шаги этой картинки" meta="в ленте — одна карточка-стопка">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            {chain.map((p, i) => {
              const here = i === at;
              const label = i === 0 && p.stepId === null ? 'Исходник' : `Шаг ${i + 1}`;
              return (
                <div key={p.stepId ?? 'source'} style={{
                  display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.xxs, borderRadius: R.md,
                  border: `1px solid ${here ? C.accent : 'transparent'}`, opacity: i > at ? 0.6 : 1,
                }}>
                  <span style={{ width: 56, flex: '0 0 56px', aspectRatio: '4 / 3', borderRadius: R.sm, overflow: 'hidden', background: C.bgInset }}>
                    <img src={imageSrc(projectId, thread, p.stepId) ?? ''} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
                  </span>
                  <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textPrimary }}>
                    {label}{here && <span style={{ color: C.textMuted }}> · сейчас</span>}
                  </span>
                  {!here && (
                    <Button size="xs" variant="ghost" disabled={running || transforming}
                      onClick={() => { void rollbackTo(projectId, sessionId, thread, p.stepId, label); }}>
                      Откатиться
                    </Button>
                  )}
                </div>
              );
            })}
          </div>
        </Section>
      )}
    </div>
  );

  const subtitle = viewed
    ? [versionName(viewed), personal ? null : inProject ? 'в проекте' : 'черновик', isCurrent ? 'в работе' : null].filter(Boolean).join(' · ')
    : [versionLabel(thread, cur, saved), chain.length ? stepOf(at, chain.length) : null].filter(Boolean).join(' · ');

  return (
    <>
      <Modal size="fullscreen" title={`Редактор · ${threadName(thread)}`} subtitle={subtitle} onClose={closeEditor}
        closeOnBackdrop={false}
        footer={(
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', width: '100%', justifyContent: 'flex-end' }}>
            {!mobile && (
              <span style={{ marginRight: 'auto', fontSize: FS.xs, color: C.textMuted }}>
                {!marks.length ? 'Отметьте место кистью, рамкой или стрелкой — модель это увидит'
                  : personal ? 'Пометки уйдут со следующим запуском в режиме «Картинка»'
                  : 'Пометки уйдут со следующим сообщением — в режиме «Картинка» или агенту'}
              </span>
            )}
            {personal ? (
              <Button size="sm" variant="secondary" leftIcon={ic(Download)} disabled={!src || !hasImage || transforming}
                title={PERSONAL_DOWNLOAD_HINT}
                onClick={() => { if (src) void download(src, mime => (viewed ? downloadName(thread, viewed, mime) : threadName(thread))); }}>
                Скачать
              </Button>
            ) : (
              <>
                <Button size="sm" variant="secondary" leftIcon={ic(Save)} disabled={!canSave} onClick={() => setSaveAs(true)}>Сохранить как…</Button>
                <Button size="sm" variant="secondary" disabled={!canSave || inProject} loading={saving}
                  onClick={() => { void save(); }}>
                  {inProject ? 'В проекте' : 'Сохранить в проект'}
                </Button>
              </>
            )}
            <Button size="sm" variant="primary" leftIcon={ic(Check)} onClick={closeEditor}>Готово</Button>
          </div>
        )}>
        {/* Телефон: холст фиксированной высоты, секции ниже прокручиваются вместе с телом окна */}
        <div style={{ display: 'flex', flexDirection: mobile ? 'column' : 'row', flex: mobile ? 'none' : 1, minHeight: 0 }}>
          <div style={{
            display: 'flex', background: C.bgInset, minWidth: 0,
            ...(mobile ? { height: 360, flexShrink: 0 } : { flex: 1, minHeight: 0 }),
          }}>{canvas}</div>
          {side}
        </div>
      </Modal>
      {labelAt && (
        <Modal title="Подпись на картинке" width={380} onClose={() => setLabelAt(null)}
          footer={<ModalActions confirmLabel="Добавить" confirmDisabled={!labelAt.text.trim()} onCancel={() => setLabelAt(null)}
            onConfirm={() => { setMarks([...marks, { type: 'text', x: labelAt.x, y: labelAt.y, text: labelAt.text.trim() }]); setLabelAt(null); }} />}>
          <Field>
            <TextField value={labelAt.text} autoFocus placeholder="сюда лампу" onChange={text => setLabelAt({ ...labelAt, text })}
              onEnter={() => { if (labelAt.text.trim()) { setMarks([...marks, { type: 'text', x: labelAt.x, y: labelAt.y, text: labelAt.text.trim() }]); setLabelAt(null); } }} />
          </Field>
        </Modal>
      )}
      {brushConfirm && (
        <ConfirmDialog title={FS_TEXT.brushConfirm} confirmLabel={FS_TEXT.brush}
          onCancel={() => setBrushConfirm(false)}
          onConfirm={() => { setBrushConfirm(false); faceSwap.clearAssignments(); startBrush(); }} />
      )}
      {saveAs && !personal && (
        <SaveAsDialog projectId={projectId} sourcePath={thread.file}
          defaultName={thread.file ? baseName : nameStem(baseName)} folder={folder} format={format}
          onCheck={(f, name) => api.saveCheck(projectId, { folder: f, name, format })}
          onSave={async v => { await saveAsInThread(projectId, sessionId, thread, v, undefined, viewedStep); setSaveAs(false); }}
          onClose={() => setSaveAs(false)} />
      )}
    </>
  );
}
