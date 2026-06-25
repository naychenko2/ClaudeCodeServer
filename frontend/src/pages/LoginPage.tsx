import React, { useState } from 'react';
import type { AuthState } from '../types';
import { api } from '../lib/api';
import { OfflineError } from '../lib/offline';
import { C, R, FONT } from '../lib/design';
import { IconField, Toggle, Button } from '../components/ui';

interface LoginPageProps {
  onConnect: (auth: AuthState) => void;
}

type PageState = 'idle' | 'loading' | 'error';

export const LoginPage: React.FC<LoginPageProps> = ({ onConnect }) => {
  const serverUrl = window.location.origin;
  const [username, setUsername] = useState(() => localStorage.getItem('cc_username') || '');
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(() => !!localStorage.getItem('cc_token'));
  const [pageState, setPageState] = useState<PageState>('idle');
  const [errorMessage, setErrorMessage] = useState('');

  const handleConnect = async () => {
    setPageState('loading');
    setErrorMessage('');
    try {
      const result = await api.auth.login(username, password);
      // Получаем роль сразу после логина — токен уже активен в request через storage
      let role: string | undefined;
      let userId: string | undefined;
      try {
        // Временно кладём токен в sessionStorage чтобы request его подхватил
        sessionStorage.setItem('cc_token', result.token);
        const me = await api.auth.me();
        role = me.role;
        userId = me.userId;
      } catch { /* роль недоступна — не критично */ }

      if (remember) {
        localStorage.setItem('cc_server_url', serverUrl);
        localStorage.setItem('cc_token', result.token);
        localStorage.setItem('cc_username', result.username);
        if (role) localStorage.setItem('cc_role', role);
        if (userId) localStorage.setItem('cc_user_id', userId);
        sessionStorage.removeItem('cc_token');
        sessionStorage.removeItem('cc_role');
        sessionStorage.removeItem('cc_user_id');
      } else {
        sessionStorage.setItem('cc_token', result.token);
        if (role) sessionStorage.setItem('cc_role', role);
        if (userId) sessionStorage.setItem('cc_user_id', userId);
        localStorage.removeItem('cc_token');
        localStorage.removeItem('cc_server_url');
        localStorage.removeItem('cc_username');
        localStorage.removeItem('cc_role');
        localStorage.removeItem('cc_user_id');
      }
      onConnect({ serverUrl, token: result.token, username: result.username, role, id: userId });
    } catch (err: unknown) {
      const message = err instanceof OfflineError
        ? 'Нет соединения с сервером. Проверьте подключение к интернету.'
        : err instanceof Error ? err.message : 'Не удалось подключиться к серверу';
      setErrorMessage(message);
      setPageState('error');
    }
  };

  const handleRetry = () => {
    setPageState('idle');
    setErrorMessage('');
    handleConnect();
  };

  const isLoading = pageState === 'loading';
  const isError = pageState === 'error';
  const isDisabled = isLoading || !username.trim() || !password.trim();

  return (
    <div
      style={{
        minHeight: '100vh', background: C.bgMain,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        padding: '24px 20px', boxSizing: 'border-box', fontFamily: FONT.sans,
      }}
    >
      <div style={{ width: '100%', maxWidth: 440 }}>
        {/* Логотип */}
        <div style={{ display: 'flex', justifyContent: 'center', marginBottom: 28 }}>
          <div style={{
            width: 54, height: 54, borderRadius: 15, background: C.accent,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <svg width="32" height="32" viewBox="0 0 512 512" fill="none">
              <g stroke="#FFFFFF" strokeWidth="52" strokeLinecap="round" fill="none">
                <line x1="256" y1="130" x2="256" y2="382"/>
                <line x1="130" y1="256" x2="382" y2="256"/>
                <line x1="160" y1="160" x2="352" y2="352"/>
                <line x1="352" y1="160" x2="160" y2="352"/>
              </g>
            </svg>
          </div>
        </div>

        {/* Заголовок */}
        <h1 style={{
          fontFamily: FONT.serif, fontSize: 34, fontWeight: 500, color: C.textHeading,
          margin: '0 0 8px', textAlign: 'center', lineHeight: 1.1, letterSpacing: '-0.01em',
        }}>
          Вход в Claude Home Server
        </h1>

        <p style={{ fontSize: 15, color: C.textSecondary, margin: '0 0 30px', textAlign: 'center', lineHeight: 1.55 }}>
          Введите логин и пароль для доступа к проектам.
        </p>

        {/* Поле: логин */}
        <div style={{ marginBottom: 12 }}>
          <IconField
            type="text"
            value={username}
            onChange={setUsername}
            placeholder="Имя пользователя"
            disabled={isLoading}
            icon={
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <circle cx="12" cy="8" r="4"/>
                <path d="M4 20c0-4 3.6-7 8-7s8 3 8 7"/>
              </svg>
            }
          />
        </div>

        {/* Поле: пароль */}
        <div style={{ marginBottom: 18 }}>
          <IconField
            type="password"
            value={password}
            onChange={setPassword}
            placeholder="Пароль"
            disabled={isLoading}
            mono
            icon={
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <rect x="4" y="11" width="16" height="10" rx="2" />
                <path d="M8 11V7a4 4 0 0 1 8 0v4" />
              </svg>
            }
          />
        </div>

        {/* Toggle «Запомнить меня» */}
        <div
          onClick={() => !isLoading && setRemember(!remember)}
          style={{ display: 'flex', alignItems: 'center', gap: 11, cursor: isLoading ? 'default' : 'pointer', marginBottom: 28, userSelect: 'none' }}
        >
          <Toggle checked={remember} onChange={setRemember} disabled={isLoading} />
          <span style={{ fontSize: 14, color: C.textSecondary }}>
            Запомнить меня на этом устройстве
          </span>
        </div>

        {/* Кнопка «Войти» */}
        {!isError && (
          <Button
            variant="primary" size="lg" fullWidth glow
            loading={isLoading} disabled={isDisabled} onClick={handleConnect}
            leftIcon={
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M5 12h14M13 6l6 6-6 6" />
              </svg>
            }
          >
            {isLoading ? 'Вхожу…' : 'Войти'}
          </Button>
        )}

        {/* Состояние ошибки */}
        {isError && (
          <>
            <div style={{
              background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.xl,
              padding: '12px 14px', display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 16,
            }}>
              <span style={{ fontSize: 16, flexShrink: 0, marginTop: 1 }}>⚠</span>
              <span style={{ fontSize: 13.5, color: C.danger, lineHeight: 1.45 }}>
                {errorMessage}
              </span>
            </div>
            <div style={{ display: 'flex', gap: 10 }}>
              <Button variant="ghostAccent" fullWidth onClick={() => { setPageState('idle'); setErrorMessage(''); }}>
                Изменить данные
              </Button>
              <Button variant="primary" fullWidth onClick={handleRetry}>Повторить</Button>
            </div>
          </>
        )}

        {!isError && (
          <div style={{ textAlign: 'center', fontSize: 12.5, color: C.textMuted, marginTop: 14 }}>
            Соединение шифруется end-to-end
          </div>
        )}
      </div>
    </div>
  );
};
