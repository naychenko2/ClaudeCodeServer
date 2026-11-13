// «Исполнители ночи» (W7b): что админ даёт месту, которое умеет вести несколько ночных
// работ одновременно — список локальных движков с параллельностью каждого и общий
// потолок слотов. Данные — блок снимка GET /api/usage (ollama.localEngines +
// actions[].executors, W2c); сохранение — через onSave родителя (тело ровно формы
// modelsApi.putLocalActionExecutors, W7a). Компонент сам ничего не читает и не пишет:
// источник правды — value из props, черновик живёт здесь и сбрасывается при новой value.
//
// Порядок списка — приоритет (контракт стора): строки и items — в порядке engines,
// фронт ничего не сортирует. Пустой выбор — сброс к одному маршруту: тело несёт
// пустой список, бэк снимает ключ, место идёт по своему маршруту, как до фичи.
//
// Основная локаль («local») предлагается только когда она включена: бэк ставит её в
// localEngines всегда, даже при выключенной основной локали (контракт из ревью W2c) —
// движок, который не может ответить, исполнителем быть не может. Подпись движка —
// title как он пришёл (бэк формирует её сам: модель, а без модели — «Основная»);
// фронт подписей не придумывает.
//
// Конкурентность сохранения: пока onSave не завершился, кнопка выключена — ровно один
// вызов; отказ — тексты валидатора в Notice, черновик цел. Ответ сохранения никогда
// не трогает черновик: если value сменилась во время сохранения, её уже сбросил
// переход «value → черновик» выше, а затирать её старым черновиком — значит
// проигнорировать более свежее состояние.

import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { C, FONT, FS, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Button } from '../../components/ui/Button';
import { Checkbox } from '../../components/ui/Checkbox';
import { Notice } from '../../components/ui/Notice';
import { Stepper } from '../../components/ui/Stepper';
import type { OllamaActionInfo, OllamaUsageInfo } from '../../types';

// Строка выбора движка — элемент ollama.localEngines (W2c); значение места —
// запись actions[].executors (W7a); тело сохранения — форма PUT (W7a).
type EngineOption = NonNullable<OllamaUsageInfo['localEngines']>[number];
type ExecutorsValue = NonNullable<OllamaActionInfo['executors']>;

export interface NightExecutorsBody {
  maxTotal: number;
  executors: { engine: string; parallel: number }[];
}

// Потолок слотов одного владельца — то же число, что держит бэковый валидатор
// (LocalActionExecutorsStore.MaxTotalLimit)
const MAX_TOTAL = 4;
// Ключ основной локали — то же значение, которым админ зовёт её маршрут на бэке
const MAIN_LOCAL = 'local';
// Подсказка у параллельности (постановка W7b): почему «больше 1» — не всегда ускорение
const PARALLEL_HINT = 'больше 1 на одном движке ускоряет, только если памяти хватает на два окна';

const clamp = (v: number, lo: number, hi: number) => Math.min(Math.max(v, lo), hi);
// Потолок движка не меньше 1: при maxConcurrency 0 (выключенный движок) range
// мин–макс у Stepper съехал бы, и поле стало бы неуправляемым
const capOf = (e: EngineOption) => Math.max(1, e.maxConcurrency);

interface Draft {
  maxTotal: number;
  checked: Record<string, boolean>;
  parallel: Record<string, number>;
}

// Черновик из значения: отмечены только движки из value, параллельность — её число,
// ограниченное потолком движка; при отсутствии значения — ничего не отмечено,
// потолок 1 (при пустом выборе он не значится)
function draftFrom(value: ExecutorsValue | null, engines: EngineOption[]): Draft {
  const checked: Record<string, boolean> = {};
  const parallel: Record<string, number> = {};
  for (const e of engines) {
    const item = value?.items.find((it) => it.engine === e.name) ?? null;
    checked[e.name] = item !== null;
    parallel[e.name] = item ? clamp(item.parallel, 1, capOf(e)) : 1;
  }
  return { maxTotal: clamp(value?.maxTotal ?? 1, 1, MAX_TOTAL), checked, parallel };
}

// Тело сохранения: items — в порядке engines (порядок = приоритет, не сортировать);
// пустой выбор — пустой список и maxTotal 0 (контракт бэка: при пустом списке он
// не значится и не может блокировать снятие ключа)
function bodyFrom(draft: Draft, engines: EngineOption[]): NightExecutorsBody {
  const items = engines
    .filter((e) => draft.checked[e.name])
    .map((e) => ({ engine: e.name, parallel: clamp(draft.parallel[e.name] ?? 1, 1, capOf(e)) }));
  if (items.length === 0) return { maxTotal: 0, executors: [] };
  return { maxTotal: clamp(draft.maxTotal, 1, MAX_TOTAL), executors: items };
}

// Текст отказа: 400 несёт список текстов валидатора ({ errors }) — админ обязан видеть,
// ЧТО именно не принято (контракт W2b/W7a), а не «HTTP 400»
function refusalText(e: unknown): string {
  const errors = (e as { body?: { errors?: unknown } } | null)?.body?.errors;
  if (Array.isArray(errors) && errors.length > 0) return errors.map(String).join('; ');
  return e instanceof Error && e.message ? e.message : 'Не удалось сохранить';
}

export interface NightExecutorsEditorProps {
  /** Локальные движки из ollama.localEngines; порядок списка = приоритет */
  engines: EngineOption[];
  /** Действующий список места (actions[].executors); null — списка нет, место по своему маршруту */
  value: ExecutorsValue | null;
  /** Основная локаль включена (ollama.enabled): при false «local» не предлагаем (ревью W2c) */
  mainLocalEnabled: boolean;
  /** Сохранение: тело — формы PUT (W7a); отказ — бросает ошибку (у 400 — body.errors) */
  onSave: (body: NightExecutorsBody) => Promise<void>;
}

const TITLE = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.07em', margin: `${SP.md}px ${SP.xs}px ${SP.xs}px`,
} as const;

export function NightExecutorsEditor({ engines, value, mainLocalEnabled, onSave }: NightExecutorsEditorProps) {
  const isMobile = useIsMobile();
  // Основная локаль — исполнитель только когда включена: бэк ставит «local» в список
  // всегда (ревью W2c), здесь она скрывается и из черновика, и из тела сохранения
  const visible = engines.filter((e) => mainLocalEnabled || e.name !== MAIN_LOCAL);

  const valueKey = JSON.stringify(value ?? null);
  const [draft, setDraft] = useState<Draft>(() => draftFrom(value, visible));
  const [syncedKey, setSyncedKey] = useState(valueKey);
  // Новая value сбрасывает черновик: источник правды — value, черновик — только
  // незавершённая работа. Сравнение по содержанию, не по ссылке: перечитка тех же
  // настроек не должна стирать то, что человек уже пометил
  if (syncedKey !== valueKey) {
    setSyncedKey(valueKey);
    setDraft(draftFrom(value, visible));
  }

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  // Правка черновика. На время сохранения черновик заморожен: тело запроса уже
  // построено, и правка посреди сохранения оставила бы экран расходящимся с тем,
  // что ушло на сервер. Сброс от новой value (выше) этой заморозке не подчиняется:
  // он не правка человека, а приход более свежего состояния
  const patch = (fn: (d: Draft) => Draft) => {
    if (saving) return;
    setDraft(fn);
  };

  const save = async () => {
    // Защита от двойного клика, пока выключенная кнопка ещё не отрендерилась:
    // второй вызов не должен дойти до onSave
    if (saving) return;
    setSaving(true);
    setSaveError(null);
    try {
      await onSave(bodyFrom(draft, visible));
    } catch (e) {
      // Отказ — тексты валидатора в Notice; черновик цел: человек правит то,
      // что названо, и повторяет
      setSaveError(refusalText(e));
    } finally {
      setSaving(false);
    }
  };

  const checkedCount = visible.filter((e) => draft.checked[e.name]).length;

  return (
    <div data-testid="night-executors">
      <div style={TITLE}>Исполнители ночи</div>
      <div style={{ fontSize: FS.sm, color: C.textMuted, margin: `0 ${SP.xs}px ${SP.sm}px`, lineHeight: 1.45 }}>
        Ночной разбор может идти сразу на нескольких локальных движках. Порядок списка —
        приоритет: первый получает чат раньше.
      </div>

      {saveError && (
        <div data-testid="night-executors-error" style={{ margin: `0 ${SP.xs}px ${SP.sm}px` }}>
          <Notice tone="danger" icon={AlertTriangle} title="Список не принят">{saveError}</Notice>
        </div>
      )}

      {visible.length === 0 && (
        <div data-testid="night-executors-empty" style={{ fontSize: FS.sm, color: C.textMuted, margin: `0 ${SP.xs}px ${SP.sm}px` }}>
          Локальных движков нет — предлагать нечего.
        </div>
      )}

      {visible.map((e) => {
        const on = !!draft.checked[e.name];
        return (
          <div
            key={e.name}
            data-testid={`night-exec-row-${e.name}`}
            style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${SP.xs}px` }}
          >
            <Checkbox
              checked={on}
              disabled={saving}
              ariaLabel={e.title}
              onChange={(v) => patch((d) => ({ ...d, checked: { ...d.checked, [e.name]: v } }))}
            />
            <span style={{ flex: '1 1 180px', minWidth: 0, fontSize: FS.base, color: C.textHeading, overflowWrap: 'anywhere' }}>
              {e.title}
            </span>
            {/* Параллельность — только у отмеченного движка: у неотмеченного она не
                имеет смысла, и активный контрол обещал бы то, что не действует */}
            {on && (
              <Stepper
                value={draft.parallel[e.name] ?? 1}
                min={1}
                max={capOf(e)}
                ariaLabel={`Параллельность «${e.title}»`}
                touch={isMobile}
                onChange={(v) => patch((d) => ({ ...d, parallel: { ...d.parallel, [e.name]: v } }))}
              />
            )}
          </div>
        );
      })}

      {visible.length > 0 && (
        <div data-testid="night-executors-parallel-hint" style={{ fontSize: FS.xs, color: C.textMuted, margin: `${SP.xs}px ${SP.xs}px ${SP.sm}px` }}>
          {PARALLEL_HINT}
        </div>
      )}

      <div
        data-testid="night-exec-total"
        style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${SP.xs}px` }}
      >
        <span style={{ flex: '1 1 180px', minWidth: 0, fontSize: FS.base, color: C.textHeading }}>
          Общий потолок
          <span style={{ fontSize: FS.xs, color: C.textMuted, marginLeft: SP.sm }}>
            сколько ночных задач может идти у вас одновременно
          </span>
        </span>
        <Stepper
          value={draft.maxTotal}
          min={1}
          max={MAX_TOTAL}
          ariaLabel="Общий потолок одновременных ночных задач"
          touch={isMobile}
          onChange={(v) => patch((d) => ({ ...d, maxTotal: v }))}
        />
      </div>

      {/* Пустой выбор — не ошибка, а состояние: оно и есть «одна работа за один проход»,
          как место шло до фичи. Сказано до сохранения, чтобы «Сохранить» не выглядело
          сохранением списка из нуля движков */}
      {checkedCount === 0 && (
        <div data-testid="night-executors-none-note" style={{ fontSize: FS.sm, color: C.textMuted, margin: `${SP.xs}px ${SP.xs}px` }}>
          Ничего не отмечено — ночной разбор пойдёт по своему одному маршруту, как сейчас.
        </div>
      )}

      <div style={{ margin: `${SP.sm}px ${SP.xs}px` }}>
        <Button
          variant="primary"
          // тач-цель ≥40 на мобильной ширине (target-devices.md); на десктопе — компактно
          size={isMobile ? 'md' : 'sm'}
          dataTestId="night-executors-save"
          loading={saving}
          onClick={() => { void save(); }}
        >
          Сохранить
        </Button>
      </div>
    </div>
  );
}
