// «Звук идёт» — те же полоски-эквалайзер, которыми композер показывает запись голоса:
// знак в продукте уже знакомый, и он влезает в кнопку. WaitingIndicator сюда не годится
// вовсе — это индикатор ЛЕНТЫ, с логотипом, аватаром персоны и печатающимся текстом:
// внутри кнопки-иконки он раскрывался в целый блок не на своём месте.
export function PlayingBars({ height }: { height: number }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2, height }} aria-hidden>
      {[0, 0.15, 0.3].map(delay => (
        <span key={delay} className="cc-wave-bar" style={{ height, animationDelay: `${delay}s` }} />
      ))}
    </span>
  );
}
