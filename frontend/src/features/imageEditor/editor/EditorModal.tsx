// Попап «Редактор» (записка v3, решение Андрея 1): Modal size="fullscreen" вместо окна
// v2. Слева холст с зумом, панорамой и пометками, справа колонка 310 px: быстрые
// действия, «Без ИИ», переход между версиями (у старой нити — варианты и шаги с откатом).
// На 390 — на весь экран, секции ниже холста прокруткой. Открытая не текущая версия
// становится текущей при первой правке: следующая правка всегда идёт от версии в работе. Закрытие (✕, «Готово», Esc) выбор картинки не снимает; пометки
// остаются в сторе нити и уходят чипом со следующим сообщением.

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Check, Download, Eye, Save } from 'lucide-react';
import {
  Button, Chip, Field, Modal, ModalActions, TextField, C, FS, R, SP, ICON_SIZE, ICON_STROKE, showToast, useIsMobile,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageTransformBase, type ImageTransformOp, type ImageEncodeSpec, type ImageEncodeFormat } from '../api';
import { AdjustPanel } from '../AdjustPanel';
import { CropBar } from '../CropBar';
import { EditorCanvas } from '../EditorCanvas';
import { MarksTools, MobileToolbar } from '../EditorSections';
import { QuickActions } from '../PanelSections';
import { isPersonalScope } from '../scope';
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
import { imageSrc, launchThread, quickAvailabilityFor, threadHasImage, versionSrc } from '../thread/useThreadLaunch';
import { useCatalog } from '../thread/catalog';
import { effectiveSettings, modeSettings, usePrefs } from '../thread/prefs';
import { modeAware } from '../thread/modeState';
import { useJobStatus } from '../thread/useJobStatus';

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
  const viewedSrc = useMemo(() => {
    if (!thread) return null;
    const v = isLegacyThread(thread) ? null : findVersion(thread, versionId) ?? currentVersion(thread);
    return v ? versionSrc(projectId, thread, v) : imageSrc(projectId, thread, thread.currentStepId);
  }, [projectId, thread, versionId]);
  const sourceInfo = useSourceInfo(viewedSrc);
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
  // Быстрые действия редактора — правка: с режимами (image-panel-v5) идут выбором «Править»
  const settings = modeAware() ? modeSettings('edit', prefs, thread.settings) : effectiveSettings(prefs, thread.settings);
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

  const canvas = shownSrc ? (
    <div style={{ position: 'relative', flex: 1, minHeight: 0, display: 'flex' }}>
      <EditorCanvas src={shownSrc} size={preview && !before ? null : dims} marks={preview && !before ? [] : marks}
        onMarksChange={setMarks} tool={preview ? 'hand' : tool}
        onTextAt={(x, y) => setLabelAt({ x, y, text: '' })}
        onImageLoad={onImageLoad}
        crop={crop} onCropChange={rect => setCrop(c => (c ? { ...c, rect } : c))}
        hint={preview ? (before ? 'До: текущий шаг' : 'После: выбранный вариант') : crop ? 'Тяните рамку или её углы' : transforming ? 'Сохраняем правку…' : undefined}
        overlay={crop && dims ? (
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
      {mobile && !preview && !crop && <MobileToolbar tool={tool} onTool={setTool} />}
    </div>
  ) : (
    <div style={{
      flex: 1, minHeight: 240, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: SP.lg,
      color: C.textMuted, fontSize: FS.sm, textAlign: 'center', background: C.bgInset,
    }}>
      Картинки ещё нет. Опишите её в композере в режиме «Картинка» — варианты появятся в карточке и здесь.
    </div>
  );

  const side = (
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
