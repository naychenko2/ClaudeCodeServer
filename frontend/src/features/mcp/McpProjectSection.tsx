import { useEffect, useRef, useState } from 'react';
import { Plug } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FS, SP } from '../../lib/design';
import { Toggle } from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import type { McpServer, Project } from '../../types';
import { AccordionSection } from '../projects/dialogs/AccordionSection';

// Секция «MCP-серверы» в настройках проекта: тумблеры своих серверов в этом проекте.
// Allow-list модель: Project.McpServersOn — сервер не едет никуда, пока его здесь
// не включили явно. Встроенных серверов продукта тут нет: они доступны всегда.
// Своих серверов нет — секция не рисуется вовсе, чтобы не занимать место пустотой.
export function McpProjectSection({ project, onUpdated }: { project: Project; onUpdated?: (updated: Project) => void }) {
  const isMobile = useIsMobile();
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

  // Сводка статуса в заголовке аккордеона: сколько серверов включено. Считаем по
  // фактически отрисованным серверам (on может хранить ключи уже удалённых).
  const onCount = servers.filter(s => on.includes(s.key)).length;
  const summary = onCount === 0
    ? 'Ничего не включено'
    : isMobile ? `${onCount} из ${servers.length}` : `${onCount} из ${servers.length} включено`;

  return (
    <AccordionSection
      icon={Plug}
      title={isMobile ? 'MCP-серверы' : 'MCP-серверы в проекте'}
      summary={summary}
    >
      {servers.map((server, i) => (
        <div
          key={server.id}
          style={{
            display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
            padding: '8px 4px',
            // Контейнер аккордеона уже дал верхнюю границу тела — у первой строки
            // свою не рисуем, иначе двойная линия.
            borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
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
          padding: '7px 4px', fontSize: FS.sm, color: C.dangerText, background: C.dangerBg,
        }}>{err}</div>
      )}
    </AccordionSection>
  );
}
