import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Badge } from '../../components/ui';
import { C, FONT, FS, R } from '../../lib/design';
import { api } from '../../lib/api';
import type { HiggsfieldStatus } from '../../lib/api';

// Плитка Higgsfield для пользователя: показывает подключение И состояние из
// McpStatusStore. До правки карточка смотрела только на `connected` из
// higgsfield.json — а HiggsfieldToolset туда ничего не пишет при лежащем апстриме,
// и карточка зеленела даже когда интеграция мертва. Теперь контроллер Status()
// подмешивает health/error из стора, и здесь мы рисуем тон по нему (задача
// 67b7c30a, раздел «громкий отказ не доезжает до человека»). Администратор
// подключает один раз на весь продукт — пользователю кнопки не нужны.

export function HiggsfieldCard() {
  const [status, setStatus] = useState<HiggsfieldStatus | null>(null);
  const [loading, setLoading] = useState(true);

  const loadStatus = () => {
    setLoading(true);
    api.higgsfield.status()
      .then(s => { setStatus(s); setLoading(false); })
      .catch(() => { setStatus(null); setLoading(false); });
  };

  useEffect(() => {
    loadStatus();
  }, []);

  const cardStyle: CSSProperties = {
    background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
    padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: 8,
  };

  const titleStyle: CSSProperties = {
    fontSize: FS.md, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
  };

  const subtitleStyle: CSSProperties = {
    fontSize: FS.sm, color: C.textMuted,
  };

  if (loading) {
    return (
      <div style={{ ...cardStyle, alignItems: 'center' }}>
        <span style={subtitleStyle}>Загрузка…</span>
      </div>
    );
  }

  const connected = status?.connected ?? false;
  // health из McpStatusStore: failed/needs-auth перекрывают зелёный connected.
  // unknown = наблюдения ещё не было, не красним.
  const health = status?.health ?? null;
  const isFailed = health === 'failed' || health === 'needs-auth';

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={titleStyle}>Higgsfield</span>
          <span style={subtitleStyle}>Видео и картинки по подписке</span>
        </div>
        {isFailed
          ? <Badge tone="danger" size="sm">
              {health === 'needs-auth' ? 'Нужен вход' : 'Не отвечает'}
            </Badge>
          : connected
            ? <Badge tone="success" size="sm">Подключено</Badge>
            : <Badge tone="neutral" size="sm">Не подключено</Badge>}
      </div>

      {isFailed && status?.error && (
        <div style={{
          fontSize: FS.sm, color: C.dangerText, background: C.dangerBg,
          padding: '6px 10px', borderRadius: R.md, lineHeight: 1.45,
        }}>{status.error}</div>
      )}

      {!connected && !isFailed && (
        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
          Администратор ещё не подключил Higgsfield. Функция станет доступна после подключения.
        </div>
      )}
    </div>
  );
}