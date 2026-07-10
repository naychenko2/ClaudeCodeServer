import { useState } from 'react';
import type { Persona, PersonaScope } from '../../types';
import { api } from '../../lib/api';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R, FIELD, SHADOW } from '../../lib/design';
import { Toolbar, tbBtnGhost } from '../../components/Toolbar';
import { IconButton } from '../../components/ui';
import { SectionLabel } from '../tasks/bits';

// Экран быстрого создания персоны по свободному промпту — первый шаг флоу «Новая персона».
// Пользователь описывает, кто это и чем будет заниматься, LLM придумывает роль/имя/
// характер/приветствие/цвет и генерирует фото-аватар. «Заполнить вручную» — запасной
// путь к пустой PersonaForm. Используется и в глобальной студии (PersonasPage),
// и в проектной вкладке «Команда» (ProjectPersonaPane).
export function PersonaQuickCreate({ scope, projectId, onCreated, onManual, onCancel, onBack, isMobile }: {
  scope: PersonaScope;
  projectId?: string;
  onCreated: (p: Persona) => void;
  onManual: () => void;
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
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M15 18l-6-6 6-6" />
            </svg>
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
              Кто это и чем будет заниматься — остальное придумает ИИ: роль, имя, характер, приветствие и аватар.
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
                Придумываю характер и генерирую аватар — до минуты
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
        </div>
      </div>
    </div>
  );
}
