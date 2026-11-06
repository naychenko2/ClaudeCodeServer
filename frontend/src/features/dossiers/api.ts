// Домен «История решений» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// листинг досье, статус/запуск экспорта в ветку и импорт из неё. Тонкая обёртка над
// живым REST; DTO-типы — types/index.ts (DossierEntry доедет в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type { DossierEntry } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.dossiers.* остаются прежними для всех потребителей).
export const dossiersApi = {
  // История решений (change-dossiers) — записи «зачем менялось, что решили,
  // что отвергли, какие грабли», привязанные к коммитам. ADR-004 §4/§6/§8.
  // Этап 1: просмотр. Этап 3: экспорт в ветку ccs/dossiers/v1 через git-плюминг.
  dossiers: {
    list: (projectId: string, filter?: { file?: string; symbol?: string; commit?: string }) => {
      const qs = new URLSearchParams();
      if (filter?.file) qs.set('file', filter.file);
      if (filter?.symbol) qs.set('symbol', filter.symbol);
      if (filter?.commit) qs.set('commit', filter.commit);
      const s = qs.toString();
      // Контракт GET /dossiers (ADR-004 §4, спринт «История решений» блок В):
      // { entries, coverage }. coverage — метрика охвата за окно periodDays:
      // знаменатель commits (с --follow при file), числитель dossiers. При сбое git
      // числитель/знаменатель обнуляются (контракт бэка — листинг не роняется).
      // coverage помечена опциональной: ранний ответ или альтернативная сборка бэка
      // могут её не прислать — панель тогда просто не рисует строку охвата.
      return request<{
        entries: DossierEntry[];
        coverage?: { periodDays: number; commits: number; dossiers: number } | null;
      }>(`/projects/${projectId}/dossiers${s ? `?${s}` : ''}`);
    },
    // Готовность проекта к экспорту (ADR-004 §6): isGitRepo гейтит кнопку в UI,
    // sharedFolder — предупреждение о втором владельце той же папки. hasDossierBranch —
    // наличие локальной refs/heads/ccs/dossiers/v1: пока ветки нет, импорт из неё
    // бессмыслен, кнопку «Загрузить» в UI гейтим этим признаком.
    // autoExport — причина гейта АВТОвыгрузки: панель выбирает по ней текст подсказки
    // (после сужения фона «ветка заведомо наша» общая фраза «выгружается само» врала
    // бы при чужом tip / одной origin-ветке / общей папке). null у не-git проекта.
    exportStatus: (projectId: string) =>
      request<{
        isGitRepo: boolean;
        sharedFolder: boolean;
        hasDossierBranch: boolean;
        autoExport: 'active' | 'foreignTip' | 'originOnly' | 'sharedFolder' | null;
      }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/export/status`),
    // Запуск экспорта. push=true — единственное место UI, откуда вызывается git push
    // (ADR §6: «Push — только вручную»). Ответ — состояние финальной карточки:
    // count подставляется в текст успеха, nothingToExport переключает на состояние «пусто».
    // timeoutMs 120 с: plumbing-экспорт на большой истории (~38 с на 217 паспортов
    // наблюдалось на проде) — дефолтные 30 с обрывали запрос, хотя сервер ещё работал.
    exportRun: (projectId: string, push: boolean) =>
      request<{ status: 'exported' | 'nothingToExport'; count: number }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/export`,
        { method: 'POST', body: JSON.stringify({ push }), timeoutMs: 120_000 }),
    // Импорт «Историй решений» из ветки ccs/dossiers/v1 (этап 4): читает ветку
    // plumbing-командами и кладёт записи в стор с origin='imported' и importedAuthor.
    // added — реально добавленные, skipped — остальные записи index.json
    // (уже были импортированными / нечитаемые / кривые sha). noBranch — ветки нет
    // ни локально, ни в origin (нейтральный ответ, не ошибка).
    importRun: (projectId: string) =>
      request<{ status: 'imported' | 'nothingToImport' | 'noBranch'; added: number; skipped: number }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/import`,
        { method: 'POST' }),
  },
};
