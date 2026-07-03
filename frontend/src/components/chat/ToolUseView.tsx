import { memo, useState, useEffect, useMemo, useContext } from 'react';
import type { ChatItem } from '../../types';
import { C, FONT } from '../../lib/design';
import { relPath, stripRoot } from '../../lib/paths';
import { ChatProjectContext, FalCostContext } from './contexts';
import { MediaBlock, extractMediaFromResult, extractMediaMeta, mediaLabel } from './MediaBlock';

// Спиннер для выполняющегося инструмента
function ToolSpinner() {
  return <div className="tool-spinner" />;
}

// Иконка и цвет по типу инструмента — чтобы read/edit/bash/web/mcp различались с первого взгляда
function toolMeta(name: string): { color: string; icon: React.ReactNode } {
  const n = name.toLowerCase();
  const svg = (children: React.ReactNode) => (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
  );
  if (n.startsWith('mcp__'))
    return { color: '#8E4A82', icon: svg(<><path d="M9 2v6M15 2v6" /><path d="M6 8h12v3a6 6 0 0 1-12 0z" /><path d="M12 17v5" /></>) };
  if (['read', 'glob', 'grep', 'ls'].includes(n))
    return { color: C.info, icon: svg(<><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" /><circle cx="12" cy="12" r="3" /></>) };
  if (['edit', 'write', 'multiedit', 'notebookedit'].includes(n))
    return { color: '#C2693B', icon: svg(<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></>) };
  if (n.startsWith('bash') || n.includes('shell'))
    return { color: C.success, icon: svg(<><polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" /></>) };
  if (['websearch', 'webfetch'].includes(n))
    return { color: '#8E4A82', icon: svg(<><circle cx="12" cy="12" r="10" /><line x1="2" y1="12" x2="22" y2="12" /><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" /></>) };
  if (n === 'task')
    return { color: '#B05C38', icon: svg(<><circle cx="12" cy="8" r="4" /><path d="M4 21a8 8 0 0 1 16 0" /></>) };
  if (n === 'skill')
    return { color: '#8E4A82', icon: svg(<><path d="M12 3l1.9 5.2L19 10l-5.1 1.8L12 17l-1.9-5.2L5 10l5.1-1.8z" /><path d="M19 15l.7 2 2 .7-2 .7-.7 2-.7-2-2-.7 2-.7z" /></>) };
  // Todo-задачи — та же «галочка в рамке», что у карточки плана
  if (['taskcreate', 'taskupdate', 'tasklist', 'taskget'].includes(n))
    return { color: C.accent, icon: svg(<><path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" /></>) };
  return { color: C.info, icon: svg(<path d="M14.7 6.3a4 4 0 0 0-5.4 5.4l-6 6 2 2 6-6a4 4 0 0 0 5.4-5.4l-2.3 2.3-2-2 2.3-2.3z" />) };
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
function toolLabel(name: string): string {
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
          background: kind === 'del' ? '#FBEAE7' : '#EAF4E6',
          color: kind === 'del' ? '#A8392C' : '#37722B',
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
          <span style={{ fontSize: 11, color: item.isError ? '#C0392B' : C.textMuted, flexShrink: 0 }}>
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
        <pre style={{
          margin: '0 0 9px', padding: '8px 10px', borderRadius: 7,
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
            const r = stripRoot(item.result!, project?.rootPath);
            return r.length > 4000 ? r.slice(0, 4000) + '\n…(обрезано)' : r;
          })()}
        </pre>
      )}
    </div>
  );
});
