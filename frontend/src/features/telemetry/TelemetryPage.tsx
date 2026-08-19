// Раздел «Телеметрия» как полноценная страница-вкладка хаба (TABLESS, как «Аналитика
// токенов»): главная шапка HubHeader сверху, под ней — шапка раздела с переключателем
// вкладок «Инциденты | SigNoz».
//
// «Инциденты» — наш разбор: список горящих и недавних + досье по выбранному. Дефолтная
// вкладка именно она: админ приходит сюда по уведомлению об алерте, и открывать ему
// сразу тот самый SigNoz, от которого фича уводит (да ещё десктопный дашборд на
// телефоне), — значит не решать задачу.
// «SigNoz» — прежний iframe через same-origin проброс /telemetry-proxy/ (бэкенд
// форвардит на локальный SigNoz). Проброс выключен или SigNoz не отвечает — заглушка
// «настрой, администратор», решение по /api/telemetry/status (а не по ненадёжному
// iframe onerror).
import { useEffect, useRef, useState } from 'react';
import { Gauge, Unplug } from 'lucide-react';
import type { AuthState } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { TAB_LABELS } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { EmptyState } from '../../components/ui/EmptyState';
import { MiniSegment } from '../home/WidgetCard';
import { C, CONTENT_MAX_W, FONT, FS, ISLAND, SHADOW, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { api } from '../../lib/api';
import { IncidentsPanel } from './IncidentsPanel';
import { takePendingIncident, INCIDENT_OPEN_EVENT } from './incidentLink';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  // Крестик/Esc — уйти с раздела (возврат на дашборд). Раздел заполняет экран, отдельного
  // крестика нет: выход через таб или логотип «Домой», как у «Аналитики токенов».
  onClose: () => void;
  // Переход в затронутый чат из карточки инцидента (каналы проектного и внепроектного
  // чата разные — решает App)
  onOpenChat?: (chatId: string, projectId?: string | null) => void;
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
type Tab = 'incidents' | 'signoz';

export function TelemetryPage({ auth, onLogout, onHubTab, onOpenChat }: Props) {
  const isMobile = useIsMobile();
  const [status, setStatus] = useState<Status | null>(null);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<Tab>('incidents');
  const [pendingIncident, setPendingIncident] = useState<string | null>(() => takePendingIncident());
  const iframeRef = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    let alive = true;
    api.telemetry.status()
      .then(s => { if (alive) setStatus(s); })
      .catch(() => { if (alive) setStatus(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  // Диплинк в УЖЕ открытый раздел: switchHubTab не перемонтирует страницу, поэтому
  // pending читается ещё и по событию — иначе тап по уведомлению не делал бы ничего,
  // а инцидент всплыл бы при следующем заходе, когда он уже неактуален
  useEffect(() => {
    const open = () => {
      const fingerprint = takePendingIncident();
      setTab('incidents');
      if (fingerprint) setPendingIncident(fingerprint);
    };
    window.addEventListener(INCIDENT_OPEN_EVENT, open);
    return () => window.removeEventListener(INCIDENT_OPEN_EVENT, open);
  }, []);

  const ready = !!status?.configured && !!status?.reachable;
  const proxyPath = status?.proxyPath ?? '/telemetry-proxy/';
  // Cookie и тема — до того, как iframe (и его сабресурсы) уйдут на проброс
  if (ready && tab === 'signoz') { ensureTelemetryCookie(); ensureSignozLightDefault(); }

  const header = (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.md, minHeight: 48,
      padding: `${SP.sm}px ${isMobile ? SP.lg : SP.lg}px`,
      background: C.bgInset, borderBottom: `1px solid ${C.borderLight}`, flexWrap: 'wrap',
      borderRadius: isMobile ? 0 : `${ISLAND.radius}px ${ISLAND.radius}px 0 0`,
    }}>
      <span style={{
        fontFamily: FONT.serif, fontSize: isMobile ? FS.lg : 17, fontWeight: 700,
        color: C.textHeading, whiteSpace: 'nowrap',
      }}>
        {TAB_LABELS.telemetry}
      </span>
      {/* MiniSegment (белая плашка), а не оранжевый сегмент: рядом в карточке живёт
          primary «Объяснить», и второй акцент на экране спорил бы с ним */}
      <MiniSegment
        value={tab}
        options={[
          { value: 'incidents' as const, label: 'Инциденты' },
          { value: 'signoz' as const, label: 'SigNoz' },
        ]}
        onChange={setTab}
      />
    </div>
  );

  const signoz = loading ? (
    <div style={{
      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
      color: C.textMuted, fontSize: FS.md,
    }}>
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
  );

  return (
    <PageCanvas>
      <HubHeader
        value="telemetry" onTab={onHubTab} auth={auth} onLogout={onLogout}
        // Иконка «Открыть в новом окне» в шапке — только когда SigNoz доступен
        onOpenExternal={ready ? () => window.open(proxyPath, '_blank', 'noopener') : undefined}
      />
      <div style={{
        flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden',
        // Остров — только вкладке «Инциденты»: iframe рисует свою страницу целиком,
        // скруглять и утапливать её незачем (как «Сервисы»)
        ...(isMobile ? {} : {
          margin: `0 ${ISLAND.pad}px ${ISLAND.pad}px`,
          maxWidth: CONTENT_MAX_W, width: '100%', alignSelf: 'center',
        }),
        ...(isMobile || tab === 'signoz' ? {} : {
          background: C.bgPanel, border: `1px solid ${ISLAND.border}`,
          borderRadius: ISLAND.radius, boxShadow: SHADOW.island,
        }),
      }}>
        {header}
        {tab === 'incidents'
          ? (
            <IncidentsPanel
              status={status ? { configured: status.configured, reachable: status.reachable } : null}
              statusLoading={loading}
              initialFingerprint={pendingIncident}
              onOpenChat={onOpenChat}
            />
          )
          : signoz}
      </div>
    </PageCanvas>
  );
}
