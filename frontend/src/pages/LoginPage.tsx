import React, { useState, useEffect } from 'react';
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
  // Адрес сервера = origin, с которого открыта страница (фронт и бэк на одном источнике)
  const serverUrl = window.location.origin;
  const [apiKey, setApiKey] = useState('');
  const [saveKey, setSaveKey] = useState(false);
  const [pageState, setPageState] = useState<PageState>('idle');
  const [errorMessage, setErrorMessage] = useState('');

  useEffect(() => {
    const savedKey = localStorage.getItem('cc_api_key');
    if (savedKey) {
      setApiKey(savedKey);
      setSaveKey(true);
    } else {
      const sessionKey = sessionStorage.getItem('cc_api_key');
      if (sessionKey) setApiKey(sessionKey);
    }
  }, []);

  const handleConnect = async () => {
    setPageState('loading');
    setErrorMessage('');
    try {
      await api.auth.ping(serverUrl, apiKey);
      if (saveKey) {
        localStorage.setItem('cc_server_url', serverUrl);
        localStorage.setItem('cc_api_key', apiKey);
        sessionStorage.removeItem('cc_api_key');
      } else {
        sessionStorage.setItem('cc_api_key', apiKey);
        localStorage.removeItem('cc_api_key');
        localStorage.removeItem('cc_server_url');
      }
      onConnect({ serverUrl, apiKey });
    } catch (err: unknown) {
      const message = err instanceof OfflineError
        ? 'Нет соединения с сервером. Проверьте подключение к интернету.'
        : err instanceof Error ? err.message : 'Не удалось подключиться к серверу';
      setErrorMessage(message);
      setPageState('error');
    }
  };

  const handleChangeKey = () => {
    setApiKey('');
    setPageState('idle');
    setErrorMessage('');
  };

  const handleRetry = () => {
    handleConnect();
  };

  const isLoading = pageState === 'loading';
  const isError = pageState === 'error';
  const isDisabled = isLoading || !apiKey.trim();

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
          Подключение к Claude Code Server
        </h1>

        {/* Подзаголовок */}
        <p style={{ fontSize: 15, color: C.textSecondary, margin: '0 0 30px', textAlign: 'center', lineHeight: 1.55 }}>
          Укажите API-ключ, чтобы открыть проекты и чаты.
        </p>

        {/* Поле: API-ключ */}
        <div style={{ marginBottom: 18 }}>
          <IconField
            type="password"
            value={apiKey}
            onChange={setApiKey}
            placeholder="sk-ant-••••••••••••"
            disabled={isLoading}
            mono
            letterSpacing="0.08em"
            icon={
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <rect x="4" y="11" width="16" height="10" rx="2" />
                <path d="M8 11V7a4 4 0 0 1 8 0v4" />
              </svg>
            }
          />
        </div>

        {/* Toggle «Сохранить ключ» */}
        <div
          onClick={() => !isLoading && setSaveKey(!saveKey)}
          style={{ display: 'flex', alignItems: 'center', gap: 11, cursor: isLoading ? 'default' : 'pointer', marginBottom: 28, userSelect: 'none' }}
        >
          <Toggle checked={saveKey} onChange={setSaveKey} disabled={isLoading} />
          <span style={{ fontSize: 14, color: C.textSecondary }}>
            Сохранить ключ в связке устройства
          </span>
        </div>

        {/* Кнопка «Подключиться» */}
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
            {isLoading ? 'Подключаюсь…' : 'Подключиться'}
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
              <Button variant="ghostAccent" fullWidth onClick={handleChangeKey}>Изменить ключ</Button>
              <Button variant="primary" fullWidth onClick={handleRetry}>Повторить</Button>
            </div>
          </>
        )}

        {/* Подпись под кнопкой */}
        {!isError && (
          <div style={{ textAlign: 'center', fontSize: 12.5, color: C.textMuted, marginTop: 14 }}>
            Соединение шифруется end-to-end
          </div>
        )}
      </div>
    </div>
  );
};
