import { useState, useRef, useEffect, type CSSProperties } from 'react';
import { C, FONT, R } from '../lib/design';
import { PillSwitch } from './Toolbar';
import { MarkdownViewer } from './MarkdownViewer';
import { useSessionArtifacts, type ArtifactFile, type ArtifactLink, type PlanStatus } from '../hooks/useSessionArtifacts';

interface Props {
  sessionId: string | null;
  projectId: string;
  rootPath: string;
  onOpenFile: (path: string) => void;
  onClose: () => void;
  isMobile?: boolean;
}

type TabKey = 'plan' | 'files' | 'links';

function basename(p: string): string {
  const norm = p.replace(/\\/g, '/');
  return norm.slice(norm.lastIndexOf('/') + 1);
}

function dirname(p: string): string {
  const norm = p.replace(/\\/g, '/');
  const i = norm.lastIndexOf('/');
  return i > 0 ? norm.slice(0, i) : '';
}

// Единый стиль кнопок-чипов в навигаторе плана («последний», «оглавление») —
// утопленный фон (не белый), одинаковые размеры/типографика.
const navChip: CSSProperties = {
  height: 28, padding: '0 10px', borderRadius: R.md, cursor: 'pointer',
  display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
  fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap',
  border: `1px solid ${C.border}`, background: C.bgInset, color: C.textSecondary,
};

// Заголовок оглавления = реальный <h*> узел из отрендеренного плана.
// Единый источник (DOM), чтобы список TOC и цель скролла были тем же узлом —
// иначе строковый парсер разъезжается с рендером remark (Setext, blockquote и пр.).
interface Heading { level: number; text: string; el: HTMLElement }

const STATUS_META: Record<PlanStatus, { label: string; fg: string; bg: string }> = {
  approved: { label: 'одобрен', fg: C.successText, bg: C.successBg },
  rejected: { label: 'отклонён', fg: C.dangerText, bg: C.dangerBg },
  pending:  { label: 'ожидает', fg: C.textSecondary, bg: C.bgInset },
};

function FileRow({ file, onOpen }: { file: ArtifactFile; onOpen: () => void }) {
  const [hover, setHover] = useState(false);
  const [copied, setCopied] = useState(false);
  const dir = dirname(file.path);
  const showDelta = file.changed && file.hasDelta && (file.added > 0 || file.removed > 0);

  const handleClick = () => {
    if (file.external) {
      // На Windows копируем с обратными слэшами (как ждёт проводник/cmd).
      // Optional chaining до .then включительно — буфер может быть недоступен (http-контекст).
      const toCopy = /^[A-Za-z]:\//.test(file.path) ? file.path.replace(/\//g, '\\') : file.path;
      navigator.clipboard?.writeText(toCopy)?.then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      })?.catch(() => { /* буфер недоступен — молча */ });
    } else {
      onOpen();
    }
  };

  return (
    <button
      onClick={handleClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={file.external ? `${file.path} — скопировать путь` : file.path}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9,
        padding: '7px 14px', border: 'none', cursor: 'pointer', textAlign: 'left',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      {file.external ? (
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2"
          strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
          <rect x="9" y="9" width="13" height="13" rx="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
        </svg>
      ) : (
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2"
          strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
          <path d="M14 2v6h6" />
        </svg>
      )}
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {basename(file.path)}
        </div>
        {dir && (
          <div style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {dir}
          </div>
        )}
      </div>
      {showDelta ? (
        <div style={{ display: 'flex', gap: 6, flexShrink: 0, fontFamily: FONT.mono, fontSize: 11, fontWeight: 600 }}>
          {file.added > 0 && <span style={{ color: C.diffAddText }}>+{file.added}</span>}
          {file.removed > 0 && <span style={{ color: C.diffRemText }}>−{file.removed}</span>}
        </div>
      ) : (
        <span style={{ flexShrink: 0, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: copied ? C.successText : C.textMuted, whiteSpace: 'nowrap' }}>
          {copied ? 'скопировано' : file.external ? 'вне проекта' : !file.changed ? 'упомянут' : ''}
        </span>
      )}
    </button>
  );
}

function LinkRow({ link }: { link: ArtifactLink }) {
  const [hover, setHover] = useState(false);
  return (
    <a
      href={link.url}
      target="_blank"
      rel="noopener noreferrer"
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={link.url}
      style={{
        display: 'flex', flexDirection: 'column', gap: 1,
        padding: '7px 14px', textDecoration: 'none',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      <span style={{ fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.domain}
      </span>
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.url}
      </span>
    </a>
  );
}

// Иконка-кнопка навигатора планов (стрелка ‹ / ›)
function NavArrow({ dir, disabled, onClick }: { dir: 'prev' | 'next'; disabled: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={dir === 'prev' ? 'Предыдущий план' : 'Следующий план'}
      style={{
        width: 24, height: 24, border: 'none', borderRadius: R.sm, background: 'transparent',
        cursor: disabled ? 'default' : 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: disabled ? C.border : C.textSecondary, flexShrink: 0,
      }}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
        {dir === 'prev' ? <path d="M15 6l-6 6 6 6" /> : <path d="M9 6l6 6-6 6" />}
      </svg>
    </button>
  );
}

export function ArtifactsPanel({ sessionId, projectId, rootPath, onOpenFile, onClose, isMobile }: Props) {
  const { files, plans, links } = useSessionArtifacts(sessionId, projectId, rootPath);

  // Вкладки — только непустые, в порядке: План → Файлы → Ссылки
  const tabs: { value: TabKey; label: string }[] = [];
  if (plans.length) tabs.push({ value: 'plan', label: 'План' });
  if (files.length) tabs.push({ value: 'files', label: `Файлы · ${files.length}` });
  if (links.length) tabs.push({ value: 'links', label: `Ссылки · ${links.length}` });

  const [active, setActive] = useState<TabKey>('plan');
  const activeKey: TabKey | undefined = tabs.some(t => t.value === active) ? active : tabs[0]?.value;
  const isEmpty = tabs.length === 0;

  // Навигация по планам: null = «не выбирал» → показываем последний
  const [planIdx, setPlanIdx] = useState<number | null>(null);
  const effIdx = planIdx == null ? plans.length - 1 : Math.min(Math.max(planIdx, 0), plans.length - 1);
  const curPlan = plans[effIdx];

  // Оглавление текущего плана + поповер
  const [tocOpen, setTocOpen] = useState(false);
  const [headings, setHeadings] = useState<Heading[]>([]);
  const planContentRef = useRef<HTMLDivElement>(null);

  // Заголовки берём из реального DOM плана (после рендера MarkdownViewer) — один источник,
  // никакого рассинхрона со строковым парсером. Пересбор при смене текста плана/вкладки.
  const planText = activeKey === 'plan' ? curPlan?.plan : undefined;
  useEffect(() => {
    const root = planContentRef.current;
    if (!root) { setHeadings([]); return; }
    const list: Heading[] = [];
    root.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(n => {
      const el = n as HTMLElement;
      const text = (el.textContent ?? '').trim();
      if (text) list.push({ level: Number(el.tagName[1]), text, el });
    });
    setHeadings(list);
  }, [planText]);

  const scrollToHeading = (h: Heading) => {
    h.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    setTocOpen(false);
  };

  return (
    <div style={{ width: '100%', height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, overflow: 'hidden' }}>
      {/* Шапка */}
      <div style={{
        flexShrink: 0, height: 52, display: 'flex', alignItems: 'center', gap: 8,
        padding: '0 10px 0 14px', borderBottom: `1px solid ${C.border}`,
      }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, flex: 1 }}>
          Артефакты сессии
        </span>
        <button
          onClick={onClose}
          title="Скрыть панель"
          style={{ width: 30, height: 30, border: 'none', borderRadius: R.md, background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
            {isMobile ? <path d="M6 9l6 6 6-6" /> : <path d="M9 6l6 6-6 6" />}
          </svg>
        </button>
      </div>

      {isEmpty ? (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 10, padding: 24, textAlign: 'center' }}>
          <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" style={{ opacity: 0.6 }}>
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <path d="M14 2v6h6M9 13h6M9 17h3" />
          </svg>
          <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>
            Пока ничего не менялось.<br />Здесь появятся план, файлы и ссылки.
          </span>
        </div>
      ) : (
        <>
          {/* Переключатель вкладок (только непустые) */}
          <div style={{ flexShrink: 0, padding: '10px 12px', borderBottom: `1px solid ${C.border}` }}>
            <PillSwitch<TabKey> value={activeKey!} options={tabs} onChange={setActive} fill />
          </div>

          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            {activeKey === 'plan' && curPlan && (
              <>
                {/* Навигатор планов + статус + оглавление */}
                <div style={{
                  flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: 6,
                  padding: '8px 10px 8px 12px', borderBottom: `1px solid ${C.border}`,
                }}>
                  {plans.length > 1 && (
                    <NavArrow dir="prev" disabled={effIdx === 0} onClick={() => setPlanIdx(effIdx - 1)} />
                  )}
                  <span style={{ fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
                    {plans.length > 1 ? `План ${effIdx + 1} / ${plans.length}` : 'План'}
                  </span>
                  {plans.length > 1 && (
                    <NavArrow dir="next" disabled={effIdx === plans.length - 1} onClick={() => setPlanIdx(effIdx + 1)} />
                  )}
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, padding: '2px 7px', borderRadius: R.sm,
                    color: STATUS_META[curPlan.status].fg, background: STATUS_META[curPlan.status].bg, whiteSpace: 'nowrap',
                  }}>
                    {STATUS_META[curPlan.status].label}
                  </span>
                  <div style={{ flex: 1 }} />
                  {plans.length > 1 && effIdx !== plans.length - 1 && (
                    <button
                      onClick={() => setPlanIdx(null)}
                      title="К последнему плану"
                      style={navChip}
                    >
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M13 17l5-5-5-5" /><path d="M6 17l5-5-5-5" />
                      </svg>
                      последний
                    </button>
                  )}
                  {headings.length > 0 && (
                    <button
                      onClick={() => setTocOpen(v => !v)}
                      title="Оглавление"
                      style={tocOpen
                        ? { ...navChip, background: C.accentMuted, border: `1px solid ${C.accentMuted}`, color: C.accent }
                        : navChip}
                    >
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <line x1="8" y1="6" x2="21" y2="6" /><line x1="8" y1="12" x2="21" y2="12" /><line x1="8" y1="18" x2="21" y2="18" />
                        <line x1="3" y1="6" x2="3.01" y2="6" /><line x1="3" y1="12" x2="3.01" y2="12" /><line x1="3" y1="18" x2="3.01" y2="18" />
                      </svg>
                      оглавление
                    </button>
                  )}

                  {/* Поповер оглавления */}
                  {tocOpen && headings.length > 0 && (
                    <>
                      <div onClick={() => setTocOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
                      <div style={{
                        position: 'absolute', top: '100%', right: 8, marginTop: 4, zIndex: 41,
                        width: 'min(280px, calc(100% - 16px))', maxHeight: 320, overflowY: 'auto',
                        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
                        boxShadow: '0 8px 28px rgba(60,50,35,0.16)', padding: '6px 0',
                      }}>
                        {headings.map((h, i) => (
                          <button
                            key={i}
                            onClick={() => scrollToHeading(h)}
                            style={{
                              width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                              padding: '5px 12px', paddingLeft: 12 + (h.level - 1) * 12,
                              fontFamily: FONT.sans, fontSize: 12.5, color: h.level <= 2 ? C.textHeading : C.textSecondary,
                              fontWeight: h.level <= 2 ? 600 : 400,
                              whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: 1.35,
                            }}
                            onMouseEnter={e => (e.currentTarget.style.background = C.bgSelected)}
                            onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
                          >
                            {h.text}
                          </button>
                        ))}
                      </div>
                    </>
                  )}
                </div>

                {/* Текст плана (скроллится) */}
                <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px' }}>
                  <MarkdownViewer content={curPlan.plan} />
                </div>
              </>
            )}

            {activeKey === 'files' && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '6px 0' }}>
                {files.map(f => <FileRow key={f.path} file={f} onOpen={() => onOpenFile(f.path)} />)}
              </div>
            )}

            {activeKey === 'links' && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '6px 0' }}>
                {links.map(l => <LinkRow key={l.url} link={l} />)}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}
