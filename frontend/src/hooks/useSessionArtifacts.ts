import { useMemo } from 'react';
import type { ChatItem } from '../types';
import { useSession } from './useSession';
import { toRelative } from '../lib/paths';
import { workflowName } from '../lib/workflowMeta';
import { splitAgentResultTail, isBgLaunchResult } from '../lib/agentTail';

// Артефакты, собранные за сессию из ленты чата:
//  - файлы: измененные (file_changed/Write) + упомянутые путем в тексте ответа,
//  - планы (из ExitPlanMode) со статусами,
//  - задачи (TodoWrite либо TaskCreate/TaskUpdate) — актуальный чек-лист прогресса,
//  - ссылки, упомянутые в ответах и запросах WebFetch,
//  - агенты: субагенты (Task/Agent) + агенты внутри Workflow.

export interface ArtifactFile {
  path: string;       // внутри проекта — относительный (кликабельный); вне — абсолютный (копируется)
  added: number;      // суммарно добавлено строк
  removed: number;    // суммарно удалено строк
  hasDelta: boolean;  // пришло ли file_changed с дельтой (иначе путь только из tool_use/текста)
  changed: boolean;   // файл менялся (file_changed/Write) — иначе только упомянут в тексте
  external: boolean;  // путь вне корня проекта — открыть нельзя, клик копирует путь
}

export interface ArtifactLink {
  url: string;
  domain: string;
}

// Один план из ExitPlanMode + его судьба (по ответу пользователя на plan_review)
export type PlanStatus = 'approved' | 'rejected' | 'pending';
export interface PlanArtifact {
  plan: string;
  status: PlanStatus;
}

// Пункт todo-списка. Источника два (зависит от версии CLI):
//  - старый TodoWrite — каждый вызов шлет полный список, последний побеждает;
//  - новые TaskCreate/TaskUpdate — инкрементальные: create заводит задачу
//    (id из результата "Task #N created…"), update меняет статус/текст по taskId.
export interface TodoItem {
  content: string;
  status: string; // 'pending' | 'in_progress' | 'completed'
  activeForm?: string;
}

// Агент сессии: субагент (tool_use Task/Agent) либо агент внутри Workflow.
// Статус выводится из ленты: нет result и ход ещё идёт → running; isError → error.
export type AgentStatus = 'running' | 'done' | 'error';

// Один дочерний вызов инструмента субагента — строка мини-ленты в раскрытой карточке
export interface AgentToolCall {
  id: string;
  name: string;
  arg?: string;     // человекочитаемый аргумент (команда/путь/паттерн/запрос)
  running: boolean; // result ещё не пришёл (и ход не завершён)
  isError?: boolean;
}

export interface AgentArtifact {
  id: string;
  kind: 'subagent' | 'workflow';
  type?: string;       // subagent_type из input (для workflow не приходит)
  label: string;       // description либо первая строка prompt/summary
  status: AgentStatus;
  background: boolean; // запущен с run_in_background — result приходит сразу, статус завершения неизвестен
  toolCount: number;   // сколько инструментов вызвал (дочерние tool_use / tools у workflow-агента)
  lastTool?: string;   // последний дочерний инструмент — «чем занят сейчас»
  prompt?: string;     // полный промпт, выданный агенту родителем
  resultText?: string; // финальный ответ агента (у workflow — summary)
  calls?: AgentToolCall[];                    // дочерние вызовы по порядку (только субагенты)
  tools?: { name: string; count: number }[];  // сводка инструментов (только workflow)
  files?: string[];                           // затронутые файлы (только workflow)
}

// Группа агентов одного запуска Workflow — секция-аккордеон на вкладке «Агенты»
export interface WorkflowGroup {
  id: string;         // id tool_use Workflow
  name: string;       // meta.name из script либо имя сохранённого workflow
  agents: AgentArtifact[];
  doneCount: number;
  settled: boolean;   // workflow целиком завершён
}

export interface SessionArtifacts {
  files: ArtifactFile[];
  plans: PlanArtifact[];
  todos: TodoItem[];
  links: ArtifactLink[];
  agents: AgentArtifact[];      // одиночные субагенты (workflow-агенты — в workflows)
  workflows: WorkflowGroup[];
  notes: string[];              // заголовки заметок, созданных через mcp__notes__*
  executingTask: string | null; // заголовок задачи, если чат запущен для её выполнения
}

// Инструменты, которые меняют файл — путь берём из их аргументов как запасной источник
// (на случай, если file_changed не пришёл, например файл вне зоны watcher'а).
// write_file/edit_file — имена из историй старого DeepSeek-адаптера (replay старых чатов).
const WRITE_TOOLS = new Set(['Write', 'Edit', 'MultiEdit', 'NotebookEdit', 'write_file', 'edit_file']);
// Инструменты загрузки веба — источник ссылок (web_fetch — legacy старого DeepSeek-адаптера).
const WEBFETCH_TOOLS = new Set(['WebFetch', 'web_fetch']);
// MCP-инструменты управления заметками приложения.
const MCP_NOTES_NAMES = new Set(['mcp__notes__notes_create']);

const URL_RE = /https?:\/\/[^\s<>()[\]"'`]+/g;
// Хвостовая пунктуация, прилипающая к URL в тексте (точка в конце предложения, запятая, скобка)
const TRAILING = /[.,;:!?)\]}>'"]+$/;

function extractToolPath(input: unknown): string | null {
  if (!input || typeof input !== 'object') return null;
  const o = input as Record<string, unknown>;
  const p = o.file_path ?? o.notebook_path ?? o.path;
  return typeof p === 'string' && p.length > 0 ? p : null;
}

// Белый список расширений — чтобы не ловить версии (api/v2.0), даты и «слово/слово.ещё»
// из прозы. Длинные варианты раньше коротких (jsonc до json) для корректного \b.
const FILE_EXT = 'tsx?|jsx?|mjs|cjs|jsonc|json|csproj|css|cs|sln|mdx|markdown|md|txt|ya?ml|xml|html?|scss|less|pyi|py|go|rs|java|kts|kt|cpp|cxx|cc|hpp|hxx|sh|bash|ps1|psm1|bat|cmd|sql|toml|ini|cfg|conf|env|vue|svelte|astro|rb|php|lua|swift|dart|gradle|props|targets|proto|graphql|gql|tf';

// Путь в голом тексте: с разделителем (/ или \), без пробелов, известное расширение.
// Абсолютные Windows (C:\…), Unix (/…), относительные (src/…).
const PATH_RE = new RegExp(
  `(?:[A-Za-z]:[\\\\/]|\\.{0,2}[\\\\/])?(?:[\\w.@~-]+[\\\\/])+[\\w.@~-]+\\.(?:${FILE_EXT})\\b`,
  'gi',
);
// Тот же путь, но с пробелами — только для путей внутри `кавычек`/'…'/"…" (C:\My Project\a.ts).
const PATH_DELIMITED_RE = new RegExp(
  `^(?:[A-Za-z]:[\\\\/]|\\.{0,2}[\\\\/])?(?:[\\w.@~ -]+[\\\\/])+[\\w.@~ -]+\\.(?:${FILE_EXT})$`,
  'i',
);
// Содержимое `inline-code`, "строк" и 'строк' — кандидаты на путь с пробелами.
const DELIM_SPAN_RE = /`([^`\n]+)`|"([^"\n]+)"|'([^'\n]+)'/g;

// Все пути к файлам в тексте: делимитированные (допускают пробелы) + голые (строгий regex).
function extractTextFilePaths(text: string): string[] {
  const out: string[] = [];
  for (const m of text.matchAll(DELIM_SPAN_RE)) {
    const inner = (m[1] ?? m[2] ?? m[3] ?? '').trim();
    if (PATH_DELIMITED_RE.test(inner)) out.push(inner);
  }
  const bare = text.replace(URL_RE, ' ').match(PATH_RE);
  if (bare) out.push(...bare);
  return out;
}

// Классификация пути из текста: внутри проекта → относительный (external=false),
// вне проекта (абсолютный за пределами rootPath) → абсолютный (external=true),
// относительный с выходом за корень (../) → null (пропускаем).
function classifyTextPath(raw: string, rootPath: string): { path: string; external: boolean } | null {
  const isAbs = /^([A-Za-z]:[\\/]|[\\/])/.test(raw);
  const rel = toRelative(raw, rootPath);
  if (rel) return { path: rel, external: false };
  if (isAbs) return { path: raw.replace(/\\/g, '/'), external: true };
  return null; // относительный, но вне корня — мусор
}

// Собрать актуальный todo-список сессии из ленты чата. Понимает оба механизма CLI:
// старый TodoWrite (каждый вызов несет полный список — последний побеждает) и новые
// TaskCreate/TaskUpdate (инкрементальные). Экспортируется отдельно от computeArtifacts:
// по нему же ChatPanel рисует карточку чек-листа в ленте.
export function computeTodos(items: ChatItem[]): TodoItem[] {
  let todos: TodoItem[] = [];          // из TodoWrite (полный список, последний побеждает)
  const tasks = new Map<string, TodoItem>(); // из TaskCreate/TaskUpdate, ключ — taskId
  let taskAutoId = 0; // запасная нумерация, если id не удалось достать из результата

  for (const it of items) {
    if (it.kind !== 'tool_use') continue;
    if (it.name === 'TodoWrite') {
      const t = (it.input as { todos?: unknown } | null)?.todos;
      if (Array.isArray(t)) {
        const parsed = t.filter((x): x is TodoItem =>
          !!x && typeof x === 'object' && typeof (x as TodoItem).content === 'string');
        if (parsed.length) todos = parsed;
      }
    } else if (it.name === 'TaskCreate') {
      const o = it.input as { subject?: unknown; description?: unknown; activeForm?: unknown } | null;
      const subject = typeof o?.subject === 'string' && o.subject
        ? o.subject
        : typeof o?.description === 'string' ? o.description : '';
      if (subject) {
        taskAutoId += 1;
        // Сервер отвечает "Task #N created successfully: …" — берем id оттуда;
        // пока результата нет (стрим) — порядковый номер (в свежей сессии совпадает)
        const m = typeof it.result === 'string' ? it.result.match(/#(\d+)/) : null;
        const id = m ? m[1] : String(taskAutoId);
        tasks.set(id, {
          content: subject,
          status: 'pending',
          activeForm: typeof o?.activeForm === 'string' ? o.activeForm : undefined,
        });
      }
    } else if (it.name === 'TaskUpdate') {
      const o = it.input as { taskId?: unknown; status?: unknown; subject?: unknown } | null;
      const id = typeof o?.taskId === 'string' ? o.taskId
        : typeof o?.taskId === 'number' ? String(o.taskId) : null;
      const ex = id ? tasks.get(id) : undefined;
      if (id && ex) {
        if (o?.status === 'cancelled' || o?.status === 'deleted') {
          tasks.delete(id);
        } else {
          if (typeof o?.subject === 'string' && o.subject) ex.content = o.subject;
          if (typeof o?.status === 'string' && o.status) ex.status = o.status;
        }
      }
    } else if (it.name === 'mcp__tasks__tasks_create') {
      // Создание задачи в прикладном трекере через MCP
      const o = it.input as { title?: unknown; name?: unknown } | null;
      const title = typeof o?.title === 'string' && o.title
        ? o.title : typeof o?.name === 'string' && o.name ? o.name : '';
      if (title) {
        taskAutoId += 1;
        tasks.set(`mcp-${taskAutoId}`, { content: title, status: 'pending' });
      }
    } else if (it.name === 'mcp__tasks__tasks_update') {
      // Обновление статуса задачи: ищем по совпадению заголовка в созданных MCP-задачах
      // (полный taskId из прикладного трекера нам неизвестен — матчим по title)
      const o = it.input as { title?: unknown; status?: unknown } | null;
      const status = typeof o?.status === 'string' ? o.status : null;
      const title = typeof o?.title === 'string' && o.title ? o.title : null;
      if (status && title) {
        for (const [, t] of tasks) {
          if (t.content === title) {
            if (status === 'done' || status === 'completed') t.status = 'completed';
            else if (status === 'in_progress') t.status = 'in_progress';
            break;
          }
        }
      }
    }
  }

  // Механизмы не смешиваются в одной сессии; если вдруг оба — Task* новее и точнее
  return tasks.size ? [...tasks.values()] : todos;
}

// Имена инструментов запуска субагентов и шеллов (регистр в разных версиях CLI плавает)
const AGENT_TOOLS = new Set(['task', 'agent']);

// Первая строка длинного текста (prompt/summary) для подписи карточки
function firstLine(s: string): string {
  const line = s.split('\n')[0].trim();
  return line.length > 120 ? line.slice(0, 117) + '…' : line;
}

// Человекочитаемый аргумент дочернего вызова — по приоритету полей input,
// как в строке инструмента в ленте чата (команда → путь → паттерн → запрос → …)
function toolCallArg(input: unknown): string | undefined {
  if (!input || typeof input !== 'object') return undefined;
  const o = input as Record<string, unknown>;
  const v = o.command ?? o.file_path ?? o.path ?? o.notebook_path ?? o.pattern
    ?? o.query ?? o.url ?? o.description ?? o.prompt;
  return typeof v === 'string' && v ? firstLine(v) : undefined;
}

// Собрать агентов сессии из ленты чата:
//  - субагенты: tool_use Task/Agent верхнего уровня; работает, пока нет result
//    (и ход не завершился — после result/interrupted незакрытые вызовы считаем оборванными);
//  - workflow-агенты: массив workflowAgents у tool_use Workflow (приходит через workflow_progress).
export function computeAgents(items: ChatItem[]): { agents: AgentArtifact[]; workflows: WorkflowGroup[] } {
  // Всё, что до последнего конца хода, уже не может быть running
  let lastTurnEndIdx = -1;
  items.forEach((it, i) => {
    if (it.kind === 'result' || it.kind === 'interrupted' || it.kind === 'session_ended') lastTurnEndIdx = i;
  });

  // Дочерние tool_use по id родителя — мини-лента действий субагента
  const children = new Map<string, AgentToolCall[]>();
  items.forEach((it, i) => {
    if (it.kind !== 'tool_use' || !it.parentToolUseId) return;
    const arr = children.get(it.parentToolUseId) ?? [];
    arr.push({
      id: it.id,
      name: it.name,
      arg: toolCallArg(it.input),
      running: it.result === undefined && i > lastTurnEndIdx,
      isError: it.isError,
    });
    children.set(it.parentToolUseId, arr);
  });

  const agents: AgentArtifact[] = [];
  const workflows: WorkflowGroup[] = [];

  items.forEach((it, i) => {
    if (it.kind !== 'tool_use' || it.parentToolUseId) return;
    const name = it.name.toLowerCase();
    const input = (it.input && typeof it.input === 'object' ? it.input : {}) as Record<string, unknown>;

    if (AGENT_TOOLS.has(name)) {
      const prompt = typeof input.prompt === 'string' && input.prompt ? input.prompt : undefined;
      const label = typeof input.description === 'string' && input.description
        ? input.description
        : prompt ? firstLine(prompt) : 'Субагент';
      const isBg = input.run_in_background === true || isBgLaunchResult(it.result);
      // Для фоновых result приходит сразу при запуске — о завершении не говорит;
      // достоверный признак — bgDone (событие bg_agent_done / история после рестарта)
      const settled = isBg
        ? it.bgDone === true
        : it.result !== undefined || i <= lastTurnEndIdx;
      const calls = children.get(it.id) ?? [];
      const last = calls[calls.length - 1];
      agents.push({
        id: it.id,
        kind: 'subagent',
        type: typeof input.subagent_type === 'string' ? input.subagent_type : undefined,
        label,
        status: it.isError || it.bgAborted ? 'error' : settled ? 'done' : 'running',
        background: isBg,
        toolCount: calls.length,
        lastTool: !settled ? last?.name : undefined,
        prompt,
        // Для фоновых result — это подтверждение запуска, а не ответ агента;
        // системный хвост CLI (agentId + <usage>) вырезаем
        resultText: !isBg && !it.isError && it.result?.trim()
          ? splitAgentResultTail(it.result).body : undefined,
        calls,
      });
    } else if (name === 'workflow') {
      // У ФОНОВОГО workflow result — мгновенная квитанция запуска, по нему судить нельзя:
      // завершение — только workflowDone от ватчера (или конец хода у блокирующего)
      const wfBg = isBgLaunchResult(it.result);
      const wfSettled = it.workflowDone === true || it.workflowAborted === true || it.bgDone === true
        || (it.result !== undefined && !wfBg)
        || (!wfBg && i <= lastTurnEndIdx);
      const wfAgents: AgentArtifact[] = (it.workflowAgents ?? []).map(a => {
        const src = a.summary?.trim() || a.prompt;
        return {
          id: `${it.id}:${a.id}`,
          kind: 'workflow',
          label: src ? firstLine(src) : 'Агент workflow',
          status: a.isDone || wfSettled ? 'done' : 'running',
          background: false,
          toolCount: (a.tools ?? []).reduce((s, t) => s + t.count, 0),
          prompt: a.prompt || undefined,
          resultText: a.summary?.trim() || undefined,
          tools: a.tools,
          files: a.files,
        };
      });
      // Группу показываем даже пока агенты ещё не появились (workflow только стартовал)
      workflows.push({
        id: it.id,
        name: workflowName(input),
        agents: wfAgents,
        doneCount: wfAgents.filter(a => a.status === 'done').length,
        settled: wfSettled,
      });
    }
  });

  return { agents, workflows };
}

export function computeArtifacts(items: ChatItem[], rootPath: string, executingTask: string | null = null): SessionArtifacts {
  // path → агрегированные дельты, порядок последнего касания сохраняем через Map
  const files = new Map<string, ArtifactFile>();
  const plans: PlanArtifact[] = [];
  const links = new Map<string, ArtifactLink>();
  const notes: string[] = [];

  // Изменённый файл (file_changed/Write): дельты + флаг changed
  const touchChanged = (path: string, added: number, removed: number, hasDelta: boolean) => {
    const pretty = path.replace(/\\/g, '/').replace(/^\.\//, '');
    // Канонический ключ дедупа: регистр и разделители не должны разъезжать один файл на две строки
    // (file_changed шлёт относительный путь, tool_use — абсолютный с возможным иным регистром папок)
    const key = pretty.toLowerCase();
    const ex = files.get(key);
    // Map не двигает ключ при set существующего — удаляем, чтобы последний тронутый был в конце
    if (ex) files.delete(key);
    files.set(key, {
      path: ex?.path ?? pretty, // для отображения и клика берём первый встреченный «красивый» путь
      added: (ex?.added ?? 0) + added,
      removed: (ex?.removed ?? 0) + removed,
      hasDelta: (ex?.hasDelta ?? false) || hasDelta,
      changed: true,
      external: ex?.external ?? false,
    });
  };

  // Упомянутый в тексте путь: добавляем только если файла ещё нет (изменённый приоритетнее)
  const touchMentioned = (path: string, external: boolean) => {
    const key = path.toLowerCase();
    if (files.has(key)) return; // уже есть запись (изменён или упомянут) — не перетираем
    files.set(key, { path, added: 0, removed: 0, hasDelta: false, changed: false, external });
  };

  const addLink = (raw: string) => {
    const url = raw.replace(TRAILING, '');
    if (!url || links.has(url)) return;
    let domain = url;
    try { domain = new URL(url).hostname.replace(/^www\./, ''); } catch { /* оставим url */ }
    links.set(url, { url, domain });
  };

  for (const it of items) {
    switch (it.kind) {
      case 'file_changed':
        touchChanged(it.path, it.added, it.removed, true);
        break;
      case 'tool_use': {
        if (WRITE_TOOLS.has(it.name)) {
          const raw = extractToolPath(it.input);
          const rel = raw ? toRelative(raw, rootPath) : null;
          if (rel) touchChanged(rel, 0, 0, false);
        } else if (WEBFETCH_TOOLS.has(it.name)) {
          const o = it.input as Record<string, unknown> | null;
          const u = o?.url;
          if (typeof u === 'string') addLink(u);
        } else if (MCP_NOTES_NAMES.has(it.name)) {
          const o = it.input as Record<string, unknown> | null;
          const title = typeof o?.title === 'string' && o.title.trim() ? o.title.trim() : null;
          if (title && !notes.includes(title)) notes.push(title);
        }
        break;
      }
      case 'plan_review':
        if (it.plan) {
          // Все планы сессии по порядку; статус — из ответа пользователя на plan_review
          const status: PlanStatus = it.resolved
            ? (it.approved ? 'approved' : 'rejected')
            : 'pending';
          plans.push({ plan: it.plan, status });
        }
        break;
      case 'text': {
        const urls = it.text.match(URL_RE);
        if (urls) for (const m of urls) addLink(m);
        for (const p of extractTextFilePaths(it.text)) {
          const c = classifyTextPath(p, rootPath);
          if (c) touchMentioned(c.path, c.external);
        }
        break;
      }
    }
  }

  // Последний тронутый — первым; изменённые файлы выше упомянутых (sort стабильный)
  const arr = [...files.values()].reverse();
  arr.sort((a, b) => (a.changed === b.changed ? 0 : a.changed ? -1 : 1));
  return {
    files: arr,
    plans,
    todos: computeTodos(items),
    links: [...links.values()],
    ...computeAgents(items),
    notes,
    executingTask,
  };
}

// Счётчик файлов для бейджа в шапке — ровно то же множество, что и список «Файлы»
// (изменённые + упомянутые в тексте), чтобы число на бейдже совпадало со вкладкой.
// Отдельно от computeArtifacts: не собирает ссылки/план (без new URL), только ключи файлов.
export function countFiles(items: ChatItem[], rootPath: string): number {
  const keys = new Set<string>();
  for (const it of items) {
    if (it.kind === 'file_changed') {
      keys.add(it.path.replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase());
    } else if (it.kind === 'tool_use' && WRITE_TOOLS.has(it.name)) {
      const raw = extractToolPath(it.input);
      const rel = raw ? toRelative(raw, rootPath) : null;
      if (rel) keys.add(rel.toLowerCase());
    } else if (it.kind === 'text') {
      for (const p of extractTextFilePaths(it.text)) {
        const c = classifyTextPath(p, rootPath);
        if (c) keys.add(c.path.toLowerCase());
      }
    }
  }
  return keys.size;
}

// Хук: подписывается на ленту активной сессии и мемоизует артефакты.
// projectId/rootPath опциональны: в чат-режиме проекта нет, лента едет через
// api.chats.getHistory, а с пустым rootPath файлы из абсолютных путей отсекаются
// сами (toRelative → null) — план/задачи/агенты/ссылки собираются без проекта.
// executingTaskTitle — если сессия запущена для выполнения задачи (заголовок для артефактов).
export function useSessionArtifacts(sessionId: string | null, projectId?: string, rootPath = '', executingTaskTitle: string | null = null): SessionArtifacts {
  const { items } = useSession(sessionId, projectId);
  return useMemo(() => computeArtifacts(items, rootPath, executingTaskTitle), [items, rootPath, executingTaskTitle]);
}
