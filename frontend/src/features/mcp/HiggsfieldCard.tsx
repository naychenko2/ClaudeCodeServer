import { useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Button, Badge } from '../../components/ui';
import { C, FS, FONT, R, SP } from '../../lib/design';
import { api } from '../../lib/api';
import type { HiggsfieldStatus } from '../../lib/api';

// Окно входа и таймер опроса — не React-состояние: Window-ссылку и interval id
// достаточно хранить в ref, перекладывать в setState незачем.
interface OAuthWindowRef { win: Window; state: string; timer: number; }

function formatExpiry(iso: string | null): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

export function HiggsfieldCard() {
  const [status, setStatus] = useState<HiggsfieldStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [loginStarted, setLoginStarted] = useState(false); // окно открыто
  const [loginError, setLoginError] = useState<string | null>(null);
  const [showManual, setShowManual] = useState(false);
  const [manualCode, setManualCode] = useState('');
  const [completingManual, setCompletingManual] = useState(false);
  const [manualError, setManualError] = useState<string | null>(null);

  const oauthRef = useRef<OAuthWindowRef | null>(null);
  const manualErrorTimer = useRef<number | null>(null);

  const loadStatus = () => {
    setLoading(true);
    api.higgsfield.status()
      .then(s => { setStatus(s); setLoading(false); })
      .catch(() => { setStatus(null); setLoading(false); });
  };

  useEffect(() => {
    loadStatus();
  }, []);

  // Очистка таймера автосброса ошибки
  useEffect(() => {
    return () => { if (manualErrorTimer.current) window.clearTimeout(manualErrorTimer.current); };
  }, []);

  const clearError = () => {
    if (manualErrorTimer.current) { window.clearTimeout(manualErrorTimer.current); manualErrorTimer.current = null; }
    setLoginError(null);
    setManualError(null);
  };

  const scheduleErrorClear = (setter: (v: string | null) => void, msg: string) => {
    setter(msg);
    if (manualErrorTimer.current) window.clearTimeout(manualErrorTimer.current);
    manualErrorTimer.current = window.setTimeout(() => setter(null), 5000);
  };

  const stopOAuth = () => {
    if (oauthRef.current) {
      window.clearInterval(oauthRef.current.timer);
      if (!oauthRef.current.win.closed) oauthRef.current.win.close();
      oauthRef.current = null;
    }
    setLoginStarted(false);
  };

  const startLogin = async () => {
    clearError();
    stopOAuth();
    try {
      const { authorizeUrl, state } = await api.higgsfield.login();
      const win = window.open(authorizeUrl, 'higgsfield-oauth', 'width=600,height=700');
      if (!win) {
        scheduleErrorClear(setLoginError, 'Не удалось открыть окно входа — разрешите всплывающие окна и попробуйте снова');
        return;
      }
      setLoginStarted(true);
      // Таймер 2 мин: если окно closed без postMessage — показываем ручной ввод
      const timer = window.setInterval(() => {
        if (win.closed) {
          window.clearInterval(timer);
          oauthRef.current = null;
          setLoginStarted(false);
          setShowManual(true);
        }
      }, 500);
      oauthRef.current = { win, state, timer };
    } catch (e) {
      // Сбой старта (наш 4xx/5xx): обычно наша сторона — старт OAuth провалился
      // раньше, чем провайдер успел что-то сделать. Подписка тут ни при чём.
      scheduleErrorClear(setLoginError, 'Не удалось начать вход в Higgsfield. Это сбой на нашей стороне — попробуйте позже.');
    }
  };

  const completeManual = async () => {
    if (!manualCode.trim() || completingManual || !oauthRef.current) return;
    setCompletingManual(true);
    try {
      const result = await api.higgsfield.completeOAuth(oauthRef.current.state, manualCode.trim());
      stopOAuth();
      setShowManual(false);
      setManualCode('');
      if (result.ok) loadStatus();
      else scheduleErrorClear(setManualError, 'Код не принят — попробуйте ещё раз');
    } catch {
      // Обмен кода на токены не прошёл: токен-эндпоинт провайдера отверг код.
      scheduleErrorClear(setManualError, 'Higgsfield не принял код входа. Попробуйте войти заново.');
    } finally {
      setCompletingManual(false);
    }
  };

  const handleLogout = async () => {
    await api.higgsfield.logout();
    loadStatus();
  };

  // Слушаем postMessage от callback-страницы
  useEffect(() => {
    if (!loginStarted) return;
    const onMessage = (e: MessageEvent) => {
      const p = e.data as { type?: string; ok?: boolean; key?: string; error?: string } | null;
      if (!p || p.type !== 'mcp-oauth') return;
      if (p.key !== 'higgsfield') return;
      stopOAuth();
      if (p.ok) {
        loadStatus();
      } else {
        scheduleErrorClear(setLoginError, p.error || 'Вход не удался');
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [loginStarted]);

  if (loading) {
    return (
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: '11px 13px', color: C.textMuted, fontSize: FS.sm,
      }}>
        Загрузка…
      </div>
    );
  }

  const connected = status?.connected ?? false;
  const expiresAt = status?.expiresAt ?? null;
  // Запись выключена на уровне API: тумблера «включить» в интерфейсе для этого сервера
  // нет (McpServerList рисует Toggle только для своих), и вход бэк тоже отвергнет.
  // Единственное, что человек может сделать сам — выйти и войти заново: Logout чистит
  // запись, а свежий LoginAsync создаст её с Enabled=true.
  const disabledByRecord = status?.enabled === false;

  const cardStyle: CSSProperties = {
    background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
    padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: SP.sm,
  };

  const titleStyle: CSSProperties = {
    fontSize: FS.md, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
  };

  const subtitleStyle: CSSProperties = {
    fontSize: FS.sm, color: C.textMuted,
  };

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={titleStyle}>Higgsfield</span>
          <span style={subtitleStyle}>Видео и картинки по вашей подписке</span>
        </div>
        {connected && (
          <Badge tone="success" size="sm">Подключено</Badge>
        )}
      </div>

      {/* Ошибка входа */}
      {loginError && (
        <div style={{ fontSize: FS.sm, color: C.dangerText, background: C.dangerBg, padding: '6px 10px', borderRadius: R.md }}>
          {loginError}
        </div>
      )}

      {/* Запись выключена на уровне API — вход бессмысленен, пока её не пересоздать.
          Тумблера «включить» в интерфейсе для этого сервера нет: единственное действие,
          которое человек может сделать сам — выйти из Higgsfield (когда подключён) и войти
          заново. Logout чистит запись, LoginAsync создаёт её с Enabled=true. */}
      {disabledByRecord && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
          Higgsfield выключен на нашей стороне. Кнопки «включить» для него в интерфейсе нет —
          выйдите из Higgsfield (если подключены) и войдите заново: это вернёт запись.
        </div>
      )}

      {/* Состояние «не подключено» */}
      {!connected && !loginStarted && !showManual && !disabledByRecord && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
          Войдите в свой аккаунт Higgsfield — и сможете просить ролики и картинки прямо в разговоре.
          Списывается с вашей подписки, отдельный ключ не нужен.
        </div>
      )}

      {/* Кнопка «Войти» */}
      {!connected && !showManual && !disabledByRecord && (
        <Button variant="primary" size="md" onClick={() => void startLogin()}>
          Войти в Higgsfield
        </Button>
      )}

      {/* Состояние «подключено» */}
      {connected && (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
            {expiresAt
              ? <>Токен действует до <strong style={{ color: C.textHeading }}>{formatExpiry(expiresAt)}</strong></>
              : 'Вход выполнен'}
          </span>
          <Button variant="ghost" size="sm" onClick={() => void handleLogout()}>
            Выйти
          </Button>
        </div>
      )}

      {/* Окно не вернулось само — ручной ввод кода */}
      {showManual && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>
            Вход не завершён — окно закрылось раньше времени. Вставьте код из адресной строки окна входа.
          </span>
          {manualError && (
            <div style={{ fontSize: FS.xs, color: C.dangerText }}>{manualError}</div>
          )}
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
            <input
              type="text"
              value={manualCode}
              onChange={e => setManualCode(e.target.value)}
              placeholder="Код из адресной строки"
              style={{
                flex: 1, minWidth: 0,
                fontFamily: FONT.mono, fontSize: FS.xs,
                padding: '7px 10px', borderRadius: R.md,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                color: C.textHeading, outline: 'none',
              }}
              onKeyDown={e => { if (e.key === 'Enter') void completeManual(); }}
            />
            <Button
              variant="ghost" size="md"
              disabled={!manualCode.trim() || completingManual}
              loading={completingManual}
              onClick={() => void completeManual()}
            >
              Завершить
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
