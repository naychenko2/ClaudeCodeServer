// Экран редактора картинок, раскладка v2 (макет docs/mockups/image-editor-v2.html):
// слева шесть секций инструментов, в центре холст или генерация / варианты, справа
// сверху поле промпта, под ним место чата картинки (пока там «Обсудить» из v1).
// На телефоне холст на весь экран, промпт внизу, «Инструменты» и «Чат» — шторки.

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ArrowLeft, Brush, Image as ImageIcon, MessageSquare, Upload, Wrench } from 'lucide-react';
import {
  Button, EmptyState, Field, IconButton, Island, Modal, ModalActions, TextField, ICON_SIZE, ICON_STROKE,
  C, FS, ISLAND, R, SP, useIsMobile, api as appApi, showToast, useMe, ModelsSpendModal,
} from 'aihome_shell/kit';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditCatalogReason, type ImageEditQuoteRequest } from './api';
import { EditorCanvas } from './EditorCanvas';
import { exportAnnotated, exportMask, hasAnnotationMark, hasMaskMark, marksToJson, type Mark, type Tool } from './marks';
import { effectiveProvider, isRemovalPrompt, modelBlockReason, money, nextVersionName, pickOp, plural, priceSum, priceText, splitPath, variantsWord, type ProviderChoice } from './format';
import { currentModel, ProviderModelPicker } from './ProviderModelPicker';
import { EditorSections, MarksTools, MobileToolbar, SectionHint, useEditorSections, type EditorSection } from './EditorSections';
import { HistorySteps, ProjectImagePicker, QuickActions, SamplesSection } from './PanelSections';
import {
  actionTitle, currentSrc, EMPTY_HISTORY, goToStep, maxSamples, panelJobInput, pushStep, quickBlockReason, quickPlan,
  type History, type LaunchAction, type LaunchPlan, type OutpaintRatio, type QuickAction, type Sample,
} from './editorInputs';
import { PromptCard } from './PromptCard';
import { useQuote } from './useQuote';
import { useImageEditJob } from './useImageEditJob';
import { ErrorView, GenerationView, VariantsView } from './ResultViews';
import { SaveDialog } from './SaveDialog';
import { CharacterChip, CharacterSection, useCharacterDialogs, useCharacters } from './characters/CharacterPicker';
import { DiscussPanel, openProjectChat, type DiscussState } from './discuss/DiscussPanel';
import { buildDiscussText } from './discuss/discussText';
import { discussSnapshot } from './discuss/snapshot';

export type ImageEditorTarget =
  | { kind: 'edit'; path: string }       // «Редактировать» у картинки проекта
  | { kind: 'create'; folder: string };  // «Нарисовать картинку» у папки

const ic = (I: typeof Brush, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export function ImageEditor({ projectId, projectName, target, onClose, onShowInFiles, onDirtyChange }: {
  projectId: string;
  projectName: string;
  target: ImageEditorTarget;
  onClose: () => void;
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
  const [history, setHistory] = useState<History>(() => target.kind === 'edit'
    ? { steps: [{ id: 'original', original: true, title: 'Оригинал', src: appApi.files.fileUrl(projectId, target.path) }], cur: 0 }
    : EMPTY_HISTORY);
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
  const [prompt, setPrompt] = useState('');
  const [count, setCount] = useState(3);
  const [selected, setSelected] = useState(0);
  const [saveOpen, setSaveOpen] = useState(false);
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
  const [discuss, setDiscuss] = useState<DiscussState | null>(null);
  // Чат обсуждения переиспользуется внутри одного сеанса редактора
  const discussSession = useRef<string | null>(null);

  const me = useMe();
  const [providersOpen, setProvidersOpen] = useState(false);
  const sectionsState = useEditorSections(me.userId);
  // Телефон: шторки «Инструменты» и «Чат»
  const [sheet, setSheet] = useState<'tools' | 'chat' | null>(null);

  const job = useImageEditJob(api, projectId);
  const busy = job.phase === 'starting' || job.phase === 'running';
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
        if (c.default.provider) setModel(c.default.model);
        else setProvider(c.providers[0]?.key ?? 'settings');
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
  };

  const canGenerate = !!quote && !quoteLoading && !blocked && !busy && (hasImage || !!prompt.trim());

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
      ...panelJobInput(samples, plan),
    }, n, q.expectedSeconds);
  }, [api, projectId, pv, m, quote, count, planOf, hasMask, hasAnnotations, size, src, marks, sourcePath, character, samples, job]);

  const generate = () => launch({ kind: 'prompt', prompt });
  const runQuick = (a: QuickAction) => { setSheet(null); void launch(a === 'outpaint' ? { kind: a, ratio } : { kind: a }); };

  const quickBlock = (a: QuickAction) => {
    if (busy) return 'Идёт генерация';
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
  const startFrom = (url: string) => {
    setHistory({ steps: [{ id: `o${++stepSeq.current}`, original: true, title: 'Оригинал', src: url }], cur: 0 });
    setSize(null);
    setMarks([]);
  };
  const goStep = (i: number) => {
    if (i === history.cur) return;
    setHistory(h => goToStep(h, i));
    // Размер подхватит холст, когда шаг загрузится
    setSize(null);
    setSheet(null);
  };

  const startDiscuss = async () => {
    // Ручка обсуждения без картинки с пометками не работает: кнопка активна только при картинке
    const img = imgRef.current;
    if (!img || !size) return;
    const annotated = await discussSnapshot(img, marks, size.w, size.h).catch(() => null);
    const q = prompt.trim();
    const marksWord = marks.length ? `, ${marks.length} ${plural(marks.length, 'пометка', 'пометки', 'пометок')}` : '';
    setDiscuss(d => {
      if (d?.thumbUrl) URL.revokeObjectURL(d.thumbUrl);
      return {
        sessionId: null, error: annotated ? null : 'Не удалось собрать картинку с пометками',
        thumbUrl: annotated ? URL.createObjectURL(annotated) : null,
        summary: `${hasImage ? 'Картинка' : 'Новая картинка'}${marksWord} и запрос${q ? `: «${q.length > 50 ? `${q.slice(0, 50)}…` : q}»` : ''}`,
      };
    });
    if (!annotated) return;
    try {
      const res = await api.discuss(projectId, {
        text: buildDiscussText({ fileName: sourcePath ? splitPath(sourcePath).name : null, prompt, marks, size }),
        annotated, sourcePath: sourcePath ?? undefined, characterSlug: character?.slug,
        sessionId: discussSession.current ?? undefined,
      });
      discussSession.current = res.sessionId;
      setDiscuss(d => (d ? { ...d, sessionId: res.sessionId } : d));
    } catch (e) {
      setDiscuss(d => (d ? { ...d, error: (e as Error).message } : d));
    }
  };

  const closeDiscuss = () => setDiscuss(d => {
    if (d?.thumbUrl) URL.revokeObjectURL(d.thumbUrl);
    return null;
  });

  const takeAsBase = () => {
    if (!job.jobId) return;
    const url = api.variantUrl(projectId, job.jobId, sel);
    setHistory(h => pushStep(h, { id: `s${++stepSeq.current}`, original: false, title: actionTitle(lastAction.current), src: url }));
    setSize(null);
    setMarks([]);
    setPrompt('');
    job.reset();
  };

  const save = async ({ fileName, folder }: { fileName: string; folder: string }) => {
    if (!job.jobId) return;
    const res = await api.save(projectId, sourcePath
      ? { jobId: job.jobId, variant: sel, sourcePath }
      : { jobId: job.jobId, variant: sel, folder: folder || undefined, fileName });
    setSaveOpen(false);
    setSavedPath(res.path);
    setSavedJobId(job.jobId);
    // Дальше правим уже сохранённый файл: следующая версия ляжет рядом с ним
    setSourcePath(res.path);
    showToast(`Сохранено в проект: ${res.path}`, '', 'info',
      onShowInFiles ? { label: 'Показать в файлах', onClick: () => onShowInFiles(res.path) } : undefined);
  };

  const title = !hasImage && target.kind === 'create' ? 'Новая картинка' : splitPath(sourcePath ?? initial.name).name;
  const folder = sourcePath ? splitPath(sourcePath).folder : initial.folder;

  // ── Центр ──
  let center: ReactNode;
  if (job.phase === 'running' || job.phase === 'starting') {
    center = <GenerationView count={job.count} progress={job.progress} onCancel={job.cancel} mobile={mobile} />;
  } else if (job.phase === 'variants' && job.jobId) {
    const jobId = job.jobId;
    center = (
      <VariantsView variants={job.variants} variantUrl={n => api.variantUrl(projectId, jobId, n)}
        before={src} cost={job.cost} selected={sel}
        onSelect={setSelected} onApply={() => setSaveOpen(true)} onBase={takeAsBase}
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
        onImageLoad={img => { imgRef.current = img; setSize({ w: img.naturalWidth, h: img.naturalHeight }); }} />
    );
  } else {
    center = <DropZone folder={folder} mobile={mobile} onFile={f => startFrom(URL.createObjectURL(f))} />;
  }

  const marksOn = hasImage && job.phase === 'idle';
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
      id: 'model', title: 'Чем рисовать',
      meta: pv && m ? `${pv.label} · ${m.label}` : undefined,
      body: (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {catalogError && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{catalogError}</div>}
          {notConfigured && catalog && (
            <NotConfigured reason={catalogReason(catalog)} isAdmin={me.role === 'admin'} onSetup={() => setProvidersOpen(true)} />
          )}
          {catalog && !notConfigured && (
            <ProviderModelPicker catalog={catalog} provider={provider} model={m?.id ?? model}
              onProvider={onProvider} onModel={setModel} hasImage={hasImage} hasMask={hasMask}
              priceLabel={priceLabel} mobile={mobile} />
          )}
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
    <PromptCard prompt={prompt} onPrompt={setPrompt} busy={busy} busyCount={job.count} onCancel={job.cancel}
      count={count} onCount={setCount} priceSum={notConfigured ? null : priceSumLabel}
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

  // ── Место чата картинки: пока живёт «Обсудить» из v1 ──
  const chatArea = (
    <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, textAlign: 'center' }}>
        Здесь будет чат картинки. Пока можно обсудить её с Claude: ответ придёт сюда, а весь разговор откроется в чатах проекта.
      </div>
      <div style={{ display: 'flex', justifyContent: 'center' }}>
        <Button variant="ghost" size="sm" leftIcon={ic(MessageSquare, ICON_SIZE.xs)}
          disabled={busy || !hasImage || !size} title={hasImage ? undefined : 'Сначала загрузите картинку'}
          onClick={() => { void startDiscuss(); }}>
          Обсудить с Claude
        </Button>
      </div>
      {discuss && (
        <DiscussPanel projectId={projectId} projectName={projectName} state={discuss}
          onUsePrompt={text => { setPrompt(text); setSheet(null); }}
          onOpenChat={sid => { openProjectChat(projectId, sid); onClose(); }}
          onClose={closeDiscuss} />
      )}
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
          <Button variant="ghostFilled" size="sm" fullWidth leftIcon={ic(MessageSquare, ICON_SIZE.xs)} onClick={() => setSheet('chat')}>Чат</Button>
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
          cardStyle={{ maxHeight: '86vh', background: C.bgPanel }}>
          {sheet === 'tools' ? toolPanel : chatArea}
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
      {saveOpen && job.jobId && (
        <SaveDialog mode={sourcePath ? 'edit' : 'create'} sourcePath={sourcePath}
          suggestedName={sourcePath ? nextVersionName(splitPath(sourcePath).name) : initial.name}
          folder={folder} onSave={save} onClose={() => setSaveOpen(false)} />
      )}
    </Island>
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
