import { useState, useRef, useEffect } from 'react';
import type { Project, Session, ChatItem, FileEntry } from '../types';
import { useSession } from '../hooks/useSession';
import { useOnline } from '../hooks/useOnline';
import { api } from '../lib/api';
import { modelLabel } from '../lib/models';
import { Composer } from './Composer';
import { EditSessionDialog } from './EditSessionDialog';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';

interface Props {
  session: Session;
  project: Project;
  onOpenFile: (path: string) => void;
  pendingMessage?: string;
  onPendingMessageSent?: () => void;
  onSessionUpdated?: (session: Session) => void;
  dockMode?: 'expanded' | 'collapsed';
  onToggleDock?: () => void;
  isMobile?: boolean;
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

// Общая шапка чата — одинаковая для полноэкранного режима и дока (split снизу).
// onToggleDock задаётся только в доке — добавляет кнопку сворачивания.
interface ChatHeaderBarProps {
  session: Session;
  project: Project;
  isWaiting: boolean;
  online: boolean;
  onInterrupt: () => void;
  onOpenSettings: () => void;
  onToggleDock?: () => void;
  isMobile?: boolean;
}

function ChatHeaderBar({ session, project, isWaiting, online, onInterrupt, onOpenSettings, onToggleDock, isMobile }: ChatHeaderBarProps) {
  return (
    <div style={{
      padding: isMobile ? '12px 14px' : '14px 24px', borderBottom: '1px solid #E7E0D2',
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0,
    }}>
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: 17, fontWeight: 600, color: '#2A251F', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {session.name ?? 'Новый чат'}
        </div>
        <div style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#9A8F7E', marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {project.name} · {session.mode ?? 'auto'} · {modelLabel(session.model)}
        </div>
      </div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexShrink: 0 }}>
        {online && (
        <button
          onClick={onOpenSettings}
          title="Настройки чата"
          style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#9A8F7E', padding: 6, borderRadius: 8, display: 'flex', alignItems: 'center' }}
          onMouseEnter={e => { e.currentTarget.style.color = '#5A5040'; e.currentTarget.style.background = '#EDE7DC'; }}
          onMouseLeave={e => { e.currentTarget.style.color = '#9A8F7E'; e.currentTarget.style.background = 'none'; }}
        >
          <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
          </svg>
        </button>
        )}
        {isWaiting && (
          <button
            onClick={onInterrupt}
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
        )}
        {onToggleDock && (
          <button
            onClick={onToggleDock}
            title="Свернуть чат"
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#756B5E', padding: 4, borderRadius: 6, display: 'flex', alignItems: 'center' }}
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M6 9l6 6 6-6"/></svg>
          </button>
        )}
      </div>
    </div>
  );
}

// Заглушка вместо Composer в офлайн-режиме
function OfflineComposerStub() {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
      padding: '14px', borderRadius: 14, background: '#EDE7DC',
      border: '1px solid #E0D8CC', color: '#9A8F7E', fontSize: 13, fontWeight: 600,
    }}>
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M1 1l22 22" />
        <path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55M5 12.55a10.94 10.94 0 0 1 5.17-2.39M10.71 5.05A16 16 0 0 1 22.58 9M1.42 9a15.91 15.91 0 0 1 4.7-2.88M8.53 16.11a6 6 0 0 1 6.95 0" />
        <line x1="12" y1="20" x2="12.01" y2="20" />
      </svg>
      Отправка недоступна офлайн
    </div>
  );
}

// Рендер текста Claude с поддержкой Markdown
function MarkdownContent({ text }: { text: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        p: ({ children }) => (
          <p style={{ margin: '0 0 8px 0', lineHeight: 1.6 }}>{children}</p>
        ),
        h1: ({ children }) => (
          <h1 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 20, fontWeight: 600, margin: '10px 0 6px', color: '#2A251F', letterSpacing: '-0.01em' }}>{children}</h1>
        ),
        h2: ({ children }) => (
          <h2 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 17, fontWeight: 600, margin: '8px 0 5px', color: '#2A251F', letterSpacing: '-0.01em' }}>{children}</h2>
        ),
        h3: ({ children }) => (
          <h3 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 15, fontWeight: 600, margin: '6px 0 4px', color: '#2A251F' }}>{children}</h3>
        ),
        pre: ({ children }) => <>{children}</>,
        code: ({ className, children, ...props }) => {
          const language = /language-(\w+)/.exec(className || '')?.[1];
          const text = String(children).replace(/\n$/, '');
          if (language) {
            return (
              <SyntaxHighlighter
                language={language}
                style={oneDark}
                customStyle={{ borderRadius: 8, fontSize: 12.5, margin: '6px 0', padding: '10px 14px', fontFamily: "'JetBrains Mono', monospace", overflowX: 'auto' }}
              >
                {text}
              </SyntaxHighlighter>
            );
          }
          if (text.includes('\n')) {
            return (
              <pre style={{ background: '#2A251F', borderRadius: 8, padding: '10px 14px', margin: '6px 0', overflowX: 'auto' }}>
                <code style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12.5, color: '#E8E1D4', lineHeight: 1.5 }} {...props}>{text}</code>
              </pre>
            );
          }
          return (
            <code style={{ fontFamily: "'JetBrains Mono', monospace", background: '#EDE7DA', padding: '1px 5px', borderRadius: 4, fontSize: '0.88em', color: '#5A3322' }} {...props}>
              {children}
            </code>
          );
        },
        ul: ({ children }) => <ul style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ul>,
        ol: ({ children }) => <ol style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ol>,
        li: ({ children }) => <li style={{ marginBottom: 3, lineHeight: 1.6 }}>{children}</li>,
        blockquote: ({ children }) => (
          <blockquote style={{ borderLeft: '3px solid #D97757', paddingLeft: 12, margin: '6px 0', color: '#756B5E', fontStyle: 'italic' }}>
            {children}
          </blockquote>
        ),
        a: ({ children, href }) => (
          <a href={href} style={{ color: '#D97757', textDecoration: 'underline' }} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        ),
        strong: ({ children }) => <strong style={{ fontWeight: 600 }}>{children}</strong>,
        hr: () => <hr style={{ border: 'none', borderTop: '1px solid #E0D7C8', margin: '10px 0' }} />,
        table: ({ children }) => (
          <div style={{ overflowX: 'auto', margin: '6px 0' }}>
            <table style={{ borderCollapse: 'collapse', minWidth: '100%', fontSize: 13 }}>{children}</table>
          </div>
        ),
        th: ({ children }) => (
          <th style={{ border: '1px solid #E0D7C8', padding: '6px 10px', background: '#EDE7DA', fontWeight: 600, textAlign: 'left' }}>{children}</th>
        ),
        td: ({ children }) => (
          <td style={{ border: '1px solid #E0D7C8', padding: '6px 10px' }}>{children}</td>
        ),
      }}
    >
      {text}
    </ReactMarkdown>
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

export function ChatPanel({ session, project, onOpenFile, pendingMessage, onPendingMessageSent, onSessionUpdated, dockMode, onToggleDock, isMobile }: Props) {
  const { items, isWaiting, isJoined, send, allowPermission, denyPermission, interrupt, toggleThinking } = useSession(session.id, project.id);
  const online = useOnline();
  const [mode, setMode] = useState<'auto' | 'plan' | 'ask'>(session.mode);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const [showAttachPicker, setShowAttachPicker] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  const [miniText, setMiniText] = useState('');
  const bottomRef = useRef<HTMLDivElement>(null);
  const pendingRef = useRef<string | undefined>(pendingMessage);
  pendingRef.current = pendingMessage;

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'instant' });
  }, [items]);

  // При раскрытии дока — моментально проматываем в конец
  useEffect(() => {
    if (dockMode === 'expanded') {
      bottomRef.current?.scrollIntoView({ behavior: 'instant' });
    }
  }, [dockMode]);

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

  const handleRetry = () => {
    const lastUser = [...items].reverse().find(it => it.kind === 'user_message');
    if (lastUser && lastUser.kind === 'user_message') send(lastUser.text, lastUser.attachedPaths ?? []);
  };

  // Dock: свёрнутая полоска
  if (dockMode === 'collapsed') {
    const lastPreview = (() => {
      for (let i = items.length - 1; i >= 0; i--) {
        const it = items[i];
        if (it.kind === 'text') return it.text;
        if (it.kind === 'user_message') return it.text;
      }
      return '';
    })();

    const handleMiniSend = () => {
      if (!miniText.trim() || isWaiting || !online) return;
      send(miniText, []);
      setMiniText('');
    };

    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '0 16px', height: 56, background: '#EDE7DC', boxSizing: 'border-box' }}>
        <div style={{ width: 28, height: 28, borderRadius: '50%', background: '#D97757', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <div style={{ width: 10, height: 10, borderRadius: '50%', background: '#F4F0E8' }} />
        </div>
        <span style={{ flex: 1, fontSize: 13, color: '#5A5040', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {lastPreview || (session.name ?? 'Новый чат')}
        </span>
        <input
          value={miniText}
          onChange={e => setMiniText(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleMiniSend(); } }}
          placeholder={online ? 'Ответить…' : 'Офлайн'}
          disabled={!online}
          style={{ width: 180, padding: '6px 10px', border: '1px solid #D4CFC4', borderRadius: 8, fontSize: 13, background: '#F4F0E8', outline: 'none', fontFamily: 'inherit', color: '#2A251F' }}
        />
        <button
          onClick={handleMiniSend}
          disabled={!miniText.trim() || isWaiting || !online}
          style={{ width: 28, height: 28, borderRadius: '50%', border: 'none', cursor: miniText.trim() && !isWaiting && online ? 'pointer' : 'default', background: miniText.trim() && !isWaiting && online ? '#D97757' : '#DDD4C4', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}
        >
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="#FFF" strokeWidth="2.5" strokeLinecap="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
        </button>
        <button
          onClick={onToggleDock}
          title="Развернуть чат"
          style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#756B5E', padding: 4, borderRadius: 6, display: 'flex', alignItems: 'center', flexShrink: 0 }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M18 15l-6-6-6 6"/></svg>
        </button>
      </div>
    );
  }

  // Dock: развёрнутая панель
  if (dockMode === 'expanded') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: '#F4F0E8' }}>
        <ChatHeaderBar
          session={session}
          project={project}
          isWaiting={isWaiting}
          online={online}
          onInterrupt={interrupt}
          onOpenSettings={() => setShowEdit(true)}
          onToggleDock={onToggleDock}
        />

        {/* Сообщения */}
        <div style={{ flex: 1, overflowY: 'auto', overflowX: 'hidden', padding: '12px 16px' }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {items.map((item, i) => (
              <ChatItemView
                key={i}
                item={item}
                index={i}
                online={online}
                onToggleThinking={toggleThinking}
                onAllowPermission={allowPermission}
                onDenyPermission={denyPermission}
                onOpenFile={onOpenFile}
                onRevert={path => api.files.revert(project.id, path)}
                onRetry={handleRetry}
              />
            ))}
            {isWaiting && !items.some(it => it.kind === 'permission_request' && !it.resolved) && (
              <div style={{ fontSize: 12, color: '#8A8070', display: 'flex', gap: 4 }}>
                <span className="dots">ожидаю ответа</span>
                <span>…</span>
              </div>
            )}
            <div ref={bottomRef} />
          </div>
        </div>

        {/* Composer */}
        <div style={{ padding: '10px 16px 14px', borderTop: '1px solid #E7E0D2', flexShrink: 0 }}>
          {online ? (
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
          ) : <OfflineComposerStub />}
        </div>

        {showAttachPicker && (
          <AttachPicker
            projectId={project.id}
            onPick={path => setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path])}
            onClose={() => setShowAttachPicker(false)}
          />
        )}

        {showEdit && (
          <EditSessionDialog
            session={session}
            onSaved={s => onSessionUpdated?.(s)}
            onClose={() => setShowEdit(false)}
          />
        )}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <ChatHeaderBar
        session={session}
        project={project}
        isWaiting={isWaiting}
        online={online}
        onInterrupt={interrupt}
        onOpenSettings={() => setShowEdit(true)}
        isMobile={isMobile}
      />

      {/* Сообщения */}
      <div style={{ flex: 1, overflowY: 'auto', overflowX: 'hidden', padding: isMobile ? '16px 12px' : '20px 24px' }}><div style={{ maxWidth: 760, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 20 }}>
        {/* Empty state */}
        {items.length === 0 && online && (
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
            online={online}
            onToggleThinking={toggleThinking}
            onAllowPermission={allowPermission}
            onDenyPermission={denyPermission}
            onOpenFile={onOpenFile}
            onRevert={path => api.files.revert(project.id, path)}
            onRetry={handleRetry}
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
      <div style={{ padding: isMobile ? '10px 12px 14px' : '12px 24px 18px', borderTop: '1px solid #E7E0D2' }}><div style={{ maxWidth: 760, margin: '0 auto' }}>
        {online ? (
          <Composer
            onSend={handleSend}
            onStop={interrupt}
            onAttach={() => setShowAttachPicker(true)}
            isGenerating={isWaiting}
            mode={mode}
            onModeChange={setMode}
            attachments={attachedFiles}
            onRemoveAttachment={path => setAttachedFiles(prev => prev.filter(p => p !== path))}
            isMobile={isMobile}
          />
        ) : <OfflineComposerStub />}
      </div></div>

      {/* Пикер вложений */}
      {showAttachPicker && (
        <AttachPicker
          projectId={project.id}
          onPick={path => setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path])}
          onClose={() => setShowAttachPicker(false)}
        />
      )}

      {/* Настройки чата */}
      {showEdit && (
        <EditSessionDialog
          session={session}
          onSaved={s => onSessionUpdated?.(s)}
          onClose={() => setShowEdit(false)}
        />
      )}
    </div>
  );
}

interface ItemProps {
  item: ChatItem;
  index: number;
  online: boolean;
  onToggleThinking: (i: number) => void;
  onAllowPermission: (id: string) => void;
  onDenyPermission: (id: string) => void;
  onOpenFile: (path: string) => void;
  onRevert: (path: string) => void;
  onRetry: () => void;
}

function ChatItemView({ item, index, online, onToggleThinking, onAllowPermission, onDenyPermission, onOpenFile, onRevert, onRetry }: ItemProps) {
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
              Чат запущен
            </div>
            <div style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 11, color: '#9A8F7E' }}>
              {item.model} · {item.mode}
            </div>
          </div>
        </div>
      );

    case 'text':
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, maxWidth: '100%', overflow: 'hidden' }}>
          <ClaudeHeader />
          <div style={{ fontSize: 14, color: '#2A251F', wordBreak: 'break-word', paddingLeft: 30 }}>
            <MarkdownContent text={item.text} />
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
          {item.resolved ? (
            <div style={{ fontSize: 12, color: '#8A8070' }}>Решение принято</div>
          ) : online ? (
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
            <div style={{ fontSize: 12, color: '#9A8F7E' }}>Недоступно офлайн</div>
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
            {online && (
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
            )}
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
          {item.canRetry && online && (
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
