import { C } from '../../lib/design';

// Чипы-подсказки для empty state проектного чата
const HINTS = ['Объясни структуру проекта', 'Найди и почини падающие тесты'];

// Чипы-подсказки для чата вне проекта — универсальный ассистент (тексты, поиск, генерация медиа)
const CHAT_HINTS = ['Найди информацию в интернете', 'Напиши пост для соцсетей', 'Сгенерируй картинку'];

// Empty state пустого чата: приветствие/чипы-подсказки; для проекта без CLAUDE.md — CTA /init
export function ChatEmptyState({ hasProject, hasCLAUDEmd, onHint }: {
  hasProject: boolean;
  hasCLAUDEmd: boolean | null;
  onHint: (hint: string) => void;
}) {
  return (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: 12, paddingTop: 40,
          }}>
            {/* Логотип */}
            <div style={{
              width: 46, height: 46, borderRadius: 13, background: C.accent,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}>
              <div style={{
                width: 22, height: 22, borderRadius: '50%', background: '#FFF',
              }} />
            </div>

            {!hasProject ? (
              <>
                {/* Приветствие чата вне проекта — general-purpose ассистент */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Чем помочь?
                </div>

                <div style={{ fontSize: 13, color: '#8A8070', textAlign: 'center', maxWidth: 320 }}>
                  Спросите что угодно — тексты и идеи, поиск в интернете, генерация картинок
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {CHAT_HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: '#FFF', border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = '#FFF')}
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
                <div style={{ fontSize: 13, color: '#8A8070', textAlign: 'center', maxWidth: 260 }}>
                  Запустите /init, чтобы Claude изучил проект и создал CLAUDE.md
                </div>

                {/* Кнопка CTA */}
                <button
                  onClick={() => onHint('/init')}
                  style={{
                    marginTop: 4,
                    background: C.accent, border: 'none',
                    borderRadius: 10, padding: '10px 20px',
                    fontSize: 13, color: '#FFF', cursor: 'pointer',
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
                <div style={{ fontSize: 13, color: '#8A8070', textAlign: 'center' }}>
                  Опишите задачу или начните с подсказки
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: '#FFF', border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = '#FFF')}
                    >
                      {hint}
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
  );
}
