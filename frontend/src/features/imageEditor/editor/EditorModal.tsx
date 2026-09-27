// Попап «Редактор» (записка v3, решение Андрея 1): Modal size="fullscreen" вместо окна
// v2. Слева холст с зумом, панорамой и пометками, справа колонка 310 px: варианты,
// быстрые действия, «Без ИИ», шаги с откатом. На 390 — на весь экран, секции ниже
// холста прокруткой. Закрытие (✕, «Готово», Esc) выбор картинки не снимает; пометки
// остаются в сторе нити и уходят чипом со следующим сообщением.

import { useMemo, useState, type ReactNode } from 'react';
import { Check, Eye, Save } from 'lucide-react';
import {
  Button, Field, Modal, ModalActions, TextField, C, FS, R, SP, ICON_SIZE, ICON_STROKE, showToast, useIsMobile,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageTransformBase, type ImageTransformOp, type ImageEncodeSpec, type ImageEncodeFormat } from '../api';
import { AdjustPanel } from '../AdjustPanel';
import { CropBar } from '../CropBar';
import { EditorCanvas } from '../EditorCanvas';
import { MarksTools, MobileToolbar } from '../EditorSections';
import { QuickActions } from '../PanelSections';
import { SaveAsDialog } from '../SaveAsDialog';
import { QUICK_ACTIONS, quickUsesOwnModel, type OutpaintRatio, type QuickAction } from '../editorInputs';
import { hasMaskMark, type Mark, type Tool } from '../marks';
import { defaultStem, nameStem } from '../saveAs';
import { splitPath } from '../format';
import { fitCropRatio, initialCrop, isFullCrop, type CropRatio } from '../transforms';
import type { ImageFractionRect } from '../api';
import { rollbackTo, saveAsInThread, saveToProject, savedStepOf, takeVariant } from '../thread/actions';
import { JobBlock } from '../thread/ThreadCard';
import { chainOf, currentIndex, currentStack, saveFolder, stepOf, threadName, versionLabel } from '../thread/model';
import { closeEditor, getThreadMarks, setThreadMarks, useThreads } from '../thread/threadStore';
import { imageSrc, launchThread, threadHasImage } from '../thread/useThreadLaunch';
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

export function EditorModal({ projectId, sessionId, threadId }: { projectId: string; sessionId: string; threadId: string }) {
  const mobile = useIsMobile();
  const api = useMemo(() => imageEditorApi(), []);
  const state = useThreads(projectId, sessionId);
  const thread = state.threads.find(t => t.id === threadId) ?? null;
  const [tool, setTool] = useState<Tool>('mask');
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

  if (!thread) return null;
  const stack = currentStack(thread);
  const chain = chainOf(thread, stack);
  const at = currentIndex(thread, chain, stack);
  const cur = chain[at];
  const src = imageSrc(projectId, thread, thread.currentStepId);
  const shownSrc = preview && !before ? api.variantUrl(projectId, preview.jobId, preview.variant) : src;
  const dims = size && size.src === src ? { w: size.w, h: size.h } : null;
  const { marks } = getThreadMarks(thread.id);
  const running = !!thread.pendingJobId && (status?.phase ?? 'run') === 'run';
  const hasImage = threadHasImage(thread);
  const saved = savedStepOf(thread.id);
  const canSave = !!thread.currentStepId && !transforming;

  const setMarks = (ms: Mark[]) => setThreadMarks(thread.id, ms, dims);
  const onImageLoad = (img: HTMLImageElement) => {
    if (img.getAttribute('src') !== src) return;
    setSize({ src: src!, w: img.naturalWidth, h: img.naturalHeight });
  };

  // Правка без ИИ: сервер пишет шаг, шаг привязывается к нити тем же take
  const transformBase: ImageTransformBase | null = thread.currentStepId
    ? { stepId: thread.currentStepId } : thread.file ? { path: thread.file } : null;
  const runTransform = async (ops: ImageTransformOp[], encode: ImageEncodeSpec | null) => {
    if (!transformBase) return;
    setTransforming(true);
    try {
      const r = await api.transform(projectId, { base: transformBase, ops, encode });
      if (r.stepId) await takeVariant(projectId, sessionId, thread, { stepId: r.stepId });
      setThreadMarks(thread.id, [], null);
      setCrop(null);
    } catch (e) {
      showToast(`Правка не выполнена: ${(e as Error).message}`, '', 'error');
    } finally {
      setTransforming(false);
    }
  };

  const quickBlock = (a: QuickAction) => {
    if (running) return 'Идёт генерация';
    if (transforming) return 'Правка ещё сохраняется';
    if (!hasImage) return 'Сначала нарисуйте картинку';
    if ((a === 'removeMarked') && !hasMaskMark(marks)) return 'Закрасьте кистью, что убрать';
    return '';
  };
  const runQuick = (a: QuickAction) => {
    void launchThread(projectId, sessionId, thread, a === 'outpaint' ? { kind: a, ratio } : { kind: a });
    if (quickUsesOwnModel(a)) showToast('Улучшаем лица — один вариант', '', 'info');
  };

  const save = async () => { setSaving(true); await saveToProject(projectId, sessionId, thread); setSaving(false); };
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
        <Section title="Пометки" meta="уйдут со следующим сообщением">
          <MarksTools tool={tool} onTool={setTool} marksCount={marks.length} onClear={() => setMarks([])} disabled={!!preview || !!crop} />
        </Section>
      )}
      <Section title="Быстрые действия" meta="сразу, без промпта">
        <QuickActions actions={QUICK_ACTIONS} blockReason={quickBlock} ratio={ratio} onRatio={setRatio} onRun={runQuick} />
      </Section>
      {hasImage && (
        <Section title="Без ИИ" meta="бесплатно, мгновенно">
          <AdjustPanel api={api} projectId={projectId} stepId={thread.currentStepId ?? thread.file ?? ''} size={dims}
            base={transformBase ? Promise.resolve(transformBase) : null} sourceFormat={null} beforeBytes={null}
            blockReason={running ? 'Идёт генерация' : transforming ? 'Правка ещё сохраняется' : preview ? 'Сначала возьмите вариант или вернитесь к картинке' : ''}
            cropping={!!crop}
            onOp={op => { void runTransform([op], null); }}
            onCrop={() => { if (dims) setCrop(c => (c ? null : { rect: initialCrop('free', dims), ratio: 'free' })); }}
            onApply={(ops, encode) => { void runTransform(ops, encode); }} />
        </Section>
      )}
      {chain.length > 0 && (
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

  const subtitle = [versionLabel(thread, cur, saved), chain.length ? stepOf(at, chain.length) : null].filter(Boolean).join(' · ');

  return (
    <>
      <Modal size="fullscreen" title={`Редактор · ${threadName(thread)}`} subtitle={subtitle} onClose={closeEditor}
        closeOnBackdrop={false}
        footer={(
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', width: '100%', justifyContent: 'flex-end' }}>
            {!mobile && (
              <span style={{ marginRight: 'auto', fontSize: FS.xs, color: C.textMuted }}>
                {marks.length ? 'Пометки уйдут со следующим сообщением — в режиме «Картинка» или агенту' : 'Отметьте место кистью, рамкой или стрелкой — модель это увидит'}
              </span>
            )}
            <Button size="sm" variant="secondary" leftIcon={ic(Save)} disabled={!canSave} onClick={() => setSaveAs(true)}>Сохранить как…</Button>
            <Button size="sm" variant="secondary" disabled={!canSave || thread.currentStepId === saved} loading={saving}
              onClick={() => { void save(); }}>
              {thread.currentStepId && thread.currentStepId === saved ? 'В проекте' : 'Сохранить в проект'}
            </Button>
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
      {saveAs && (
        <SaveAsDialog projectId={projectId} sourcePath={thread.file}
          defaultName={thread.file ? baseName : nameStem(baseName)} folder={folder} format={format}
          onCheck={(f, name) => api.saveCheck(projectId, { folder: f, name, format })}
          onSave={async v => { await saveAsInThread(projectId, sessionId, thread, v); setSaveAs(false); }}
          onClose={() => setSaveAs(false)} />
      )}
    </>
  );
}
