import { useState } from 'react';
import type { RegistrySkill, SkillSuggestion } from '../types';
import { C, R, FONT } from '../lib/design';
import { api } from '../lib/api';
import { Modal, Button, IconField } from './ui';

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

const keyOf = (s: RegistrySkill) => `${s.source}@${s.skill}`;

export function SkillSearchDialog({ onClose, projectId, persona, onInstalled }: Props) {
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [results, setResults] = useState<RegistrySkill[]>([]);
  // reason по ключу навыка — заполняется при LLM-подборе (иначе карточка без обоснования)
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [mode, setMode] = useState<'search' | 'suggest' | null>(null);
  const [cardStates, setCardStates] = useState<Record<string, CardState>>({});

  const canContextSuggest = !!persona || !!projectId;

  const runFind = async () => {
    const q = query.trim();
    if (q.length < 2) { setError('Введите минимум 2 символа'); return; }
    setLoading(true); setError(null); setReasons({}); setMode('search');
    try {
      setResults(await api.skills.find(q));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Поиск не удался');
      setResults([]);
    } finally { setLoading(false); }
  };

  const runSuggest = async () => {
    const q = query.trim();
    // С текстом — подбор по запросу; без текста — по контексту (персона/проект)
    const ctx = q.length >= 2
      ? { query: q }
      : persona ? { personaId: persona.id }
      : projectId ? { projectId }
      : null;
    if (!ctx) { setError('Введите запрос для подбора'); return; }
    setLoading(true); setError(null); setMode('suggest');
    try {
      const { candidates } = await api.skills.suggest(ctx);
      setResults(candidates.map((c: SkillSuggestion) => c.skill));
      setReasons(Object.fromEntries(candidates.map((c: SkillSuggestion) => [keyOf(c.skill), c.reason])));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Подбор не удался');
      setResults([]);
    } finally { setLoading(false); }
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
            icon={svg(<><circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" /></>)}
          />
        </div>
        <Button variant="secondary" onClick={runFind} disabled={loading}>Найти</Button>
        <Button variant="primary" onClick={runSuggest} disabled={loading}>✨ Подобрать</Button>
      </div>

      {canContextSuggest && (
        <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: -8 }}>
          «✨ Подобрать» без запроса предложит навыки под {persona ? 'персону' : 'проект'} с помощью ИИ.
        </div>
      )}

      {error && (
        <div style={{
          padding: '10px 12px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          borderRadius: R.lg, fontSize: 12.5, color: C.dangerText,
        }}>{error}</div>
      )}

      {/* Результаты */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minHeight: 80 }}>
        {loading && (
          <div style={{ padding: '28px 0', textAlign: 'center', fontSize: 13, color: C.textMuted }}>
            {mode === 'suggest' ? 'ИИ подбирает навыки — до минуты…' : 'Ищем…'}
          </div>
        )}

        {!loading && mode && results.length === 0 && !error && (
          <div style={{ padding: '28px 0', textAlign: 'center', fontSize: 13, color: C.textMuted }}>
            Ничего не найдено. Попробуйте другой запрос.
          </div>
        )}

        {!loading && results.map(s => (
          <SkillResultCard
            key={keyOf(s)}
            skill={s}
            reason={reasons[keyOf(s)]}
            state={cardStates[keyOf(s)] ?? 'idle'}
            persona={persona}
            projectId={projectId}
            onInstallProject={() => installProject(s)}
            onInstallGlobal={() => installGlobal(s)}
            onInstallPersona={() => installForPersona(s)}
          />
        ))}
      </div>
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

function svg(children: React.ReactNode, size = 15) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      {children}
    </svg>
  );
}
