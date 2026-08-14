import { Sparkles, Gem, Brain, Feather, Lock, Zap, Cpu } from 'lucide-react';
import { useModels, modelProvider, providerLabel, modelLabel, useDefaultModelOption,
  USAGE, type UsageKey } from '../lib/models';
import { WindowBadge } from './ModelPicker';
import { ComposerMenu, type ComposerMenuGroup } from './ComposerMenu';
import { useEffectiveLine } from '../lib/presets';
import { C, FONT } from '../lib/design';

// Выбор модели в полосе контролов композера. Вся механика меню — в ComposerMenu
// (общая с пикером усилия и визуально одинаковая с меню режимов прав); здесь только
// сборка групп и иконки.
//
// Провайдер-агностичен: модели группируются по провайдеру (Claude, DeepSeek, GLM,
// OpenRouter, …), группа Claude идёт первой. Модели прямого HTTP-адаптера
// (provider «…-direct») скрыты — они только для фоновых задач, не для чата.
//
// Смена провайдера у НАЧАТОГО чата не проходит обычным update (транскрипт живёт у
// эндпоинта провайдера) — её проводит родитель через миграцию чата. Поэтому у таких
// групп в списке стоит пометка о переносе, чтобы выбор не выглядел безобидным.
interface Props {
  value?: string | null;
  onChange: (model: string) => void;
  // Чат уже начат (есть транскрипт) — смена провайдера означает перенос чата
  started?: boolean;
  isMobile?: boolean;
  // Схлопнуть плашку до иконки (узкая полоса контролов)
  compact?: boolean;
  // Место применения: подпись пункта «По умолчанию» — от назначения ЭТОГО места
  // (чат персоны и обычный чат могут быть назначены на разные модели)
  usage?: UsageKey;
  // Показывать строку «Сейчас пойдёт» под плашкой. По умолчанию — да, в компактной
  // полосе контролов строка не влезает и зрительно спорит с плашкой усилия.
  showPreview?: boolean;
  // Показывать пометку о заморозке модели у НАЧАТОГО чата. По умолчанию — да, когда started.
  showFreezeNote?: boolean;
}

// Иконка модели по «классу» (мощная/универсальная/экономичная/быстрая). Тир угадываем
// по id, а не по провайдеру: у сторонних моделей те же классы (mini/flash/turbo — быстрые,
// max/ultra/pro — тяжёлые). Незнакомая модель получает нейтральный чип.
export function ModelIcon({ value, size = 14 }: { value?: string | null; size?: number }) {
  const props = { size, strokeWidth: 2, style: { flexShrink: 0 } as const };
  const v = (value ?? '').toLowerCase();
  if (!v) return <Sparkles {...props} />;                                  // «По умолчанию»
  if (/fable|ultra|\bmax\b/.test(v)) return <Gem {...props} />;            // самая мощная
  if (/opus|\bpro\b|reasoner|\br1\b/.test(v)) return <Brain {...props} />; // тяжёлые рассуждения
  if (/haiku|mini|flash|lite|fast|turbo|nano|small/.test(v)) return <Zap {...props} />; // быстрая
  if (/sonnet|chat|\bv3\b/.test(v)) return <Feather {...props} />;         // экономичная
  return <Cpu {...props} />;                                              // нейтральная
}

export function ComposerModelPicker({ value, onChange, started, isMobile, compact,
  usage = USAGE.chatNew, showPreview = !compact, showFreezeNote }: Props) {
  const models = useModels();
  const defaultOption = useDefaultModelOption(usage);

  // Прямой HTTP-адаптер в чате не годится (нужны агентские вызовы) — прячем
  const selectable = models.filter(m => !(m.provider ?? 'claude').endsWith('-direct'));
  if (selectable.length === 0) return null;

  // «По умолчанию» лежит в каталоге как value '', а в сессии тот же смысл несёт null —
  // без нормализации активная строка не подсвечивалась бы вовсе
  const current = value ?? '';
  const currentProvider = modelProvider(current);

  // Группировка по провайдеру, Claude первой, остальные по алфавиту подписи.
  // Пункт «По умолчанию» (value '') из групп исключён — он стоит НАД ними отдельной
  // строкой: настройка может указывать на модель любого провайдера, и место внутри
  // Claude — артефакт того, что этот пункт приходит из каталога CLI.
  const byProvider = new Map<string, typeof selectable>();
  for (const m of selectable) {
    if (!m.value) continue;
    const key = m.provider ?? 'claude';
    if (!byProvider.has(key)) byProvider.set(key, []);
    byProvider.get(key)!.push(m);
  }
  const providerKeys = [...byProvider.keys()].sort((a, b) =>
    a === 'claude' ? -1 : b === 'claude' ? 1 : providerLabel(a).localeCompare(providerLabel(b)));
  // Заголовки групп нужны, только когда провайдеров больше одного
  const showHeaders = providerKeys.length > 1;

  // Отдельная группа-шапка с единственным пунктом «По умолчанию (<модель>)»
  const defaultGroup: ComposerMenuGroup[] = selectable.some(m => !m.value) ? [{
    key: '__default',
    label: undefined,
    // Отчерк: при одном провайдере заголовков групп нет, и пункт сливался бы со списком
    divider: true,
    note: started && modelProvider('', usage) !== currentProvider
      ? `Перенесёт чат к ${providerLabel(modelProvider('', usage))} — контекст сохранится`
      : undefined,
    items: [{
      value: '',
      label: defaultOption.label,
      description: defaultOption.description,
      icon: <ModelIcon value="" />,
      badge: <WindowBadge tokens={defaultOption.contextWindow} />,
    }],
  }] : [];

  const groups: ComposerMenuGroup[] = providerKeys.map(pk => ({
    key: pk,
    label: showHeaders ? providerLabel(pk) : undefined,
    // Перенос чата: другой провайдер И разговор уже начат
    note: started && pk !== currentProvider
      ? `Перенесёт чат к ${providerLabel(pk)} — контекст сохранится`
      : undefined,
    items: byProvider.get(pk)!.map(m => ({
      value: m.value,
      label: m.label,
      description: m.description,
      icon: <ModelIcon value={m.value} />,
      badge: <WindowBadge tokens={m.contextWindow} />,
    })),
  }));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <ComposerMenu
        value={current}
        groups={[...defaultGroup, ...groups]}
        onChange={onChange}
        triggerIcon={<ModelIcon value={current} />}
        // На дефолте пишем «Модель», а не «По умолчанию»: так плашка называет, чем
        // управляет, и не сливается с соседней плашкой усилия. Точное значение — в тултипе.
        triggerLabel={current ? modelLabel(current) : 'Модель'}
        title={`Модель: ${current ? modelLabel(current) : defaultOption.label}`}
        isMobile={isMobile}
        compact={compact}
      />
      {showPreview && <ComposerModelCaption value={value ?? ''} usage={usage} compact={!!compact} />}
      {started && (showFreezeNote ?? true) && <ComposerFreezeNote />}
    </div>
  );
}

// Строка «Сейчас пойдёт» под плашкой модели: при явном выборе — просто подпись
// модели, на дефолте — резолв места по матрице персоны/слота/назначения.
function ComposerModelCaption({ value, usage, compact }: { value: string; usage: UsageKey; compact: boolean }) {
  const effective = useEffectiveLine({ kind: 'action', actionKey: usage });
  // На явной модели показываем её саму — резолв превью не имеет смысла, бэкенд
  // уже выбрал эту модель, никакого наследования тут нет
  const text = value
    ? `Сейчас пойдёт: ${modelLabel(value)}`
    : (effective ?? 'Сейчас пойдёт: выбираем…');
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: compact ? 10.5 : 11, color: C.textMuted,
      lineHeight: 1.35, paddingInline: 2,
      // Без обёртки «caption» (нет смысла дублировать стили) — просто muted-строка
    }}>
      {text}
    </div>
  );
}

// Пометка о заморозке модели у начатого чата: чат держит выбранную модель до конца,
// правки цепочки и уровней действуют на новые чаты. Краткая версия для композера
// (полная — в NewChatSetup, чтобы пользователь увидел её ещё ДО первого хода).
function ComposerFreezeNote() {
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: 5,
      fontFamily: FONT.sans, fontSize: 11, color: C.textMuted,
      lineHeight: 1.4, paddingInline: 2,
    }}>
      <Lock size={11} strokeWidth={2} style={{ flexShrink: 0, marginTop: 2 }} />
      <span>
        Модель этого разговора зафиксирована при создании. Правки цепочки и уровней
        действуют на новые чаты, этот — продолжит на старой модели.
      </span>
    </div>
  );
}
