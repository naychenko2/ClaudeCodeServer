import { useEffect, useState } from 'react';
import { ChevronLeft } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { PantheonTemplate, Persona, PersonaScope } from '../../types';
import { api } from '../../lib/api';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R, FIELD, SHADOW } from '../../lib/design';
import { Toolbar, tbBtnGhost } from '../../components/Toolbar';
import { IconButton } from '../../components/ui';
import { SectionLabel } from '../tasks/bits';
import { agentDotColor } from '../../components/AgentSelector';
import { PERSONA_TEMPLATES, type PersonaTemplate } from './personaTemplates';

// Экран быстрого создания персоны по свободному промпту — первый шаг флоу «Новая персона».
// Пользователь описывает, кто это и чем будет заниматься, LLM придумывает роль/имя/
// характер/приветствие/цвет и генерирует фото-аватар. «Заполнить вручную» — запасной
// путь к пустой PersonaForm. Используется и в глобальной студии (PersonasPage),
// и в проектной вкладке «Команда» (ProjectPersonaPane).
export function PersonaQuickCreate({ scope, projectId, onCreated, onManual, onTemplate, onCancel, onBack, isMobile }: {
  scope: PersonaScope;
  projectId?: string;
  onCreated: (p: Persona) => void;
  onManual: () => void;
  // Выбор готового шаблона роли — родитель откроет PersonaForm с предзаполнением
  onTemplate?: (t: PersonaTemplate) => void;
  // Отмена создания (кнопка в тулбаре) — есть не во всех точках встраивания
  onCancel?: () => void;
  // Кнопка «Назад» для мобильной раскладки
  onBack?: () => void;
  isMobile?: boolean;
}) {
  const [prompt, setPrompt] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [focused, setFocused] = useState(false);
  // Шаблоны «Пантеон OmO»: каталог живёт на бэкенде (GET /api/personas/pantheon),
  // там же — признак «уже подключена» (connectedPersonaId). Пока грузится —
  // секция просто не показывается.
  const [omoTemplates, setOmoTemplates] = useState<PantheonTemplate[] | null>(null);
  const loadPantheon = () =>
    api.personas.pantheon()
      .then(r => setOmoTemplates(r.templates))
      .catch(() => { /* не критично: секция пантеона просто не появится */ });
  useEffect(() => { void loadPantheon(); }, []);

  const canSubmit = !!prompt.trim() && !busy;

  const submit = async () => {
    if (!canSubmit) return;
    setBusy(true);
    setError(null);
    try {
      const created = await api.personas.quickCreate({ prompt: prompt.trim(), scope, projectId });
      bumpPersonas();
      onCreated(created);
      // busy не сбрасываем: родитель уводит на редактор созданной персоны
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось создать персону. Попробуйте ещё раз.');
      setBusy(false);
    }
  };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <Toolbar isMobile={isMobile} style={{ borderLeft: `3px solid ${C.accent}` }}>
        {onBack && (
          <IconButton onClick={onBack} title="Назад" size={isMobile ? 'lg' : 'md'}>
            <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          </IconButton>
        )}
        <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Новая персона
        </div>
        {onCancel && <button onClick={onCancel} style={tbBtnGhost}>Отмена</button>}
      </Toolbar>
      {/* Тонкая акцентная полоса — как у остальных панелей студии */}
      <div style={{ flex: 'none', height: 2, background: `${C.accent}55` }} />

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{
          maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
          padding: isMobile ? '24px 16px 40px' : '40px 24px 60px',
          display: 'flex', flexDirection: 'column', gap: 20, fontFamily: FONT.sans,
        }}>
          {/* Заголовок и подводка: что произойдёт после нажатия «Создать» */}
          <div>
            <div style={{ fontFamily: FONT.serif, fontSize: isMobile ? 21 : 24, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
              Опишите персону
            </div>
            <div style={{ marginTop: 6, fontSize: 13.5, color: C.textMuted, lineHeight: 1.5 }}>
              Кто это и чем будет заниматься. ИИ придумает роль, имя, характер и приветствие — и сразу подберёт умения: проекты, базы знаний и инструменты под задачи персоны.
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <SectionLabel>Описание</SectionLabel>
            <textarea
              value={prompt}
              onChange={e => setPrompt(e.target.value)}
              placeholder={'Опишите, кто это и чем будет заниматься… Например: «Личный тренер по бегу: мотивирует, составляет планы тренировок»'}
              autoFocus={!isMobile}
              disabled={busy}
              onFocus={() => setFocused(true)}
              onBlur={() => setFocused(false)}
              onKeyDown={e => {
                // Ctrl/Cmd+Enter — отправить, как в композере чата
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); void submit(); }
              }}
              style={{
                minHeight: 120, resize: 'vertical', lineHeight: 1.5,
                background: FIELD.background, color: FIELD.color, fontSize: 14,
                border: `1px solid ${focused ? FIELD.borderFocus : C.border}`,
                borderRadius: FIELD.borderRadius, padding: '12px 14px',
                outline: 'none', fontFamily: 'inherit', width: '100%', boxSizing: 'border-box',
                boxShadow: focused ? SHADOW.focus : 'none',
                transition: 'border-color 0.15s, box-shadow 0.15s',
                opacity: busy ? 0.65 : 1,
              }}
            />
            {/* Под полем: мягкий статус во время генерации или ошибка с возможностью повторить */}
            {busy ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12.5, color: C.textMuted }}>
                <span style={{
                  width: 8, height: 8, borderRadius: R.full, background: C.accent, flexShrink: 0,
                  animation: 'cc-quick-pulse 1.2s ease-in-out infinite',
                }} />
                Придумываю характер, подбираю умения и генерирую аватар — до пары минут
                <style>{'@keyframes cc-quick-pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 1; } }'}</style>
              </div>
            ) : error ? (
              <div style={{ fontSize: 12.5, color: C.danger, lineHeight: 1.4 }}>{error}</div>
            ) : null}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <button
              onClick={() => void submit()}
              disabled={!canSubmit}
              style={{
                background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
                padding: '10px 18px', fontSize: 13.5, fontWeight: 600, fontFamily: FONT.sans,
                cursor: canSubmit ? 'pointer' : 'default', opacity: canSubmit ? 1 : 0.55,
                display: 'inline-flex', alignItems: 'center', gap: 7,
              }}>
              {busy ? 'Создаю персону…' : '✨ Создать'}
            </button>
            <button
              onClick={onManual}
              disabled={busy}
              style={{
                background: 'none', border: `1px solid ${C.border}`, color: C.textSecondary,
                borderRadius: R.md, padding: '10px 16px', fontSize: 13.5, fontWeight: 600,
                fontFamily: FONT.sans, cursor: busy ? 'default' : 'pointer', opacity: busy ? 0.55 : 1,
              }}>
              Заполнить вручную
            </button>
          </div>

          {/* Библиотека шаблонов: готовые роли с выверенным характером.
              Выбор открывает ручную форму, предзаполненную шаблоном. */}
          {onTemplate && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10, opacity: busy ? 0.5 : 1, pointerEvents: busy ? 'none' : 'auto' }}>
              <SectionLabel>Или начните с шаблона</SectionLabel>
              <div style={{ display: 'grid', gridTemplateColumns: isMobile ? 'minmax(0,1fr)' : 'minmax(0,1fr) minmax(0,1fr)', gap: 10 }}>
                {PERSONA_TEMPLATES.map(t => (
                  <TemplateCard key={t.key} template={t} title={t.role} onSelect={() => onTemplate(t)} />
                ))}
              </div>
            </div>
          )}

          {/* Пантеон OmO: роли-специалисты из oh-my-openagent с полными регламентами.
              Каталог приходит с бэкенда; клик по карточке — кастомная копия через форму.
              Роли доступны всегда и в селекторах выбора собеседника (usePantheon),
              там же материализуются по требованию — здесь их создавать не нужно. */}
          {onTemplate && omoTemplates && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10, opacity: busy ? 0.5 : 1, pointerEvents: busy ? 'none' : 'auto' }}>
              <SectionLabel>Пантеон OmO</SectionLabel>
              <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.45, marginTop: -4 }}>
                Роли-специалисты из oh-my-openagent. Они всегда доступны при выборе собеседника —
                карточкой ниже можно создать свою вариацию роли с правками.
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: isMobile ? 'minmax(0,1fr)' : 'minmax(0,1fr) minmax(0,1fr)', gap: 10 }}>
                {omoTemplates.map(t => (
                  <TemplateCard
                    key={t.key}
                    template={pantheonToTemplate(t)}
                    title={`${t.role} (${t.name})`}
                    badge={t.connectedPersonaId != null ? 'в команде' : undefined}
                    onSelect={() => onTemplate(pantheonToTemplate(t))}
                  />
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// Маппинг шаблона пантеона (API) в фронтовый PersonaTemplate — для карточки
// и предзаполнения PersonaForm (onTemplate)
function pantheonToTemplate(t: PantheonTemplate): PersonaTemplate {
  return {
    key: t.key,
    role: t.role,
    namePlaceholder: t.name,
    description: t.description,
    contract: t.contract,
    greeting: t.greeting,
    avatarColor: t.color,
    tools: t.tools ?? undefined,
    access: t.access,
    model: t.model,
    effort: t.effort,
  };
}

// Карточка шаблона роли — общая для наших шаблонов и пантеона OmO.
// badge — пилюля в правом углу (у подключённых ролей пантеона — «в команде»).
function TemplateCard({ template: t, title, badge, onSelect }: {
  template: PersonaTemplate;
  title: string;
  badge?: string;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      onMouseEnter={e => { e.currentTarget.style.borderColor = agentDotColor(t.avatarColor); e.currentTarget.style.background = C.bgWhite; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.background = C.bgWhite; }}
      style={{
        display: 'flex', alignItems: 'center', gap: 11, textAlign: 'left',
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: '11px 13px', cursor: 'pointer', fontFamily: FONT.sans,
        transition: 'border-color 0.15s', minWidth: 0,
      }}
    >
      <span style={{
        width: 36, height: 36, borderRadius: R.full, flexShrink: 0,
        background: agentDotColor(t.avatarColor), color: '#fff',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontSize: 15, fontWeight: 600,
      }}>
        {t.role.slice(0, 1)}
      </span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>
          {title}
        </span>
        <span style={{
          display: 'block', fontSize: 12, color: C.textMuted, marginTop: 2, lineHeight: 1.4,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {t.description}
        </span>
      </span>
      {badge && (
        <span style={{
          flexShrink: 0, alignSelf: 'flex-start',
          background: C.accentLight, color: C.accent, borderRadius: R.full,
          padding: '2px 8px', fontSize: 9.5, fontWeight: 700,
          letterSpacing: '0.06em', textTransform: 'uppercase', whiteSpace: 'nowrap',
        }}>
          {badge}
        </span>
      )}
    </button>
  );
}
