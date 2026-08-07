import type { CSSProperties } from 'react';
import { Toggle } from '../../components/ui';
import { groupHeaderStyle } from '../../lib/modelProvidersShared';
import { C, FS, R, SP } from '../../lib/design';
import { navPush } from '../../lib/nav';
import { personaOffFor } from './useMcpData';
import type { McpData } from './useMcpData';

// Вкладка «Доступ»: сверху правимая матрица «сервер × проект» (deny-list проекта),
// снизу read-only обзор «персона × сервер». Доступ персон правится в её студии —
// здесь только видно, кого заденет выключение или удаление сервера.

const wrapStyle: CSSProperties = {
  overflowX: 'auto', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite,
};

const thStyle: CSSProperties = {
  fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase',
  letterSpacing: '0.05em', textAlign: 'left', padding: '8px 12px',
  borderBottom: `1px solid ${C.borderLight}`, whiteSpace: 'nowrap',
};

const tdStyle: CSSProperties = {
  padding: '8px 12px', borderTop: `1px solid ${C.borderLight}`, fontSize: FS.sm,
  verticalAlign: 'middle',
};

const nameCellStyle: CSSProperties = {
  ...tdStyle, color: C.textHeading, fontWeight: 600, whiteSpace: 'nowrap',
};

const hintStyle: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px',
};

export function McpAccessMatrix({ data, onClose }: { data: McpData; onClose: () => void }) {
  const servers = data.servers ?? [];
  const { projects, personas } = data;

  // Доступ персоны правится в её студии — уводим туда и закрываем модалку
  const openStudio = (personaId: string) => {
    navPush({ screen: 'personas', persona: personaId });
    onClose();
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={groupHeaderStyle}>Проекты</div>
      <div style={hintStyle}>
        Сервер включён везде, пока его не выключили в конкретном проекте. Встроенные серверы
        продукта здесь не показаны — они доступны всегда.
      </div>
      {servers.length === 0 || projects.length === 0 ? (
        <div style={hintStyle}>
          {servers.length === 0
            ? 'Своих серверов пока нет — выключать нечего.'
            : 'Проектов пока нет: сервер доступен во всех чатах вне проектов.'}
        </div>
      ) : (
        <div style={wrapStyle}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 460 }}>
            <thead>
              <tr>
                <th style={thStyle}>Сервер</th>
                {projects.map(p => (
                  <th key={p.id} style={{ ...thStyle, textAlign: 'center' }}>{p.name}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {servers.map(server => (
                <tr key={server.id}>
                  <td style={nameCellStyle}>
                    {server.label || server.key}
                    {server.source !== 'manual' && (
                      <span style={{
                        display: 'block', fontWeight: 400, fontSize: 10.5, color: C.textMuted,
                      }}>наследство</span>
                    )}
                  </td>
                  {projects.map(project => {
                    const off = (project.mcpServersOff ?? []).includes(server.key);
                    return (
                      <td key={project.id} style={{ ...tdStyle, textAlign: 'center' }}>
                        <div style={{ display: 'flex', justifyContent: 'center' }}>
                          <Toggle
                            checked={!off}
                            width={34}
                            height={21}
                            onChange={v => data.setProjectOff(project, server.key, !v)}
                            ariaLabel={`${server.label || server.key} в проекте ${project.name}`}
                          />
                        </div>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div style={groupHeaderStyle}>Персоны</div>
      <div style={hintStyle}>
        Доступ персон настраивается в студии персоны — здесь только обзор, чтобы было видно,
        у кого сервер уже задействован, прежде чем его выключать или удалять.
      </div>
      {servers.length === 0 || personas.length === 0 ? (
        <div style={hintStyle}>
          {personas.length === 0 ? 'Персон пока нет.' : 'Своих серверов пока нет.'}
        </div>
      ) : (
        <div style={wrapStyle}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 460 }}>
            <thead>
              <tr>
                <th style={thStyle}>Персона</th>
                {servers.map(s => (
                  <th key={s.id} style={{ ...thStyle, textAlign: 'center' }}>{s.label || s.key}</th>
                ))}
                <th style={thStyle} />
              </tr>
            </thead>
            <tbody>
              {personas.map(persona => (
                <tr key={persona.id}>
                  <td style={nameCellStyle}>
                    {persona.name}
                    {persona.role && (
                      <span style={{
                        display: 'block', fontWeight: 400, fontSize: 10.5, color: C.textMuted,
                      }}>{persona.role}</span>
                    )}
                  </td>
                  {servers.map(server => {
                    const off = personaOffFor(persona, server.key);
                    return (
                      <td key={server.id} style={{ ...tdStyle, textAlign: 'center' }}>
                        {off
                          ? <span style={{ color: C.textMuted }}>—</span>
                          : <span style={{ color: C.successText, fontWeight: 700 }}>✓</span>}
                      </td>
                    );
                  })}
                  <td style={{ ...tdStyle, textAlign: 'center' }}>
                    <button
                      type="button"
                      onClick={() => openStudio(persona.id)}
                      style={{
                        font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
                        border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
                        whiteSpace: 'nowrap',
                      }}
                    >Студия →</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
