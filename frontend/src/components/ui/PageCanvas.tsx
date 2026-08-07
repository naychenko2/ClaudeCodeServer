import type { CSSProperties, ReactNode } from 'react';
import { C, FONT } from '../../lib/design';
import { CanvasBackdrop } from './CanvasBackdrop';

// Корень страницы-холста: общий каркас всех полноэкранных разделов плюс дудл-фон
// под ними. Заменяет копипасту корневого div'а, которая раньше повторялась на
// каждой странице дословно — вместе с ней терялся и сам фон: экраны, написанные
// в обход этого шаблона (вход, заглушка загрузки, экран ошибки), оставались
// с монотонной заливкой.
//
// Почему это именно примитив, а не «не забудь добавить <CanvasBackdrop/>»:
// холст лежит на zIndex:-1, поэтому без position:'relative' + isolation:'isolate'
// у родителя он проваливается под его собственный background и не виден вовсе.
// Два обязательных свойства и вставка компонента — три места, где можно ошибиться;
// здесь они склеены в одно.
//
// style — точечные отличия конкретного экрана (центрирование, position:'fixed'
// у оверлея истории, height:'100%' у вложенной страницы). Перекрывает дефолты.
export function PageCanvas({ children, style }: { children?: ReactNode; style?: CSSProperties }) {
  return (
    <div style={{
      height: '100dvh',
      background: C.bgMain,
      fontFamily: FONT.sans,
      display: 'flex',
      flexDirection: 'column',
      overflow: 'hidden',
      position: 'relative',
      isolation: 'isolate',
      ...style,
    }}>
      {/* Дудл-фон на всю страницу — от самого верха окна, шапка лежит на нём */}
      <CanvasBackdrop />
      {children}
    </div>
  );
}
