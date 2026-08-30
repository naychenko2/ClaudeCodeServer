// Состояние бейджа ротации аккаунта пула подписок Claude (экран «Использование»).
// IsInRotation с бэка — абсолютный предикат («не исчерпан и ниже порога»), а роутинг
// новых чатов относительный: при отсутствии свободных Pick спиллит на перегруженные.
// Поэтому честный бейдж считается по ДВУМ осям: цель роутинга (routingTarget) × в ротации.
// Ограничения тарифа (supportsOpus/supports1M) — ТРЕТЬЯ ось: они не двигают бейдж
// (аккаунт без Opus всё ещё «в ротации» для Sonnet/Haiku), но ВСЕГДА подмешиваются в
// reason как суффикс «, кроме ходов Opus и 1M». Pick при роутинге Opus/1M-целей
// отсекает этот аккаунт (SupportsModel), и без оговорки бейдж «новые чаты направляются
// сюда» врёт — на самом деле ~2/3 чатов идут мимо. Tone и label не двигаем: ограничения
// не «выключают» аккаунт, а только уточняют, какие ходы он примет.
// Ранг тарифа (tierBelowTarget) — ЧЕТВЁРТАЯ ось, того же свойства: TopTier в
// ClaudeSubscriptionPool срезает всё, кроме высшего тарифа набора, поэтому Max 5× при
// живом Max 20× не получит ни одного нового чата — «может принимать новые чаты» врёт.
// Tone и label и здесь не двигаем: аккаунт не выключен, он резерв.
// Причина вывода из ротации считается по ДВУМ окнам (5ч и недельное): пул выводит
// аккаунт по любому из них (IsOverloaded), и называть всегда пятичасовое — враньё.

export interface RotationInfo {
  inRotation?: boolean;
  utilization?: number;   // эффективная утилизация 5ч-окна, 0..1
  threshold?: number;     // мягкий порог вывода из ротации, 0..1
  weeklyUtilization?: number; // эффективная утилизация недельного окна, 0..1
  weeklyThreshold?: number;   // порог недельного окна (дефолт бэка 0.95), 0..1
  exhausted?: boolean;    // жёсткое исчерпание (rejected/100%)
  isTarget?: boolean;     // этот аккаунт — фактическая цель роутинга новых чатов
  targetName?: string;    // имя аккаунта-цели (куда идут чаты, если не сюда)
  freeAvailable?: boolean; // есть ли в пуле аккаунты в ротации (свободные)
  // Способности аккаунта (false = не принимает Opus/1M-ходы). undefined — поле не пришло,
  // неинформативно, в reason не подмешиваем (см. CLAUDE.md §«LLM-провайдеры»,
  // ClaudeSubscriptionPool.SupportsModel).
  supportsOpus?: boolean;
  supports1M?: boolean;
  // Тариф аккаунта ниже тарифа цели роутинга — Pick до него не дойдёт (TopTier),
  // даже если он в ротации и свободен. undefined/false — ось неприменима или неизвестна.
  tierBelowTarget?: boolean;
}

export interface RotationBadgeState {
  tone: 'ok' | 'warn';
  label: string;
  reason: string;
}

// Четыре честных состояния: цель×ротация. Спилл (цель, но вне ротации) — жёлтый:
// аккаунт перегружен, но новые чаты всё равно идут сюда — свободных нет.
export function rotationBadgeState(info: RotationInfo): RotationBadgeState {
  const inRotation = info.inRotation !== false;
  // Почему аккаунт вне ротации: жёсткое исчерпание vs мягкий порог по нагрузке
  const outReason = info.exhausted ? 'лимит исчерпан' : loadReason(info);
  // Суффикс ограничений тарифа — ВСЕГДА подмешиваем (см. шапку файла): даже в зелёной
  // ветке без него бейдж «направляются сюда» врёт про Opus/1M-ходы.
  const limitsSuffix = capabilitySuffix(info);

  if (info.isTarget) {
    if (inRotation)
      return {
        tone: 'ok', label: 'В ротации',
        reason: appendSuffix('новые чаты направляются сюда', limitsSuffix),
      };
    return {
      tone: 'warn',
      label: 'Принимает новые чаты',
      reason: appendSuffix(`свободных аккаунтов нет — ${info.exhausted ? 'все аккаунты исчерпаны' : outReason}`, limitsSuffix),
    };
  }

  if (inRotation)
    return {
      tone: 'ok', label: 'В ротации',
      // Тариф ниже цели — аккаунт свободен, но Pick до него не дойдёт: это резерв,
      // а не «может принимать новые чаты».
      reason: appendSuffix(
        info.tierBelowTarget
          ? 'резерв — новые чаты идут на аккаунты старшего тарифа'
          : 'может принимать новые чаты',
        limitsSuffix),
    };

  // «Идут на свободные» — только если свободные реально есть; иначе называем цель по имени
  const destination = info.freeAvailable
    ? 'новые чаты идут на свободные аккаунты'
    : info.targetName
      ? `новые чаты идут на «${info.targetName}»`
      : 'новые чаты идут на наименее загруженный аккаунт';
  return {
    tone: 'warn',
    label: 'Выведен из ротации',
    reason: appendSuffix(`${outReason} — ${destination}`, limitsSuffix),
  };
}

// Причина по нагрузке: называем то окно (или оба), которое реально перешло свой порог.
// Сравниваем по СЫРЫМ долям — бэкенд в IsOverloaded делает так же, и округление
// процентов сдвинуло бы границу: 0.796 ≥ 0.8 ложь, но Math.round дал бы «80% ≥ 80%».
// Math.round остаётся только для отображения. Ни одно окно не перешло порог (расхождение
// снимка и предиката бэка, или аккаунт вне ротации по другой причине — например,
// протухший OAuth) — НЕ выдумываем сравнение: ровно этот класс вранья мы и убираем.
// Конкретную причину (выделенный authDead) бэкенд начнёт отдавать отдельно.
function loadReason(info: RotationInfo): string {
  const utilization = info.utilization ?? 0;
  const threshold = info.threshold ?? 0.8;
  const weeklyUtilization = info.weeklyUtilization ?? 0;
  const weeklyThreshold = info.weeklyThreshold ?? 0.95;
  const parts: string[] = [];
  if (utilization >= threshold) {
    parts.push(`нагрузка 5ч ${Math.round(utilization * 100)}% ≥ порога ${Math.round(threshold * 100)}%`);
  }
  if (weeklyUtilization >= weeklyThreshold) {
    parts.push(`нагрузка 7д ${Math.round(weeklyUtilization * 100)}% ≥ порога ${Math.round(weeklyThreshold * 100)}%`);
  }
  return parts.length ? parts.join(' · ') : 'аккаунт недоступен для новых чатов';
}

// Суффикс ограничений: «, кроме ходов Opus и 1M». Это оговорка ко второму пункту reason
// (исчерпание/назначение), а не третий равноправный — поэтому запятая, не «·», и «и»
// вместо слэша. undefined — добавлять нечего.
function capabilitySuffix(info: RotationInfo): string | undefined {
  const parts: string[] = [];
  if (info.supportsOpus === false) parts.push('Opus');
  if (info.supports1M === false) parts.push('1M');
  return parts.length ? `, кроме ходов ${parts.join(' и ')}` : undefined;
}

function appendSuffix(reason: string, suffix: string | undefined): string {
  return suffix ? `${reason}${suffix}` : reason;
}
