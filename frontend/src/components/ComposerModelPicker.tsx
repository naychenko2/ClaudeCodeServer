import { Sparkles, Gem, Brain, Feather, Zap, Cpu } from 'lucide-react';
import { useModels, modelProvider, providerLabel, modelLabel } from '../lib/models';
import { WindowBadge } from './ModelPicker';
import { ComposerMenu, type ComposerMenuGroup } from './ComposerMenu';

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

export function ComposerModelPicker({ value, onChange, started, isMobile, compact }: Props) {
  const models = useModels();

  // Прямой HTTP-адаптер в чате не годится (нужны агентские вызовы) — прячем
  const selectable = models.filter(m => !(m.provider ?? 'claude').endsWith('-direct'));
  if (selectable.length === 0) return null;

  // «По умолчанию» лежит в каталоге как value '', а в сессии тот же смысл несёт null —
  // без нормализации активная строка не подсвечивалась бы вовсе
  const current = value ?? '';
  const currentProvider = modelProvider(current);

  // Группировка по провайдеру, Claude первой, остальные по алфавиту подписи
  const byProvider = new Map<string, typeof selectable>();
  for (const m of selectable) {
    const key = m.provider ?? 'claude';
    if (!byProvider.has(key)) byProvider.set(key, []);
    byProvider.get(key)!.push(m);
  }
  const providerKeys = [...byProvider.keys()].sort((a, b) =>
    a === 'claude' ? -1 : b === 'claude' ? 1 : providerLabel(a).localeCompare(providerLabel(b)));
  // Заголовки групп нужны, только когда провайдеров больше одного
  const showHeaders = providerKeys.length > 1;

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
    <ComposerMenu
      value={current}
      groups={groups}
      onChange={onChange}
      triggerIcon={<ModelIcon value={current} />}
      // На дефолте пишем «Модель», а не «По умолчанию»: так плашка называет, чем
      // управляет, и не сливается с соседней плашкой усилия. Точное значение — в тултипе.
      triggerLabel={current ? modelLabel(current) : 'Модель'}
      title={`Модель: ${modelLabel(current)}`}
      isMobile={isMobile}
      compact={compact}
    />
  );
}
