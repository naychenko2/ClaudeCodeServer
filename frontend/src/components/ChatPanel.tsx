import { useState, useRef, useEffect } from 'react';
import type { Project, Session, ChatItem, FileEntry } from '../types';
import { useSession } from '../hooks/useSession';
import { api } from '../lib/api';
import { Composer } from './Composer';

interface Props {
  session: Session;
  project: Project;
  onOpenFile: (path: string) => void;
  pendingMessage?: string;
  onPendingMessageSent?: () => void;
}

// Спиннер для выполняющегося инструмента
function ToolSpinner() {
  return <div className="tool-spinner" />;
}

// Заголовок блока Claude: аватар + подпись
function ClaudeHeader() {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <div style={{
        width: 22, height: 22, borderRadius: 6, background: '#D97757',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        <div style={{ width: 9, height: 9, borderRadius: '50%', background: '#F4F0E8' }} />
      </div>
      <span style={{
        fontFamily: "'JetBrains Mono', monospace",
        fontSize: 11, letterSpacing: '0.06em', color: '#9A8F7E',
      }}>CLAUDE</span>
    </div>
  );
}

// Чипы-подсказки для empty state
const HINTS = ['Объясни структуру проекта', 'Найди и почини падающие тесты'];

// Модальный пикер вложений
interface AttachPickerProps {
  projectId: string;
  onPick: (path: string) => void;
  onClose: () => void;
}

function AttachPicker({ projectId, onPick, onClose }: AttachPickerProps) {
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.files.list(projectId)
      .then(setFiles)
      .finally(() => setLoading(false));
  }, [projectId]);

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        zIndex: 1000,
      }}
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{
          background: '#F4F0E8', borderRadius: 20, padding: '16px 0',
          minWidth: 340, maxWidth: 440, maxHeight: '60vh',
          display: 'flex', flexDirection: 'column',
          boxShadow: '0 24px 60px rgba(23,19,15,0.4)',
        }}
      >
        <div style={{ padding: '0 18px 12px', fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 18, letterSpacing: '-0.01em', color: '#2A251F', borderBottom: '1px solid #E8E1D4' }}>
          Прикрепить файл
        </div>
        <div style={{ overflowY: 'auto', flex: 1, padding: '8px 0' }}>
          {loading && (
            <div style={{ padding: '16px', color: '#8A8070', fontSize: 13, textAlign: 'center' }}>
              Загрузка…
            </div>
          )}
          {!loading && files.filter(f => !f.isDirectory).map(f => (
            <div
              key={f.path}
              onClick={() => { onPick(f.path); onClose(); }}
              style={{
                padding: '8px 16px', cursor: 'pointer', fontSize: 13,
                color: '#39332B', display: 'flex', alignItems: 'center', gap: 8,
              }}
              onMouseEnter={e => (e.currentTarget.style.background = '#F0EAE0')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
            >
              <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {f.path}
              </span>
            </div>
          ))}
          {!loading && files.filter(f => !f.isDirectory).length === 0 && (
            <div style={{ padding: '16px', color: '#8A8070', fontSize: 13, textAlign: 'center' }}>
              Файлы не найдены
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export function ChatPanel({ session, project, onOpenFile, pendingMessage, onPendingMessageSent }: Props) {
  const { items, isWaiting, isJoined, send, allowPermission, denyPermission, interrupt, toggleThinking } = useSession(session.id);
  const [mode, setMode] = useState<'auto' | 'plan' | 'ask'>(session.mode);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const [showAttachPicker, setShowAttachPicker] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const pendingRef = useRef<string | undefined>(pendingMessage);
  pendingRef.current = pendingMessage;

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [items]);

  // Автоотправка первого сообщения сразу после присоединения к сессии
  useEffect(() => {
    if (isJoined && pendingRef.current) {
      const msg = pendingRef.current;
      pendingRef.current = undefined;
      onPendingMessageSent?.();
      send(msg, []);
    }
  }, [isJoined]);

  const handleSend = async (text: string) => {
    if (!text.trim() && attachedFiles.length === 0) return;
    const paths = [...attachedFiles];
    setAttachedFiles([]);
    await send(text, paths);
  };

  const handleHint = (hint: string) => {
    send(hint, []);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Шапка */}
      <div style={{
        padding: '14px 24px', borderBottom: '1px solid #E7E0D2',
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0,
      }}>
        <div>
          <div style={{ fontSize: 17, fontWeight: 600, color: '#2A251F' }}>
            {session.name ?? 'Новая сессия'}
          </div>
          <div style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#9A8F7E', marginTop: 2 }}>
            {project.name} · {session.mode ?? 'auto'}
          </div>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {isWaiting ? (
            <button
              onClick={interrupt}
              style={{
                fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 5,
                color: '#B4452F', background: '#FFF0EE',
                border: '1px solid #F5C5BC', borderRadius: 8,
                padding: '5px 12px', cursor: 'pointer', fontWeight: 600,
              }}
            >
              <svg width="10" height="10" viewBox="0 0 10 10" fill="#B4452F"><rect width="10" height="10" rx="2"/></svg>
              Остановить
            </button>
          ) : null}
        </div>
      </div>

      {/* Сообщения */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '20px 24px' }}><div style={{ maxWidth: 760, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 20 }}>
        {/* Empty state */}
        {items.length === 0 && (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: 12, paddingTop: 40,
          }}>
            {/* Логотип */}
            <div style={{
              width: 46, height: 46, borderRadius: 13, background: '#D97757',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}>
              <div style={{
                width: 22, height: 22, borderRadius: '50%', background: '#FFF',
              }} />
            </div>

            {/* Заголовок */}
            <div style={{
              fontFamily: '"PT Serif", Georgia, serif',
              fontWeight: 500, fontSize: 20, color: '#2A251F', letterSpacing: '-0.01em',
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
                  onClick={() => handleHint(hint)}
                  style={{
                    background: '#FFF', border: '1px solid #E8E1D4',
                    borderRadius: 10, padding: '9px 12px',
                    fontSize: 13, color: '#39332B', cursor: 'pointer',
                    fontFamily: 'inherit',
                  }}
                  onMouseEnter={e => (e.currentTarget.style.background = '#F4ECE1')}
                  onMouseLeave={e => (e.currentTarget.style.background = '#FFF')}
                >
                  {hint}
                </button>
              ))}
            </div>
          </div>
        )}

        {items.map((item, i) => (
          <ChatItemView
            key={i}
            item={item}
            index={i}
            onToggleThinking={toggleThinking}
            onAllowPermission={allowPermission}
            onDenyPermission={denyPermission}
            onOpenFile={onOpenFile}
            onRevert={path => api.files.revert(project.id, path)}
            onRetry={() => {
              const lastUser = [...items].reverse().find(it => it.kind === 'user_message');
              if (lastUser && lastUser.kind === 'user_message') send(lastUser.text, lastUser.attachedPaths ?? []);
            }}
          />
        ))}

        {isWaiting && !items.some(it => it.kind === 'permission_request' && !it.resolved) && (
          <div style={{ fontSize: 12, color: '#8A8070', display: 'flex', gap: 4 }}>
            <span className="dots">ожидаю ответа</span>
            <span>…</span>
          </div>
        )}
        <div ref={bottomRef} />
      </div></div>

      {/* Composer */}
      <div style={{ padding: '12px 24px 18px', borderTop: '1px solid #E7E0D2' }}><div style={{ maxWidth: 760, margin: '0 auto' }}>
        <Composer
          onSend={handleSend}
          onStop={interrupt}
          onAttach={() => setShowAttachPicker(true)}
          isGenerating={isWaiting}
          mode={mode}
          onModeChange={setMode}
          attachments={attachedFiles}
          onRemoveAttachment={path => setAttachedFiles(prev => prev.filter(p => p !== path))}
        />
      </div></div>

      {/* Пикер вложений */}
      {showAttachPicker && (
        <AttachPicker
          projectId={project.id}
          onPick={path => setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path])}
          onClose={() => setShowAttachPicker(false)}
        />
      )}
    </div>
  );
}

interface ItemProps {
  item: ChatItem;
  index: number;
  onToggleThinking: (i: number) => void;
  onAllowPermission: (id: string) => void;
  onDenyPermission: (id: string) => void;
  onOpenFile: (path: string) => void;
  onRevert: (path: string) => void;
  onRetry: () => void;
}

function ChatItemView({ item, index, onToggleThinking, onAllowPermission, onDenyPermission, onOpenFile, onRevert, onRetry }: ItemProps) {
  switch (item.kind) {
    case 'user_message':
      return (
        <div style={{
          alignSelf: 'flex-end', background: '#F1DDD1', color: '#5A3322',
          borderRadius: '18px 18px 4px 18px', padding: '12px 17px',
          maxWidth: '80%', fontSize: 14,
        }}>
          {item.text}
          {item.attachedPaths && item.attachedPaths.length > 0 && (
            <div style={{ marginTop: 6, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {item.attachedPaths.map(p => (
                <span key={p} style={{
                  background: 'rgba(90,51,34,0.1)', borderRadius: 5,
                  padding: '1px 6px', fontSize: 11,
                }}>
                  {p.replace(/\\/g, '/').split('/').pop()}
                </span>
              ))}
            </div>
          )}
        </div>
      );

    case 'session_started':
      return (
        <div style={{
          background: '#FFFFFF', border: '1px solid #E8E1D4',
          borderRadius: 14, padding: '11px 13px',
          boxShadow: '0 2px 8px rgba(60,50,35,0.04)',
          display: 'flex', alignItems: 'center', gap: 10,
        }}>
          <div style={{
            width: 26, height: 26, borderRadius: 7, background: '#D97757',
            display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
          }}>
            <div style={{ width: 10, height: 10, borderRadius: '50%', background: '#F4F0E8' }} />
          </div>
          <div>
            <div style={{ fontSize: 13, fontWeight: 600, color: '#2A251F', marginBottom: 2 }}>
              Сессия запущена
            </div>
            <div style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 11, color: '#9A8F7E' }}>
              {item.model} · {item.mode}
            </div>
          </div>
        </div>
      );

    case 'text':
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, maxWidth: '90%' }}>
          <ClaudeHeader />
          <div style={{
            fontSize: 14, lineHeight: 1.5, color: '#2A251F',
            whiteSpace: 'pre-wrap', wordBreak: 'break-word',
            paddingLeft: 30,
          }}>
            {item.text}
          </div>
        </div>
      );

    case 'thinking':
      return (
        <div style={{
          background: '#EFEAE0', border: '1px solid #E4DDCE',
          borderRadius: 12, overflow: 'hidden', maxWidth: '90%',
        }}>
          <div
            style={{
              display: 'flex', alignItems: 'center', gap: 9,
              padding: '10px 12px', cursor: 'pointer',
            }}
            onClick={() => onToggleThinking(index)}
          >
            <span style={{ color: '#9A8F7E', display: 'flex', alignItems: 'center' }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3z" />
                <path d="M9 20h6M10 22h4" />
              </svg>
            </span>
            <span style={{ fontSize: 12.5, fontStyle: 'italic', color: '#756B5E', flex: 1 }}>
              Размышление
            </span>
            {item.text && (
              <span style={{ fontSize: 10.5, color: '#9A8F7E', fontFamily: "'JetBrains Mono', monospace" }}>
                {item.text.length} симв.
              </span>
            )}
            <span style={{
              color: '#9A8F7E', fontSize: 12,
              transform: item.expanded ? 'rotate(180deg)' : 'rotate(0deg)',
              transition: 'transform 0.2s',
              display: 'inline-block',
            }}>▾</span>
          </div>
          {item.expanded && (
            <div style={{
              padding: '0 12px 11px',
              fontSize: 12, fontStyle: 'italic', lineHeight: 1.6, color: '#8A8072',
              whiteSpace: 'pre-wrap',
            }}>
              {item.text}
            </div>
          )}
        </div>
      );

    case 'tool_use': {
      // Получаем краткое отображение аргумента инструмента (путь, команда и т.п.)
      const toolArg = (() => {
        if (!item.input) return '';
        const inp = item.input as Record<string, unknown>;
        return String(inp.path ?? inp.command ?? inp.file_path ?? inp.pattern ?? '');
      })();
      return (
        <div style={{
          padding: '9px 0',
          borderTop: '1px solid #E7E0D2', borderBottom: '1px solid #E7E0D2',
          display: 'flex', alignItems: 'center', gap: 10,
        }}>
          {item.result === undefined && <ToolSpinner />}
          <span style={{
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: 10, color: '#3E7CA6', fontWeight: 600,
            flexShrink: 0,
          }}>
            {item.name}
          </span>
          {toolArg && (
            <span style={{
              fontFamily: "'JetBrains Mono', monospace",
              fontSize: 12.5, flex: 1, color: '#39332B',
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {toolArg}
            </span>
          )}
          {item.result !== undefined && (
            <span style={{ fontSize: 11, color: item.isError ? '#C0392B' : '#9A8F7E', flexShrink: 0 }}>
              {item.isError ? 'ошибка' : 'готово'}
            </span>
          )}
        </div>
      );
    }

    case 'permission_request':
      return (
        <div style={{
          border: '1px solid #E6C9B8', borderLeft: '3px solid #D97757',
          borderRadius: 12, padding: '13px 14px', background: '#FBF1EA',
        }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4, color: '#2A251F' }}>
            Запрос разрешения
          </div>
          <div style={{ fontSize: 12, color: '#5A5040', marginBottom: 10 }}>
            Claude хочет выполнить:
          </div>
          <div style={{
            background: '#2A251F', borderRadius: 7, padding: '7px 10px',
            color: '#E8E1D4', fontFamily: "'JetBrains Mono', monospace",
            fontSize: 12, marginBottom: 12,
          }}>
            {item.toolName}
          </div>
          {!item.resolved ? (
            <div style={{ display: 'flex', gap: 8 }}>
              <button
                onClick={() => onAllowPermission(item.requestId)}
                style={{
                  flex: 1, background: '#D97757', color: '#FBF8F2',
                  borderRadius: 9, padding: 9, border: 'none',
                  cursor: 'pointer', fontSize: 13, fontWeight: 600,
                }}
              >
                Разрешить
              </button>
              <button
                onClick={() => onDenyPermission(item.requestId)}
                style={{
                  flex: 1, background: '#FFFFFF', border: '1px solid #E0D7C8',
                  color: '#756B5E', borderRadius: 9, padding: 9,
                  cursor: 'pointer', fontSize: 13,
                }}
              >
                Отклонить
              </button>
            </div>
          ) : (
            <div style={{ fontSize: 12, color: '#8A8070' }}>Решение принято</div>
          )}
        </div>
      );

    case 'file_changed': {
      const fileName = item.path.replace(/\\/g, '/').split('/').pop() ?? item.path;
      return (
        <div style={{
          border: '1px solid #E8E1D4', borderRadius: 14, overflow: 'hidden',
          background: '#FFFFFF', boxShadow: '0 2px 10px rgba(60,50,35,0.05)',
        }}>
          <div style={{
            display: 'flex', alignItems: 'center', gap: 11,
            padding: '12px 13px', cursor: 'pointer',
            borderBottom: '1px solid #EFE9DD',
          }}
            onClick={() => onOpenFile(item.path)}
          >
            <div style={{
              width: 28, height: 28, borderRadius: 8,
              background: '#FBEBE0', color: '#C2693B',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              flexShrink: 0,
            }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
              </svg>
            </div>
            <span style={{ fontSize: 13, fontWeight: 600, color: '#2A251F', flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {fileName}
            </span>
            <span style={{ fontSize: 11.5, color: '#27AE60', fontFamily: "'JetBrains Mono', monospace" }}>
              +{item.added}
            </span>
            <span style={{ fontSize: 11.5, color: '#C0392B', fontFamily: "'JetBrains Mono', monospace" }}>
              -{item.removed}
            </span>
          </div>
          <div style={{ padding: '8px 13px', display: 'flex', gap: 6 }}>
            <button
              onClick={() => onOpenFile(item.path)}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: '1px solid #E8E1D4', background: '#FFF', cursor: 'pointer', color: '#39332B',
              }}
            >
              Открыть
            </button>
            <button
              onClick={() => onRevert(item.path)}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: '1px solid #E0D8CC', background: '#FFF',
                cursor: 'pointer', color: '#C0392B',
              }}
            >
              Откатить
            </button>
          </div>
        </div>
      );
    }

    case 'result':
      return (
        <div style={{
          fontSize: 11, color: '#8A8070', alignSelf: 'center',
          background: '#E8E2D6', borderRadius: 8, padding: '4px 10px',
        }}>
          {item.subtype === 'success' ? '✓' : '✗'} {item.numTurns} шагов · {(item.durationMs / 1000).toFixed(1)}с
        </div>
      );

    case 'error':
      return (
        <div style={{
          background: '#FDECEA', borderRadius: 8, padding: '8px 12px',
          fontSize: 13, color: '#C0392B', border: '1px solid #F5C6CB',
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10,
        }}>
          <span>⚠ {item.text}</span>
          {item.canRetry && (
            <button
              onClick={onRetry}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: '1px solid #C0392B', background: '#FFF',
                cursor: 'pointer', color: '#C0392B', whiteSpace: 'nowrap', flexShrink: 0,
              }}
            >
              Повторить
            </button>
          )}
        </div>
      );

    default:
      return null;
  }
}
