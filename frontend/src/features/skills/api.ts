// Домен «Навыки» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// проектные и глобальные скиллы, файлы агентов, реестр skills.sh (поиск, установка,
// подбор и генерация). Тонкая обёртка над живым REST; DTO-типы — types/index.ts
// (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import { projectRequest } from '../../lib/deviceAgent';
import type {
  GeneratedSkill,
  RegistrySkill,
  SkillInfo,
  SkillsData,
  SkillSuggestion,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.skills.* остаются прежними для всех потребителей).
export const skillsApi = {
  skills: {
    list: (projectId: string) => projectRequest<SkillsData>(`/projects/${projectId}/skills`),
    // Глобальные скиллы без привязки к проекту (для чатов вне проекта)
    listGlobal: () => request<SkillInfo[]>('/skills'),
    getSkill: (skillName: string) => request<{ content: string }>(`/skills/${skillName}`),
    saveSkill: (skillName: string, content: string) =>
      request<void>(`/skills/${skillName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createSkill: (name: string, content: string) =>
      request<{ name: string }>('/skills', { method: 'POST', body: JSON.stringify({ name, content }) }),
    getAgent: (projectId: string, agentName: string) =>
      projectRequest<{ content: string }>(`/projects/${projectId}/agents/${agentName}`),
    saveAgent: (projectId: string, agentName: string, content: string) =>
      projectRequest<void>(`/projects/${projectId}/agents/${agentName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createAgent: (projectId: string, name: string, content: string) =>
      projectRequest<{ name: string }>(`/projects/${projectId}/agents`, { method: 'POST', body: JSON.stringify({ name, content }) }),

    // --- Реестр skills.sh (обёртка npx skills) ---
    // Поиск навыков по реестру; owner — опциональное сужение по GitHub-владельцу.
    // Русский запрос переводится на английский (реестр англоязычный) — translatedQuery
    // показывает, что реально искали (null, если перевод не понадобился).
    // Все операции с реестром долгие (перевод LLM, клонирование репозиториев, подбор) —
    // задаём щедрый timeoutMs, иначе дефолтные 30с обрывают запрос и офлайн-слой
    // ложно решает, что мы офлайн («Действие недоступно офлайн»).
    find: (q: string, owner?: string) =>
      request<{ query: string; translatedQuery: string | null; results: RegistrySkill[] }>(
        `/skills/find?q=${encodeURIComponent(q)}${owner ? `&owner=${encodeURIComponent(owner)}` : ''}`,
        { timeoutMs: 90_000 }),
    // Установка навыка: scope 'project' требует projectId, 'global' — нет
    install: (source: string, skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<{ installed: string; scope: string }>('/skills/install', {
        method: 'POST', body: JSON.stringify({ source, skill, scope, projectId }), timeoutMs: 180_000,
      }),
    uninstall: (skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<void>(`/skills/installed?skill=${encodeURIComponent(skill)}&scope=${scope}${projectId ? `&projectId=${projectId}` : ''}`,
        { method: 'DELETE', timeoutMs: 90_000 }),
    // LLM-подбор: ровно один из personaId / projectId / query
    suggest: (ctx: { personaId?: string; projectId?: string; query?: string }) =>
      request<{ candidates: SkillSuggestion[] }>('/skills/suggest',
        { method: 'POST', body: JSON.stringify(ctx), timeoutMs: 200_000 }),
    // LLM-генерация нового навыка (SKILL.md) по свободному промпту: кандидат для превью, не сохраняется
    generate: (prompt: string) =>
      request<GeneratedSkill>('/skills/generate',
        { method: 'POST', body: JSON.stringify({ prompt }), timeoutMs: 200_000 }),
    // Установить навык персоне: глобальная установка + привязка (Skill)
    installForPersona: (personaId: string, source: string, skill: string) =>
      request<{ installed: string; bound: boolean; warning?: string }>(`/personas/${personaId}/skills`, {
        method: 'POST', body: JSON.stringify({ source, skill }), timeoutMs: 180_000,
      }),
  },
};
