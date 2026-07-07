import type { Session } from '../../types';
import { C } from '../../lib/design';
import { NewChatSetup } from './NewChatSetup';

// Чипы-подсказки для empty state проектного чата
const HINTS = ['Объясни структуру проекта', 'Найди и почини падающие тесты'];

// Чипы-подсказки для чата вне проекта — универсальный ассистент (тексты, поиск, генерация медиа)
const CHAT_HINTS = ['Найди информацию в интернете', 'Напиши пост для соцсетей', 'Сгенерируй картинку'];

// Empty state пустого чата: приветствие/чипы-подсказки; для проекта без CLAUDE.md — CTA /init.
// Внизу — настройка будущего чата (модель + усилие рассуждения), пока не отправлено первое сообщение.
export function ChatEmptyState({ hasProject, hasCLAUDEmd, onHint, session, onSessionUpdated, isMobile }: {
  hasProject: boolean;
  hasCLAUDEmd: boolean | null;
  onHint: (hint: string) => void;
  session?: Session;
  onSessionUpdated?: (s: Session) => void;
  isMobile?: boolean;
}) {
  return (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: 12, paddingTop: 40,
          }}>
            {/* Логотип */}
            <img src="/favicon.svg" alt="" width={46} height={46} style={{ display: 'block' }} />

            {!hasProject ? (
              <>
                {/* Приветствие чата вне проекта — general-purpose ассистент */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Чем помочь?
                </div>

                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center', maxWidth: 320 }}>
                  Спросите что угодно — тексты и идеи, поиск в интернете, генерация картинок
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {CHAT_HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = C.bgWhite)}
                    >
                      {hint}
                    </button>
                  ))}
                </div>
              </>
            ) : hasCLAUDEmd === false ? (
              <>
                {/* Заголовок */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Новый проект
                </div>

                {/* Подзаголовок */}
                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center', maxWidth: 260 }}>
                  Запустите /init, чтобы Claude изучил проект и создал CLAUDE.md
                </div>

                {/* Кнопка CTA */}
                <button
                  onClick={() => onHint('/init')}
                  style={{
                    marginTop: 4,
                    background: C.accent, border: 'none',
                    borderRadius: 10, padding: '10px 20px',
                    fontSize: 13, color: C.onAccent, cursor: 'pointer',
                    fontFamily: 'inherit', fontWeight: 500,
                  }}
                  onMouseEnter={e => (e.currentTarget.style.opacity = '0.85')}
                  onMouseLeave={e => (e.currentTarget.style.opacity = '1')}
                >
                  Инициализировать проект
                </button>
              </>
            ) : (
              <>
                {/* Заголовок */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Чем помочь?
                </div>

                {/* Подзаголовок */}
                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center' }}>
                  Опишите задачу или начните с подсказки
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = C.bgWhite)}
                    >
                      {hint}
                    </button>
                  ))}
                </div>
              </>
            )}

            {/* Настройка чата — модель и усилие рассуждения (до первого сообщения) */}
            {session && (
              <NewChatSetup session={session} onSessionUpdated={onSessionUpdated} isMobile={isMobile} />
            )}
          </div>
  );
}
