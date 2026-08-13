// Цветная точка-индикатор: статус, легенда серии, метка источника.
// Жила копиями по фичам (spend, notes, knowledge) — по правилу «паттерн встретился
// повторно → его место в ui/» вынесена сюда. Размер и цвет задаёт вызывающий:
// смысл у точки всегда локальный, а форма общая.
//
// Рисуем SVG-кругом, а не CSS-кружком (border-radius:50% на квадрате): на дробном
// масштабе Windows (125/150%) маленький CSS-круг 6–8px кладётся между пиксельной
// сеткой и часть точек выходит овалом — векторный <circle> растеризуется по геометрии
// с антиалиасингом и остаётся круглым на любом DPR.
export function Dot({ color, size = 8 }: { color: string; size?: number }) {
  const r = size / 2;
  return (
    <svg
      width={size} height={size}
      viewBox={`-0.5 -0.5 ${size + 1} ${size + 1}`}
      aria-hidden
      style={{ display: 'inline-block', flexShrink: 0, verticalAlign: 'middle' }}
    >
      <circle cx={r} cy={r} r={r} fill={color} />
    </svg>
  );
}
