import { useEffect, useRef, useState } from 'react';
import { api } from '../../lib/api';
import { C, FS, R, SP } from '../../lib/design';
import { Toggle } from '../../components/ui';
import type { McpServer, Project } from '../../types';

// Секция «MCP-серверы» в настройках проекта: тумблеры своих серверов в этом проекте.
// Allow-list модель: Project.McpServersOn — сервер не едет никуда, пока его здесь
// не включили явно. Встроенных серверов продукта тут нет: они доступны всегда.
// Своих серверов нет — секция не рисуется вовсе, чтобы не занимать место пустотой.
export function McpProjectSection({ project, onUpdated }: { project: Project; onUpdated?: (updated: Project) => void }) {
  const [servers, setServers] = useState<McpServer[]>([]);
  const [on, setOn] = useState<string[]>(project.mcpServersOn ?? []);
  const [err, setErr] = useState('');
  // Тумблеры бьют пачкой — счётчик защищает от устаревшего ответа (тот же приём,
  // что в useMcpData и «Поставщиках моделей»)
  const seqRef = useRef(0);

  useEffect(() => {
    let cancelled = false;
    api.mcp.list()
      .then(list => { if (!cancelled) setServers(list); })
      .catch(() => { /* реестр недоступен — секции просто не будет */ });
    return () => { cancelled = true; };
  }, []);

  if (servers.length === 0) return null;

  const toggleOn = (key: string, checked: boolean) => {
    const next = checked ? [...on.filter(k => k !== key), key] : on.filter(k => k !== key);
    const prev = on;
    const seq = ++seqRef.current;
    setOn(next);
    setErr('');
    api.projects.update(project.id, { mcpServersOn: next })
      .then(saved => {
        if (seqRef.current !== seq) return;
        setOn(saved.mcpServersOn ?? []);
        onUpdated?.(saved);
      })
      .catch((e: unknown) => {
        if (seqRef.current !== seq) return;
        setOn(prev);
        setErr(e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
      });
  };

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
    }}>
      <div style={{
        padding: '10px 14px 6px', fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
      }}>
        MCP-серверы в проекте
        <span style={{ display: 'block', fontWeight: 400, fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
          Включённый здесь сервер поедет в ходы этого проекта — всем его чатам и персонам. В остальных проектах он не появится, пока не включён и там.
        </span>
      </div>
      {servers.map(server => (
        <div
          key={server.id}
          style={{
            display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
            padding: '9px 14px', borderTop: `1px solid ${C.borderLight}`,
            opacity: server.enabled ? 1 : 0.62,
          }}
          title={server.enabled ? undefined : 'Сервер выключен в личном реестре — в ходы он не едет нигде'}
        >
          <span style={{
            fontSize: 13, color: C.textPrimary, minWidth: 0,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {server.label || server.key}
          </span>
          <Toggle
            checked={on.includes(server.key)}
            onChange={v => toggleOn(server.key, v)}
            ariaLabel={`${server.label || server.key} в проекте`}
          />
        </div>
      ))}
      {err && (
        <div style={{
          padding: '7px 14px', fontSize: FS.sm, color: C.dangerText, background: C.dangerBg,
        }}>{err}</div>
      )}
    </div>
  );
}
