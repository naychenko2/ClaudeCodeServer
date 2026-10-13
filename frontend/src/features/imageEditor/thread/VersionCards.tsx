// Версии картинки в ленте (изменение 27.09 к ADR-019, прототип полос, вариант C): каждый
// запуск ИИ — карточка хода, пока рисуем, и по готовности полноценная карточка на каждый
// вариант, во всю ширину ленты. «В работе» —
// рамка на версии, от которой пойдёт следующая правка; старые версии выше живые:
// «Открыть», «Продолжить от неё», «Скачать», «Сохранить в проект». В history.json лежат
// только якоря module_record — image_thread { threadId, versionId } и
// image_launch_versions { threadId, jobId, … }; всё остальное — живьём из стора нитей.

import { useState, type ReactNode } from 'react';
import {
  AlertTriangle, ChevronDown, ChevronUp, Download, Expand, MoreHorizontal, RotateCcw, Save, ScanFace, Sparkles, Target, X,
} from 'lucide-react';
import {
  Badge, Button, Dot, IconButton, Menu, MenuItem, Notice, ProgressBar, C, FS, R, SP, ICON_SIZE, ICON_STROKE, isCardPick, useChatContext, useIsMobile,
  FLAGS, useFeature,
} from 'aihome_shell/kit';
import { stripVisible } from './characterPhotos';
import {
  faceResultRows, facePairsCaption, launchFaces, partialNotice, passStage, versionSimilarityShown,
  type FaceResultRow, type PassSegment,
} from './faceSwap';
import type { ImageThreadFaceTarget } from './threadsApi';
import { useCharacters } from '../characters/useCharacters';
import { useCatalog } from './catalog';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { isFreeUnit, money, variantsWord } from '../format';
import { enterScope, isPersonalScope } from '../scope';
import { saveToProject, versionSaved } from './actions';
import { download, PERSONAL_DOWNLOAD_HINT } from './download';
import {
  downloadName, findVersion, fromVersion, launchEndNote, launchOf, launchVersions, ORIGIN,
  threadName, versionDone, versionHasImage, versionMeta, versionName, versionPrimary, versionStep,
} from './model';
import { recordOf } from './records';
import { CharacterPhotosStrip } from './CharacterPhotosStrip';
import { openEditor, useThreads } from './threadStore';
import type { ImageThread, ImageThreadVersion } from './threadsApi';
import { launchThread, versionSrc } from './useThreadLaunch';
import { CardContextActions, primaryVersion, useCardFill, useFocusedThreadId } from '../context/CardFill';
import { queueText, useJobStatus, useProgress } from './useJobStatus';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Карточка — на всю ленту: крупное превью 5:3 решено осознанно, у каждого варианта своё.
// Пропорция превью: общая у карточки и её скелетона, иначе лента прыгает при загрузке.
const PREVIEW_RATIO = '5 / 3';
// Потолок высоты превью: на широкой, но невысокой ленте карточка не должна занимать весь
// экран — рамка тогда становится шире 5:3, картинка вписывается через contain.
const PREVIEW_MAX_HEIGHT = '60vh';

// Сходство лица с эталоном: запятая и два знака, как в строке для агента
const simText = (n: number) => n.toFixed(2).replace('.', ',');

// Подача запуска с пометкой «самый похожий»: лучший крупно, остальные по номеру за кнопкой.
// Только подача: версии, их номера и порядок в нити не меняются. null — свёртывать нечего
// (одна версия, пометки нет, например нет персонажа или лицо не найдено нигде)
export function foldLaunch(versions: ImageThreadVersion[], workingId: string | null) {
  const best = versions.find(v => v.mostSimilar);
  if (!best || versions.length < 2) return null;
  const rest = versions.filter(v => v !== best).sort((a, b) => a.number - b.number);
  return { best, rest, workingInRest: rest.some(v => v.id === workingId) };
}

function Note({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

function Shell({ current, children, testId, onPick }: {
  current: boolean; children: ReactNode; testId?: string; onPick?: () => void;
}) {
  return (
    <div data-image-version={testId} data-current={current ? 'true' : 'false'}
      // Клик по карточке, а не по её кнопке или картинке, — выбор человеком: панель следует за ним
      onClick={onPick ? e => { if (isCardPick(e.target, e.currentTarget)) onPick(); } : undefined}
      style={{
      width: '100%', boxSizing: 'border-box', padding: SP.sm,
      display: 'flex', flexDirection: 'column', gap: SP.xs,
      background: C.bgCard, border: `1px solid ${current ? C.accent : C.border}`, borderRadius: R.xl,
      boxShadow: current ? `0 0 0 3px ${C.accentLight}` : 'none',
    }}>
      {children}
    </div>
  );
}

function Picture({ src, onOpen }: { src: string | null; onOpen?: () => void }) {
  const [failed, setFailed] = useState<string | null>(null);
  const frame = {
    aspectRatio: PREVIEW_RATIO, maxHeight: PREVIEW_MAX_HEIGHT, boxSizing: 'border-box', borderRadius: R.md, border: `1px solid ${C.borderLight}`, background: C.bgInset, overflow: 'hidden',
  } as const;
  if (!src || failed === src) {
    return (
      <div style={{ ...frame, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: SP.xs, color: C.textMuted, fontSize: FS.sm, padding: SP.sm, textAlign: 'center' }}>
        {ic(AlertTriangle)} Картинки уже нет в рабочей папке
      </div>
    );
  }
  return (
    <div role={onOpen ? 'button' : undefined} tabIndex={onOpen ? 0 : undefined} title={onOpen ? 'Открыть в редакторе' : undefined}
      onClick={onOpen} onKeyDown={e => { if (onOpen && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onOpen(); } }}
      style={{ ...frame, cursor: onOpen ? 'zoom-in' : 'default', display: 'flex', justifyContent: 'center' }}>
      <img src={src} alt="" onError={() => setFailed(src)}
        style={{ display: 'block', width: '100%', height: '100%', objectFit: 'contain' }} />
    </div>
  );
}

// Строка результата по лицу (v2-notes, «Карточки в ленте»): кружок 18 с ✓/✕, пара и итог
function FaceResult({ row }: { row: FaceResultRow }) {
  const ok = row.status === 'done';
  return (
    <div data-face-result={row.status} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', minWidth: 0 }}>
      <span aria-hidden style={{
        width: 18, height: 18, flex: '0 0 18px', borderRadius: R.full, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        fontSize: FS.xs, fontWeight: 700, background: ok ? C.successBg : C.dangerBg, color: ok ? C.successText : C.dangerText,
      }}>{row.mark}</span>
      <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{row.pair}</span>
      {row.result && (
        <span style={{ fontSize: FS.xs, color: row.status === 'failed' ? C.dangerText : C.textMuted }}>{row.result}</span>
      )}
    </div>
  );
}

// Полоска из N отрезков стадии «Проход k из N»: готовый — success, текущий — accent, впереди — дорожка
const SEGMENT_COLOR: Record<PassSegment, string> = { done: C.success, current: C.accent, ahead: C.progressTrack };

function PassBar({ segments, run }: { segments: PassSegment[]; run: number }) {
  return (
    <div role="progressbar" aria-valuemin={0} aria-valuemax={segments.length} aria-valuenow={run}
      style={{ display: 'flex', gap: SP.xs }}>
      {segments.map((s, i) => <span key={i} style={{ flex: 1, height: 4, borderRadius: R.max, background: SEGMENT_COLOR[s] }} />)}
    </div>
  );
}

// Карточка версии: шапка, картинка, подпись «вариант 1 из 2 · от исходника · модель», действия.
export function VersionCard({ projectId, sessionId, thread, version, focused, model, cropNote, best }: {
  projectId: string; sessionId: string; thread: ImageThread; version: ImageThreadVersion; focused: boolean;
  model?: string | null;
  // Обрезка без ИИ (image_launch_versions с model «crop»): вместо «вариант · от версии · модель» — размер и пометка
  cropNote?: string | null;
  // Самый похожий вариант свёрнутого запуска: бейдж в шапке
  best?: boolean;
}) {
  const mobile = useIsMobile();
  const [busy, setBusy] = useState(false);
  const fill = useCardFill(sessionId, thread, version.id);
  const current = fill.working;
  const src = versionSrc(projectId, thread, version);
  const saved = versionSaved(thread, version);
  const name = `${threadName(thread)} · ${versionName(version)}`;
  const done = versionDone(thread, version);
  // Блок «Фото персонажа» (ADR-027, шаг 18): не у исходника, не у обрезки без ИИ, только с персонажем
  const smart = useFeature(FLAGS.characterSmartPhotos);
  const launch = version.jobId ? launchOf(thread, version.jobId) : null;
  // Лица запуска (ADR-028): при N > 1 общего сходства версии нет — оно у каждого лица в строках
  const faces = launchFaces(launch);
  const sim = !versionSimilarityShown(faces) ? ''
    : typeof version.similarity === 'number' ? `сходство ${simText(version.similarity)}`
    : version.jobId && launchVersions(thread, version.jobId).some(x => typeof x.similarity === 'number') ? 'лицо не найдено' : '';
  const character = stripVisible({ smart, versionId: version.id, cropNote: cropNote ?? null, character: launch?.character ?? null })
    ? (launch?.character ?? null) : null;
  const personal = isPersonalScope(projectId);
  const primary = versionPrimary(thread, version, focused, saved, personal);
  const save = () => { if (src) void download(src, mime => downloadName(thread, version, mime)); };
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); await fn(); setBusy(false); };
  const open = () => openEditor(sessionId, thread.id, version.id);
  // «Подставить персонажа» (этап 2 · шаг 10b): вход — пункт меню «⋯», виден только в проекте,
  // за флагом, при персонажах в проекте и доступных локальных моделях. Нет условия — нет и «⋯»
  const faceFlag = useFeature(FLAGS.characterFaceSwap);
  const { list: faceChars } = useCharacters(projectId);
  // «Подставить персонажа» (этап 2 · шаг 10c, этап 3 · шаг 17): подпись «Мария → лицо 1, Иван → лицо 2»;
  // имя — по slug персонажа из списка проекта, персонажа больше нет — сам slug. Есть итог по
  // лицам (N > 1) — пары уходят в строки результата, а не в мету
  const nameOf = (slug: string) => faceChars?.find(c => c.slug === slug)?.name;
  const faceRows = faceResultRows(faces, nameOf);
  const partial = partialNotice(faces);
  const faceCap = faceRows.length ? null : facePairsCaption(faces, nameOf);
  const meta = cropNote ?? [faceCap, versionMeta(thread, version, null), sim, model ?? ''].filter(Boolean).join(' · ');
  const metaTitle = sim === 'лицо не найдено' ? `${meta} — сходство не посчитано` : meta;
  const faceCatalog = useCatalog(projectId);
  const faceLocal = faceCatalog ? (faceCatalog.providers.find(p => p.key === 'local')?.available ?? true) : false;
  const faceEntryVisible = !personal && faceFlag && (faceChars?.length ?? 0) > 0 && faceLocal;
  const [faceMenu, setFaceMenu] = useState(false);

  return (
    <Shell current={current} testId={String(version.number)}
      onPick={() => { void fill.pick(); }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0, flexWrap: mobile ? 'wrap' : 'nowrap' }}>
        <span title={name} style={{
          fontSize: FS.sm, fontWeight: 600, color: C.textHeading, minWidth: 0, flex: mobile && best && typeof version.similarity === 'number' ? '1 1 100%' : 1,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{name}</span>
        {best && typeof version.similarity === 'number' && (
          <Badge size="xs" tone="info" icon={ic(ScanFace)}
            title={`Ближе всех к персонажу: сходство по лицу ${simText(version.similarity)}. Чем ближе к 1, тем больше похоже`}>
            Самый похожий
          </Badge>
        )}
        {current && <Badge size="xs" tone="accent" icon={ic(Target)}>{fill.byAgent ? 'В работе ✦' : 'В работе'}</Badge>}
        {!personal && <Badge size="xs" tone={saved ? 'success' : 'warning'}>{saved ? 'в проекте' : 'черновик'}</Badge>}
      </div>
      {done && <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, overflowWrap: 'anywhere' }}>{done}</div>}
      <Picture src={src} onOpen={open} />
      {faceRows.length > 0 && (
        <div data-face-results="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {faceRows.map(r => <FaceResult key={r.n} row={r} />)}
        </div>
      )}
      {partial && <Notice tone="warning" icon={AlertTriangle} data-face-partial="">{partial}</Notice>}
      {meta && (
        <div title={metaTitle} style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {meta}
        </div>
      )}
      {character && (
        <CharacterPhotosStrip projectId={projectId} sessionId={sessionId} thread={thread} version={version} character={character} />
      )}
      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>
        <Button size="xs" variant="secondary" leftIcon={ic(Expand)} onClick={open}>Открыть</Button>
        {primary === 'save' && (
          <Button size="xs" variant="secondary" leftIcon={ic(Save)} disabled={busy}
            onClick={() => { void run(() => saveToProject(projectId, sessionId, thread, versionStep(thread, version))); }}>
            Сохранить в проект
          </Button>
        )}
        {primary === 'download' && src && (
          <Button size="xs" variant="secondary" leftIcon={ic(Download)} title={personal ? PERSONAL_DOWNLOAD_HINT : undefined} onClick={save}>
            Скачать
          </Button>
        )}
        <CardContextActions sessionId={sessionId} projectId={projectId} thread={thread} versionId={version.id} fill={fill} />
        <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: SP.xs }}>
          {src && primary !== 'download' && (
            <IconButton size="xs" title="Скачать" ariaLabel="Скачать" onClick={save}>
              {ic(Download)}
            </IconButton>
          )}
          {faceEntryVisible && (
            <span style={{ position: 'relative', display: 'inline-flex' }}>
              <IconButton size="xs" title="Ещё" ariaLabel="Ещё" active={faceMenu} onClick={() => setFaceMenu(v => !v)}>
                {ic(MoreHorizontal)}
              </IconButton>
              {faceMenu && (
                <Menu onClose={() => setFaceMenu(false)} align="right" bottom={30} minWidth={200}>
                  <MenuItem icon={ic(ScanFace)} label="Подставить персонажа"
                    onClick={() => { setFaceMenu(false); openEditor(sessionId, thread.id, version.id, { tool: 'faceSwap' }); }} />
                </Menu>
              )}
            </span>
          )}
        </span>
      </div>
    </Shell>
  );
}

// Якорь нити { threadId, versionId: "origin" }: исходник картинки или черновик
export function OriginAnchor({ projectId, sessionId, thread, focused }: {
  projectId: string; sessionId: string; thread: ImageThread; focused: boolean;
}) {
  const origin = findVersion(thread, ORIGIN);
  if (!origin || !versionHasImage(thread, origin)) {
    // Пустой черновик карточкой в ленте не рисуется: он живёт чипом в полосе «Картинки»,
    // а нить появляется в ленте с первой версией (якорь запуска ниже)
    return null;
  }
  return <VersionCard projectId={projectId} sessionId={sessionId} thread={thread} version={origin} focused={focused} />;
}

// ── Запуск: строка и карточки его вариантов ──

interface LaunchData {
  threadId?: unknown; jobId?: unknown; prompt?: unknown; model?: unknown; count?: unknown;
  estimate?: unknown; initiator?: unknown; baseVersionId?: unknown; crop?: unknown;
}

const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v : null);

function estimateText(e: unknown): string | null {
  const x = e as { amount?: unknown; unit?: unknown; approx?: unknown } | null;
  if (!x || typeof x.amount !== 'number' || typeof x.unit !== 'string') return null;
  if (isFreeUnit(x.unit)) return 'бесплатно';
  return `${x.approx ? '≈ ' : ''}${money(x.amount, x.unit)}`;
}

function RunningLine({ projectId, jobId, model, faces }: {
  projectId: string; jobId: string; model: string | null; faces: ImageThreadFaceTarget[];
}) {
  const { status, cancel } = useJobStatus(projectId, jobId);
  const mobile = useIsMobile();
  const running = (status?.phase ?? 'run') === 'run';
  const progress = useProgress(status, running);
  // Несколько лиц (ADR-028): «Проход k из N · лицо n» по run/runs из событий задачи; их нет — «Рисуем…»
  const stage = passStage(faces, status?.run, status?.runs);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary, flexWrap: 'wrap' }}>
        <Dot color={C.accent} />{stage ? stage.text : `Рисуем…${model ? ` · ${model}` : ''}`}{queueText(progress.queuePosition)}
        {/* Мобила: зона касания 44 при той же компактной кнопке xs (24) — отрицательные поля не раздувают строку */}
        <Button size="xs" variant="ghost" leftIcon={ic(X)} onClick={() => { void cancel(); }}
          style={mobile ? { height: 44, minHeight: 44, marginBlock: (24 - 44) / 2 } : undefined}>Отменить</Button>
      </div>
      {stage
        ? <PassBar segments={stage.segments} run={status?.run ?? 0} />
        : <ProgressBar value={progress.percent} transition="width .5s linear" />}
      {stage?.hint && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{stage.hint}</div>}
    </div>
  );
}

// Запуск: пока рисуем — одна карточка с ходом (очередь, «Отменить»), по готовности — карточка
// версии на каждый вариант, на месте запуска в ленте
export function LaunchAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  // Личный чат вне проекта: ctx.projectId = null, область — personal
  const projectId = enterScope(ctx.projectId, ctx.sessionId);
  const state = useThreads(projectId, ctx.sessionId);
  const focusId = useFocusedThreadId(ctx.sessionId ?? '');
  const [busy, setBusy] = useState(false);
  // null — решение человека не принято: раскрыто само, если выбранная версия среди свёрнутых
  const [open, setOpen] = useState<boolean | null>(null);
  const mobile = useIsMobile();
  const { primary } = useChatContext(ctx.sessionId ?? '');
  const { list: faceChars } = useCharacters(projectId);
  const data = (rec?.data ?? {}) as LaunchData;
  const jobId = str(data.jobId);
  const thread = typeof data.threadId === 'string' ? state.threads.find(t => t.id === data.threadId) : undefined;
  const { sessionId } = ctx;
  if (!sessionId || !thread || !jobId) return rec?.fallback ? <Note>{rec.fallback}</Note> : null;

  const launch = launchOf(thread, jobId);
  const versions = launchVersions(thread, jobId);
  const focused = focusId === thread.id;
  const model = str(data.model);
  const count = typeof data.count === 'number' && data.count > 0 ? data.count : 1;
  const prompt = str(data.prompt) ?? launch?.prompt ?? null;
  const base = findVersion(thread, str(data.baseVersionId) ?? launch?.baseVersionId);
  const agent = (str(data.initiator) ?? launch?.initiator) === 'agent';
  const head = base && versionHasImage(thread, base) ? `Правка ${fromVersion(base)}` : 'Генерация';
  const info = [model, variantsWord(count), estimateText(data.estimate)].filter(Boolean).join(' · ');
  // «Подставить персонажа» (этап 2 · шаг 10c, этап 3 · шаг 17): подпись пар «Мария → лицо 1, Иван → лицо 2»
  // в строке запуска; имя — по slug персонажа из списка проекта, персонажа больше нет — сам slug
  const faces = launchFaces(launch);
  const faceCap = facePairsCaption(faces, slug => faceChars?.find(c => c.slug === slug)?.name);
  const status = launch?.status ?? (versions.length ? 'done' : 'running');
  const endNote = launchEndNote(status, versions.length, count, faces.length);
  // Обрезка без ИИ (ADR-025): та же карточка версии, над ней строка «Вы обрезали…» / «✦ Claude обрезал…» из записи
  const crop = model === 'crop' ? data.crop as { aspect?: string; width?: number; height?: number } | undefined : undefined;
  const cropNote = crop ? `${crop.width}×${crop.height} · ${crop.aspect} · обрезано без ИИ${agent ? ' · ✦ Claude' : ''}` : null;

  const workingId = thread.id === focusId && primary ? primaryVersion(primary, thread) : null;
  const fold = status === 'running' ? null : foldLaunch(versions, workingId);
  const expanded = !fold || (open ?? fold.workingInRest);
  const shown = fold ? (expanded ? [fold.best, ...fold.rest] : [fold.best]) : versions;

  return (
    <div data-image-launch={status} style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      {crop && rec?.fallback && <Note>{rec.fallback}</Note>}
      {shown.map(v => (
        <VersionCard key={v.id} projectId={projectId} sessionId={sessionId} thread={thread} version={v} focused={focused} model={model} cropNote={cropNote}
          best={fold?.best === v} />
      ))}
      {fold && (
        <div style={{ display: 'flex' }}>
          <Button size={mobile ? 'sm' : 'xs'} variant="secondary" fullWidth={mobile}
            leftIcon={ic(expanded ? ChevronUp : ChevronDown)} onClick={() => setOpen(!expanded)}>
            {expanded ? 'Свернуть варианты'
              : `Ещё ${variantsWord(fold.rest.length)}${fold.workingInRest ? ' · один в работе' : ''}`}
          </Button>
        </div>
      )}

      {status === 'running' && (
        <Shell current={false} testId="running">
          <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
            <span style={{ display: 'inline-flex', alignSelf: 'center', color: C.textMuted }}>{ic(Sparkles)}</span>
            <b style={{ color: C.textHeading }}>{agent ? `Claude: ${head.charAt(0).toLowerCase()}${head.slice(1)}` : head}</b>
            <span>{threadName(thread)}{faceCap ? ` · ${faceCap}` : ''} · {info}</span>
            {prompt && <span style={{ color: C.textMuted, fontStyle: 'italic', overflowWrap: 'anywhere' }}>«{prompt}»</span>}
          </div>
          <RunningLine projectId={projectId} jobId={jobId} model={model} faces={faces} />
          <div className="cc-skel" style={{ aspectRatio: PREVIEW_RATIO, maxHeight: PREVIEW_MAX_HEIGHT, borderRadius: R.md }} />
        </Shell>
      )}

      {endNote && (
        <div data-image-launch-empty={versions.length ? undefined : ''} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <Note>{endNote}</Note>
          {status === 'interrupted' && !versions.length && prompt && focused && (
            <Button size="xs" variant="secondary" leftIcon={ic(RotateCcw)} disabled={busy} title={`«${prompt}»`}
              onClick={() => {
                setBusy(true);
                void launchThread(projectId, sessionId, thread, { kind: 'prompt', prompt }).finally(() => setBusy(false));
              }}>
              Запустить заново
            </Button>
          )}
        </div>
      )}
    </div>
  );
}
