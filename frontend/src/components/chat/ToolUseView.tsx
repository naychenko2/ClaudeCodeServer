import { memo, useState, useEffect, useMemo, useContext } from 'react';
import { Plug, Eye, SquarePen, Terminal, Globe, CircleUser, Sparkles, SquareCheck, Wrench } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FONT } from '../../lib/design';
import { relPath, stripRoot } from '../../lib/paths';
import { splitAgentResultTail, formatTailTokens, formatTailDuration } from '../../lib/agentTail';
import { ChatProjectContext, FalCostContext } from './contexts';
import { MediaBlock, extractMediaFromResult, extractMediaMeta, mediaLabel } from './MediaBlock';

// Спиннер для выполняющегося инструмента
function ToolSpinner() {
  return <div className="tool-spinner" />;
}

// Иконка и цвет по типу инструмента — чтобы read/edit/bash/web/mcp различались с первого взгляда
function toolMeta(name: string): { color: string; icon: React.ReactNode } {
  const n = name.toLowerCase();
  const common = { size: 13, strokeWidth: 2 } as const;
  if (n.startsWith('mcp__'))
    return { color: C.plan, icon: <Plug {...common} /> };
  if (['read', 'glob', 'grep', 'ls'].includes(n))
    return { color: C.info, icon: <Eye {...common} /> };
  if (['edit', 'write', 'multiedit', 'notebookedit'].includes(n))
    return { color: C.accent, icon: <SquarePen {...common} /> };
  if (n.startsWith('bash') || n.includes('shell'))
    return { color: C.success, icon: <Terminal {...common} /> };
  if (['websearch', 'webfetch'].includes(n))
    return { color: C.plan, icon: <Globe {...common} /> };
  if (n === 'task')
    return { color: C.accent, icon: <CircleUser {...common} /> };
  if (n === 'skill')
    return { color: C.plan, icon: <Sparkles {...common} /> };
  // Todo-задачи — та же «галочка в рамке», что у карточки плана
  if (['taskcreate', 'taskupdate', 'tasklist', 'taskget'].includes(n))
    return { color: C.accent, icon: <SquareCheck {...common} /> };
  return { color: C.info, icon: <Wrench {...common} /> };
}

// Статусы todo-задач (TaskUpdate) по-русски — для компактной строки в ленте
const TASK_STATUS_RU: Record<string, string> = {
  pending: 'в очереди', in_progress: 'в работе', completed: 'готово',
  cancelled: 'отменена', deleted: 'удалена',
};

// Русские названия инструментов для ленты чата
const TOOL_LABELS: Record<string, string> = {
  read: 'Чтение', edit: 'Правка', write: 'Запись', multiedit: 'Правки',
  notebookedit: 'Правка ноутбука', bash: 'Команда', bashoutput: 'Вывод команды',
  glob: 'Поиск файлов', grep: 'Поиск', ls: 'Список', task: 'Субагент', agent: 'Субагент',
  websearch: 'Веб-поиск', webfetch: 'Загрузка страницы', skill: 'Навык',
  todowrite: 'План задач', exitplanmode: 'План', toolsearch: 'Поиск инструментов',
  taskcreate: 'Задача', taskupdate: 'Задача', tasklist: 'Список задач', taskget: 'Задача',
  killshell: 'Остановка команды',
};
// Имя инструмента для показа: MCP → «server · tool», известные — по-русски, прочее — как есть
export function toolLabel(name: string): string {
  if (name.startsWith('mcp__')) return name.slice(5).replace(/__/g, ' · ');
  return TOOL_LABELS[name.toLowerCase()] ?? name;
}

// Склонение слова «действие»
export function toolWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'действие';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'действия';
  return 'действий';
}

// Inline-diff для Edit/MultiEdit/Write: удалённые строки красным, добавленные зелёным
function DiffBody({ hunks }: { hunks: Array<{ old?: string; new?: string }> }) {
  const MAX = 240;
  let count = 0;
  const rows: React.ReactNode[] = [];
  const pushLines = (text: string, kind: 'del' | 'add') => {
    for (const ln of text.split('\n')) {
      if (count >= MAX) return;
      rows.push(
        <div key={count} style={{
          display: 'flex', gap: 7, padding: '0 9px',
          background: kind === 'del' ? C.diffRemBg : C.diffAddBg,
          color: kind === 'del' ? C.diffRemText : C.diffAddText,
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>
          <span style={{ userSelect: 'none', opacity: 0.55, flexShrink: 0 }}>{kind === 'del' ? '−' : '+'}</span>
          <span style={{ flex: 1 }}>{ln || ' '}</span>
        </div>
      );
      count++;
    }
  };
  hunks.forEach(h => { if (h.old) pushLines(h.old, 'del'); if (h.new) pushLines(h.new, 'add'); });
  return (
    <div style={{
      margin: '0 0 9px', borderRadius: 7, overflow: 'hidden', border: `1px solid ${C.bgInset}`,
      fontFamily: FONT.mono, fontSize: 11.5, lineHeight: 1.55,
      maxHeight: 320, overflowY: 'auto',
    }}>
      {rows}
      {count >= MAX && <div style={{ padding: '2px 9px', color: C.textMuted, fontStyle: 'italic' }}>…(обрезано)</div>}
    </div>
  );
}

export type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Строка инструмента с раскрываемым телом результата (вывод Bash/Read и т.п.).
// React.memo: элементы ленты иммутабельны по ссылке (кроме стримящегося последнего) —
// при дописывании ленты завершённые строки не перерендериваются.
export const ToolUseView = memo(function ToolUseView({ item, online = true, onOpenFile }: { item: Extract<ChatItem, { kind: 'tool_use' }>; online?: boolean; onOpenFile?: (path: string) => void }) {
  const meta = toolMeta(item.name);
  const [open, setOpen] = useState(false);
  const project = useContext(ChatProjectContext);
  const n = item.name.toLowerCase();
  const inp = (item.input ?? {}) as Record<string, any>;
  // Во время стриминга показываем накопленный partial_json («печатает команду»), затем — разобранный аргумент.
  // Пути показываем относительно корня проекта: file_path/path — целиком, в командах и
  // glob-шаблонах вырезаем абсолютный корень из текста (там путь — часть строки).
  const pathVal = inp.file_path ?? inp.path ?? inp.notebook_path;
  // Человекочитаемый аргумент для todo-задач: TaskCreate — тема, TaskUpdate — «#id → статус»
  const taskArg = n === 'taskcreate' && typeof inp.subject === 'string' ? inp.subject
    : n === 'taskupdate' && inp.taskId != null
      ? `#${inp.taskId}${typeof inp.status === 'string' ? ` → ${TASK_STATUS_RU[inp.status] ?? inp.status}` : ''}`
      : null;
  const toolArg = item.streamingArg ?? taskArg ?? String(
    (inp.command != null ? stripRoot(String(inp.command), project?.rootPath) : null)
    ?? (pathVal != null ? relPath(String(pathVal), project?.rootPath) : null)
    ?? (inp.pattern != null ? stripRoot(String(inp.pattern), project?.rootPath) : null)
    ?? inp.query ?? inp.url ?? inp.description ?? inp.prompt ?? '');
  // Аргумент-путь (Read/Edit/…) — на мобиле обрезаем слева, чтобы было видно имя файла
  const argIsPath = inp.command == null && pathVal != null && item.streamingArg == null;
  // Имя инструмента по-русски (MCP → «server · tool»)
  const displayName = toolLabel(item.name);
  // Inline-diff из input (доступен сразу, не дожидаясь tool_result)
  const editHunks: Array<{ old?: string; new?: string }> =
    n === 'edit' && (typeof inp.old_string === 'string' || typeof inp.new_string === 'string')
      ? [{ old: inp.old_string, new: inp.new_string }]
    : n === 'multiedit' && Array.isArray(inp.edits)
      ? inp.edits.map((e: any) => ({ old: e.old_string, new: e.new_string }))
    : n === 'write' && typeof inp.content === 'string'
      ? [{ new: inp.content }]
    : [];
  const hasDiff = editHunks.length > 0;
  const hasResult = item.result != null && item.result.trim().length > 0;
  // Системный хвост результата сабагента (agentId + <usage>…</usage>) — сырым текстом
  // в ленте выглядит мусором: вырезаем из тела и показываем аккуратной строкой метрик
  const isAgentTool = n === 'task' || n === 'agent';
  const agentSplit = useMemo(
    () => (isAgentTool && item.result != null ? splitAgentResultTail(item.result) : null),
    [isAgentTool, item.result]);
  // Медиа (изображения + видео) из результата MCP-инструментов
  const media = hasResult && !item.isError ? extractMediaFromResult(item.result!) : [];
  const mediaMeta = hasResult && !item.isError ? extractMediaMeta(item.result!) : {};
  const hasMedia = media.length > 0;

  // Точная стоимость генерации fal.ai приходит с backend (billing-events по request_id).
  // Сопоставляем по request_id, извлечённому из результата вызова.
  const falCostByRequest = useContext(FalCostContext);
  const falRequestId = useMemo(() => {
    if (!hasMedia || !hasResult || item.isError) return undefined;
    try { return JSON.parse(item.result!).request_id as string | undefined; }
    catch { return undefined; }
  }, [hasMedia, hasResult, item.isError, item.result]);
  const falCostUsd = falRequestId ? falCostByRequest.get(falRequestId) : undefined;
  // Генерация fal распознана, но стоимость ещё не подсчитана (биллинг приходит с задержкой)
  const costPending = hasMedia && !!falRequestId && falCostUsd === undefined;
  // «Считается…» не должно висеть вечно: если стоимость так и не пришла (напр. старое
  // изображение под другим аккаунтом fal.ai — его нет в текущем billing), убираем метку через 30с.
  const [pendingExpired, setPendingExpired] = useState(false);
  useEffect(() => {
    if (!costPending) { setPendingExpired(false); return; }
    const t = setTimeout(() => setPendingExpired(true), 30000);
    return () => clearTimeout(t);
  }, [costPending]);
  // Имя модели — из input вызова (в результате fal его нет)
  const falModel = ((inp.endpoint_id as string | undefined) ?? mediaMeta.model)?.split('/').pop();
  // Медиа показываем сразу, без клика; текст/diff — за клик
  const hasBody = hasDiff || (hasResult && !hasMedia);
  // Консольные инструменты (Bash/shell) → тёмный «терминальный» вывод.
  // Остальные (Read/Grep/Glob/MCP и пр.) → светлая «панель вывода», чтобы текст/код не давил тёмным фоном.
  const isConsole = n.startsWith('bash') || n.includes('shell');

  return (
    <div>
      <div
        style={{ padding: '4px 0', display: 'flex', alignItems: 'center', gap: 10, cursor: hasBody ? 'pointer' : 'default' }}
        onClick={() => hasBody && setOpen(o => !o)}
      >
        {item.result === undefined && <ToolSpinner />}
        <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color: meta.color }}>
          {meta.icon}
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{displayName}</span>
        </span>
        {toolArg
          ? (() => {
              // Путь к файлу делаем кликабельным — открывает файл на просмотр (десктоп: split справа от чата).
              const clickable = argIsPath && !!onOpenFile && pathVal != null;
              return (
                <span
                  className={argIsPath ? 'cc-trunc-left' : undefined}
                  onClick={clickable ? (e) => { e.stopPropagation(); onOpenFile!(relPath(String(pathVal), project?.rootPath)); } : undefined}
                  title={clickable ? 'Открыть файл' : undefined}
                  style={{ fontFamily: FONT.mono, fontSize: 11, flex: 1, color: clickable ? C.accent : C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: clickable ? 'pointer' : 'inherit' }}
                >
                  {toolArg}
                </span>
              );
            })()
          : <span style={{ flex: 1 }} />}
        {item.result !== undefined && (
          <span style={{ fontSize: 11, color: item.isError ? C.dangerText : C.textMuted, flexShrink: 0 }}>
            {item.isError ? 'ошибка' : hasMedia ? mediaLabel(media) : 'готово'}
          </span>
        )}
        {hasBody && (
          <span style={{ color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }}>▾</span>
        )}
      </div>
      {/* Медиа (изображения + видео) — сразу под шапкой, без клика */}
      {hasMedia && (
        <div style={{ paddingBottom: 8, display: 'flex', flexDirection: 'column', gap: 8 }}>
          {media.map((m, i) => {
            const filename = m.fileName ?? m.url.split('/').pop()?.split('?')[0] ?? m.kind;
            return (
              <MediaBlock key={i} m={m} filename={filename} model={falModel} inferenceTime={mediaMeta.inferenceTime} costUsd={falCostUsd} costPending={costPending && !pendingExpired} online={online} />
            );
          })}
        </div>
      )}
      {open && hasDiff && <DiffBody hunks={editHunks} />}
      {open && !hasDiff && hasResult && !hasMedia && (
        <>
          <pre style={{
            margin: agentSplit?.tail ? '0 0 4px' : '0 0 9px', padding: '8px 10px', borderRadius: 7,
            // Bash → тёмный терминал; остальное → светлая панель вывода
            background: isConsole ? C.termBg : C.outputBg,
            border: isConsole ? 'none' : `1px solid ${C.outputBorder}`,
            // На светлой панели ошибку красим в danger; на тёмной — светлый «терминальный» оттенок
            color: isConsole
              ? (item.isError ? C.termError : C.termText)
              : (item.isError ? C.dangerText : C.textPrimary),
            fontFamily: FONT.mono,
            fontSize: 11.5, lineHeight: 1.5, maxHeight: 280, overflow: 'auto',
            whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          }}>
            {(() => {
              const r = stripRoot(agentSplit?.body ?? item.result!, project?.rootPath);
              return r.length > 4000 ? r.slice(0, 4000) + '\n…(обрезано)' : r;
            })()}
          </pre>
          {/* Метрики сабагента из системного хвоста — вместо сырых строк CLI */}
          {agentSplit?.tail && (
            <div style={{
              margin: '0 0 9px', display: 'flex', alignItems: 'center', gap: 5, flexWrap: 'wrap',
              fontFamily: FONT.sans, fontSize: 11, color: C.textMuted,
            }}>
              {agentSplit.tail.tokens != null && <span>{formatTailTokens(agentSplit.tail.tokens)} токенов</span>}
              {agentSplit.tail.tokens != null && (agentSplit.tail.toolUses != null || agentSplit.tail.durationMs != null) && <span>·</span>}
              {agentSplit.tail.toolUses != null && <span>{agentSplit.tail.toolUses} {toolWord(agentSplit.tail.toolUses)}</span>}
              {agentSplit.tail.toolUses != null && agentSplit.tail.durationMs != null && <span>·</span>}
              {agentSplit.tail.durationMs != null && <span>{formatTailDuration(agentSplit.tail.durationMs)}</span>}
            </div>
          )}
        </>
      )}
    </div>
  );
});
