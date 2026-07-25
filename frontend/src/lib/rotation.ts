// Состояние бейджа ротации аккаунта пула подписок Claude (экран «Использование»).
// IsInRotation с бэка — абсолютный предикат («не исчерпан и ниже порога»), а роутинг
// новых чатов относительный: при отсутствии свободных Pick спиллит на перегруженные.
// Поэтому честный бейдж считается по ДВУМ осям: цель роутинга (routingTarget) × в ротации.

export interface RotationInfo {
  inRotation?: boolean;
  utilization?: number;   // эффективная утилизация 5ч-окна, 0..1
  threshold?: number;     // мягкий порог вывода из ротации, 0..1
  exhausted?: boolean;    // жёсткое исчерпание (rejected/100%)
  isTarget?: boolean;     // этот аккаунт — фактическая цель роутинга новых чатов
  targetName?: string;    // имя аккаунта-цели (куда идут чаты, если не сюда)
  freeAvailable?: boolean; // есть ли в пуле аккаунты в ротации (свободные)
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
  const pct = Math.round((info.utilization ?? 0) * 100);
  const thr = Math.round((info.threshold ?? 0.8) * 100);
  // Почему аккаунт вне ротации: жёсткое исчерпание vs мягкий порог по нагрузке
  const outReason = info.exhausted ? 'лимит исчерпан' : `нагрузка 5ч ${pct}% ≥ порога ${thr}%`;

  if (info.isTarget) {
    if (inRotation)
      return { tone: 'ok', label: 'В ротации', reason: 'новые чаты направляются сюда' };
    return {
      tone: 'warn',
      label: 'Принимает новые чаты',
      reason: `свободных аккаунтов нет — ${info.exhausted ? 'все аккаунты исчерпаны' : outReason}`,
    };
  }

  if (inRotation)
    return { tone: 'ok', label: 'В ротации', reason: 'может принимать новые чаты' };

  // «Идут на свободные» — только если свободные реально есть; иначе называем цель по имени
  const destination = info.freeAvailable
    ? 'новые чаты идут на свободные аккаунты'
    : info.targetName
      ? `новые чаты идут на «${info.targetName}»`
      : 'новые чаты идут на наименее загруженный аккаунт';
  return { tone: 'warn', label: 'Выведен из ротации', reason: `${outReason} — ${destination}` };
}
