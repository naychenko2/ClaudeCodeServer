// Раздел «Телеметрия» как полноценная страница-вкладка хаба (TABLESS, как «Аналитика
// токенов»): главная шапка HubHeader сверху, контент раздела — под ней. Вход — через меню
// аватара, только у админов. Контент — SigNoz UI, встроенный через <iframe> с same-origin
// пробросом /telemetry-proxy/ (бэкенд форвардит на локальный SigNoz). Если проброс выключен
// или SigNoz не отвечает — заглушка «настрой, администратор», решение по /api/telemetry/status
// (а не по ненадёжному iframe onerror).
import { useEffect, useRef, useState } from 'react';
import { Gauge, Unplug } from 'lucide-react';
import type { AuthState } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { EmptyState } from '../../components/ui/EmptyState';
import { C, FONT, FS } from '../../lib/design';
import { api } from '../../lib/api';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  // Крестик/Esc — уйти с раздела (возврат на дашборд). Раздел заполняет экран iframe'ом,
  // отдельного крестика нет: выход через таб или логотип «Домой», как у «Аналитики токенов».
  onClose: () => void;
}

// Проброс /telemetry-proxy/* аутентифицируется по cookie cc_telemetry (iframe и его
// сабресурсы не могут слать Authorization). Ставим её из токена сессии перед загрузкой
// iframe — по образцу preview (cc_preview). Secure — только на https.
function ensureTelemetryCookie() {
  const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token');
  if (!token) return;
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = `cc_telemetry=${token}; path=/telemetry-proxy; SameSite=Strict${secure}`;
}

// Светлая тема SigNoz по умолчанию. У SigNoz нет серверной настройки темы — она живёт
// в localStorage браузера (ключ THEME, дефолт у него — тёмная). Проброс same-origin, поэтому
// его localStorage = наш; ключи не конфликтуют (тема CCS хранится под 'theme-mode'). Ставим
// ТОЛЬКО если пользователь ещё не выбирал — ручной выбор внутри SigNoz не перетираем.
function ensureSignozLightDefault() {
  try {
    if (!localStorage.getItem('THEME')) localStorage.setItem('THEME', 'light');
  } catch { /* localStorage недоступен — не критично */ }
}

type Status = { configured: boolean; reachable: boolean; proxyPath: string };

export function TelemetryPage({ auth, onLogout, onHubTab }: Props) {
  const [status, setStatus] = useState<Status | null>(null);
  const [loading, setLoading] = useState(true);
  const iframeRef = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    let alive = true;
    api.telemetry.status()
      .then(s => { if (alive) setStatus(s); })
      .catch(() => { if (alive) setStatus(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  const ready = !!status?.configured && !!status?.reachable;
  const proxyPath = status?.proxyPath ?? '/telemetry-proxy/';
  // Cookie и тема — до того, как iframe (и его сабресурсы) уйдут на проброс
  if (ready) { ensureTelemetryCookie(); ensureSignozLightDefault(); }

  return (
    <PageCanvas>
      <HubHeader
        value="telemetry" onTab={onHubTab} auth={auth} onLogout={onLogout}
        // Иконка «Открыть в новом окне» в шапке — только когда SigNoz доступен
        onOpenExternal={ready ? () => window.open(proxyPath, '_blank', 'noopener') : undefined}
      />
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {loading ? (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontSize: 14 }}>
            Загрузка…
          </div>
        ) : ready ? (
          <iframe
            ref={iframeRef}
            src={proxyPath}
            style={{
              flex: 1, border: 'none', width: '100%',
              // SigNoz UI рендерится как самостоятельная страница в iframe — нашей темой
              // не управляется, поэтому подложка нейтрально-белая
              // eslint-disable-next-line design/no-raw-color
              background: '#fff',
            }}
            title="SigNoz — телеметрия"
          />
        ) : (
          <EmptyState
            // Выключенный раздел — не авария: там, где телеметрию просто не
            // включали, аварийный треугольник пугал на ровном месте
            icon={status?.configured
              ? <Unplug size={26} strokeWidth={1.8} />
              : <Gauge size={26} strokeWidth={1.8} />}
            title={status?.configured ? 'SigNoz недоступен' : 'Телеметрия не настроена'}
            subtitle={
              status?.configured
                ? 'Стек наблюдаемости не отвечает — подними его и обнови страницу.'
                : 'Раздел выключен администратором.'
            }
            action={
              <div style={{
                fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
                background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: 8,
                padding: '8px 12px', textAlign: 'left', lineHeight: 1.6, maxWidth: 420,
              }}>
                {!status?.configured && <div>Telemetry:Ui:Enabled = true в appsettings.Local.json</div>}
                <div>docker compose -f docker-compose.observability.yml up -d</div>
                <div style={{ color: C.textMuted }}>подробнее — docs/observability.md</div>
              </div>
            }
          />
        )}
      </div>
    </PageCanvas>
  );
}
