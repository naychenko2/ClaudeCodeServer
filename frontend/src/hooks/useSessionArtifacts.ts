import { useMemo } from 'react';
import type { ChatItem } from '../types';
import { useSession } from './useSession';

// Артефакты, собранные за сессию из ленты чата:
//  - файлы: изменённые (file_changed/Write) + упомянутые путём в тексте ответа,
//  - планы (из ExitPlanMode) со статусами,
//  - ссылки, упомянутые в ответах и запросах WebFetch.

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

export interface SessionArtifacts {
  files: ArtifactFile[];
  plans: PlanArtifact[];
  links: ArtifactLink[];
}

// Инструменты, которые меняют файл — путь берём из их аргументов как запасной источник
// (на случай, если file_changed не пришёл, например файл вне зоны watcher'а).
const WRITE_TOOLS = new Set(['Write', 'Edit', 'MultiEdit', 'NotebookEdit']);

const URL_RE = /https?:\/\/[^\s<>()[\]"'`]+/g;
// Хвостовая пунктуация, прилипающая к URL в тексте (точка в конце предложения, запятая, скобка)
const TRAILING = /[.,;:!?)\]}>'"]+$/;

// Привести абсолютный путь из аргументов инструмента к относительному в проекте.
// Возвращает null, если путь вне rootPath (тогда file_changed его всё равно не поймает).
function toRelative(raw: string, rootPath: string): string | null {
  const p = raw.replace(/\\/g, '/');
  // Уже относительный (Claude иногда передаёт относительные пути)
  if (!/^([a-zA-Z]:\/|\/)/.test(p)) {
    const rel = p.replace(/^\.\//, '');
    // Выход за пределы корня — не файл проекта, в артефакты не берём
    if (rel.startsWith('../') || rel.includes('/../')) return null;
    return rel;
  }
  const root = rootPath.replace(/\\/g, '/').replace(/\/+$/, '');
  if (!root) return null;
  const lp = p.toLowerCase();
  const lr = root.toLowerCase();
  if (lp === lr) return null;
  if (lp.startsWith(lr + '/')) return p.slice(root.length + 1);
  return null;
}

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

export function computeArtifacts(items: ChatItem[], rootPath: string): SessionArtifacts {
  // path → агрегированные дельты, порядок последнего касания сохраняем через Map
  const files = new Map<string, ArtifactFile>();
  const plans: PlanArtifact[] = [];
  const links = new Map<string, ArtifactLink>();

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
        } else if (it.name === 'WebFetch') {
          const o = it.input as Record<string, unknown> | null;
          const u = o?.url;
          if (typeof u === 'string') addLink(u);
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
    links: [...links.values()],
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
export function useSessionArtifacts(sessionId: string | null, projectId: string, rootPath: string): SessionArtifacts {
  const { items } = useSession(sessionId, projectId);
  return useMemo(() => computeArtifacts(items, rootPath), [items, rootPath]);
}
