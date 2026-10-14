// Строка ожидания GPU (шаг 29) и кнопка «Сделать в облаке» (шаг 36) в карточке
// идущего звука. Показывается только за флагом local-model-stats и только когда
// задача несёт живой снимок очереди GPU (gpu). Без флага или без gpu карточка —
// как раньше. Кнопка — только когда стенд лежит (downSince) и есть облачный
// исполнитель для операции («Автоматически», без локальных); запуск — только по клику
// человека, новым запуском с явным поставщиком, параллельно с локальной задачей.

import { useEffect, useRef, useState } from 'react';
import { Cloud } from 'lucide-react';
import { Button, ConfirmDialog, C, FS, SP, ICON_SIZE, ICON_STROKE, showToast } from 'aihome_shell/kit';
import { audioApi, type AudioCatalog, type AudioMode, type AudioOp, type AudioProvider, type AudioQuote, type AudioQuoteRequest, type AudioThread } from '../api';
import { opInfo, type ComposerField } from '../ops';
import { autoOrder } from '../panel/launch';
import { priceText } from './model';
import { getCatalog, useAudioStoreVersion } from './threadStore';
import { LocalGpuQueueLine } from '../../../components/LocalGpuQueueLine';
import { useBoxWidth } from '../../videoEditor/useBoxWidth';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';
import type { JobProgress } from './threadStore';
import { makeLaunchGuard, isStandDown, CLOUD_DIALOG_TITLE, cloudDialogSubtitle, cloudButtonLabel } from '../../../lib/cloudRescue';

const ic = (I: typeof Cloud, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// ── Чистая логика видимости (тестится отдельно) ──

// isStandDown — общий модуль cloudRescue (экспортируется для тестов)
export { isStandDown } from '../../../lib/cloudRescue';

// Умеет ли поставщик операцию
const supportsOp = (p: AudioProvider, op: AudioOp): boolean => p.models.some(m => m.caps.ops.includes(op));

// Облачный исполнитель для операции: первый в порядке «Авто» доступный поставщик,
// не локальные модели, умеющий операцию. Нет каталога, операции или никто не умеет — null
export function cloudCandidate(catalog: AudioCatalog | null, op: AudioOp | null): AudioProvider | null {
  if (!op) return null;
  return autoOrder(catalog).find(p => p.available && p.key !== 'local' && supportsOp(p, op)) ?? null;
}

// Кнопка «Сделать в облаке» видна: флаг включён, стенд лежит, есть облачный исполнитель
// для операции. Котировка на этот момент может ещё грузиться
export function showCloudButton(
  feature: boolean, gpu: LocalGpuQueueInfo | null, catalog: AudioCatalog | null, op: AudioOp | null,
): boolean {
  return feature && isStandDown(gpu) && cloudCandidate(catalog, op) != null;
}

// Кнопка кликабельна: котировка облачного исполнителя пришла с ценой. Пока грузится —
// кнопка видна, но disabled, на месте цены «≈ …»
export const isCloudButtonEnabled = (quote: AudioQuote | null): boolean =>
  quote != null && quote.price.amount != null;

// Строка ожидания GPU: флаг включён и задача несёт живой снимок очереди
export const showQueueLine = (feature: boolean, gpu: LocalGpuQueueInfo | null): boolean => feature && gpu != null;

// Поле, в котором вход операции уходит модели: text — озвучка, prompt — стиль/описание,
// none — не нужен (обработка). null — операция не в каталоге: поле определить нельзя.
export function opInputField(op: AudioOp | null): ComposerField | null {
  return op ? opInfo(op)?.field ?? null : null;
}

// Исходный вход задачи в поля, которых требует операция: ОДНИ И ТЕ ЖЕ поля для котировки
// и запуска (один источник правды, как в обычном запуске панели — launchFromComposer).
// null — поле операции нельзя определить (операция не в каталоге).
export function opInput(op: AudioOp | null, input: string | null): { text: string | null; prompt: string | null } | null {
  const field = opInputField(op);
  if (field == null) return null;
  if (field === 'text') return { text: input, prompt: null };
  if (field === 'prompt') return { text: null, prompt: input };
  return { text: null, prompt: null };
}

// Котировка облачного исполнителя: «Авто»-модель, операция и число вариантов текущей
// задачи; вход — исходный вход задачи (см. originalTaskParams) в поле, которого требует
// операция (см. opInput), чтобы цена котировки совпала с ценой фактического запуска;
// sessionId/threadId дописывает хук
export function cloudQuoteRequest(
  catalog: AudioCatalog | null, op: AudioOp | null, mode: AudioMode | null, count: number,
  prompt: string | null,
): AudioQuoteRequest | null {
  const p = cloudCandidate(catalog, op);
  if (!p || !mode) return null;
  const inp = opInput(op, prompt);
  if (!inp) return null;
  return { mode, operation: op ?? null, provider: p.key, model: 'auto', count: Math.max(1, count), ...inp };
}

// Исходный вход задачи: промпт запуска в нити (сервер кладёт туда input.Prompt ?? input.Text).
// null — запуск не найден или промпт пуст: кнопку не показываем (честная деградация:
// не выдумываем параметры запуска, от которых зависит цена)
export function originalTaskParams(thread: AudioThread, jobId: string | null): string | null {
  if (!jobId) return null;
  const launch = thread.launches?.find(l => l.jobId === jobId);
  const prompt = launch?.prompt?.trim();
  return prompt || null;
}

// Удовлетворён ли обязательный вход операции: операции, где композер шлёт модели text/prompt
// (озвучка, песня, эффект, …), требуют непустого входа; операции без входа (обработка) — нет.
// Пустой вход у Run-модели дал бы ненулевую цену, а запуск ушёл бы с пустым промптом и провалился.
// Поле нельзя определить (операция не в каталоге) — вход не удовлетворён: кнопку не показываем,
// не выдумывая параметры запуска, от которых зависит цена.
export function inputSatisfied(op: AudioOp | null, prompt: string | null): boolean {
  const field = opInputField(op);
  if (field == null) return false;
  if (field === 'text' || field === 'prompt') return prompt != null;
  return true;
}

// makeLaunchGuard — общий модуль cloudRescue (экспортируется для тестов)
export { makeLaunchGuard } from '../../../lib/cloudRescue';

// ── Хук: котировка облачного исполнителя, пока стенд лежит ──

export interface CloudQuote {
  provider: AudioProvider | null;
  quote: AudioQuote | null;
  // «≈ $0.04» из котировки; null — цена ещё не известна
  priceLabel: string | null;
}

export function useCloudQuote(
  scope: string, sessionId: string, threadId: string, catalog: AudioCatalog | null,
  gpu: LocalGpuQueueInfo | null, op: AudioOp | null, mode: AudioMode | null, count: number,
  prompt: string | null,
): CloudQuote {
  // Перерисовка при смене каталога/снимка: хук живёт в дереве, подписанном на стор
  useAudioStoreVersion();
  const down = isStandDown(gpu);
  const provider = cloudCandidate(catalog, op);
  // Котируем только когда стенд лежит и есть облачный исполнитель: иначе запрос лишний
  const base = cloudQuoteRequest(catalog, op, mode, count, prompt);
  const req = down && provider && base
    ? { ...base, sessionId, threadId }
    : null;
  const [state, setState] = useState<{ key: string; quote: AudioQuote | null }>({ key: '', quote: null });
  const key = req ? JSON.stringify(req) : '';

  useEffect(() => {
    if (!req) { setState({ key, quote: null }); return; }
    let alive = true;
    // Небольшая задержка: не котируем на каждом перерисовывании снимка, пока он мигает
    const t = setTimeout(() => {
      audioApi.quote(scope, sessionId, req)
        .then(q => { if (alive) setState({ key, quote: q }); })
        .catch(() => { if (alive) setState({ key, quote: null }); });
    }, 300);
    return () => { alive = false; clearTimeout(t); };
    // req сравнивается по ключу: объект пересоздаётся каждый рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope, sessionId, key]);

  const fresh = state.key === key;
  const quote = fresh ? state.quote : null;
  return {
    provider,
    quote,
    priceLabel: quote ? (priceText(quote.price) ?? null) : null,
  };
}

// ── Блок: строка ожидания + кнопка «Сделать в облаке» ──

// Короткая форма строки (compact) — когда контейнер уже < 400 px: на 320/360 px
// карточка не должна расти в высоту сверх макета
const COMPACT_MAX = 400;

export interface GpuQueueBlockProps {
  scope: string;
  sessionId: string;
  thread: AudioThread;
  job: JobProgress | null;
  feature: boolean;
  // Операция, режим и число вариантов текущей задачи — для котировки облачного исполнителя;
  // null — операция запуска не распознана, облачного исполнителя нет
  op: AudioOp | null;
  mode: AudioMode | null;
  count: number;
}

export function GpuQueueBlock({ scope, sessionId, thread, job, feature, op, mode, count }: GpuQueueBlockProps) {
  const ref = useRef<HTMLDivElement>(null);
  const width = useBoxWidth(ref);
  // Пока ширина не измерена (0) — полная форма: на широком экране короткая форма не мигает
  const compact = width > 0 && width < COMPACT_MAX;
  const gpu = job?.gpu ?? null;

  const catalog = getCatalog(scope);
  // M1: исходный вход задачи (промпт запуска) — общий источник для котировки и запуска,
  // чтобы цена на кнопке совпала с ценой фактического запуска
  const origPrompt = originalTaskParams(thread, job?.jobId ?? null);
  const { provider, quote, priceLabel } = useCloudQuote(scope, sessionId, thread.id, catalog, gpu, op, mode, count, origPrompt);

  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  // Защита от двойного клика: guard выставляется синхронно, до первого await
  const guard = useRef(makeLaunchGuard());

  // showQueueLine = feature && gpu != null; повторная проверка gpu сужает тип для TS
  if (!showQueueLine(feature, gpu) || gpu == null) return null;

  // M1: кнопка видна только когда есть облачный исполнитель для операции И исходный вход
  // задачи восстановим: для операций, где вход обязателен (озвучка/песня/эффект), пустой
  // вход дал бы ненулевую цену Run-модели, а запуск ушёл бы с пустым промптом и провалился.
  // Операции без входа (обработка) — кнопка видна и без промпта.
  const showBtn = showCloudButton(feature, gpu, catalog, op) && inputSatisfied(op, origPrompt);
  const enabled = isCloudButtonEnabled(quote);

  // Новый запуск с явным поставщиком: локальную задачу не отменяем, её ход не трогаем.
  // Котируемся по уже загруженной котировке из хука (quoteId), а не повторным запросом:
  // цена на кнопке и фактическая совпадают, лишний запрос не летает. Кнопка кликабельна
  // только когда quote свежая и с ценой, поэтому здесь quote непуст.
  const launch = async () => {
    // provider непуст только когда op распознан; mode из той же операции — сужаем тип
    if (!provider || !mode) return;
    if (!quote) return;
    if (!guard.current.begin()) return;
    setBusy(true);
    try {
      // M1: вход — тот же, что в котировке (общая сборка opInput): входы запуска = входы котировки,
      // и поле — то, которого требует операция (text у озвучки, prompt у песни/эффекта)
      const inp = opInput(op, origPrompt);
      await audioApi.startJob(scope, sessionId, {
        quoteId: quote.quoteId, sessionId, threadId: thread.id,
        text: inp?.text ?? null, prompt: inp?.prompt ?? null,
      });
      showToast('Запущено в облаке', '', 'success');
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
            Задача ждёт в очереди. Можно сделать в облаке — локальная задача продолжит.
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
