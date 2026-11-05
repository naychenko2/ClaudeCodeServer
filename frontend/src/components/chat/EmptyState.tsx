import { useState } from 'react';
import { Lock } from 'lucide-react';
import type { Session, Persona, Project } from '../../types';
import { C, R, SP, FS, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui';
import { COMPANION_LOCKED_HINT } from '../CompanionSelector';
import { NewChatSetup } from './NewChatSetup';
import { useAssistantName } from './contexts';
import { personaLabel, personaTitleLines } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { useContextPersona } from '../../lib/contextPersona';
import { useCreateShortcuts } from './composerShortcuts';
import { isGlobalPersona, isZonedPersona } from '../../lib/personaZone';

// Empty state пустого чата: приветствие/чипы-подсказки; для проекта без CLAUDE.md — CTA /init.
// Внизу — настройка будущего чата (модель, усилие, время жизни, теги), пока не отправлено первое сообщение.
export function ChatEmptyState({ hasProject, hasCLAUDEmd, onHint, session, project, onSessionUpdated, isMobile, personas, selectedPersonaId, onPickPersona, companionLocked, compact, greetingAbove }: {
  hasProject: boolean;
  hasCLAUDEmd: boolean | null;
  onHint: (hint: string) => void;
  session?: Session;
  // Полный проект — только для реестра тегов в NewChatSetup (per-project); hasProject остаётся для остального контента
  project?: Project;
  onSessionUpdated?: (s: Session) => void;
  isMobile?: boolean;
  // Доступные персоны — ряд «Поговорить с…» для пустого чата
  personas?: Persona[];
  selectedPersonaId?: string;
  onPickPersona?: (p: Persona) => void;
  // Смена собеседника закрыта по праву (W3-F1i, дефект 9227267b): ряд выключен
  // с видимой подсказкой — тем же companionLocked, что и селектор в композере
  companionLocked?: boolean;
  // Узкая колонка (стена): без пилюль настройки чата
  compact?: boolean;
  // Выше в ленте уже стоит приветствие персоны (аватар + имя + реплика):
  // своё «лицо» не рисуем, чтобы не было двух аватаров подряд
  greetingAbove?: boolean;
}) {
  const asstName = useAssistantName();
  // Ярлыки полос над композером («Голос», «Музыка» у «Звука») — кнопками под приветствием
  const shortcuts = useCreateShortcuts(project?.id ?? null, session?.id ?? null);
  // Лицо пустого чата: аватар персоны чата (или дефолт-персоны контекста);
  // нейтральный favicon — только когда персоны нет
  const facePersona = useContextPersona({
    personaId: session?.personaId ?? null,
    projectDefaultId: project ? (project.defaultPersonaId ?? null) : undefined,
  });
  return (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: SP.md, paddingTop: greetingAbove ? SP.lg : SP.xxl,
          }}>
            {/* Лицо чата: аватар релевантной персоны, fallback — нейтральный логотип.
                С приветствием сверху аватар там уже есть — второй не нужен */}
            {!greetingAbove && (facePersona
              ? <PersonaAvatar persona={facePersona} size={46} />
              : <img src="/favicon.svg" alt="" width={46} height={46} style={{ display: 'block' }} />)}

            {!hasProject ? (
              <>
                {/* Приветствие чата вне проекта — general-purpose ассистент.
                    С приветствием персоны сверху свой заголовок/подзаголовок не нужен,
                    чтобы не было двух приветствий подряд */}
                {!greetingAbove && (
                  <>
                    <div style={{
                      fontFamily: FONT.serif,
                      fontWeight: 500, fontSize: FS.xl, color: C.textHeading, letterSpacing: '-0.01em',
                    }}>
                      Чем помочь?
                    </div>

                    <div style={{ fontSize: FS.base, color: C.textMuted, textAlign: 'center', maxWidth: 320 }}>
                      Спросите что угодно — тексты и идеи, поиск в интернете, генерация картинок
                    </div>
                  </>
                )}
              </>
            ) : hasCLAUDEmd === false ? (
              <>
                {/* Заголовок-приветствие: с персоной сверху не нужен.
                    Подзаголовок и CTA /init — это действие, их оставляем */}
                {!greetingAbove && (
                  <div style={{
                    fontFamily: FONT.serif,
                    fontWeight: 500, fontSize: FS.xl, color: C.textHeading, letterSpacing: '-0.01em',
                  }}>
                    Новый проект
                  </div>
                )}

                {/* Подзаголовок */}
                <div style={{ fontSize: FS.base, color: C.textMuted, textAlign: 'center', maxWidth: 260 }}>
                  Запустите /init — так {asstName} изучит проект и создаст CLAUDE.md
                </div>

                {/* Кнопка CTA */}
                <Button variant="primary" onClick={() => onHint('/init')} style={{ marginTop: SP.xs }}>
                  Инициализировать проект
                </Button>
              </>
            ) : (
              <>
                {/* С приветствием персоны сверху заголовок не рисуем. Подзаголовка нет:
                    «опишите задачу» ничего не добавляет к полю ввода прямо под ним */}
                {!greetingAbove && (
                  <div style={{
                    fontFamily: FONT.serif,
                    fontWeight: 500, fontSize: FS.xl, color: C.textHeading, letterSpacing: '-0.01em',
                  }}>
                    Чем помочь?
                  </div>
                )}
              </>
            )}

            {shortcuts.length > 0 && (
              <div data-empty-shortcuts="" style={{ display: 'flex', flexWrap: 'wrap', justifyContent: 'center', gap: SP.sm }}>
                {shortcuts.map(sc => (
                  <Button key={sc.key} variant="secondary" size="sm" leftIcon={sc.icon}
                    title={sc.hint ? `${sc.title} — ${sc.hint}` : sc.title} onClick={sc.onSelect}>
                    {sc.title}
                  </Button>
                ))}
              </div>
            )}

            {/* Ряд персон «Поговорить с…» — назначить персону текущему пустому чату.
                В проекте команда проекта видна сразу, глобальные свёрнуты за кнопкой;
                если проектных персон нет — глобальные показываются сразу. */}
            {personas && personas.length > 0 && onPickPersona && (
              <PersonaPills
                personas={personas}
                hasProject={hasProject}
                selectedPersonaId={selectedPersonaId}
                onPick={onPickPersona}
                locked={companionLocked}
              />
            )}

            {/* Настройка чата — модель, усилие рассуждения, время жизни, теги (до первого
                сообщения). На стене (compact) пилюль нет: в колонке они занимают половину
                пустого чата, а те же настройки лежат в композере и в полном виде чата */}
            {session && !compact && (
              <NewChatSetup session={session} project={project} onSessionUpdated={onSessionUpdated} isMobile={isMobile} />
            )}
          </div>
  );
}

// Одна пилюля-аватар персоны (роль над именем). locked (W3-F1i): смена собеседника
// закрыта по праву — пиля видна, но не активна; причина — в title и в подсказке над рядом
function PersonaPill({ p, active, onPick, locked }: { p: Persona; active: boolean; onPick: (p: Persona) => void; locked?: boolean }) {
  return (
    <button
      onClick={() => onPick(p)}
      disabled={locked}
      title={locked ? COMPANION_LOCKED_HINT : `Поговорить с «${personaLabel(p)}»`}
      style={{
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4,
        border: 'none', background: 'none', padding: 2, width: 64,
        cursor: locked ? 'not-allowed' : 'pointer',
        ...(locked ? { opacity: 0.6 } : {}),
      }}
    >
      <span style={{ borderRadius: R.full, padding: 2, border: `2px solid ${active ? C.accent : 'transparent'}` }}>
        <PersonaAvatar persona={p} size={44} />
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
        maxWidth: 64, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {personaTitleLines(p).primary}
      </span>
      {personaTitleLines(p).secondary && (
        <span style={{
          fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
          maxWidth: 64, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {personaTitleLines(p).secondary}
        </span>
      )}
    </button>
  );
}

// Сколько аватаров ряд «Поговорить с…» показывает до раскрытия: ровно одна строка
// в колонке чата (64px на пилюлю + зазор), остальное — за «+N»
const PERSONAS_VISIBLE = 6;

// Ряд «Поговорить с…»: в проекте команда видна сразу, глобальные — за кнопкой-раскрывашкой.
// Без проектных персон (или вне проекта) глобальные показываются сразу. Свёрнутый ряд —
// не длиннее одной строки; выбранная персона в него попадает всегда.
function PersonaPills({ personas, hasProject, selectedPersonaId, onPick, locked }: {
  personas: Persona[];
  hasProject: boolean;
  selectedPersonaId?: string;
  onPick: (p: Persona) => void;
  // Ряд выключен по праву (W3-F1i): вместо заголовка «Поговорить с…» — причина,
  // пилюи disabled; раскрывашка «+N» остаётся: она меняет видимость, а не право
  locked?: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const projectPersonas = personas.filter(isZonedPersona);
  // Пантеонные персоны (каталог OmO, с templateKey) — всегда под раскрывашкой,
  // как и обычные глобальные в проекте с командой; по умолчанию не предлагаются.
  const regularGlobals = personas.filter(p => isGlobalPersona(p) && !p.templateKey);
  const pantheonPersonas = personas.filter(p => p.templateKey);
  // Обычные глобальные прячем только в проекте с собственной командой
  const collapseGlobals = hasProject && projectPersonas.length > 0;
  const all = [...new Set([...projectPersonas, ...regularGlobals, ...pantheonPersonas])];
  const candidates = [...projectPersonas, ...(collapseGlobals ? [] : regularGlobals)];
  let visible = expanded ? all : candidates.slice(0, PERSONAS_VISIBLE);
  // Выбранная персона за пределами строки — подставляем её последней видимой
  const selected = all.find(p => p.id === selectedPersonaId);
  if (!expanded && selected && !visible.includes(selected)) {
    visible = [...visible.slice(0, PERSONAS_VISIBLE - 1), selected];
  }
  // Скрытых по умолчанию: всё, что не попало в строку (свёрнутые глобальные, пантеон, хвост)
  const hiddenCount = all.length - visible.length;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8, marginTop: 6 }}>
      {locked ? (
        // Причина выключенного ряда — видимым текстом (на мобиле title не читается),
        // переносится, как подпись пилюль; замок сигналит закрытое состояние —
        // тот же приём, что у выключенного селектора в композере (CompanionSelector)
        <div style={{
          display: 'flex', alignItems: 'center', gap: 5, maxWidth: 320,
          fontSize: FS.xs, color: C.textMuted, lineHeight: 1.3, textAlign: 'center',
        }}>
          <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          <span>{COMPANION_LOCKED_HINT}</span>
        </div>
      ) : (
        <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
          Поговорить с…
        </div>
      )}
      <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', justifyContent: 'center', maxWidth: 480 }}>
        {visible.map(p => (
          <PersonaPill key={p.id} p={p} active={p.id === selectedPersonaId} onPick={onPick} locked={locked} />
        ))}
        {hiddenCount > 0 && (
          <button
            onClick={() => setExpanded(true)}
            title="Показать глобальные персоны и пантеон"
            style={{
              display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4,
              border: 'none', background: 'none', cursor: 'pointer', padding: 2, width: 64,
            }}
          >
            <span style={{
              width: 44, height: 44, borderRadius: R.full, margin: 2,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: C.bgWhite, border: `1px dashed ${C.border}`,
              fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, color: C.textMuted,
            }}>
              +{hiddenCount}
            </span>
            <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>ещё</span>
          </button>
        )}
      </div>
    </div>
  );
}
