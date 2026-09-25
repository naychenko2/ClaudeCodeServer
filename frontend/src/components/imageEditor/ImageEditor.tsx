// Экран редактора картинок (макет docs/mockups/image-editor-v1.html, экраны 2–8):
// холст с пометками, запрос, «Поставщик ▾ → Модель ▾», число вариантов и цена до
// запуска, генерация с прогрессом и отменой, варианты, сохранение новой версией.
// На телефоне колонки встают друг под другом: инструменты строкой, холст, панель
// запроса с прилипшим низом.

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  ArrowLeft, Brush, Eraser, Hand, Image as ImageIcon, MessageSquare, MoveUpRight, Sparkles, SquareDashed, Trash2, Type, Upload, X,
} from 'lucide-react';
import { Button, EmptyState, Field, IconButton, Island, Modal, ModalActions, SegmentedControl, TextArea, TextField } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FS, ISLAND, R, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { api as appApi } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditQuoteRequest } from '../../api/imageEditor';
import { EditorCanvas } from './EditorCanvas';
import { exportAnnotated, exportMask, hasMaskMark, marksToJson, type Mark, type Tool } from './marks';
import { effectiveProvider, modelBlockReason, money, nextVersionName, pickOp, plural, priceText, splitPath, variantsWord, type ProviderChoice } from './format';
import { currentModel, PriceLine, ProviderModelPicker, SectionLabel } from './ProviderModelPicker';
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

const TOOLS: { id: Tool; icon: typeof Brush; title: string }[] = [
  { id: 'mask', icon: Brush, title: 'Кисть: закрасить место' },
  { id: 'arrow', icon: MoveUpRight, title: 'Стрелка' },
  { id: 'rect', icon: SquareDashed, title: 'Рамка' },
  { id: 'text', icon: Type, title: 'Подпись' },
];

export function ImageEditor({ projectId, projectName, target, onClose, onShowInFiles }: {
  projectId: string;
  projectName: string;
  target: ImageEditorTarget;
  onClose: () => void;
  // «Показать в файлах» в тосте после сохранения
  onShowInFiles?: (path: string) => void;
}) {
  const mobile = useIsMobile();
  const api = useMemo(() => imageEditorApi(), []);

  const initial = target.kind === 'edit' ? splitPath(target.path) : { folder: target.folder, name: 'новая-картинка.png' };
  const [sourcePath, setSourcePath] = useState<string | null>(target.kind === 'edit' ? target.path : null);
  const [src, setSrc] = useState<string | null>(target.kind === 'edit' ? appApi.files.fileUrl(projectId, target.path) : null);
  // Нарисованное с нуля до «Взять за основу» не с чем сравнивать
  const [hasBefore, setHasBefore] = useState(target.kind === 'edit');
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

  const chars = useCharacters(api, projectId);
  const charDialogs = useCharacterDialogs(api, projectId, chars);
  const character = chars.active;
  const [discuss, setDiscuss] = useState<DiscussState | null>(null);
  // Чат обсуждения переиспользуется внутри одного сеанса редактора
  const discussSession = useRef<string | null>(null);

  const job = useImageEditJob(api, projectId);
  const busy = job.phase === 'starting' || job.phase === 'running';
  // Номера вариантов даёт сервер: выбранный по умолчанию — первый готовый
  const sel = job.variants.includes(selected) ? selected : job.variants[0] ?? 0;

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
  const pv = catalog ? effectiveProvider(catalog, provider) : null;
  const m = currentModel(pv, model);
  const blocked = m ? modelBlockReason(m, hasImage, hasMask) : '';

  const quoteReq: ImageEditQuoteRequest | null = pv && m && !blocked ? {
    provider: pv.key, model: m.id, mode: 'auto', op: pickOp(hasImage, hasMask), count,
    hasMask, references: 0, hasCharacter: !!character, width: size?.w ?? null, height: size?.h ?? null,
  } : null;
  const { quote, error: quoteError, loading: quoteLoading } = useQuote(api, projectId, quoteReq);

  const unit = quote?.estimate.unit ?? pv?.priceUnit ?? 'usd';
  const priceLabel = quote
    ? priceText(quote.estimate.amount, unit, quote.estimate.approx, count)
    // Пока котировка едет — ориентир из каталога, чтобы цена не мигала
    : m?.priceHint ? priceText(m.priceHint.amount * count, m.priceHint.unit, true, count) : `… · ${variantsWord(count)}`;

  const onProvider = (p: ProviderChoice) => {
    // Сменился поставщик — модель сбрасывается на «Авто»: список моделей у него свой
    if (p !== provider) setModel(p === 'settings' && catalog?.default.provider ? catalog.default.model : AUTO_MODEL);
    setProvider(p);
  };

  const canGenerate = !!quote && !quoteLoading && !blocked && !busy && (hasImage || !!prompt.trim());

  const generate = useCallback(async (n: number = count) => {
    if (!pv || !m) return;
    let q = quote;
    // Другое число вариантов («Нарисовать 1 вариант») или протухшая котировка — берём свежую
    if (!q || n !== count || Date.parse(q.expiresAt) - Date.now() < 30_000) {
      q = await api.quote(projectId, {
        provider: pv.key, model: m.id, mode: 'auto', op: pickOp(hasImage, hasMask), count: n,
        hasMask, references: 0, hasCharacter: !!character, width: size?.w ?? null, height: size?.h ?? null,
      }).catch(() => null);
      if (!q) return;
    }
    let source: Blob | undefined;
    let mask: Blob | undefined;
    let annotated: Blob | undefined;
    if (src && size) {
      source = await fetch(src).then(r => r.blob()).catch(() => undefined);
      mask = (await exportMask(marks, size.w, size.h)) ?? undefined;
      if (imgRef.current) annotated = (await exportAnnotated(imgRef.current, marks, size.w, size.h).catch(() => null)) ?? undefined;
    }
    setSelected(-1);
    await job.start({
      quoteId: q.quoteId, prompt: prompt.trim(),
      marks: marks.length && size ? marksToJson(marks, size.w, size.h) : undefined,
      sourcePath: sourcePath ?? undefined, source, mask, annotated, characterSlug: character?.slug,
    }, n, q.expectedSeconds);
  }, [api, projectId, pv, m, quote, count, hasImage, hasMask, size, src, marks, prompt, sourcePath, character, job]);

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
    setSrc(api.variantUrl(projectId, job.jobId, sel));
    setSize(null);
    setMarks([]);
    setPrompt('');
    setHasBefore(true);
    job.reset();
  };

  const save = async ({ fileName, folder }: { fileName: string; folder: string }) => {
    if (!job.jobId) return;
    const res = await api.save(projectId, sourcePath
      ? { jobId: job.jobId, variant: sel, sourcePath }
      : { jobId: job.jobId, variant: sel, folder: folder || undefined, fileName });
    setSaveOpen(false);
    setSavedPath(res.path);
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
        before={hasBefore ? src : null} cost={job.cost} selected={sel}
        onSelect={setSelected} onApply={() => setSaveOpen(true)} onBase={takeAsBase}
        onMore={() => generate()} onBack={job.reset} mobile={mobile} />
    );
  } else if (job.phase === 'error' && job.failure) {
    const perOne = quote?.estimate.amount != null ? quote.estimate.amount / Math.max(1, count) : null;
    center = (
      <ErrorView failure={job.failure} providerLabel={pv?.label ?? ''} priceUnit={unit}
        needText={quote?.estimate.amount != null ? priceLabel.replace(' · ', ', ') : null}
        oneVariantPrice={perOne != null ? `≈ ${money(perOne, unit)}` : null}
        onRetry={() => generate()} onRetryOne={() => { setCount(1); generate(1); }} onEdit={job.reset} mobile={mobile} />
    );
  } else if (src) {
    center = (
      <EditorCanvas src={src} size={size} marks={marks} onMarksChange={setMarks} tool={tool} mobile={mobile}
        onTextAt={(x, y) => setLabelAt({ x, y, text: '' })}
        onImageLoad={img => { imgRef.current = img; setSize({ w: img.naturalWidth, h: img.naturalHeight }); }} />
    );
  } else {
    center = <DropZone folder={folder} mobile={mobile} onFile={f => { setSrc(URL.createObjectURL(f)); setSize(null); setMarks([]); }} />;
  }

  const tools = hasImage && job.phase === 'idle' && (
    <div style={{
      display: 'flex', flexDirection: mobile ? 'row' : 'column', alignItems: 'center', gap: SP.xxs, padding: SP.sm,
      flex: mobile ? '0 0 auto' : '0 0 48px', overflowX: mobile ? 'auto' : undefined,
      [mobile ? 'borderBottom' : 'borderRight']: `1px solid ${C.borderLight}`,
    }}>
      <IconButton active={tool === 'hand'} title="Перемещать" ariaLabel="Перемещать" onClick={() => setTool('hand')}>{ic(Hand)}</IconButton>
      <ToolSep mobile={mobile} />
      {TOOLS.map(t => (
        <IconButton key={t.id} active={tool === t.id} title={t.title} ariaLabel={t.title} onClick={() => setTool(t.id)}>{ic(t.icon)}</IconButton>
      ))}
      <ToolSep mobile={mobile} />
      <IconButton active={tool === 'eraser'} title="Ластик: убрать пометку" ariaLabel="Ластик: убрать пометку" onClick={() => setTool('eraser')}>{ic(Eraser)}</IconButton>
      <IconButton title="Очистить пометки" ariaLabel="Очистить пометки" disabled={!marks.length} onClick={() => setMarks([])}>{ic(Trash2)}</IconButton>
    </div>
  );

  // ── Панель запроса ──
  const notConfigured = catalog && !catalog.providers.length;
  const ask = (
    <div style={{
      display: 'flex', flexDirection: 'column', minHeight: 0,
      ...(mobile
        ? { flex: '0 0 auto', borderTop: `1px solid ${C.borderLight}` }
        : { width: 330, flex: '0 0 330px', borderLeft: `1px solid ${C.borderLight}` }),
    }}>
      <div style={{ flex: 1, overflow: mobile ? undefined : 'auto', padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <div>
          <SectionLabel>{hasImage ? 'Запрос' : 'Что нарисовать'}</SectionLabel>
          {character && (
            <CharacterChip api={api} projectId={projectId} character={character} disabled={busy}
              onOpen={() => charDialogs.openCard(character.slug)} onOff={() => chars.setActive(null)} />
          )}
          <TextArea value={prompt} onChange={setPrompt} disabled={busy} minHeight={96} autoGrow maxHeight={220}
            placeholder={character
              ? `Где и что делает ${character.name}? Например, «${character.name} сидит в кафе у окна»`
              : hasImage
                ? 'Что изменить? Отметьте место на картинке или просто опишите'
                : 'Опишите, что нарисовать: например, «светлая гостиная, синий диван, торшер в углу, утро»'} />
          <div style={{ display: 'flex', marginTop: SP.sm }}>
            <Button variant="ghost" size="sm" leftIcon={ic(MessageSquare, ICON_SIZE.xs)}
              disabled={busy || !hasImage || !size} title={hasImage ? undefined : 'Сначала загрузите картинку'}
              onClick={() => { void startDiscuss(); }}>
              Обсудить с Claude
            </Button>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginTop: SP.sm, fontSize: FS.sm, color: C.textMuted }}>
            {ic(Sparkles, ICON_SIZE.xs)}<span>Claude учитывает описание проекта</span>
          </div>
        </div>
        {discuss && (
          <DiscussPanel projectId={projectId} projectName={projectName} state={discuss}
            onUsePrompt={text => setPrompt(text)}
            onOpenChat={sid => { openProjectChat(projectId, sid); onClose(); }}
            onClose={closeDiscuss} />
        )}
        <CharacterSection api={api} projectId={projectId} chars={chars} disabled={busy}
          onNew={charDialogs.openNew} onCard={charDialogs.openCard} />
        {catalogError && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{catalogError}</div>}
        {notConfigured && (
          <EmptyState compact inline icon={ic(ImageIcon, ICON_SIZE.lg)} title="Рисование не настроено" />
        )}
        {catalog && !notConfigured && (
          <ProviderModelPicker catalog={catalog} provider={provider} model={m?.id ?? model}
            onProvider={onProvider} onModel={setModel} hasImage={hasImage} hasMask={hasMask}
            priceLabel={priceLabel} mobile={mobile} />
        )}
        {quoteError && !blocked && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{quoteError}</div>}
      </div>
      <div style={{
        borderTop: `1px solid ${C.borderLight}`, padding: `${SP.sm}px ${SP.md}px`, background: C.bgInset,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        ...(mobile ? { position: 'sticky', bottom: 0, zIndex: 2 } : null),
      }}>
        {busy ? (
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
            <span style={{ flex: 1, fontSize: FS.sm, color: C.textSecondary }}>Рисуем {variantsWord(job.count)}…</span>
            <Button size="sm" variant="secondary" leftIcon={ic(X)} onClick={job.cancel}>Отменить</Button>
          </div>
        ) : (
          <>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
              <span style={{ fontSize: FS.sm, color: C.textMuted }}>Вариантов</span>
              <div style={{ width: 132 }}>
                <SegmentedControl value={String(count)} onChange={v => setCount(Number(v))}
                  options={['1', '2', '3', '4'].map(v => ({ value: v, label: v }))} />
              </div>
              <span style={{ flex: 1 }} />
              {!notConfigured && <PriceLine text={priceLabel} />}
            </div>
            <Button variant="primary" size="lg" fullWidth leftIcon={ic(Sparkles)} disabled={!canGenerate}
              title={blocked || undefined} onClick={() => generate()}>
              Сгенерировать
            </Button>
          </>
        )}
      </div>
    </div>
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
      <div style={{
        flex: 1, minHeight: 0, display: 'flex',
        ...(mobile ? { flexDirection: 'column', overflow: 'auto' } : null),
      }}>
        {tools}
        <div style={{ flex: mobile ? '0 0 auto' : 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {center}
        </div>
        {ask}
      </div>

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
      {saveOpen && job.jobId && (
        <SaveDialog mode={sourcePath ? 'edit' : 'create'} sourcePath={sourcePath}
          suggestedName={sourcePath ? nextVersionName(splitPath(sourcePath).name) : initial.name}
          folder={folder} onSave={save} onClose={() => setSaveOpen(false)} />
      )}
    </Island>
  );
}

function ToolSep({ mobile }: { mobile: boolean }) {
  return <div style={mobile ? { width: 1, height: 20, background: C.divider, margin: `0 ${SP.xs}px` } : { width: 20, height: 1, background: C.divider, margin: `${SP.xs}px 0` }} />;
}

// Пустой редактор (экран 2): перетащить картинку или загрузить с компьютера
function DropZone({ folder, onFile, mobile }: { folder: string; onFile: (f: File) => void; mobile: boolean }) {
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const take = (f: File | undefined) => { if (f && f.type.startsWith('image/')) onFile(f); };
  return (
    <div style={{ flex: mobile ? '0 0 300px' : 1, minHeight: 0, padding: SP.lg, display: 'flex', background: C.bgInset }}>
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
