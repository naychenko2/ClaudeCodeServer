// Миниатюра истории медиа: если ручка ответила 410/404 (файла нет, миниатюра не получилась),
// вместо битой иконки <img> рисуем fallback (плитку вида) и не ретраим. Ошибка привязана
// к src, поэтому смена src сбрасывает её сама (тот же приём, что у ThreadCard/VersionCards).

import { useState, type CSSProperties, type ReactNode } from 'react';

export function HistoryThumb({ src, fallback, style }: {
  src: string;
  fallback: ReactNode;
  style?: CSSProperties;
}) {
  const [failed, setFailed] = useState<string | null>(null);
  if (failed === src) return <>{fallback}</>;
  return <img src={src} alt="" loading="lazy" onError={() => setFailed(src)} style={style} />;
}
