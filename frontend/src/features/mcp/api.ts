// Домен «Реестр MCP» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// личный реестр MCP-серверов владельца, каталог, диагностика вызовов и OAuth-вход.
// Тонкая обёртка над живым REST; DTO-типы — types/index.ts (Mcp* доедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type {
  McpBuiltinServer,
  McpCallsResponse,
  McpCatalogRevisionResult,
  McpCatalogSearchResult,
  McpOAuthCompleteResult,
  McpOAuthStartResult,
  McpProbeResult,
  McpServer,
  McpServerUpsert,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.mcp.* остаются прежними для всех потребителей).
export const mcpApi = {
  // Личный реестр MCP-серверов владельца (фича mcp-registry). Секретные значения наружу
  // не выходят: в McpValue у секрета value = null, а в форме пустое значение секрета
  // означает «оставить как было».
  mcp: {
    list: () => request<McpServer[]>('/mcp/servers'),
    get: (id: string) => request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}`),
    // Встроенные серверы продукта — только наблюдение статуса, записи в реестре нет
    builtin: () => request<McpBuiltinServer[]>('/mcp/servers/builtin'),
    create: (data: McpServerUpsert) =>
      request<McpServer>('/mcp/servers', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: string, data: McpServerUpsert) =>
      request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(data) }),
    setEnabled: (id: string, enabled: boolean) =>
      request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}/enable`, {
        method: 'POST', body: JSON.stringify({ enabled }),
      }),
    // Пометка «для участников» (S2a-F6): меняет только владелец записи; участник общего
    // проекта получает 403 (запись для него реальна, но чужие настройки он не правит),
    // посторонний — 404. Ответ — обновлённая запись
    setShared: (id: string, shared: boolean) =>
      request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}/shared`, {
        method: 'PUT', body: JSON.stringify({ sharedWithMembers: shared }),
      }),
    delete: (id: string) => request<void>(`/mcp/servers/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Разовая проверка «по кнопке»: рукопожатие + tools/list, результат едет и в стор статусов.
    // Для записи из каталога + stdio + local-владелец передаём confirmed:true — иначе сервер
    // отвечает отказом (план §6). Запрос без тела оставлен работающим: ручные записи идут без
    // подтверждения, [FromBody] опциональный
    probe: (id: string, body?: { confirmed?: boolean }) =>
      request<McpProbeResult>(`/mcp/servers/${encodeURIComponent(id)}/probe`, {
        method: 'POST', body: JSON.stringify(body ?? {}),
      }),
    // Импорт фрагмента {"mcpServers": {...}} — записи заводятся выключенными
    import: (fragment: unknown) =>
      request<{ created: McpServer[]; skipped: { key: string; reason: string }[] }>('/mcp/servers/import', {
        method: 'POST', body: JSON.stringify(fragment),
      }),
    // Поиск по реестру MCP-серверов (волна 1, задача 9fa075ec). Ищет БЭКЕНД: реестр
    // отдаёт результат страницами, и подходящей записи на первой странице может не
    // быть вовсе (QA: «filesystem» — 14 записей, ни одной на первой). Поэтому q едет
    // в запрос, а не фильтрует загруженный массив. cursor — следующая страница из
    // nextCursor предыдущего ответа; пустой q = витрина каталога.
    // Каталог не критичный: ошибка сети показывается плашкой, ручной путь «Добавить»
    // остаётся рабочим
    catalogSearch: (q: string, cursor?: string | null) => {
      const params = new URLSearchParams({ q });
      if (cursor) params.set('cursor', cursor);
      return request<McpCatalogSearchResult>(`/mcp/catalog/search?${params.toString()}`);
    },
    // Батч-ревизия импортированных записей (волна 2): фронт шлёт имена каталожных серверов,
    // бэк для каждого отдаёт статус в реестре и факт «есть версия новее». Запрос идёт
    // ОТДЕЛЬНО от основного списка, после отрисовки раздела — реестр в статусе preview
    // может лежать, а раздел обязан открываться. names: уникальные CatalogRef.name
    catalogRevisions: (names: string[]) =>
      request<McpCatalogRevisionResult>('/mcp/catalog/revision', {
        method: 'POST', body: JSON.stringify({ names }),
      }),
    // Диагностика вызовов инструментов — только админ (данные всех владельцев)
    calls: (failures = 50) => request<McpCallsResponse>(`/mcp/calls?failures=${failures}`),
    // Вход по OAuth (волна 7): start отдаёт адрес окна провайдера, complete — запасной
    // путь с кодом, вставленным вручную (сервер принимает только loopback-адрес возврата)
    oauthStart: (id: string, clientId?: string) =>
      request<McpOAuthStartResult>(`/mcp/servers/${encodeURIComponent(id)}/oauth/start`, {
        method: 'POST', body: JSON.stringify(clientId ? { clientId } : {}),
      }),
    oauthComplete: (id: string, state: string, code: string) =>
      request<McpOAuthCompleteResult>(`/mcp/servers/${encodeURIComponent(id)}/oauth/complete`, {
        method: 'POST', body: JSON.stringify({ state, code }),
      }),
  },
};
