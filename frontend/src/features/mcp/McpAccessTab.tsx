import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import { AlertTriangle, Folder, Info, MessageSquare, Plug, Plus, User } from 'lucide-react';
import { Button, EmptyState, Menu, Modal, TextField, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { groupHeaderStyle } from '../../lib/modelProvidersShared';
import { C, FONT, FS, MODAL_W, R, SHADOW, SP } from '../../lib/design';
import { navPush } from '../../lib/nav';
import { useIsMobile } from '../../lib/breakpoints';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { showToast } from '../../lib/toast';
import { personaGrantedFor, personaOffFor, plural } from './useMcpData';
import type { McpData } from './useMcpData';
import type { McpServer, Persona, Project } from '../../types';

// Вкладка «Доступ»: ветвится по флагу mcp-allowlist (per-user, дефолт выключен).
//  - выключен — прежняя матрица v1 (deny-list: сервер доступен везде, здесь только
//    исключения);
//  - включён — новый экран v2 (allow-list: сервер не едет никуда, пока не выдан явно).
// Обе поверхности честны о своей модели — настройки одной не действуют в другой.
export function McpAccessTab({ data, onClose, onAdd, onEdit }: {
  data: McpData; onClose: () => void; onAdd: () => void; onEdit: (server: McpServer) => void;
}) {
  const allowlist = useFeature(FLAGS.mcpAllowlist);
  return allowlist
    ? <AllowAccessView data={data} onClose={onClose} onAdd={onAdd} onEdit={onEdit} />
    : <LegacyAccessMatrix data={data} onClose={onClose} />;
}

// ============================================================================
// v1 (deny-list) — оставлена для владельцев без флага mcp-allowlist
// ============================================================================

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

function LegacyAccessMatrix({ data, onClose }: { data: McpData; onClose: () => void }) {
  const servers = data.servers ?? [];
  const { projects, personas } = data;

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

// ============================================================================
// v2 (allow-list) — сервер не едет никуда, пока не выдан явно
// ============================================================================

const allowHintStyle: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px',
};

function AllowAccessView({ data, onClose, onAdd, onEdit }: {
  data: McpData; onClose: () => void; onAdd: () => void; onEdit: (server: McpServer) => void;
}) {
  const { servers, projects, personas } = data;

  if (servers === null) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={allowHintStyle}>Смотрим, где ваши серверы включены…</div>
        {[0, 1].map(i => (
          <div key={i} style={{
            height: 118, borderRadius: R.xl, background: C.bgPanel,
            border: `1px solid ${C.border}`, flexShrink: 0,
          }} />
        ))}
      </div>
    );
  }

  if (servers.length === 0) {
    return (
      <EmptyState
        compact
        icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Своих серверов пока нет"
        subtitle="Выдавать доступ пока не к чему: встроенные серверы продукта доступны всегда."
        action={<Button variant="primary" size="sm" onClick={onAdd}>Добавить сервер</Button>}
      />
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={groupHeaderStyle}>Кому доступен сервер</div>
      <div style={allowHintStyle}>
        Свой сервер едет в ход, только если он включён в проекте этого чата или у персоны
        этого чата. Чат вне проекта — по отдельной настройке сервера «Чаты вне проектов».
      </div>
      {servers.map(server => (
        <ServerAccessCard key={server.id} server={server} data={data} projects={projects} personas={personas} onClose={onClose} onEdit={onEdit} />
      ))}
    </div>
  );
}

type AccessRow =
  | { kind: 'project'; project: Project }
  | { kind: 'outside' }
  | { kind: 'persona'; persona: Persona };

function buildRows(server: McpServer, projects: Project[], personas: Persona[]): AccessRow[] {
  const rows: AccessRow[] = projects
    .filter(p => (p.mcpServersOn ?? []).includes(server.key))
    .map(project => ({ kind: 'project' as const, project }));
  if (server.allowOutsideProjects) rows.push({ kind: 'outside' });
  personas
    .filter(p => personaGrantedFor(p, server.key))
    .forEach(persona => rows.push({ kind: 'persona', persona }));
  return rows;
}

function summaryLabel(rows: AccessRow[]): string {
  const projectsCount = rows.filter(r => r.kind === 'project').length;
  const outside = rows.some(r => r.kind === 'outside');
  const personasCount = rows.filter(r => r.kind === 'persona').length;
  const parts: string[] = [];
  if (projectsCount > 0) parts.push(`${projectsCount} ${plural(projectsCount, 'проект', 'проекта', 'проектов')}`);
  if (outside) parts.push('чаты вне проектов');
  if (personasCount > 0) parts.push(`${personasCount} ${plural(personasCount, 'персона', 'персоны', 'персон')}`);
  return parts.join(' · ');
}

function ServerAccessCard({ server, data, projects, personas, onClose, onEdit }: {
  server: McpServer; data: McpData; projects: Project[]; personas: Persona[]; onClose: () => void;
  onEdit: (server: McpServer) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const isMobile = useIsMobile();

  const rows = buildRows(server, projects, personas);
  const disabled = server.enabled === false;
  const legacy = server.source !== 'manual';
  const grantedProjectIds = new Set(rows.filter(r => r.kind === 'project').map(r => (r as { project: Project }).project.id));

  const visibleRows = expanded ? rows : rows.slice(0, 5);
  const hiddenCount = rows.length - visibleRows.length;

  const openPicker = (e: MouseEvent) => {
    setAnchor(e.currentTarget.getBoundingClientRect());
  };

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      overflow: 'hidden', flexShrink: 0, opacity: disabled ? 0.62 : 1,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '11px 13px', flexWrap: 'wrap' }}>
        <span title={server.label || server.key} style={{
          fontSize: 13.5, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{server.label || server.key}</span>
        <ServerBadge tone={legacy ? 'legacy' : 'own'}>{legacy ? 'наследство' : 'свой'}</ServerBadge>
        <span style={{
          fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted,
          border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 6px', flexShrink: 0,
        }}>{server.transport}</span>
      </div>

      <div style={{ padding: '0 13px 11px' }}>
        {disabled ? (
          <StatusPill tone="warning">Выключен целиком</StatusPill>
        ) : rows.length === 0 ? (
          <StatusPill tone="neutral">Доступ не выдан</StatusPill>
        ) : (
          <StatusPill tone="success">{`Работает: ${summaryLabel(rows)}`}</StatusPill>
        )}
      </div>

      {disabled && rows.length > 0 && (
        <div style={{
          margin: '0 13px 11px', padding: '8px 10px', borderRadius: R.lg,
          background: C.warningBg, fontSize: FS.xs, color: C.warningText, lineHeight: 1.4,
        }}>
          Выданный ниже доступ сейчас ни на что не влияет — сервер выключен в личном реестре.
        </div>
      )}

      {rows.length === 0 ? (
        <div style={{
          margin: '0 13px 11px', padding: SP.sm, borderRadius: R.lg,
          background: C.bgPanel, display: 'flex', gap: SP.sm,
        }}>
          <Info size={ICON_SIZE.md} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0, marginTop: 1 }} />
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
            Сервер подключён, но в ходы не едет. Он не включён ни в одном из {projects.length}{' '}
            {plural(projects.length, 'проекта', 'проектов', 'проектов')} и ни у одной из {personas.length}{' '}
            {plural(personas.length, 'персоны', 'персон', 'персон')}. Выберите, где он нужен: проект отдаёт
            сервер всем своим чатам и персонам, персона забирает его во все свои чаты.
          </div>
        </div>
      ) : (
        <div>
          {visibleRows.map(row => (
            <AccessRowView
              key={rowKey(row)}
              row={row}
              server={server}
              data={data}
              projects={projects}
              grantedProjectIds={grantedProjectIds}
              onClose={onClose}
              onEdit={onEdit}
            />
          ))}
          {hiddenCount > 0 && (
            <button
              type="button"
              onClick={() => setExpanded(true)}
              style={{
                display: 'block', width: '100%', textAlign: 'center', font: 'inherit',
                fontSize: FS.xs, fontWeight: 600, color: C.accent, background: 'transparent',
                border: 'none', borderTop: `1px solid ${C.borderLight}`, padding: '9px 13px', cursor: 'pointer',
              }}
            >Показать ещё {hiddenCount} {plural(hiddenCount, 'строку', 'строки', 'строк')}</button>
          )}
          {expanded && rows.length > 5 && (
            <button
              type="button"
              onClick={() => setExpanded(false)}
              style={{
                display: 'block', width: '100%', textAlign: 'center', font: 'inherit',
                fontSize: FS.xs, fontWeight: 600, color: C.accent, background: 'transparent',
                border: 'none', borderTop: `1px solid ${C.borderLight}`, padding: '9px 13px', cursor: 'pointer',
              }}
            >Свернуть</button>
          )}
        </div>
      )}

      <div style={{ borderTop: `1px solid ${C.borderLight}`, padding: '10px 13px' }}>
        <Button
          variant={rows.length === 0 ? 'primary' : 'ghost'}
          size="sm"
          fullWidth={rows.length === 0}
          onClick={openPicker}
          leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        >{rows.length === 0 ? 'Выдать доступ' : 'Выдать доступ ещё'}</Button>
      </div>

      {anchor && (
        isMobile ? (
          <Modal title={`Кому выдать доступ к «${server.label || server.key}»?`} onClose={() => setAnchor(null)} width={MODAL_W.form}>
            <GrantPickerBody server={server} data={data} projects={projects} personas={personas} onDone={() => setAnchor(null)} onEdit={onEdit} />
          </Modal>
        ) : (
          <Menu anchor={anchor} onClose={() => setAnchor(null)} minWidth={330} maxWidth={330} maxHeight={420}>
            <div style={{ padding: 8, display: 'flex', flexDirection: 'column', gap: 8 }}>
              <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, padding: '2px 3px' }}>
                Кому выдать доступ к «{server.label || server.key}»?
              </div>
              <GrantPickerBody server={server} data={data} projects={projects} personas={personas} onDone={() => setAnchor(null)} onEdit={onEdit} />
            </div>
          </Menu>
        )
      )}
    </div>
  );
}

function rowKey(row: AccessRow): string {
  if (row.kind === 'project') return `project:${row.project.id}`;
  if (row.kind === 'outside') return 'outside';
  return `persona:${row.persona.id}`;
}

function AccessRowView({ row, server, data, projects, grantedProjectIds, onClose, onEdit }: {
  row: AccessRow; server: McpServer; data: McpData; projects: Project[];
  grantedProjectIds: Set<string>; onClose: () => void; onEdit: (server: McpServer) => void;
}) {
  const rowStyle: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: SP.sm, padding: '9px 13px',
    borderTop: `1px solid ${C.borderLight}`, flexWrap: 'wrap',
  };
  const iconBoxStyle: CSSProperties = {
    width: 26, height: 26, borderRadius: R.md, flexShrink: 0,
    background: C.successBg, color: C.successText,
    display: 'flex', alignItems: 'center', justifyContent: 'center',
  };
  const titleStyle: CSSProperties = {
    fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
  };
  const subtitleStyle: CSSProperties = { fontSize: FS.xs, color: C.textMuted };

  if (row.kind === 'project') {
    const { project } = row;
    const covered = data.personas.filter(p => p.projectId === project.id).length;
    return (
      <div style={rowStyle}>
        <span style={iconBoxStyle}><Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div title={project.name} style={titleStyle}>{project.name}</div>
          <div style={subtitleStyle}>
            Проект · все его чаты{covered > 0 ? ` и ${covered} ${plural(covered, 'персону', 'персоны', 'персон')}` : ''}
          </div>
        </div>
        <Button variant="ghost" size="sm" onClick={() => data.setProjectOn(project, server.key, false)}>Убрать</Button>
      </div>
    );
  }

  if (row.kind === 'outside') {
    return (
      <div style={rowStyle}>
        <span style={iconBoxStyle}><MessageSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={titleStyle}>Чаты вне проектов</div>
          <div style={subtitleStyle}>Чаты, у которых нет проекта — включая чаты с персонами</div>
        </div>
        <Button variant="ghost" size="sm" onClick={() => void data.save(server.id, { allowOutsideProjects: false })}>Убрать</Button>
      </div>
    );
  }

  const { persona } = row;
  const coveredViaProject = !!persona.projectId && grantedProjectIds.has(persona.projectId);
  const project = persona.projectId ? projects.find(p => p.id === persona.projectId) : undefined;
  const roBlocked = persona.access === 'readOnly' && !server.allowReadOnlyPersonas;
  return (
    <div style={rowStyle}>
      <span style={iconBoxStyle}><User size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div title={persona.name} style={titleStyle}>{persona.name}</div>
        <div style={{ ...subtitleStyle, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
          <span>Персона{persona.role ? ` · ${persona.role}` : ''}</span>
          {coveredViaProject && (
            <span title={`Уже доступен через проект «${project?.name ?? ''}»`} style={{
              fontSize: 10, fontWeight: 700, padding: '1px 6px', borderRadius: R.pill,
              background: C.bgSelected, color: C.textMuted, whiteSpace: 'nowrap',
            }}>есть через проект</span>
          )}
        </div>
        {roBlocked && (
          <div style={{
            marginTop: 3, display: 'flex', gap: 5, alignItems: 'flex-start',
            fontSize: FS.xs, color: C.warningText, lineHeight: 1.4,
          }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
            <span>
              Не приедет: у персоны профиль «Только чтение», а на сервере не разрешён доступ таким
              персонам.{' '}
              <button
                type="button"
                onClick={() => onEdit(server)}
                style={{
                  font: 'inherit', color: 'inherit', background: 'transparent', border: 'none',
                  padding: 0, cursor: 'pointer', textDecoration: 'underline',
                }}
              >Разрешить в настройках сервера →</button>
            </span>
          </div>
        )}
      </div>
      <button
        type="button"
        onClick={() => { navPush({ screen: 'personas', persona: persona.id }); onClose(); }}
        style={{
          font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
          border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline', whiteSpace: 'nowrap',
        }}
      >Студия →</button>
      <Button variant="ghost" size="sm" onClick={() => void data.revokePersona(persona, server.key)}>Убрать</Button>
    </div>
  );
}

// === Пикер выдачи: один на две оси (сегменты «Проекты | Персоны») ===

function GrantPickerBody({ server, data, projects, personas, onDone, onEdit }: {
  server: McpServer; data: McpData; projects: Project[]; personas: Persona[]; onDone: () => void;
  onEdit: (server: McpServer) => void;
}) {
  const [segment, setSegment] = useState<'projects' | 'personas'>('projects');
  const [query, setQuery] = useState('');
  const isMobile = useIsMobile();

  const grantedProjectIds = new Set(projects.filter(p => (p.mcpServersOn ?? []).includes(server.key)).map(p => p.id));
  const q = query.trim().toLowerCase();
  const filteredProjects = q ? projects.filter(p => p.name.toLowerCase().includes(q)) : projects;
  const filteredPersonas = q
    ? personas.filter(p => p.name.toLowerCase().includes(q) || (p.role ?? '').toLowerCase().includes(q))
    : personas;

  const togglePersona = (persona: Persona) => {
    const granted = personaGrantedFor(persona, server.key);
    if (granted) { void data.revokePersona(persona, server.key); return; }
    const coveredViaProject = !!persona.projectId && grantedProjectIds.has(persona.projectId);
    const coveringProject = coveredViaProject ? projects.find(p => p.id === persona.projectId) : undefined;
    void data.grantPersona(persona, server.key).then(() => {
      if (coveringProject) {
        showToast(
          'Доступ выдан',
          `Доступ у персоны «${persona.name}» уже был через проект «${coveringProject.name}» — теперь он не пропадёт, даже если убрать проект.`,
        );
      }
    });
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minWidth: 0 }}>
      <div style={{ display: 'flex', background: C.bgPanel, borderRadius: R.lg, padding: 3, gap: 3 }}>
        {(['projects', 'personas'] as const).map(s => (
          <button
            key={s}
            type="button"
            onClick={() => setSegment(s)}
            style={{
              flex: 1, padding: '6px 10px', borderRadius: R.md, border: 'none',
              background: segment === s ? C.bgWhite : 'transparent',
              boxShadow: segment === s ? SHADOW.thumb : 'none',
              fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
              color: segment === s ? C.textHeading : C.textSecondary, cursor: 'pointer',
            }}
          >{s === 'projects' ? 'Проекты' : 'Персоны'}</button>
        ))}
      </div>
      <TextField
        value={query}
        onChange={setQuery}
        autoFocus
        placeholder={segment === 'projects' ? `Поиск по ${projects.length} проектам` : `Поиск по ${personas.length} персонам`}
      />
      <div style={{ maxHeight: isMobile ? '46vh' : 264, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 2 }}>
        {segment === 'projects' ? (
          <>
            {!q && (
              <PickerRow
                icon={<MessageSquare size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title="Чаты вне проектов"
                granted={server.allowOutsideProjects}
                onClick={() => void data.save(server.id, { allowOutsideProjects: !server.allowOutsideProjects })}
              />
            )}
            {!q && filteredProjects.length > 0 && <div style={{ height: 1, background: C.borderLight, margin: '4px 0' }} />}
            {q && filteredProjects.length === 0 ? (
              <PickerEmpty label="Проверьте написание названия проекта" />
            ) : filteredProjects.map(project => (
              <PickerRow
                key={project.id}
                icon={<Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title={project.name}
                granted={grantedProjectIds.has(project.id)}
                onClick={() => data.setProjectOn(project, server.key, !grantedProjectIds.has(project.id))}
              />
            ))}
          </>
        ) : (
          filteredPersonas.length === 0 ? (
            <PickerEmpty label="Проверьте написание имени персоны" />
          ) : filteredPersonas.map(persona => {
            const granted = personaGrantedFor(persona, server.key);
            const covered = !granted && !!persona.projectId && grantedProjectIds.has(persona.projectId);
            const project = persona.projectId ? projects.find(p => p.id === persona.projectId) : undefined;
            const roBlocked = persona.access === 'readOnly' && !server.allowReadOnlyPersonas;
            return (
              <PickerRow
                key={persona.id}
                icon={<User size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title={persona.name}
                subtitle={`${persona.role || 'без роли'} · ${project ? project.name : 'глобальная'}`}
                granted={granted}
                covered={covered}
                warning={roBlocked ? 'Не приедет: профиль «Только чтение» не разрешён на сервере' : undefined}
                onClick={() => togglePersona(persona)}
              />
            );
          })
        )}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
        {segment === 'projects'
          ? 'Выданное помечено — повторный клик уберёт доступ. Проект отдаёт сервер всем своим чатам и персонам.'
          : 'Выданное помечено — повторный клик уберёт доступ.'}
      </div>
      {segment === 'personas' && !server.allowReadOnlyPersonas && personas.some(p => p.access === 'readOnly') && (
        <div style={{ display: 'flex', gap: 5, alignItems: 'flex-start', fontSize: FS.xs, color: C.warningText, lineHeight: 1.4 }}>
          <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
          <span>
            Персонам с профилем «Только чтение» сервер не приедет, пока это не разрешено на сервере.{' '}
            <button
              type="button"
              onClick={() => { onEdit(server); onDone(); }}
              style={{
                font: 'inherit', color: 'inherit', background: 'transparent', border: 'none',
                padding: 0, cursor: 'pointer', textDecoration: 'underline',
              }}
            >Разрешить в настройках сервера →</button>
          </span>
        </div>
      )}
      {isMobile && (
        <Button variant="ghost" size="sm" fullWidth onClick={onDone}>Готово</Button>
      )}
    </div>
  );
}

function PickerRow({ icon, title, subtitle, granted, covered, warning, onClick }: {
  icon: ReactNode; title: string; subtitle?: string; granted: boolean; covered?: boolean;
  warning?: string; onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, width: '100%', textAlign: 'left',
        background: 'transparent', border: 'none', borderRadius: R.md, padding: '7px 8px', cursor: 'pointer',
        font: 'inherit',
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      <span style={{ color: C.textMuted, flexShrink: 0, display: 'flex' }}>{icon}</span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span title={title} style={{
          display: 'block', fontSize: FS.sm, color: C.textPrimary,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{title}</span>
        {subtitle && (
          <span style={{
            display: 'block', fontSize: FS.xs, color: C.textMuted,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{subtitle}</span>
        )}
        {warning && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 3, fontSize: 10, color: C.warningText, marginTop: 1 }}>
            <AlertTriangle size={10} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{warning}</span>
          </span>
        )}
      </span>
      {granted ? (
        <span style={{
          fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.pill, flexShrink: 0,
          background: C.successBg, color: C.successText,
        }}>выдан</span>
      ) : covered ? (
        <span style={{
          fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.pill, flexShrink: 0,
          background: C.bgSelected, color: C.textMuted,
        }}>есть через проект</span>
      ) : null}
    </button>
  );
}

function PickerEmpty({ label }: { label: string }) {
  return (
    <div style={{ padding: '14px 8px', textAlign: 'center', fontSize: FS.sm, color: C.textMuted }}>
      Ничего не нашлось
      <div style={{ fontSize: FS.xs, marginTop: 2 }}>{label}</div>
    </div>
  );
}

function StatusPill({ tone, children }: { tone: 'neutral' | 'success' | 'warning'; children: ReactNode }) {
  const skin = tone === 'success'
    ? { background: C.successBg, color: C.successText }
    : tone === 'warning'
      ? { background: C.warningBg, color: C.warningText }
      : { background: C.bgSelected, color: C.textSecondary };
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', fontSize: FS.xs, fontWeight: 700,
      padding: '3px 9px', borderRadius: R.pill, ...skin,
    }}>{children}</span>
  );
}

function ServerBadge({ tone, children }: { tone: 'own' | 'legacy'; children: string }) {
  const skin = tone === 'legacy'
    ? { background: C.warningBg, color: C.warningText }
    : { background: C.bgSelected, color: C.textSecondary };
  return (
    <span style={{
      fontSize: FS.xs, fontWeight: 700, padding: '1px 6px', borderRadius: R.pill,
      whiteSpace: 'nowrap', flexShrink: 0, ...skin,
    }}>{children}</span>
  );
}
