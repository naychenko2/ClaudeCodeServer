// Строка ожидания GPU (шаг 28) и кнопка «Сделать в облаке» (шаг 35) в карточке
// идущей картинки. Показывается только за флагом local-model-stats и только когда
// задача несёт живой снимок очереди GPU (gpu). Без флага или без gpu карточка —
// как раньше. Кнопка — только когда стенд лежит (downSince) и есть котировка
// облачного исполнителя («Автоматически», без локальных); запуск — только по клику
// человека, новым запуском с явным поставщиком, параллельно с локальной задачей.

import { useMemo, useRef, useState } from 'react';
import { Cloud } from 'lucide-react';
import { Button, ConfirmDialog, C, FS, SP, ICON_SIZE, ICON_STROKE, showToast } from 'aihome_shell/kit';
import { imageEditorApi, AUTO_MODEL, type ImageEditCatalog, type ImageEditOp, type ImageEditProvider, type ImageEditQuote, type ImageEditQuoteRequest } from '../api';
import { isFreeUnit, isRemovalPrompt, priceSum } from '../format';
import { useQuote } from '../useQuote';
import { useCatalog } from './catalog';
import { LocalGpuQueueLine } from '../../../components/LocalGpuQueueLine';
import { useBoxWidth } from '../../videoEditor/useBoxWidth';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';
import type { ImageThread, ImageThreadLaunch } from './threadsApi';
import type { JobStatus } from './useJobStatus';
import { launchThread, threadHasMask } from './useThreadLaunch';
import { makeLaunchGuard, isStandDown, CLOUD_DIALOG_TITLE, cloudDialogSubtitle, cloudButtonLabel } from '../../../lib/cloudRescue';

const ic = (I: typeof Cloud, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// ── Чистая логика видимости (тестится отдельно) ──

// isStandDown — общий модуль cloudRescue (экспортируется для тестов)
export { isStandDown } from '../../../lib/cloudRescue';

// Облачный исполнитель «Автоматически»: умолчание админа, но только если оно НЕ локальные
// модели, доступно и умеет операцию. Нет каталога, умолчание локальное, недоступен
// или никто не умеет операцию — облачного исполнителя нет.
export function cloudCandidate(catalog: ImageEditCatalog | null, op?: ImageEditOp | null): ImageEditProvider | null {
  const key = catalog?.default.provider;
  if (!key || key === 'local') return null;
  const p = catalog?.providers.find(x => x.key === key);
  if (!p) return null;
  // M3: проверяем доступность (как звук и видео)
  if (p.available === false) return null;
  // M3: проверяем умение операции (как звук и видео).
  // caps=undefined (старые модели) — поддерживают все операции.
  if (op && p.models.length > 0) {
    const supports = p.models.some(m => !m.caps || m.caps.ops.includes(op));
    if (!supports) return null;
  }
  return p;
}

// Кнопка «Сделать в облаке» видна: флаг включён, стенд лежит, есть облачный исполнитель
// («Автоматически», не локальные, доступен, умеет операцию). Котировка на этот момент
// может ещё грузиться
export function showCloudButton(
  feature: boolean, gpu: LocalGpuQueueInfo | null, catalog: ImageEditCatalog | null, op?: ImageEditOp | null,
): boolean {
  return feature && isStandDown(gpu) && cloudCandidate(catalog, op) != null;
}

// Кнопка кликабельна: котировка облачного исполнителя пришла с ценой. Пока грузится —
// кнопка видна, но disabled, на месте цены «≈ …» (макет, состояние 5а)
export const isCloudButtonEnabled = (quote: ImageEditQuote | null): boolean =>
  quote != null && quote.estimate.amount != null;

// Строка ожидания GPU: флаг включён и задача несёт живой снимок очереди
export const showQueueLine = (feature: boolean, gpu: LocalGpuQueueInfo | null): boolean => feature && gpu != null;

// Котировка облачного исполнителя: «Авто»-модель, режим auto, операция, маска, число
// вариантов, референсы, персонаж и стирание — ВСЁ из исходной задачи (не текущего
// состояния нити). hasImage в тело котировки не кладётся (см. buildQuoteBody).
// M3: cloudCandidate проверяет available и умение операции
export function cloudQuoteRequest(
  catalog: ImageEditCatalog | null, op: ImageEditOp, count: number, hasMask: boolean,
  references: number, hasCharacter: boolean, removal: boolean,
): ImageEditQuoteRequest | null {
  const p = cloudCandidate(catalog, op);
  if (!p) return null;
  return {
    provider: p.key, model: AUTO_MODEL, mode: 'auto', op,
    count: Math.max(1, count), hasMask, references, hasCharacter, removal,
    width: null, height: null,
  };
}

// makeLaunchGuard — общий модуль cloudRescue (экспортируется для тестов)
export { makeLaunchGuard } from '../../../lib/cloudRescue';

// Ищет промпт и операцию исходной задачи (pendingJobId) в нити.
// Возвращает null, если промпт пуст/отсутствует — тогда кнопку не показываем
// (честная деградация: не выдумываем параметры запуска).
export function originalTaskParams(
  thread: ImageThread, pendingJobId: string | null,
): { prompt: string; op: ImageEditOp } | null {
  if (!pendingJobId) return null;
  const launch: ImageThreadLaunch | undefined = thread.launches?.find(l => l.jobId === pendingJobId);
  if (!launch) return null;
  const prompt = launch.prompt?.trim();
  if (!prompt) return null;
  // op из запуска; если null (старые нити) — 'edit' (безопасное умолчание)
  const op: ImageEditOp = (launch.op as ImageEditOp | null | undefined) ?? 'edit';
  return { prompt, op };
}

// ── Финальное ревью: операция и входы из ИСХОДНОЙ задачи, не из текущего состояния нити ──

// Операции, которые облачный исполнитель может повторить. outpaint/removeBackground/upscale/
// enhanceFaces — специфичны локальным моделям; облако их не умеет, поэтому кнопку прячем.
export const CLOUD_REPRODUCIBLE_OPS: ReadonlySet<ImageEditOp> = new Set(['generate', 'edit', 'inpaint']);

// Полные параметры исходной задачи для облачного запуска: операция, промпт, маска,
// референсы, персонаж, стирание. null — параметры восстановить нельзя (кнопку не показываем).
//
// hasMask: из меток нити (threadHasMask) — тот же хелпер, что в launchThread;
// в launch record маски не хранится.
// hasCharacter: из launch record (launch.character — реальное поле, threadsApi.ts:59);
// null — персонажа не было.
// references: в launch record не хранится; всегда 0 (число образцов не влияет на цену
// облачного «Автоматически», только на состав входа).
// removal: op === 'inpaint' && isRemovalPrompt(prompt) — та же формула, что в launchThread
// (useThreadLaunch.ts:183): стирание значимо только для инпейнта.
export function cloudOriginalParams(
  thread: ImageThread, pendingJobId: string | null,
): { op: ImageEditOp; prompt: string; hasMask: boolean; references: number; hasCharacter: boolean; removal: boolean } | null {
  const base = originalTaskParams(thread, pendingJobId);
  if (!base) return null;
  const { prompt, op } = base;

  // Операция не в репродуцируемом множестве → облако не повторит
  if (!CLOUD_REPRODUCIBLE_OPS.has(op)) return null;

  const launch: ImageThreadLaunch | undefined = thread.launches?.find(l => l.jobId === pendingJobId);

  // hasMask: из меток нити (тот же хелпер, что в launchThread); в launch record маски нет
  const hasMask = threadHasMask(thread);

  // hasCharacter: из launch record (character — реальное поле); null — персонажа не было
  const hasCharacter = launch?.character != null;

  // references: в launch record не хранится → 0 (на цену облачного «Автоматически» не влияет)
  const references = 0;

  // removal: только для inpaint (та же формула, что в launchThread, useThreadLaunch.ts:183)
  const removal = op === 'inpaint' && isRemovalPrompt(prompt);

  // inpaint без маски → облако не сможет повторить (маска обязательна для inpaint)
  if (op === 'inpaint' && !hasMask) return null;

  return { op, prompt, hasMask, references, hasCharacter, removal };
}

// ── Хук: котировка облачного исполнителя, пока стенд лежит ──

export interface CloudQuote {
  provider: ImageEditProvider | null;
  quote: ImageEditQuote | null;
  // «≈ $0.04» из котировки; null — цена ещё не известна
  priceLabel: string | null;
}

export function useCloudQuote(
  projectId: string, catalog: ImageEditCatalog | null, gpu: LocalGpuQueueInfo | null,
  op: ImageEditOp, count: number, hasMask: boolean,
  references: number, hasCharacter: boolean, removal: boolean,
): CloudQuote {
  const api = useMemo(() => imageEditorApi(), []);
  const down = isStandDown(gpu);
  const provider = cloudCandidate(catalog, op);
  // Котируем только когда стенд лежит и есть облачный исполнитель: иначе запрос лишний
  const req = down && provider
    ? cloudQuoteRequest(catalog, op, count, hasMask, references, hasCharacter, removal)
    : null;
  const { quote } = useQuote(api, projectId, req);
  const e = quote?.estimate;
  const priceLabel = e && e.amount != null
    ? (isFreeUnit(e.unit) ? 'бесплатно' : priceSum(e.amount, e.unit, e.approx))
    : null;
  return { provider, quote, priceLabel };
}

// ── Блок: строка ожидания + кнопка «Сделать в облаке» ──

// Короткая форма строки (compact) — когда контейнер уже < 400 px: на 320/360 px
// карточка не должна расти в высоту сверх макета
const COMPACT_MAX = 400;

export interface GpuQueueBlockProps {
  projectId: string;
  sessionId: string;
  thread: ImageThread;
  status: JobStatus | null;
  feature: boolean;
  // Число вариантов текущей задачи — для котировки облачного исполнителя
  count: number;
}

export function GpuQueueBlock(props: GpuQueueBlockProps) {
  const { projectId, sessionId, thread, status, feature, count } = props;
  // Финальное ревью: котировка и запуск берут операцию и входы из ИСХОДНОЙ задачи
  // (cloudOriginalParams), не из текущего состояния нити.
  const ref = useRef<HTMLDivElement>(null);
  const width = useBoxWidth(ref);
  // Пока ширина не измерена (0) — полная форма: на широком экране короткая форма не мигает
  const compact = width > 0 && width < COMPACT_MAX;
  const gpu = status?.gpu ?? null;

  const catalog = useCatalog(projectId);

  // Финальное ревью: полные параметры исходной задачи (op, prompt, hasMask, references,
  // hasCharacter, removal). null — восстановить нельзя → кнопку не показываем.
  const cloudParams = cloudOriginalParams(thread, thread.pendingJobId);
  // Котировка: op/hasMask/references/hasCharacter/removal — из исходной задачи
  const { provider, quote, priceLabel } = useCloudQuote(
    projectId, catalog, gpu,
    cloudParams?.op ?? 'edit', count, cloudParams?.hasMask ?? false,
    cloudParams?.references ?? 0, cloudParams?.hasCharacter ?? false, cloudParams?.removal ?? false,
  );

  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  // Защита от двойного клика: guard выставляется синхронно, до первого await
  const guard = useRef(makeLaunchGuard());

  // showQueueLine = feature && gpu != null; повторная проверка gpu сужает тип для TS
  if (!showQueueLine(feature, gpu) || gpu == null) return null;

  // Кнопка видна: стенд лежит, есть облачный исполнитель, и исходные параметры восстановимы
  const showBtn = showCloudButton(feature, gpu, catalog, cloudParams?.op ?? 'edit') && cloudParams != null;
  const enabled = isCloudButtonEnabled(quote);

  const launch = async () => {
    if (!provider || !cloudParams) return;
    if (!guard.current.begin()) return;
    setBusy(true);
    try {
      // Новый запуск с явным поставщиком: локальную задачу не отменяем, её поведение не меняем.
      // Промпт — из исходной задачи; quoteId — из котировки хука (без повторного расчёта).
      const ok = await launchThread(
        projectId, sessionId, thread,
        { kind: 'prompt', prompt: cloudParams.prompt },
        { provider: provider.key, quoteId: quote?.quoteId },
      );
      if (ok) showToast('Запущено в облаке', '', 'success');
    } catch (e) {
      showToast((e as Error).message || 'Не удалось запустить в облаке', '', 'error');
    } finally {
      // M3: guard освобождается даже при исключении — повторная попытка возможна
      guard.current.end();
      setBusy(false);
      setConfirm(false);
    }
  };

  return (
    <div ref={ref} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <LocalGpuQueueLine info={gpu} compact={compact} />
      {showBtn && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
            Задача ждёт в очереди. Можно сделать в облаке — локальная задача продолжит ждать.
          </div>
          <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>
            <Button size="sm" variant="secondary" leftIcon={ic(Cloud)} disabled={busy || !enabled}
              onClick={() => setConfirm(true)}>
              {cloudButtonLabel(priceLabel)}
            </Button>
          </div>
        </div>
      )}
      {confirm && (
        <ConfirmDialog
          title={CLOUD_DIALOG_TITLE}
          subtitle={cloudDialogSubtitle(priceLabel)}
          confirmLabel="Запустить"
          confirmVariant="primary"
          onConfirm={() => { void launch(); }}
          onCancel={() => { if (!busy) setConfirm(false); }}
        />
      )}
    </div>
  );
}
