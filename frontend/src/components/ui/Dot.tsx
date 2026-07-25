import { R } from '../../lib/design';

// Цветная точка-индикатор: статус, легенда серии, метка источника.
// Жила копиями по фичам (spend, notes, knowledge) — по правилу «паттерн встретился
// повторно → его место в ui/» вынесена сюда. Размер и цвет задаёт вызывающий:
// смысл у точки всегда локальный, а форма общая.
export function Dot({ color, size = 8 }: { color: string; size?: number }) {
  return (
    <span style={{
      width: size, height: size, borderRadius: R.full, background: color,
      display: 'inline-block', flexShrink: 0,
    }} />
  );
}
