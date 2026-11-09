// Методы REST «Плагинов владельца» (ADR-031, вертикаль ClaudeHomeServer.Plugins).
// lib/api.ts подмешивает их в объект api spread'ом (адрес api.plugins.* остаётся
// прежним для потребителей); типы — в ./types.ts; panelEntryPath остаётся публичным
// экспортом lib/api (re-export).
import { readStoredToken, request } from '../../lib/offline';
import type {
  PluginDraftAcceptResult,
  PluginDraftDiff,
  PluginDraftPreview,
  PluginDraftRejectResult,
  PluginDraftRestoreResult,
  PluginDraftSummary,
  PluginGrantSecret,
  PluginInfo,
  PluginPermissionsView,
} from './types';

// id → сегмент URL: publisher/slug → publisher~slug (M1: один сегмент маршрута)
function pluginUrlId(id: string): string {
  return id.replace(/\//g, '~');
}

// entry панели из манифеста (contributes.panels[].entry) — НЕДОВЕРЕННЫЙ источник
// (пакет собрал агент). Без проверки подстановка `.../assets/${entry}` позволяла
// `../../../../api/plugins/drafts` увести запрос с Bearer пользователя на чужой маршрут
// (обход allow-list `panel/**`). Принимаем только форму `panel/...`: первый сегмент
// «panel», остальные — обычные имена (без «.», «..», пустых); в строке нет «\», «?»,
// «#», «%», управляющих символов (C0/C1) и схемы (двоеточие в первом сегменте).
// Каждый сегмент кодируем encodeURIComponent ПОСЛЕ проверки — иначе «..» переживёт
// кодировку. null — entry невалиден (запрос не делаем, понятная ошибка в PluginFrame).
export function panelEntryPath(entry: unknown): string | null {
  if (typeof entry !== 'string') return null;
  if (/[\\?#%]/.test(entry)) return null;
  if (/[\u0000-\u001f\u007f-\u009f]/.test(entry)) return null;
  const segs = entry.split('/');
  if (segs.length < 2) return null;
  if (segs[0] !== 'panel') return null;
  if (/:/.test(segs[0])) return null;
  for (const s of segs) {
    if (s === '' || s === '.' || s === '..') return null;
  }
  return segs.map(s => encodeURIComponent(s)).join('/');
}

export const pluginsApi = {
  // Плагины владельца (ADR-031 §2.3, §4). Гейты бэка: подсистема выключена → 503,
  // флаг plugins-mvp off → 403; install/grant/uninstall/тумблер — только пользовательский
  // JWT (сервисный → 403). Секреты: значение не возвращается, только {name, set}.
  plugins: {
    // Список установленных (DTO без значений секретов)
    list: () => request<PluginInfo[]>('/plugins'),
    // Принять пакет по метке источника: draft:{slug} | upload:{id} | catalog:{ref}
    install: (source: string) =>
      request<PluginInfo>('/plugins/install', { method: 'POST', body: JSON.stringify({ source }) }),
    // Выдать права (подмножество requested) и ввести значения секретов
    grant: (id: string, permissions: string[], secrets: PluginGrantSecret[]) =>
      request<PluginPermissionsView>(`/plugins/${pluginUrlId(id)}/grant`, {
        method: 'POST', body: JSON.stringify({ permissions, secrets }),
      }),
    // Значение секрета (шаг 3.3, UI 3.4): PUT /secrets/{name}, значение в теле.
    // Сервер вернул {name, set, updatedAt} — значение наружу не уходит.
    setSecret: (id: string, name: string, value: string) =>
      request<{ name: string; set: boolean; updatedAt: string }>(
        `/plugins/${pluginUrlId(id)}/secrets/${encodeURIComponent(name)}`,
        { method: 'PUT', body: JSON.stringify({ value }) },
      ),
    // Убрать значение секрета: DELETE /secrets/{name} → {name, set:false}
    deleteSecret: (id: string, name: string) =>
      request<{ name: string; set: boolean }>(
        `/plugins/${pluginUrlId(id)}/secrets/${encodeURIComponent(name)}`,
        { method: 'DELETE' },
      ),
    // Удалить пакет и запись (каскад)
    uninstall: (id: string) =>
      request<{ id: string; removed: boolean }>(`/plugins/${pluginUrlId(id)}/uninstall`, { method: 'POST' }),
    // requested + granted для диалога согласия/редактирования
    permissions: (id: string) =>
      request<PluginPermissionsView>(`/plugins/${pluginUrlId(id)}/permissions`),
    // Тумблер включения: выключенный не даёт --plugin-dir и не монтирует панели
    setEnabled: (id: string, enabled: boolean) =>
      request<{ id: string; enabled: boolean }>(`/plugins/${pluginUrlId(id)}/${enabled ? 'enable' : 'disable'}`, {
        method: 'POST',
      }),
    // Вызов операции моста (ADR-031 §3.4). Тело HTTP-запроса — РОВНО {capability, args, projectId}
    // (контракт бэкенда, PluginCallContractTests): capability — имя операции (внутренне в мосту
    // это `op`, но в HTTP — capability), args — объект, projectId — проект слота (null — вне проекта).
    // Лишних полей (op, context) сервер не принимает. Ответ — {ok, result?} | {ok:false, error:{code,
    // message}}; коды error — ADR §3.4 (denied/not_found/invalid/unavailable/upstream/timeout/limit).
    call: (id: string, body: { capability: string; args: Record<string, unknown>; projectId: string | null }) =>
      request<{ ok: boolean; result?: unknown; error?: { code: string; message: string } }>(
        `/plugins/${pluginUrlId(id)}/call`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
    // HTML панели пакета (ADR-031 §3.1): хост забирает его по REST с user JWT
    // (навигация iframe не несёт Authorization) и сам строит srcdoc. entry — путь
    // панели из манифеста (contributes.panels[].entry, например panel/index.html);
    // маршрут {**path} — catch-all, слэши entry остаются литеральными. entry —
    // недоверенный (пакет собрал агент): валидируем form'ой panel/... (panelEntryPath)
    // и кодируем сегменты, иначе «..»/«?»/«#» уведут запрос на чужой маршрут.
    // Прямой fetch (не request()): ответ — текст, а не JSON, и request() упёрся бы
    // в JSON.parse. 404 — у плагина нет панели (макет 3, «пусто»).
    panelHtml: async (id: string, version: string, entry: string): Promise<string> => {
      // entry из недоверенного манифеста: валидируем ДО запроса (иначе «..»/«?»/«#»
      // уводят запрос на чужой маршрут). Невалидно — понятная ошибка, запрос не идёт.
      const path = panelEntryPath(entry);
      if (path === null) throw new Error('Панель плагина указана неверно');
      const token = readStoredToken();
      const res = await fetch(
        `/api/plugins/${pluginUrlId(id)}/${encodeURIComponent(version)}/assets/${path}`,
        { headers: token ? { Authorization: `Bearer ${token}` } : {} },
      );
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        const e = new Error(err.error ?? res.statusText) as Error & { status?: number };
        e.status = res.status;
        throw e;
      }
      return res.text();
    },

    // === Черновики build-it-yourself (шаги 4.3a/4.3b/4.3c). Гейты бэка:
    // подсистема выключена → 503, plugins-mvp/plugins-build-yourself off → 403,
    // сервисный JWT → 403, чужой/несуществующий черновик → 404 ===
    // Список черновиков владельца (активные + отклонённые)
    draftsList: () => request<PluginDraftSummary[]>('/plugins/drafts'),
    // Предпросмотр черновика: вклады, запрошенные права, секреты, файлы, результат проверки
    draftPreview: (slug: string) =>
      request<PluginDraftPreview>(`/plugins/drafts/${encodeURIComponent(slug)}/preview`),
    // Сравнение черновика с последней установленной версией того же id (diff)
    draftDiff: (slug: string) =>
      request<PluginDraftDiff>(`/plugins/drafts/${encodeURIComponent(slug)}/diff`),
    // Принять черновик: установка по метке draft:{slug}; права НЕ выдаёт
    draftAccept: (slug: string) =>
      request<PluginDraftAcceptResult>(`/plugins/drafts/${encodeURIComponent(slug)}/accept`, { method: 'POST' }),
    // Отклонить черновик: перенос в _rejected/{slug}
    draftReject: (slug: string) =>
      request<PluginDraftRejectResult>(`/plugins/drafts/${encodeURIComponent(slug)}/reject`, { method: 'POST' }),
    // Вернуть отклонённый черновик обратно в _drafts/{slug}
    draftRestore: (slug: string) =>
      request<PluginDraftRestoreResult>(`/plugins/drafts/${encodeURIComponent(slug)}/restore`, { method: 'POST' }),
    // Панель черновика для предпросмотра (GET /api/plugins/drafts/{slug}/panel, шаг 4.3d):
    // сырой HTML, как panelHtml (текст ответа)
    draftPanel: async (slug: string): Promise<string> => {
      const token = readStoredToken();
      const res = await fetch(`/api/plugins/drafts/${encodeURIComponent(slug)}/panel`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        const e = new Error(err.error ?? res.statusText) as Error & { status?: number };
        e.status = res.status;
        throw e;
      }
      return res.text();
    },
  },
};
