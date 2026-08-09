import type { CSSProperties } from 'react';
import { C, R } from '../../lib/design';

// Точка-приглашение «знакомство не пройдено» (фича default-personas-onboarding).
// Отдельный примитив, а не проп PersonaAvatar: обёртка лица на FAB (AiLauncher)
// имеет overflow:hidden — точка внутри аватара была бы срезана. Родитель даёт
// position:relative, примитив сам позиционируется в правый нижний угол.
// boxSizing:'content-box' — при глобальном border-box кольцо обязано расти
// наружу, ядро остаётся размера size. Декоративна: доступное имя несёт хозяин
// (заголовок карточки / aria-label кнопки FAB), не сама точка.
// style — точечный докрут места (FAB требует transform+zIndex сверх базовой позиции).
export function IntroDot({ size = 8, style }: { size?: number; style?: CSSProperties }) {
  return (
    <span
      aria-hidden
      style={{
        position: 'absolute', right: 0, bottom: 0, pointerEvents: 'none',
        width: size, height: size, borderRadius: R.max, background: C.accent,
        border: `2px solid ${C.bgMain}`, boxSizing: 'content-box',
        ...style,
      }}
    />
  );
}
