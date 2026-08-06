import { useState } from 'react';
import type { CSSProperties } from 'react';
import { Pencil, Plug, X } from 'lucide-react';
import { Button, EmptyState, IconButton, TextField, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { groupHeaderStyle } from '../../components/modelProvidersShared';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { accessSummary, mcpAuthLine, mcpStatusTone } from './useMcpData';
import type { McpData } from './useMcpData';
import type { McpBuiltinServer, McpServer } from '../../types';

// Вкладка «Серверы»: свои записи полноразмерными карточками, встроенные серверы
// продукта — компактными плитками (их восемь, карточками они похоронили бы свои под
// собой). Пустое состояние показано только там, где пусто: плитки остаются на месте.

const hintStyle: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px',
};

export function McpServerList({ data, onEdit, onAdd, onOpenAccess, onDelete }: {
  data: McpData;
  onEdit: (server: McpServer) => void;
  onAdd: () => void;
  onOpenAccess: () => void;
  onDelete: (server: McpServer) => void;
}) {
  const { servers, builtin } = data;
  const hasLegacy = servers?.some(s => s.source !== 'manual') ?? false;
  const builtinTiles = builtin.filter(t => t.builtin);
  const fromConfigTiles = builtin.filter(t => !t.builtin);

  if (servers === null) {
    return <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={groupHeaderStyle}>Ваши серверы</div>

      {servers.length === 0 ? (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xxl, padding: `${SP.sm}px 0`,
        }}>
          <EmptyState
            icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Своих серверов пока нет"
            subtitle="Пока подключены только встроенные серверы продукта. Добавьте свой — например, Miro или файловый сервер — и он станет доступен в чатах и персонам"
            action={<Button variant="primary" size="sm" onClick={onAdd}>Добавить сервер</Button>}
          />
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {servers.map(server => (
            <ServerCard
              key={server.id}
              server={server}
              data={data}
              onEdit={() => onEdit(server)}
              onOpenAccess={onOpenAccess}
              onDelete={() => onDelete(server)}
            />
          ))}
          {hasLegacy && (
            <div style={hintStyle}>
              «Наследство» — записи, пришедшие из готового конфига (вставка JSON или старый
              .mcp.json): они работают как раньше, здесь их можно проверить и доправить.
              Крестика удаления у них нет — сам сервер остался бы в исходном конфиге,
              который AI&nbsp;Home не редактирует.
            </div>
          )}
        </div>
      )}

      <div style={groupHeaderStyle}>Встроенные в AI Home</div>
      {builtinTiles.length === 0 ? (
        <div style={hintStyle}>
          Пока ничего не наблюдалось: статус встроенных серверов приезжает из первого же хода в чате.
        </div>
      ) : (
        <>
          <div style={{
            display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: SP.sm,
          }}>
            {builtinTiles.map(tile => <BuiltinTile key={tile.key} tile={tile} />)}
          </div>
          <div style={hintStyle}>
            Встроенные серверы — часть AI Home: у них только статус, удалить или выключить их отсюда нельзя.
          </div>
        </>
      )}

      {fromConfigTiles.length > 0 && (
        <>
          <div style={groupHeaderStyle}>Из общего конфига</div>
          <div style={{
            display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: SP.sm,
          }}>
            {fromConfigTiles.map(tile => <BuiltinTile key={tile.key} tile={tile} />)}
          </div>
          <div style={hintStyle}>
            Эти серверы подключены файлом конфигурации на этом сервере. Здесь виден только их
            статус — чтобы управлять ими из AI Home, добавьте их как свои на вкладке «Добавить».
          </div>
        </>
      )}
    </div>
  );
}

function ServerCard({ server, data, onEdit, onOpenAccess, onDelete }: {
  server: McpServer;
  data: McpData;
  onEdit: () => void;
  onOpenAccess: () => void;
  onDelete: () => void;
}) {
  const checking = !!data.checking[server.id];
  const tone = mcpStatusTone(server.status?.status);
  const needsAuth = !checking && server.status?.status === 'needs-auth';
  // OAuth есть только у http/sse (McpOAuthService.StartAsync отказывает stdio) — у него
  // «Войти» ведёт к настоящему действию, у stdio остаётся только правка настроек записи
  const canLogin = needsAuth && server.transport !== 'stdio';
  const oauthBusy = !!data.oauthPending[server.id];
  const oauthNotice = data.oauthNotice[server.id];
  const legacy = server.source !== 'manual';
  const personasOff = data.personasOffCount(server.key);
  const projectsOff = data.projectsOffCount(server.key);
  const target = server.transport === 'stdio'
    ? [server.command, ...server.args].filter(Boolean).join(' ')
    : server.url ?? '';

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: 6,
      opacity: server.enabled ? 1 : 0.62,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{
          width: 8, height: 8, borderRadius: R.full, flexShrink: 0,
          background: checking ? C.warning : tone.dot,
          animation: checking ? 'pulsedot 1s ease-in-out infinite' : undefined,
        }} />
        <div style={{
          display: 'flex', alignItems: 'center', gap: 7, minWidth: 0, flex: 1,
          fontSize: 13.5, fontWeight: 600, color: C.textHeading,
        }}>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {server.label || server.key}
          </span>
          <Badge tone={legacy ? 'legacy' : 'own'}>{legacy ? 'наследство' : 'свой'}</Badge>
        </div>
        <span style={{
          fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted,
          border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 6px', flexShrink: 0,
        }}>{server.transport}</span>
        <Toggle
          checked={server.enabled}
          onChange={v => data.setEnabled(server, v)}
          ariaLabel={`Включён: ${server.label || server.key}`}
        />
      </div>

      {target && (
        <div title={target} style={{
          fontFamily: FONT.mono, fontSize: 11, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{target}</div>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', fontSize: FS.sm }}>
        <span style={{ color: personasOff || projectsOff ? C.textSecondary : C.textMuted }}>
          {accessSummary(personasOff, projectsOff)}
        </span>
        <button
          type="button"
          onClick={onOpenAccess}
          style={{
            font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
            border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
          }}
        >Настроить</button>
      </div>

      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        gap: SP.sm, flexWrap: 'wrap', minHeight: 40,
      }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
          <div style={{ fontSize: FS.sm, color: checking ? C.textMuted : tone.text }}>
            {checking ? 'Проверяем… обычно 2–3 секунды' : mcpAuthLine(server, data.probes[server.id])}
          </div>
          {oauthNotice && (
            <div style={{ fontSize: FS.xs, color: C.warningText }}>{oauthNotice}</div>
          )}
          {needsAuth && !canLogin && (
            <div style={{ fontSize: FS.xs, color: C.textMuted }}>
              Проверьте ключ в{' '}
              <button
                type="button"
                onClick={onEdit}
                style={{
                  font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
                  border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
                }}
              >настройках сервера</button>
            </div>
          )}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
          {canLogin && (
            <Button
              variant="primary"
              size="md"
              loading={oauthBusy}
              disabled={checking}
              onClick={() => void data.startOAuth(server)}
            >Войти</Button>
          )}
          <Button variant="ghost" size="md" disabled={checking} onClick={() => void data.probe(server)}>
            Проверить
          </Button>
          <IconButton size="lg" title="Изменить" onClick={onEdit} disabled={checking}>
            <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
          {/* У наследства крестика нет: запись пришла из общего конфига, и её удаление
              здесь не убрало бы сам сервер — кнопка обещала бы то, чего продукт не делает */}
          {!legacy && (
            <IconButton size="lg" tone="danger" title="Удалить" onClick={onDelete} disabled={checking}>
              <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          )}
        </div>
      </div>

      {canLogin && oauthBusy && (
        <ManualOAuthCode server={server} data={data} />
      )}
    </div>
  );
}

// Запасной путь входа: часть серверов принимает только http://127.0.0.1:PORT/… и до
// нашего callback код не доезжает — окно провайдера открыто, но повисает на чужом адресе.
// Свёрнуто по умолчанию: 95% входов закрываются сами через postMessage, поле — не для них.
function ManualOAuthCode({ server, data }: { server: McpServer; data: McpData }) {
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);

  if (!open) {
    return (
      <button
        type="button"
        onClick={() => setOpen(true)}
        style={{
          alignSelf: 'flex-start', font: 'inherit', fontSize: FS.xs, color: C.textMuted,
          background: 'transparent', border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
        }}
      >Окно не вернулось само? Вставить код вручную</button>
    );
  }

  const submit = async () => {
    if (!code.trim() || busy) return;
    setBusy(true);
    try {
      // На отказе оставляем поле открытым с введённым кодом — ошибка уже видна строкой
      // выше (oauthNotice), а закрытие формы читалось бы как «получилось»
      if (await data.completeOAuth(server, code)) { setCode(''); setOpen(false); }
    } finally { setBusy(false); }
  };

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <TextField value={code} onChange={setCode} mono placeholder="Код из адресной строки окна входа" onEnter={submit} />
      </div>
      <Button variant="ghost" size="md" disabled={!code.trim() || busy} loading={busy} onClick={() => void submit()}>
        Завершить
      </Button>
    </div>
  );
}

function BuiltinTile({ tile }: { tile: McpBuiltinServer }) {
  const tone = mcpStatusTone(tile.status?.status);
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
      padding: '9px 11px', display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0,
    }}>
      <div style={{
        fontSize: 12.5, fontWeight: 600, color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{tile.key}</div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: FS.xs, color: C.textSecondary }}>
        <span style={{ width: 6, height: 6, borderRadius: R.full, background: tone.dot, flexShrink: 0 }} />
        {tone.label}
      </div>
    </div>
  );
}

function Badge({ tone, children }: { tone: 'own' | 'legacy'; children: string }) {
  const skin = tone === 'legacy'
    ? { background: C.warningBg, color: C.warningText }
    : { background: C.bgSelected, color: C.textSecondary };
  return (
    <span style={{
      fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: 9,
      whiteSpace: 'nowrap', flexShrink: 0, ...skin,
    }}>{children}</span>
  );
}
