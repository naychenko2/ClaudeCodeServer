import { useEffect, useState } from 'react';
import type { GeneratedSkill } from '../types';
import { C, R, FONT } from '../lib/design';
import { api } from '../lib/api';
import { Modal, Button, TextField, TextArea, WaitingIndicator } from './ui';
import { useAiJob, runAiJob, resetAiJob } from '../lib/aiJobStore';

// Генерация нового навыка (SKILL.md) по свободному промпту с редактируемым превью.
// Контекст:
//  • persona   — после сохранения навык дополнительно привязывается к персоне (binding Skill);
//  • projectId — навык всё равно сохраняется глобально (project-scope создания у навыков нет),
//    projectId держим для onSaved-обновления списка панели проекта.
// Долгая генерация живёт в aiJobStore по ключу контекста — переживает случайное закрытие диалога.
interface Props {
  onClose: () => void;
  persona?: { id: string; name: string };
  projectId?: string;
  onSaved?: () => void;
  // Предзаполненный промпт (кнопка-продолжение «создать навык по этому описанию»)
  initialPrompt?: string;
}

// Приводит имя к безопасному слагу [a-z0-9-] — тот же контракт, что на бэке (SkillGenerationService.Slugify):
// имя = имя папки и frontmatter name = target привязки. Пустой → "skill".
function slugify(name: string): string {
  const s = (name || '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return s || 'skill';
}

// Собирает финальный SKILL.md: frontmatter name/description + тело.
function buildSkillMarkdown(name: string, description: string, body: string): string {
  const desc = description.replace(/\r?\n/g, ' ').trim();
  return `---\nname: ${name}\ndescription: ${desc}\n---\n\n${body.trim()}\n`;
}

export function SkillGenerateDialog({ onClose, persona, projectId, onSaved, initialPrompt }: Props) {
  const genKey = `skills-generate:${persona ? `persona:${persona.id}` : projectId ? `project:${projectId}` : 'global'}`;
  const job = useAiJob<GeneratedSkill>(genKey);

  const [prompt, setPrompt] = useState(initialPrompt ?? '');
  const [phase, setPhase] = useState<'input' | 'preview'>('input');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [body, setBody] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [existing, setExisting] = useState<Set<string>>(new Set());

  // Готовый результат генерации (в т.ч. если завершилась, пока диалог был закрыт) → в превью
  useEffect(() => {
    if (job.status === 'done' && job.result) {
      setName(job.result.name);
      setDescription(job.result.description);
      setBody(job.result.body);
      setPhase('preview');
      setError(null);
      resetAiJob(genKey);
    }
  }, [job.status, job.result, genKey]);

  // Существующие глобальные навыки — для мягкого предупреждения о коллизии имён
  useEffect(() => {
    api.skills.listGlobal()
      .then(list => setExisting(new Set(list.map(s => s.name.toLowerCase()))))
      .catch(() => { /* не критично: предупреждение просто не покажем */ });
  }, []);

  const running = job.status === 'running';
  const slug = slugify(name);
  const collision = phase === 'preview' && existing.has(slug);

  const startGenerate = () => {
    const p = prompt.trim();
    if (!p) return;
    setError(null);
    runAiJob<GeneratedSkill>(genKey, () => api.skills.generate(p));
  };

  const regenerate = () => {
    resetAiJob(genKey);
    setPhase('input');
    setError(null);
  };

  const save = async () => {
    if (!slug || !body.trim()) return;
    setSaving(true);
    setError(null);
    try {
      await api.skills.createSkill(slug, buildSkillMarkdown(slug, description, body));
      if (persona) await api.personas.addBinding(persona.id, { type: 'skill', target: slug });
      onSaved?.();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить навык');
    } finally { setSaving(false); }
  };

  const subtitle = persona
    ? `Опишите навык словами — ИИ соберёт SKILL.md и привяжет его к персоне «${persona.name}»`
    : 'Опишите навык словами — ИИ соберёт SKILL.md, вы проверите и сохраните';

  const footer = phase === 'preview' ? (
    <div style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
      <Button variant="ghost" disabled={saving} onClick={regenerate}>Перегенерировать</Button>
      <Button variant="secondary" disabled={saving} onClick={onClose}>Отмена</Button>
      <Button variant="primary" loading={saving} disabled={saving || !body.trim()} onClick={() => void save()}>
        Сохранить навык
      </Button>
    </div>
  ) : (
    <div style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
      <Button variant="ghost" onClick={onClose}>Отмена</Button>
      <Button variant="primary" disabled={running || !prompt.trim()} onClick={startGenerate}>
        ✨ Сгенерировать
      </Button>
    </div>
  );

  return (
    <Modal width={640} title="Создать навык по промпту" subtitle={subtitle} onClose={onClose} footer={footer}>
      {error && (
        <div style={{
          padding: '10px 12px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          borderRadius: R.lg, fontSize: 12.5, color: C.dangerText,
        }}>{error}</div>
      )}
      {job.status === 'error' && phase === 'input' && (
        <div style={{
          padding: '10px 12px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          borderRadius: R.lg, fontSize: 12.5, color: C.dangerText,
        }}>{job.error || 'Не удалось сгенерировать навык — попробуйте ещё раз.'}</div>
      )}

      {phase === 'input' ? (
        <>
          <TextArea
            value={prompt}
            onChange={setPrompt}
            autoFocus
            autoGrow
            minHeight={120}
            maxHeight={280}
            disabled={running}
            placeholder="Например: навык для извлечения таблиц из PDF в markdown — шаги, инструменты, формат вывода…"
          />
          {running && (
            <div style={{ padding: '8px 4px' }}>
              <WaitingIndicator hint="Генерирую навык — до минуты" />
            </div>
          )}
        </>
      ) : (
        <>
          <label style={labelStyle}>Имя навыка (слаг)</label>
          <TextField value={name} onChange={setName} mono placeholder="pdf-table-extract" />
          <div style={{ fontSize: 11.5, color: collision ? C.dangerText : C.textMuted, marginTop: -4 }}>
            {collision
              ? `Навык «${slug}» уже существует — сохранение перезапишет его.`
              : `Будет сохранён как ${slug}/SKILL.md`}
          </div>

          <label style={labelStyle}>Описание</label>
          <TextField value={description} onChange={setDescription} placeholder="Что делает навык и когда применять" />

          <label style={labelStyle}>Тело SKILL.md</label>
          <TextArea value={body} onChange={setBody} autoGrow minHeight={200} maxHeight={420}
            style={{ fontFamily: FONT.mono, fontSize: 12.5 }} />

          <div style={{ fontSize: 12, color: C.textMuted }}>
            Навык будет добавлен в глобальные (~/.claude/skills){persona ? ` и привязан к персоне «${persona.name}»` : ''}.
          </div>
        </>
      )}
    </Modal>
  );
}

const labelStyle = {
  fontSize: 12, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
  marginBottom: -4,
} as const;
