import { useCallback, useEffect, useRef, useState } from 'react';
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
  const [notice, setNotice] = useState<string | null>(null);

  // Таймер polling'а закрытого OAuth-окна (fallback, если postMessage не пришёл)
  const oauthTimerRef = useRef<number | null>(null);

  const loadStatus = useCallback(() => {
    setLoading(true);
    api.higgsfield.adminStatus()
      .then(s => { setStatus(s); setLoading(false); })
      .catch(() => { setStatus(null); setLoading(false); });
  }, []);

  useEffect(() => {
    loadStatus();
  }, [loadStatus]);

  const stopOAuthPoll = useCallback(() => {
    if (oauthTimerRef.current !== null) {
      window.clearInterval(oauthTimerRef.current);
      oauthTimerRef.current = null;
    }
  }, []);

  // Размонтирование — снять polling
  useEffect(() => () => stopOAuthPoll(), [stopOAuthPoll]);

  // postMessage: callback-страница шлёт { type: 'mcp-oauth', ok, key: 'higgsfield', error }.
  // Тот же контракт, что у личного OAuth-входа MCP-серверов (useMcpData).
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      const payload = e.data as { type?: string; ok?: boolean; key?: string; error?: string } | null;
      if (!payload || payload.type !== 'mcp-oauth' || payload.key !== 'higgsfield') return;
      stopOAuthPoll();
      if (payload.ok) {
        setNotice(null);
        loadStatus();
      } else {
        setError(payload.error || 'Вход не удался');
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [stopOAuthPoll, loadStatus]);

  const handleConnect = async () => {
    setBusy('connect');
    setError(null);
    setNotice(null);
    try {
      const { authorizeUrl } = await api.higgsfield.connect();
      const win = window.open(authorizeUrl, 'higgsfield-admin-oauth', 'width=600,height=700');
      if (!win) {
        setError('Не удалось открыть окно входа — разрешите всплывающие окна и попробуйте снова');
        return;
      }
      // Polling: окно закрылось, postMessage не пришёл
      stopOAuthPoll();
      oauthTimerRef.current = window.setInterval(() => {
        if (!win.closed) return;
        stopOAuthPoll();
        setNotice('Окно закрыто — вход не завершён');
      }, 500);
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

      {notice && (
        <div style={{ fontSize: FS.xs, color: C.warningText }}>{notice}</div>
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
