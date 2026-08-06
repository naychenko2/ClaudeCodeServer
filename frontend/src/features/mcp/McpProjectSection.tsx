import { useEffect, useRef, useState } from 'react';
import { api } from '../../lib/api';
import { C, FS, R, SP } from '../../lib/design';
import { Toggle } from '../../components/ui';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import type { McpServer, Project } from '../../types';

// Секция «MCP-серверы» в настройках проекта: тумблеры включённости своих серверов
// в этом проекте (deny-list Project.McpServersOff — сервер едет в ход везде, пока его
// не выключили здесь). Встроенных серверов продукта тут нет: они доступны всегда.
// Своих серверов нет — секция не рисуется вовсе, чтобы не занимать место пустотой.
export function McpProjectSection({ project }: { project: Project }) {
  const [servers, setServers] = useState<McpServer[]>([]);
  const [off, setOff] = useState<string[]>(project.mcpServersOff ?? []);
  const [err, setErr] = useState('');
  // Тумблеры бьют пачкой — счётчик защищает от устаревшего ответа (тот же приём,
  // что в useMcpData и «Поставщиках моделей»)
  const seqRef = useRef(0);
  const enabled = useFeature(FLAGS.mcpRegistry);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    api.mcp.list()
      .then(list => { if (!cancelled) setServers(list); })
      .catch(() => { /* реестр недоступен — секции просто не будет */ });
    return () => { cancelled = true; };
  }, [enabled]);

  if (!enabled || servers.length === 0) return null;

  const toggle = (key: string, on: boolean) => {
    const next = on ? off.filter(k => k !== key) : [...off.filter(k => k !== key), key];
    const prev = off;
    const seq = ++seqRef.current;
    setOff(next);
    setErr('');
    api.projects.update(project.id, { mcpServersOff: next })
      .then(saved => { if (seqRef.current === seq) setOff(saved.mcpServersOff ?? []); })
      .catch((e: unknown) => {
        if (seqRef.current !== seq) return;
        setOff(prev);
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
          Выключенный здесь сервер не поедет в ходы этого проекта. В остальных проектах он останется доступен.
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
            checked={!off.includes(server.key)}
            onChange={v => toggle(server.key, v)}
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
