import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../../lib/api';
import { C } from '../../lib/design';
import { relTime } from '../../lib/gitFormat';
import type {
  McpBuiltinServer, McpProbeResult, McpServer, McpServerUpsert, Persona, Project,
} from '../../types';

// Владелец состояния раздела «MCP-серверы» — один на всю модалку: список серверов,
// наблюдения встроенных, проекты и персоны нужны сразу нескольким вкладкам
// («Серверы» подписывает карточку строкой «кому доступен», «Доступ» рисует те же данные
// матрицей). Паттерн — useProviderData из соседнего раздела «Поставщики моделей».

// === Статусы ===
// Набор значений задаёт бэк (McpServerStatuses): connected | failed | needs-auth | unknown.
// «Не проверялся» — не ошибка, а «ещё не спрашивали»: самый частый статус на старте.
export interface McpStatusTone {
  label: string;
  dot: string;
  text: string;
}

export function mcpStatusTone(status?: string | null): McpStatusTone {
  switch (status) {
    case 'connected': return { label: 'Работает', dot: C.success, text: C.successText };
    case 'needs-auth': return { label: 'Нужен вход', dot: C.warning, text: C.warningText };
    case 'failed': return { label: 'Не отвечает', dot: C.danger, text: C.dangerText };
    default: return { label: 'Не проверялся', dot: C.textMuted, text: C.textMuted };
  }
}

export function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

// Подпись под карточкой: статус + чем он подтверждён. Число инструментов знает только
// свежая проба (в сторе наблюдений его нет) — поэтому она передаётся отдельно.
export function mcpStatusLine(status: McpServer['status'], probe?: McpProbeResult): string {
  if (!status || status.status === 'unknown') return 'Не проверялся · нажмите «Проверить»';
  const tone = mcpStatusTone(status.status);
  const parts = [tone.label];
  const tools = probe?.toolCount;
  if (status.status === 'connected' && tools) {
    parts.push(`${tools} ${plural(tools, 'инструмент', 'инструмента', 'инструментов')}`);
  }
  if (status.error) parts.push(status.error);
  const when = relTime(status.observedAt);
  if (when) parts.push(status.source === 'probe' ? `проверен ${when}` : `наблюдался в чатах ${when}`);
  return parts.join(' · ');
}

// Строка «кому доступен» на карточке: главный сценарий фичи — человек, выдавший сервер
// одной персоне, через неделю должен понимать, почему у остальных он «не работает».
export function accessSummary(personasOff: number, projectsOff: number): string {
  if (personasOff === 0 && projectsOff === 0) return 'Доступен всем персонам и во всех проектах';
  const parts: string[] = [];
  if (personasOff > 0) {
    parts.push(`у ${personasOff} ${plural(personasOff, 'персоны', 'персон', 'персон')}`);
  }
  if (projectsOff > 0) {
    parts.push(`в ${projectsOff} ${plural(projectsOff, 'проекте', 'проектах', 'проектах')}`);
  }
  return `Выключен ${parts.join(' и ')}`;
}

// Персона выключила сервер Off-привязкой «mcp:<ключ>» (по умолчанию сервер ей доступен)
export function personaOffFor(persona: Persona, serverKey: string): boolean {
  const target = `mcp:${serverKey}`.toLowerCase();
  return (persona.bindings ?? []).some(b =>
    b.type === 'tool' && b.mode === 'off' && b.target.toLowerCase() === target);
}

export interface McpData {
  servers: McpServer[] | null;      // null — ещё грузится
  builtin: McpBuiltinServer[];
  projects: Project[];
  personas: Persona[];
  error: string | null;
  setError: (e: string | null) => void;
  // Идёт проба этой карточки: ожидание локальное, остальной список живёт дальше
  checking: Record<string, boolean>;
  probes: Record<string, McpProbeResult>;
  reload: () => void;
  setEnabled: (server: McpServer, enabled: boolean) => void;
  probe: (server: McpServer) => Promise<void>;
  remove: (server: McpServer) => Promise<void>;
  save: (id: string | null, data: McpServerUpsert) => Promise<McpServer>;
  importJson: (fragment: unknown) => Promise<{ created: McpServer[]; skipped: { key: string; reason: string }[] }>;
  setProjectOff: (project: Project, serverKey: string, off: boolean) => void;
  personasOffCount: (serverKey: string) => number;
  projectsOffCount: (serverKey: string) => number;
}

export function useMcpData(): McpData {
  const [servers, setServers] = useState<McpServer[] | null>(null);
  const [builtin, setBuiltin] = useState<McpBuiltinServer[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState<Record<string, boolean>>({});
  const [probes, setProbes] = useState<Record<string, McpProbeResult>>({});

  // Порядковый номер записи на сущность: тумблеры бьют пачкой (человек щёлкает подряд),
  // ответы приходят не в том порядке — без счётчика устаревший ответ перезаписывал бы
  // актуальное состояние. Тот же приём, что в handleSaveLayer «Поставщиков моделей».
  const saveSeq = useRef<Record<string, number>>({});

  const loadServers = useCallback(() => {
    api.mcp.list()
      .then(list => setServers(list))
      .catch(e => { setServers([]); setError(msg(e, 'Не удалось загрузить список серверов')); });
    api.mcp.builtin()
      .then(list => setBuiltin(list))
      .catch(() => { /* плитки встроенных не критичны — покажем без них */ });
  }, []);

  useEffect(() => {
    loadServers();
    api.projects.list().then(setProjects).catch(() => { /* матрица доступа покажет пусто */ });
    api.personas.list().then(setPersonas).catch(() => { /* обзор персон покажет пусто */ });
  }, [loadServers]);

  const replace = (updated: McpServer) =>
    setServers(list => list?.map(s => (s.id === updated.id ? updated : s)) ?? list);

  const setEnabled = (server: McpServer, enabled: boolean) => {
    const seq = (saveSeq.current[server.id] ?? 0) + 1;
    saveSeq.current[server.id] = seq;
    const prev = server;
    replace({ ...server, enabled });
    setError(null);
    api.mcp.setEnabled(server.id, enabled)
      .then(saved => { if (saveSeq.current[server.id] === seq) replace(saved); })
      .catch(e => {
        if (saveSeq.current[server.id] !== seq) return;
        replace(prev);
        setError(msg(e, 'Не удалось сохранить'));
      });
  };

  const probe = async (server: McpServer) => {
    if (checking[server.id]) return;
    setChecking(c => ({ ...c, [server.id]: true }));
    setError(null);
    try {
      const result = await api.mcp.probe(server.id);
      setProbes(p => ({ ...p, [server.id]: result }));
      // Наблюдение уже записано на бэке: тот же результат кладём в карточку,
      // чтобы статус и время проверки обновились без перезапроса списка
      replace({
        ...server,
        status: {
          status: result.status,
          observedAt: new Date().toISOString(),
          source: 'probe',
          sessionId: null,
          error: result.error ?? null,
        },
      });
    } catch (e) {
      setError(msg(e, 'Проверка не удалась'));
    } finally {
      setChecking(c => ({ ...c, [server.id]: false }));
    }
  };

  const remove = async (server: McpServer) => {
    await api.mcp.delete(server.id);
    setServers(list => list?.filter(s => s.id !== server.id) ?? list);
    // Сервер мог быть выключен в проектах — deny-list на бэке чистится вместе с записью
    // только у персон, поэтому список проектов перечитываем
    api.projects.list().then(setProjects).catch(() => { /* не критично */ });
  };

  const save = async (id: string | null, data: McpServerUpsert) => {
    const saved = id ? await api.mcp.update(id, data) : await api.mcp.create(data);
    setServers(list => {
      if (!list) return [saved];
      return id ? list.map(s => (s.id === saved.id ? saved : s)) : [...list, saved];
    });
    return saved;
  };

  const importJson = async (fragment: unknown) => {
    const result = await api.mcp.import(fragment);
    if (result.created.length > 0) loadServers();
    return result;
  };

  // Deny-list проекта правится тем же PUT /api/projects/{id}, что и остальные настройки
  const setProjectOff = (project: Project, serverKey: string, off: boolean) => {
    const current = project.mcpServersOff ?? [];
    const next = off
      ? [...current.filter(k => k !== serverKey), serverKey]
      : current.filter(k => k !== serverKey);
    const seq = (saveSeq.current[project.id] ?? 0) + 1;
    saveSeq.current[project.id] = seq;
    setProjects(list => list.map(p => (p.id === project.id ? { ...p, mcpServersOff: next } : p)));
    setError(null);
    api.projects.update(project.id, { mcpServersOff: next })
      .then(saved => {
        if (saveSeq.current[project.id] !== seq) return;
        setProjects(list => list.map(p => (p.id === saved.id ? saved : p)));
      })
      .catch(e => {
        if (saveSeq.current[project.id] !== seq) return;
        setProjects(list => list.map(p => (p.id === project.id ? project : p)));
        setError(msg(e, 'Не удалось сохранить'));
      });
  };

  const personasOffCount = (serverKey: string) =>
    personas.filter(p => personaOffFor(p, serverKey)).length;

  const projectsOffCount = (serverKey: string) =>
    projects.filter(p => (p.mcpServersOff ?? []).includes(serverKey)).length;

  return {
    servers, builtin, projects, personas, error, setError,
    checking, probes, reload: loadServers,
    setEnabled, probe, remove, save, importJson, setProjectOff,
    personasOffCount, projectsOffCount,
  };
}

function msg(e: unknown, fallback: string): string {
  return e instanceof Error && e.message ? e.message : fallback;
}
