import React, { useState, useEffect } from 'react';
import type { AuthState } from '../types';
import { api } from '../lib/api';

interface LoginPageProps {
  onConnect: (auth: AuthState) => void;
}

type PageState = 'idle' | 'loading' | 'error';

export const LoginPage: React.FC<LoginPageProps> = ({ onConnect }) => {
  const [serverUrl, setServerUrl] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [saveKey, setSaveKey] = useState(false);
  const [pageState, setPageState] = useState<PageState>('idle');
  const [errorMessage, setErrorMessage] = useState('');
  const [focusedField, setFocusedField] = useState<'server' | 'key' | null>(null);

  useEffect(() => {
    const savedUrl = localStorage.getItem('cc_server_url');
    const savedKey = localStorage.getItem('cc_api_key');
    if (savedUrl) setServerUrl(savedUrl);
    if (savedKey) {
      setApiKey(savedKey);
      setSaveKey(true);
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
      }
      onConnect({ serverUrl, apiKey });
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'Не удалось подключиться к серверу';
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
  const isDisabled = isLoading || !serverUrl.trim() || !apiKey.trim();

  return (
    <div
        style={{
          minHeight: '100vh',
          background: '#F4F0E8',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          padding: '24px 20px',
          boxSizing: 'border-box',
          fontFamily: "'Hanken Grotesk', -apple-system, sans-serif",
        }}
      >
        <div style={{ width: '100%', maxWidth: 440 }}>
          {/* Логотип */}
          <div style={{ display: 'flex', justifyContent: 'center', marginBottom: 28 }}>
            <div
              style={{
                width: 54,
                height: 54,
                borderRadius: 15,
                background: '#D97757',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              <svg width="28" height="28" viewBox="0 0 28 28" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="3" y="5" width="22" height="18" rx="3" stroke="white" strokeWidth="1.8" fill="none" />
                <path d="M8 11L12 14L8 17" stroke="white" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
                <path d="M14 17H20" stroke="white" strokeWidth="1.8" strokeLinecap="round" />
              </svg>
            </div>
          </div>

          {/* Заголовок */}
          <h1
            style={{
              fontFamily: "'PT Serif', serif",
              fontSize: 34,
              fontWeight: 500,
              color: '#1A1612',
              margin: '0 0 8px',
              textAlign: 'center',
              lineHeight: 1.1,
              letterSpacing: '-0.01em',
            }}
          >
            Подключение к Claude Code Server
          </h1>

          {/* Подзаголовок */}
          <p
            style={{
              fontSize: 15,
              color: '#756B5E',
              margin: '0 0 30px',
              textAlign: 'center',
              lineHeight: 1.55,
            }}
          >
            Укажите адрес сервера и API-ключ, чтобы открыть проекты и чаты.
          </p>

          {/* Поле: Адрес сервера */}
          <div
            style={{
              display: 'flex', alignItems: 'center', background: '#FFFFFF',
              border: `1px solid ${focusedField === 'server' ? '#D97757' : '#E0D7C8'}`,
              borderRadius: 13, padding: '0 14px', height: 50, marginBottom: 16,
              boxShadow: focusedField === 'server' ? '0 0 0 3px rgba(217,119,87,0.14)' : 'none',
              transition: 'border-color 0.15s, box-shadow 0.15s',
            }}
          >
            <span style={{ color: focusedField === 'server' ? '#D97757' : '#9A8F7E', marginRight: 9, display: 'flex', flexShrink: 0, transition: 'color 0.15s' }}>
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <circle cx="12" cy="12" r="9" />
                <path d="M3 12h18M12 3a15 15 0 0 1 0 18M12 3a15 15 0 0 0 0 18" />
              </svg>
            </span>
            <input
              type="text"
              value={serverUrl}
              onChange={(e) => setServerUrl(e.target.value)}
              onFocus={() => setFocusedField('server')}
              onBlur={() => setFocusedField(null)}
              placeholder="https://example.com"
              disabled={isLoading}
              style={{ border: 'none', background: 'none', flex: 1, fontSize: 15, color: '#2A251F', fontFamily: "'JetBrains Mono', monospace", outline: 'none', opacity: isLoading ? 0.6 : 1 }}
            />
          </div>

          {/* Поле: API-ключ */}
          <div
            style={{
              display: 'flex', alignItems: 'center', background: '#FFFFFF',
              border: `1px solid ${focusedField === 'key' ? '#D97757' : '#E0D7C8'}`,
              borderRadius: 13, padding: '0 14px', height: 50, marginBottom: 18,
              boxShadow: focusedField === 'key' ? '0 0 0 3px rgba(217,119,87,0.14)' : 'none',
              transition: 'border-color 0.15s, box-shadow 0.15s',
            }}
          >
            <span style={{ color: focusedField === 'key' ? '#D97757' : '#9A8F7E', marginRight: 9, display: 'flex', flexShrink: 0, transition: 'color 0.15s' }}>
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <rect x="4" y="11" width="16" height="10" rx="2" />
                <path d="M8 11V7a4 4 0 0 1 8 0v4" />
              </svg>
            </span>
            <input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              onFocus={() => setFocusedField('key')}
              onBlur={() => setFocusedField(null)}
              placeholder="sk-ant-••••••••••••"
              disabled={isLoading}
              style={{ border: 'none', background: 'none', flex: 1, fontSize: 15, color: '#2A251F', fontFamily: "'JetBrains Mono', monospace", letterSpacing: '0.08em', outline: 'none', opacity: isLoading ? 0.6 : 1 }}
            />
          </div>

          {/* Toggle switch «Сохранить ключ» */}
          <div
            onClick={() => !isLoading && setSaveKey(!saveKey)}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 11,
              cursor: isLoading ? 'default' : 'pointer',
              marginBottom: 28,
              userSelect: 'none',
            }}
          >
            <div
              style={{
                width: 42,
                height: 25,
                borderRadius: 13,
                background: saveKey ? '#D97757' : '#D8CFBE',
                padding: 3,
                display: 'flex',
                alignItems: 'center',
                transition: 'background .2s',
                flexShrink: 0,
              }}
            >
              <div
                style={{
                  width: 19,
                  height: 19,
                  borderRadius: '50%',
                  background: '#FFFFFF',
                  boxShadow: '0 1px 3px rgba(0,0,0,0.2)',
                  marginLeft: saveKey ? 17 : 0,
                  transition: 'margin .2s',
                }}
              />
            </div>
            <span style={{ fontSize: 14, color: '#5C5246' }}>
              Сохранить ключ в связке устройства
            </span>
          </div>

          {/* Кнопка «Подключиться» */}
          {!isError && (
            <button
              onClick={handleConnect}
              disabled={isDisabled}
              style={{
                width: '100%',
                height: 52,
                background: '#D97757',
                color: '#FBF8F2',
                border: 'none',
                borderRadius: 14,
                fontSize: 16,
                fontWeight: 600,
                cursor: isDisabled ? 'not-allowed' : 'pointer',
                opacity: isDisabled ? 0.7 : 1,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 8,
                boxShadow: '0 4px 14px rgba(217,119,87,0.3)',
                transition: 'opacity 0.15s',
                fontFamily: "'Hanken Grotesk', -apple-system, sans-serif",
              }}
            >
              {isLoading ? (
                <span
                  style={{
                    display: 'inline-block',
                    width: 18,
                    height: 18,
                    border: '2.5px solid rgba(255,255,255,0.35)',
                    borderTop: '2.5px solid #FFFFFF',
                    borderRadius: '50%',
                    animation: 'cc-spin 0.8s linear infinite',
                  }}
                />
              ) : (
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M5 12h14M13 6l6 6-6 6" />
                </svg>
              )}
              {isLoading ? 'Подключаюсь…' : 'Подключиться'}
            </button>
          )}

          {/* Состояние ошибки */}
          {isError && (
            <>
              <div
                style={{
                  background: '#FFF0EE',
                  border: '1px solid #F5C6BF',
                  borderRadius: 11,
                  padding: '12px 14px',
                  display: 'flex',
                  alignItems: 'flex-start',
                  gap: 10,
                  marginBottom: 16,
                }}
              >
                <span style={{ fontSize: 16, flexShrink: 0, marginTop: 1 }}>⚠</span>
                <span style={{ fontSize: 13.5, color: '#B03010', lineHeight: 1.45 }}>
                  {errorMessage}
                </span>
              </div>
              <div style={{ display: 'flex', gap: 10 }}>
                <button
                  onClick={handleChangeKey}
                  style={{
                    flex: 1,
                    background: 'transparent',
                    color: '#D97757',
                    border: '1.5px solid #D97757',
                    borderRadius: 11,
                    padding: '11px 8px',
                    fontSize: 14,
                    fontWeight: 600,
                    cursor: 'pointer',
                    fontFamily: "'Hanken Grotesk', -apple-system, sans-serif",
                  }}
                >
                  Изменить ключ
                </button>
                <button
                  onClick={handleRetry}
                  style={{
                    flex: 1,
                    background: '#D97757',
                    color: '#FBF8F2',
                    border: 'none',
                    borderRadius: 11,
                    padding: '11px 8px',
                    fontSize: 14,
                    fontWeight: 600,
                    cursor: 'pointer',
                    fontFamily: "'Hanken Grotesk', -apple-system, sans-serif",
                  }}
                >
                  Повторить
                </button>
              </div>
            </>
          )}

          {/* Подпись под кнопкой */}
          {!isError && (
            <div style={{ textAlign: 'center', fontSize: 12.5, color: '#9A8F7E', marginTop: 14 }}>
              Соединение шифруется end-to-end
            </div>
          )}
        </div>
      </div>
  );
};
