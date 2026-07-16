import { useState } from 'react';
import { Search, ExternalLink } from 'lucide-react';
import type { RegistrySkill, SkillSuggestion } from '../types';
import { C, R, FONT } from '../lib/design';
import { api } from '../lib/api';
import { Modal, Button, IconField, WaitingIndicator } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useAiJob, runAiJob } from '../lib/aiJobStore';
import { SkillGenerateDialog } from './SkillGenerateDialog';

// Контекст установки диалога определяет доступные действия:
//  • persona     — «Установить персоне» (глобально + привязка Skill) и «✨ Подобрать под персону»;
//  • projectId   — «В проект» / «Глобально» и «✨ Подобрать под проект»;
//  • без обоих   — «Установить» (глобально) и подбор только по свободному запросу.
interface Props {
  onClose: () => void;
  projectId?: string;
  persona?: { id: string; name: string };
  onInstalled?: () => void;
}

type CardState = 'idle' | 'busy' | 'project-done' | 'global-done' | 'error';

// Результат «✨ Подобрать» — статус и результат живут в aiJobStore по ключу контекста,
// переживают закрытие/переоткрытие диалога (тот же персона/проект/глобально)
interface SuggestJobResult {
  results: RegistrySkill[];
  reasons: Record<string, string>;
}

const keyOf = (s: RegistrySkill) => `${s.source}@${s.skill}`;

export function SkillSearchDialog({ onClose, projectId, persona, onInstalled }: Props) {
  const suggestKey = `skills-suggest:${persona ? `persona:${persona.id}` : projectId ? `project:${projectId}` : 'global'}`;
  const suggestJob = useAiJob<SuggestJobResult>(suggestKey);

  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [results, setResults] = useState<RegistrySkill[]>([]);
  // Если по этому контексту уже идёт/готов подбор (диалог переоткрыли) — сразу его и показываем
  const [mode, setMode] = useState<'search' | 'suggest' | null>(
    () => (suggestJob.status !== 'idle' ? 'suggest' : null),
  );
  const [translatedQuery, setTranslatedQuery] = useState<string | null>(null);
  const [cardStates, setCardStates] = useState<Record<string, CardState>>({});
  const [showGenerate, setShowGenerate] = useState(false);

  const canContextSuggest = !!persona || !!projectId;
  const suggesting = suggestJob.status === 'running';
  const busy = mode === 'suggest' ? suggesting : mode === 'search' ? loading : false;
  const displayResults = mode === 'suggest' ? (suggestJob.result?.results ?? []) : results;
  const displayReasons = mode === 'suggest' ? (suggestJob.result?.reasons ?? {}) : {};
  const suggestError = mode === 'suggest' && suggestJob.status === 'error' ? suggestJob.error : null;

  const runFind = async () => {
    const q = query.trim();
    if (q.length < 2) { setError('Введите минимум 2 символа'); return; }
    setLoading(true); setError(null); setMode('search'); setTranslatedQuery(null);
    try {
      const { results, translatedQuery } = await api.skills.find(q);
      setResults(results);
      setTranslatedQuery(translatedQuery);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Поиск не удался');
      setResults([]);
    } finally { setLoading(false); }
  };

  const runSuggest = () => {
    const q = query.trim();
    // С текстом — подбор по запросу; без текста — по контексту (персона/проект)
    const ctx = q.length >= 2
      ? { query: q }
      : persona ? { personaId: persona.id }
      : projectId ? { projectId }
      : null;
    if (!ctx) { setError('Введите запрос для подбора'); return; }
    setError(null); setMode('suggest'); setTranslatedQuery(null);
    runAiJob<SuggestJobResult>(suggestKey, async () => {
      const { candidates } = await api.skills.suggest(ctx);
      return {
        results: candidates.map((c: SkillSuggestion) => c.skill),
        reasons: Object.fromEntries(candidates.map((c: SkillSuggestion) => [keyOf(c.skill), c.reason])),
      };
    });
  };

  const setCard = (k: string, s: CardState) =>
    setCardStates(prev => ({ ...prev, [k]: s }));

  const installProject = async (s: RegistrySkill) => {
    if (!projectId) return;
    const k = keyOf(s);
    setCard(k, 'busy');
    try {
      await api.skills.install(s.source, s.skill, 'project', projectId);
      setCard(k, 'project-done'); onInstalled?.();
    } catch { setCard(k, 'error'); }
  };

  const installGlobal = async (s: RegistrySkill) => {
    const k = keyOf(s);
    setCard(k, 'busy');
    try {
      await api.skills.install(s.source, s.skill, 'global');
      setCard(k, 'global-done'); onInstalled?.();
    } catch { setCard(k, 'error'); }
  };

  const installForPersona = async (s: RegistrySkill) => {
    if (!persona) return;
    const k = keyOf(s);
    setCard(k, 'busy');
    try {
      const r = await api.skills.installForPersona(persona.id, s.source, s.skill);
      setCard(k, 'global-done'); onInstalled?.();
      if (r.warning) setError(`Навык установлен, но привязка не создана: ${r.warning}`);
    } catch { setCard(k, 'error'); }
  };

  const subtitle = persona
    ? `Найдите навык и добавьте его персоне «${persona.name}»`
    : projectId
    ? 'Найдите навык и установите в проект или глобально'
    : 'Найдите навык и установите глобально';

  return (
    <Modal
      width={620}
      title="Навыки из реестра"
      subtitle={subtitle}
      onClose={onClose}
      footer={<Button variant="ghost" onClick={onClose}>Закрыть</Button>}
    >
      {/* Поиск + подбор */}
      <div style={{ display: 'flex', gap: 8, alignItems: 'stretch' }}>
        <div style={{ flex: 1 }}>
          <IconField
            value={query}
            onChange={setQuery}
            autoFocus
            placeholder={canContextSuggest ? 'Найти навык или описать задачу…' : 'Найти навык…'}
            height={40}
            radius={R.lg}
            fontSize={13.5}
            onEnter={runFind}
            icon={<Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          />
        </div>
        <Button variant="secondary" onClick={runFind} disabled={loading || suggesting}>Найти</Button>
        <Button variant="primary" onClick={runSuggest} disabled={loading || suggesting}>✨ Подобрать</Button>
      </div>

      {canContextSuggest && (
        <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: -8 }}>
          «✨ Подобрать» без запроса предложит навыки под {persona ? 'персону' : 'проект'} с помощью ИИ.
        </div>
      )}

      {/* Нет подходящего в реестре — сгенерировать свой навык по описанию */}
      <div style={{ fontSize: 12, color: C.textMuted, marginTop: -4 }}>
        Нет подходящего?{' '}
        <button
          onClick={() => setShowGenerate(true)}
          style={{
            background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
            fontSize: 12, fontFamily: FONT.sans, textDecoration: 'underline', padding: 0,
          }}
        >
          ✨ Создать навык по промпту
        </button>
      </div>

      {(error || suggestError) && (
        <div style={{
          padding: '10px 12px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          borderRadius: R.lg, fontSize: 12.5, color: C.dangerText,
        }}>{error || suggestError}</div>
      )}

      {/* Показываем, что запрос переведён на английский для поиска по реестру */}
      {!loading && mode === 'search' && translatedQuery && (
        <div style={{ fontSize: 12, color: C.textMuted, marginTop: -4 }}>
          Искал по-английски: <span style={{ fontFamily: FONT.mono, color: C.textSecondary }}>{translatedQuery}</span>
        </div>
      )}

      {/* Результаты */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minHeight: 80 }}>
        {busy && (
          mode === 'suggest' ? (
            <div style={{ padding: '20px 4px' }}>
              <WaitingIndicator hint="Подбор навыков под роль/проект — до минуты" />
            </div>
          ) : (
            <div style={{ padding: '28px 0', textAlign: 'center', fontSize: 13, color: C.textMuted }}>
              Ищем…
            </div>
          )
        )}

        {!busy && mode && !suggestError && displayResults.length === 0 && !error && (
          <div style={{ padding: '28px 0', textAlign: 'center', fontSize: 13, color: C.textMuted }}>
            Ничего не найдено. Попробуйте другой запрос.
          </div>
        )}

        {!busy && displayResults.map(s => (
          <SkillResultCard
            key={keyOf(s)}
            skill={s}
            reason={displayReasons[keyOf(s)]}
            state={cardStates[keyOf(s)] ?? 'idle'}
            persona={persona}
            projectId={projectId}
            onInstallProject={() => installProject(s)}
            onInstallGlobal={() => installGlobal(s)}
            onInstallPersona={() => installForPersona(s)}
          />
        ))}
      </div>

      {showGenerate && (
        <SkillGenerateDialog
          persona={persona}
          projectId={projectId}
          onClose={() => setShowGenerate(false)}
          onSaved={() => { onInstalled?.(); setShowGenerate(false); }}
        />
      )}
    </Modal>
  );
}

function SkillResultCard({
  skill, reason, state, persona, projectId,
  onInstallProject, onInstallGlobal, onInstallPersona,
}: {
  skill: RegistrySkill;
  reason?: string;
  state: CardState;
  persona?: { id: string; name: string };
  projectId?: string;
  onInstallProject: () => void;
  onInstallGlobal: () => void;
  onInstallPersona: () => void;
}) {
  const busy = state === 'busy';
  const done = state === 'project-done' || state === 'global-done';

  return (
    <div style={{
      border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '11px 14px',
      background: C.bgWhite, display: 'flex', flexDirection: 'column', gap: 6,
    }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: 13, fontWeight: 600, color: C.textHeading }}>
          {skill.skill}
        </span>
        <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.mono }}>{skill.source}</span>
        {skill.installs != null && (
          <span style={{ marginLeft: 'auto', fontSize: 11.5, color: C.textMuted }}>
            {formatInstalls(skill.installs)} установок
          </span>
        )}
      </div>

      {reason && (
        <div style={{ fontSize: 12.5, color: C.accent, lineHeight: 1.45 }}>
          ✨ {reason}
        </div>
      )}
      {skill.description && (
        <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
          {skill.description}
        </div>
      )}
      {skill.url && (
        <a
          href={skill.url}
          target="_blank"
          rel="noopener noreferrer"
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 4, alignSelf: 'flex-start',
            fontSize: 12, color: C.accent, textDecoration: 'none', fontFamily: FONT.sans,
          }}
        >
          Подробнее о навыке
          <ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </a>
      )}

      <div style={{ display: 'flex', gap: 8, marginTop: 2, alignItems: 'center' }}>
        {done ? (
          <span style={{ fontSize: 12.5, color: C.successText, fontWeight: 600 }}>
            ✓ Установлено{state === 'project-done' ? ' в проект' : ''}
          </span>
        ) : state === 'error' ? (
          <span style={{ fontSize: 12.5, color: C.dangerText }}>Ошибка установки</span>
        ) : persona ? (
          <Button variant="primary" size="sm" loading={busy} onClick={onInstallPersona}>
            Установить персоне
          </Button>
        ) : projectId ? (
          <>
            <Button variant="primary" size="sm" loading={busy} onClick={onInstallProject}>В проект</Button>
            <Button variant="secondary" size="sm" disabled={busy} onClick={onInstallGlobal}>Глобально</Button>
          </>
        ) : (
          <Button variant="primary" size="sm" loading={busy} onClick={onInstallGlobal}>Установить</Button>
        )}
      </div>
    </div>
  );
}

function formatInstalls(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}
