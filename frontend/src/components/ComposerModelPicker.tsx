import { Sparkles, Gem, Brain, Feather, Lock, Zap, Cpu } from 'lucide-react';
import { useModels, modelProvider, providerLabel, modelLabel, useDefaultModelOption,
  modelFamily, versionHint,
  USAGE, type UsageKey } from '../lib/models';
import { effortLabel } from '../lib/effort';
import { WindowBadge } from './ModelPicker';
import { ComposerMenu, type ComposerMenuGroup } from './ComposerMenu';
import { ComposerEffortPanel, EffortBars } from './ComposerEffortPanel';
import { C, FONT, FS, SP } from '../lib/design';

// Выбор модели и усилия в полосе контролов композера — одна плашка «модель · усилие»:
// усилие — настройка модели, отдельной кнопкой оно лишь ело место в полосе. В меню
// сверху список моделей, снизу ползунок усилия (если родитель передал onEffortChange).
// Вся механика меню — в ComposerMenu (визуально одинаковая с меню режимов прав); здесь
// только сборка групп и иконки.
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
  // Показывать пометку о заморозке модели у НАЧАТОГО чата. По умолчанию — да, когда started.
  showFreezeNote?: boolean;
  // Усилие рассуждения. Родитель не передаёт onEffortChange, если провайдер усилие не
  // поддерживает — тогда ни подписи, ни ползунка
  effort?: string | null;
  onEffortChange?: (effort: string) => void;
  // Показывать столбики уровня усилия в плашке. Узкая полоса снимает их первыми
  // (лестница STRIP_RIGHT_MAX): уровень остаётся в тултипе и в меню
  showEffortLabel?: boolean;
  // Потолок ширины триггера в полной (не compact) форме. Задаётся родителем из таблицы
  // номиналов полосы контролов (STRIP_RIGHT_MAX в Composer.tsx) — иначе фактическая ширина
  // пикера может уехать за край полосы после снятия overflow:hidden. Compact-форма
  // (узкий экран) уже имеет фиксированную ширину в ComposerMenu — этот проп на неё
  // не действует
  maxTriggerWidth?: number;
}

// Иконка модели по «классу» (мощная/универсальная/экономичная/быстрая). Тир угадываем
// по id, а не по провайдеру: у сторонних моделей те же классы (mini/flash/turbo — быстрые,
// max/ultra/pro — тяжёлые). Незнакомая модель получает нейтральный чип.
export function ModelIcon({ value, size = 14 }: { value?: string | null; size?: number }) {
  const props = { size, strokeWidth: 2, style: { flexShrink: 0 } as const };
  const v = modelFamily((value ?? '').toLowerCase());
  if (!v) return <Sparkles {...props} />;                                  // «По умолчанию»
  if (v === 'fable' || /ultra|\bmax\b/.test(v)) return <Gem {...props} />;            // самая мощная
  if (v === 'opus' || /\bpro\b|reasoner|\br1\b/.test(v)) return <Brain {...props} />; // тяжёлые рассуждения
  if (v === 'haiku' || /mini|flash|lite|fast|turbo|nano|small/.test(v)) return <Zap {...props} />; // быстрая
  if (v === 'sonnet' || /chat|\bv3\b/.test(v)) return <Feather {...props} />;         // экономичная
  return <Cpu {...props} />;                                              // нейтральная
}

export function ComposerModelPicker({ value, onChange, started, isMobile, compact,
  usage = USAGE.chatNew, showFreezeNote, effort, onEffortChange, showEffortLabel = true,
  maxTriggerWidth }: Props) {
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
      hint: versionHint(m),
      description: m.description,
      icon: <ModelIcon value={m.value} />,
      badge: <WindowBadge tokens={m.contextWindow} />,
    })),
  }));

  // Уровень усилия в плашке — столбиками, словом он только в тултипе и в меню
  const effortValue = effort ?? '';
  const frozen = !!started && (showFreezeNote ?? true);

  return (
    <ComposerMenu
      value={current}
      groups={[...defaultGroup, ...groups]}
      onChange={onChange}
      triggerIcon={<ModelIcon value={current} />}
      // На дефолте пишем «Модель», а не «По умолчанию»: так плашка называет, чем
      // управляет. Точное значение — в тултипе.
      triggerLabel={current ? modelLabel(current) : 'Модель'}
      triggerSuffix={onEffortChange && showEffortLabel && <EffortBars value={effortValue} />}
      title={`Модель: ${current ? modelLabel(current) : defaultOption.label}`
        + (onEffortChange ? ` · Усилие: ${effortLabel(effortValue)}` : '')}
      isMobile={isMobile}
      compact={compact}
      maxTriggerWidth={maxTriggerWidth}
      header={frozen && <ComposerFreezeNote />}
      footer={onEffortChange && <ComposerEffortPanel value={effort} onChange={onEffortChange} />}
    />
  );
}

// Пометка о заморозке модели у начатого чата: чат держит выбранную модель до конца,
// правки цепочки и уровней действуют на новые чаты. В полосе под неё места нет, поэтому
// она стоит первой строкой меню — там, где человек и собирается модель сменить. В меню
// одна короткая строка, полное объяснение — в тултипе (и в NewChatSetup ДО первого хода).
// Текст — про цепочки, а не «модель закреплена»: сменить модель можно (строкой ниже
// меню предупреждает о переносе чата), неизменна лишь раскладка цепочек и уровней.
// Без заливки и с отчерком: это сведения о чате, а заливка — у пометки о переносе,
// иначе две одинаковые серые плашки подряд не различить.
function ComposerFreezeNote() {
  return (
    <div
      title="Модель этого чата выбрана при создании. Правки цепочек и уровней подействуют на новые чаты."
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        margin: `0 ${SP.xs}px ${SP.xs}px`, padding: `${SP.xs}px ${SP.sm}px ${SP.sm}px`,
        borderBottom: `1px solid ${C.borderLight}`, cursor: 'help',
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.35, fontFamily: FONT.sans,
      }}
    >
      <Lock size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
      <span>Правки цепочек — только для новых чатов</span>
    </div>
  );
}
