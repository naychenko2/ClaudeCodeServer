import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Badge } from '../../components/ui';
import { C, FONT, FS, R } from '../../lib/design';
import { api } from '../../lib/api';
import type { HiggsfieldStatus } from '../../lib/api';

// Плитка Higgsfield для пользователя: показывает только статус подключения.
// Администратор подключает один раз на весь продукт — пользователю кнопки не нужны.

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

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={titleStyle}>Higgsfield</span>
          <span style={subtitleStyle}>Видео и картинки по подписке</span>
        </div>
        {connected && (
          <Badge tone="success" size="sm">Подключено администратором</Badge>
        )}
      </div>

      {!connected && (
        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
          Администратор ещё не подключил Higgsfield. Функция станет доступна после подключения.
        </div>
      )}
    </div>
  );
}
