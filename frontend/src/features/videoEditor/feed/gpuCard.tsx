// Строка ожидания GPU (шаг 30) и кнопка «Сделать в облаке» (шаг 37) в карточке идущей сцены.
// Показывается только за флагом local-model-stats и только когда задача несёт живой снимок
// очереди GPU (gpu) — снимок присылает бэкенд для локальных задач. Без флага или без gpu
// карточка — как раньше. Кнопка — только когда стенд лежит (downSince) и есть облачный
// исполнитель для сцены («Автоматически», не локальные); запуск — только по клику человека,
// новым запуском с явным поставщиком, параллельно с локальной (та остаётся в очереди).

import { useEffect, useRef, useState } from 'react';
import { Cloud } from 'lucide-react';
import { Button, ConfirmDialog, C, FS, SP, ICON_SIZE, ICON_STROKE, showToast } from 'aihome_shell/kit';
import { videoApi, type VideoCatalog, type VideoProvider, type VideoQuote, type VideoQuoteRequest } from '../api';
import { LOCAL_PROVIDER, plural } from '../scene/model';
import { getCatalog, useVideoStoreVersion } from '../store/videoStore';
import { LocalGpuQueueLine } from '../../../components/LocalGpuQueueLine';
import { useBoxWidth } from '../useBoxWidth';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';
import { makeLaunchGuard, isStandDown, CLOUD_DIALOG_TITLE, cloudDialogSubtitle, cloudButtonLabel, cloudCandidate as pickCloudCandidate } from '../../../lib/cloudRescue';

const ic = (I: typeof Cloud, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// ── Чистая логика видимости (тестится отдельно) ──

// isStandDown — общий модуль cloudRescue (экспортируется для тестов)
export { isStandDown } from '../../../lib/cloudRescue';

// Облачный исполнитель: первый в порядке «Авто» (catalog.autoProviders) доступный поставщик,
// не локальные модели, с моделями. Нет каталога или облачные все недоступны/пусты —
// облачного исполнителя нет. m4: единая проверка через cloudRescue.cloudCandidate (доступен +
// умеет), как у картинки и звука; у видео «умеет» = есть модели (операции у моделей нет)
export function cloudCandidate(catalog: VideoCatalog | null, op: string | null = null): VideoProvider | null {
  if (!catalog) return null;
  const ordered = catalog.autoProviders
    .map(key => catalog.providers.find(x => x.key === key))
    .filter((p): p is VideoProvider => !!p);
  return pickCloudCandidate(ordered, op, {
    available: p => p.available && p.key !== LOCAL_PROVIDER,
    supports: p => p.models.length > 0,
  });
}

// Кнопка «Сделать в облаке» видна: флаг включён, стенд лежит, есть облачный исполнитель.
// Котировка на этот момент может ещё грузиться
export function showCloudButton(feature: boolean, gpu: LocalGpuQueueInfo | null, catalog: VideoCatalog | null): boolean {
  return feature && isStandDown(gpu) && cloudCandidate(catalog) != null;
}

// Кнопка кликабельна: котировка облачного исполнителя пришла с ценой («free» — цена известна).
// Пока грузится — кнопка видна, но disabled, на месте цены «≈ …»
export const isCloudButtonEnabled = (quote: VideoQuote | null): boolean =>
  quote != null && (quote.price.unit === 'free' || quote.price.amount != null);

// Строка ожидания GPU: флаг включён и задача несёт живой снимок очереди
export const showQueueLine = (feature: boolean, gpu: LocalGpuQueueInfo | null): boolean => feature && gpu != null;

// Котировка облачного исполнителя: «Авто»-модель и параметры сцены из тех же источников, что
// фактический запуск (count/durationSec/aspect/sound) — цена на кнопке = цена запуска.
// sessionId дописывает хук, чтобы сервер привязал котировку к чату
export function cloudQuoteRequest(
  catalog: VideoCatalog | null,
  p: { sceneId: string; count: number; durationSec: number; aspect: string; sound: boolean },
): VideoQuoteRequest | null {
  const prov = cloudCandidate(catalog);
  if (!catalog || !prov) return null;
  return {
    sessionId: '', sceneId: p.sceneId,
    provider: prov.key, model: catalog.autoModelId,
    count: Math.max(1, p.count), durationSec: p.durationSec, aspect: p.aspect, sound: p.sound,
  };
}

// «≈ $3.20» / «2 кредита» / «бесплатно» из котировки; null — цена ещё не известна
export function priceLabel(q: VideoQuote | null): string | null {
  if (!q) return null;
  const p = q.price;
  if (p.unit === 'free') return 'бесплатно';
  if (p.amount == null) return null;
  if (p.unit === 'credits') return `${Math.round(p.amount)} ${plural(Math.round(p.amount), 'кредит', 'кредита', 'кредитов')}`;
  return `${p.approx ? '≈ ' : ''}$${p.amount.toFixed(2)}`;
}

// makeLaunchGuard — общий модуль cloudRescue (экспортируется для тестов)
export { makeLaunchGuard } from '../../../lib/cloudRescue';

// ── Хук: котировка облачного исполнителя, пока стенд лежит ──

export interface CloudQuote {
  provider: VideoProvider | null;
  quote: VideoQuote | null;
  // «≈ $3.20» из котировки; null — цена ещё не известна
  priceLabel: string | null;
}

export function useCloudQuote(
  scope: string, sessionId: string, sceneId: string, catalog: VideoCatalog | null,
  gpu: LocalGpuQueueInfo | null, params: { count: number; durationSec: number; aspect: string; sound: boolean },
): CloudQuote {
  // Перерисовка при смене каталога/снимка: хук живёт в дереве, подписанном на стор
  useVideoStoreVersion();
  const down = isStandDown(gpu);
  const provider = cloudCandidate(catalog);
  // Котируем только когда стенд лежит и есть облачный исполнитель: иначе запрос лишний
  const base = cloudQuoteRequest(catalog, { sceneId, ...params });
  const req = down && provider && base ? { ...base, sessionId } : null;
  const [state, setState] = useState<{ key: string; quote: VideoQuote | null }>({ key: '', quote: null });
  const key = req ? JSON.stringify(req) : '';

  useEffect(() => {
    if (!req) { setState({ key, quote: null }); return; }
    let alive = true;
    // Небольшая задержка: не котируем на каждом перерисовывании снимка, пока он мигает
    const t = setTimeout(() => {
      videoApi.quote(scope, sessionId, req)
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
    priceLabel: quote ? (priceLabel(quote) ?? null) : null,
  };
}

// ── Блок: строка ожидания + кнопка «Сделать в облаке» ──

// Короткая форма строки (compact) — когда контейнер уже < 400 px: на 320/360 px
// карточка не должна расти в высоту сверх макета
const COMPACT_MAX = 400;

export interface GpuQueueBlockProps {
  scope: string;
  sessionId: string;
  sceneId: string;
  feature: boolean;
  // Живой снимок очереди GPU идущей задачи; null — блок не рендерится
  gpu: LocalGpuQueueInfo | null;
  // Параметры сцены из тех же источников, что фактический запуск: для котировки облачного исполнителя
  params: { count: number; durationSec: number; aspect: string; sound: boolean };
}

export function GpuQueueBlock({ scope, sessionId, sceneId, feature, gpu, params }: GpuQueueBlockProps) {
  const ref = useRef<HTMLDivElement>(null);
  const width = useBoxWidth(ref);
  // Пока ширина не измерена (0) — полная форма: на широком экране короткая форма не мигает
  const compact = width > 0 && width < COMPACT_MAX;

  const catalog = getCatalog(scope);
  const { provider, quote, priceLabel } = useCloudQuote(scope, sessionId, sceneId, catalog, gpu, params);

  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  // Защита от двойного клика: guard выставляется синхронно, до первого await
  const guard = useRef(makeLaunchGuard());

  // showQueueLine = feature && gpu != null; повторная проверка gpu сужает тип для TS
  if (!showQueueLine(feature, gpu) || gpu == null) return null;

  const showBtn = showCloudButton(feature, gpu, catalog);
  const enabled = isCloudButtonEnabled(quote);

  // Новый запуск с явным поставщиком: локальную задачу не отменяем, её ход не трогаем.
  // Котируемся по уже загруженной котировке из хука (quoteId), а не повторным запросом:
  // цена на кнопке и фактическая совпадают, лишний запрос не летает. Кнопка кликабельна
  // только когда quote свежая и с ценой, поэтому здесь quote непуст
  const launch = async () => {
    if (!provider || !quote) return;
    if (!guard.current.begin()) return;
    setBusy(true);
    try {
      await videoApi.startJob(scope, sessionId, { quoteId: quote.quoteId, sessionId, sceneId });
      showToast('Запущено в облаке', '', 'success');
    } catch (e) {
      showToast((e as Error).message || 'Не удалось запустить в облаке', '', 'error');
    } finally {
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
