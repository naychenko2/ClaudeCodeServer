import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, Plug } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ConfirmDialog, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import type { McpServer, Project } from '../../types';
import { AccordionSection } from '../projects/dialogs/AccordionSection';

// Серверы, которые требуют подтверждения включения (каталог + stdio + local-владелец):
// бэк возвращает массив {key, command} с ПОЛНОЙ строкой запуска — её показываем
// человеку перед повторным запросом с mcpCatalogConfirmed=true
interface PendingCatalogConfirm {
  // Хранится в state для повторного запроса с mcpCatalogConfirmed=true
  next: string[];
  servers: { key: string; command: string }[];
}

// Секция «MCP-серверы» в настройках проекта: тумблеры своих серверов в этом проекте.
// Allow-list модель: Project.McpServersOn — сервер не едет никуда, пока его здесь
// не включили явно. Встроенных серверов продукта тут нет: они доступны всегда.
// Своих серверов нет — секция не рисуется вовсе, чтобы не занимать место пустотой.
export function McpProjectSection({ project, onUpdated }: { project: Project; onUpdated?: (updated: Project) => void }) {
  const isMobile = useIsMobile();
  const [servers, setServers] = useState<McpServer[]>([]);
  const [on, setOn] = useState<string[]>(project.mcpServersOn ?? []);
  const [err, setErr] = useState('');
  // Подтверждение включения каталожной stdio-записи у local-владельца: бэк отдаёт
  // 400 с requiresConfirmation=true и полной строкой запуска в servers[].command.
  // Показываем диалог, по согласию шлём тот же запрос с mcpCatalogConfirmed=true
  const [pending, setPending] = useState<PendingCatalogConfirm | null>(null);
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

  // Дёргаем сервер ровно один раз: первый запрос — без флага (поймаем 400 с
  // requiresConfirmation), второй — с mcpCatalogConfirmed=true поверх уже
  // подтверждённого состояния. Бэкенд отличает «новые ключи» от «уже включённых»,
  // поэтому повторный запрос не провоцирует новый диалог
  const sendUpdate = (next: string[], confirmed: boolean, prev: string[], seq: number) =>
    api.projects.update(project.id, { mcpServersOn: next, mcpCatalogConfirmed: confirmed })
      .then(saved => {
        if (seqRef.current !== seq) return;
        setOn(saved.mcpServersOn ?? []);
        onUpdated?.(saved);
      })
      .catch((e: unknown) => {
        if (seqRef.current !== seq) return;
        const body = (e as { body?: { requiresConfirmation?: boolean; servers?: { key: string; command: string }[] } } | null)?.body;
        if (!confirmed && body?.requiresConfirmation && body.servers) {
          // Откатываем оптимистичный апдейт и открываем диалог с командами
          setOn(prev);
          setPending({ next, servers: body.servers });
          return;
        }
        setOn(prev);
        setErr(e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
      });

  const toggleOn = (key: string, checked: boolean) => {
    const next = checked ? [...on.filter(k => k !== key), key] : on.filter(k => k !== key);
    const prev = on;
    const seq = ++seqRef.current;
    setOn(next);
    setErr('');
    void sendUpdate(next, false, prev, seq);
  };

  const confirmPending = () => {
    if (!pending) return;
    const seq = ++seqRef.current;
    setOn(pending.next);
    setPending(null);
    void sendUpdate(pending.next, true, pending.next, seq);
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
      {pending && (
        <ConfirmDialog
          title="Включить каталожный сервер в проекте?"
          subtitle={
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
                <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.warningText} style={{ flexShrink: 0, marginTop: 2 }} />
                <span>
                  AI Home выполнит эту команду целиком на вашем компьютере. Код сервера
                  писали не мы, и после включения он попадёт в ходы проекта.
                </span>
              </div>
              <div style={{
                fontFamily: FONT.mono, fontSize: FS.xs, lineHeight: 1.45,
                background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.md,
                padding: '8px 10px', color: C.textPrimary,
                wordBreak: 'break-all', whiteSpace: 'pre-wrap',
              }}>
                {pending.servers.map(s => s.command).join('\n')}
              </div>
            </div>
          }
          confirmLabel="Включить"
          confirmVariant="danger"
          onConfirm={confirmPending}
          onCancel={() => setPending(null)}
        />
      )}
    </AccordionSection>
  );
}
