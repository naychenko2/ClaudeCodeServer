import { useCallback, useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Button, Badge } from '../../components/ui';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { api } from '../../lib/api';
import type { HiggsfieldAdminStatus } from '../../lib/api';

// Вкладка «Higgsfield» (admin): подключение/отключение общего OAuth-токена.
// Видна только администратору (гейт в McpServersModal, как у «Диагностики»).

const cardStyle: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: SP.md,
};

const titleStyle: CSSProperties = {
  fontSize: FS.md, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
};

const infoStyle: CSSProperties = {
  fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
};

function formatExpiry(iso: string | null | undefined): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
}

export function HiggsfieldAdminTab() {
  const [status, setStatus] = useState<HiggsfieldAdminStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<'connect' | 'disconnect' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadStatus = useCallback(() => {
    setLoading(true);
    api.higgsfield.adminStatus()
      .then(s => { setStatus(s); setLoading(false); })
      .catch(() => { setStatus(null); setLoading(false); });
  }, []);

  useEffect(() => {
    loadStatus();
  }, [loadStatus]);

  const handleConnect = async () => {
    setBusy('connect');
    setError(null);
    try {
      const { authorizeUrl } = await api.higgsfield.connect();
      window.open(authorizeUrl, 'higgsfield-admin-oauth', 'width=600,height=700');
      // После авторизации в окне — статус обновится (пользователь закроет окно,
      // а мы просто обновим; можно добавить polling, но минимум — по reload)
      loadStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось начать подключение');
    } finally {
      setBusy(null);
    }
  };

  const handleDisconnect = async () => {
    setBusy('disconnect');
    setError(null);
    try {
      await api.higgsfield.disconnect();
      loadStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось отключить');
    } finally {
      setBusy(null);
    }
  };

  if (loading) {
    return (
      <div style={cardStyle}>
        <span style={infoStyle}>Загрузка…</span>
      </div>
    );
  }

  const connected = status?.connected ?? false;
  const expiresAt = status?.expiresAt;

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={titleStyle}>Higgsfield</span>
          <span style={{ ...infoStyle, color: C.textMuted }}>
            Общий вход администратора на весь продукт
          </span>
        </div>
        {connected
          ? <Badge tone="success" size="sm">Подключено</Badge>
          : <Badge tone="neutral" size="sm">Не подключено</Badge>}
      </div>

      {error && (
        <div style={{
          fontSize: FS.sm, color: C.dangerText, background: C.dangerBg,
          padding: '6px 10px', borderRadius: R.md,
        }}>{error}</div>
      )}

      {connected && (
        <div style={infoStyle}>
          {expiresAt
            ? <>Токен действует до <strong style={{ color: C.textHeading }}>{formatExpiry(expiresAt)}</strong></>
            : 'Вход выполнен'}
        </div>
      )}

      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button
          variant="primary" size="md"
          loading={busy === 'connect'}
          disabled={busy !== null}
          onClick={() => void handleConnect()}
        >
          Подключить
        </Button>
        {connected && (
          <Button
            variant="ghost" size="md"
            loading={busy === 'disconnect'}
            disabled={busy !== null}
            onClick={() => void handleDisconnect()}
          >
            Отключить
          </Button>
        )}
      </div>
    </div>
  );
}
