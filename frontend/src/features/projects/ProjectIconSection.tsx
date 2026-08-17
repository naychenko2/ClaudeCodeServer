import { useState } from 'react';
import type { ComponentType, SVGProps } from 'react';
import { Pencil, Check, Sparkles } from 'lucide-react';
import type { Project } from '../../types';
import { api, type GlyphCandidate } from '../../lib/api';
import { C, R, FONT, SHADOW, SP } from '../../lib/design';
import { Button } from '../../components/ui';
import { Menu, MenuItem } from '../../components/ui/Menu';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { ProjectIcon } from './ProjectIcon';
import { GLYPHS } from '../../lib/projectGlyphs';
import { invalidateProjectsCache } from './useAllProjects';

// Черновик значка при создании проекта (проекта ещё нет — держим кандидата в памяти
// вкладки и досылаем через selectIcon после create()). Источник данных — глиф, не blob:
// `name` для lucide-имени, `paths` для нарисованных моделью строк d.
export type DraftGlyph = { name?: string | null; paths?: string[] | null };

// Текст ошибки подбора — дословно из продуктовой спеки (docs/product/project-icon-glyphs.md §1.3).
const GLYPH_FAIL_MSG = 'Не удалось подобрать значок — оставили инициалы. Попробуйте ещё раз или опишите своими словами.';

// Техническая недоступность маршрута подбора (HTTP 405): отличаем от отказа модели,
//
// потому что «Попробовать снова» тут бесполезен — ретрай бьёт в тот же отсутствующий
// маршрут. Retry имеет смысл только после починки/деплоя бэкенда.
const GLYPH_ROUTE_UNAVAILABLE_MSG = 'Подбор значка сейчас недоступен — попробуйте позже.';

// Пункт меню и пояснение под ним — (дословно §1.1 спеки).
// Пояснение выводим в подписи под пунктом: оставляем строку как «По смыслу названия,
// в цвете проекта» без точки в конце (как подпись, а не предложение).
const MENU_LABEL_GLYPH = '✨ Подобрать значок';
const MENU_HINT_GLYPH = 'По смыслу названия, в цвете проекта';
const MENU_LABEL_COLOR = 'Цвет проекта';
// «Цвет фона» переименован: палитра красит и инициалы, и плитку глифа (макет §"Композиция секции").
const MENU_LABEL_RESET_TO_INITIALS = 'Вернуть инициалы';
const MENU_LABEL_RESET_TO_GLYPH = 'Вернуть значок';
const PLACEHOLDER_PROMPT = 'Опишите, что изобразить (необязательно)…';
const SUGGESTING_LABEL = 'Подбираю…';
const REST_PICKING_LABEL = 'Подобрать';

// Секция значка в настройках проекта и в диалоге создания (ADR-009, макет project-icon-glyph.md).
//
// Режим editing (по умолчанию): значок мутируется СРАЗУ вызовами по project.id
// (suggest/select/mode), возвращается Project → onIconUpdated.
//
// Режим creating: проекта ещё нет. Кандидаты добываются через suggestIconPreview по
// текущему имени. После выбора кандидат лежит в pendingGlyph; при confirm() родитель
// создаёт проект и вызывает selectIcon с этим же кандидатом.
//
// Цвет применяется в Edit — на «Сохранить» родителем (color/onColorChange), в creating —
// сохраняется в color и уезжает в create(). Превью цвета — живое (projectMainColor).
export function ProjectIconSection({ project, name, onNameChange, color, onColorChange, onIconUpdated, onDraftGlyphChange, creating = false }: {
  project: Project;
  name: string;
  onNameChange: (v: string) => void;
  color: string | null;
  onColorChange: (c: string | null) => void;
  onIconUpdated: (updated: Project) => void;
  // Только в creating: отдаёт выбранный значок наверх (диалог создания отправит его
  // через selectIcon после create()).
  onDraftGlyphChange?: (glyph: DraftGlyph | null) => void;
  creating?: boolean;
}) {
  // Активный значок: в edit — на сохранённой записи проекта; в creating — в pendingGlyph.
  const activeGlyph = (creating ? project.icon?.glyph : project.icon?.glyph) ?? null;
  const isGlyphActive = !creating && project.icon?.kind === 'glyph' && !!activeGlyph;

  // Блок подбора: в editing держим имя и id для повторного «Попробовать снова»;
  // в creating — список приходит с preview-эндпоинта и валится в onDraftGlyphChange.
  const [menuOpen, setMenuOpen] = useState(false);
  const [showSuggest, setShowSuggest] = useState(false);
  const [suggInput, setSuggInput] = useState('');
  const [suggBusy, setSuggBusy] = useState(false);
  const [suggErr, setSuggErr] = useState('');
  // Отдельный флаг «маршрут подбора недоступен» (HTTP 405): не выводим GLYPH_FAIL_MSG и
  // не показываем кнопку «Попробовать снова» — ретрай в тот же отсутствующий маршрут
  // бессмысленен. Это техническая проблема бэкенда, а не отказ модели.
  const [suggRouteDown, setSuggRouteDown] = useState(false);
  const [suggestions, setSuggestions] = useState<GlyphCandidate[] | null>(null);

  // Превью для <ProjectIcon>: в creating значок-предпросмотр передаётся через
  // «теневой» project.icon; иначе — просто project.icon. Цвет плитки — projectMainColor,
  // который читает Icon.Color (или hash от id, LEGACY-фолбэк).
  const preview: Project = {
    ...project,
    icon: {
      ...(project.icon ?? { kind: 'initials' }),
      // В creating активный глиф — pendingGlyph; в editing — то, что уже записано у проекта.
      glyph: activeGlyph,
      color: color ?? project.icon?.color,
    },
  };

  const applyUpdated = (updated: Project) => {
    invalidateProjectsCache();
    onIconUpdated(updated);
  };

  const suggest = async () => {
    setSuggBusy(true);
    setSuggErr('');
    setSuggRouteDown(false);
    setSuggestions(null);
    try {
      const r = creating
        ? await api.projects.suggestIconPreview({ name, prompt: suggInput })
        : await api.projects.suggestIcon(project.id, { prompt: suggInput });
      // Любой исход «годных кандидатов ноль» — отказ, не только исключение: бэкенд
      // не бросает ошибку на недоступной модели, а молча возвращает пустой набор
      // с failReason (ADR-009 §7). Кандидатов 1–3 — НЕ ошибка, грид покажет сколько есть.
      if (r.candidates.length === 0) {
        setSuggErr(GLYPH_FAIL_MSG);
      } else {
        setSuggestions(r.candidates);
      }
    } catch (e: unknown) {
      // 405 «Method Not Allowed» на маршруте подбора = бэкенд по этому пути не
      // отвечает (маршрут не задеплоен). Это не «модель не подобрала», а техническая
      // недоступность — отдельное сообщение без кнопки «Попробовать снова».
      const status = (e as { status?: number })?.status;
      if (status === 405) {
        // Диагностика в консоль — фронт без неё о причине только догадывается.
        console.error('[ProjectIconSection] suggest route unavailable', {
          creating,
          projectId: project.id,
          status,
          message: e instanceof Error ? e.message : String(e),
        });
        setSuggRouteDown(true);
      } else {
        setSuggErr(e instanceof Error ? e.message : GLYPH_FAIL_MSG);
      }
    } finally {
      setSuggBusy(false);
    }
  };

  const choose = async (c: GlyphCandidate) => {
    setSuggErr('');
    try {
      if (creating) {
        const draft: DraftGlyph = { name: c.name ?? null, paths: c.paths ?? null };
        onDraftGlyphChange?.(draft);
        setSuggestions(null);
        setShowSuggest(false);
        setMenuOpen(false);
        return;
      }
      const updated = await api.projects.selectIcon(project.id, {
        name: c.name ?? null,
        paths: c.paths ?? null,
      });
      applyUpdated(updated);
      setSuggestions(null);
      setShowSuggest(false);
      setMenuOpen(false);
    } catch (e: unknown) {
      setSuggErr(e instanceof Error ? e.message : GLYPH_FAIL_MSG);
    }
  };

  const setMode = async (kind: 'initials' | 'glyph') => {
    setSuggErr('');
    try {
      applyUpdated(await api.projects.setIconMode(project.id, kind));
      setMenuOpen(false);
    } catch (e: unknown) {
      setSuggErr(e instanceof Error ? e.message : GLYPH_FAIL_MSG);
    }
  };

  return (
    <div>
      {((suggErr || suggRouteDown) && !showSuggest) && (
        <div style={{ fontSize: 12, color: C.danger, marginBottom: 6 }}>
          {suggRouteDown ? GLYPH_ROUTE_UNAVAILABLE_MSG : suggErr}
        </div>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        {/* Превью + ✎-кнопка в углу. Все действия — в меню, чтобы layout не прыгал. */}
        <div style={{ position: 'relative', flexShrink: 0 }}>
          <ProjectIcon project={preview} size={56} radius={14} />
          <button
            type="button"
            onClick={() => setMenuOpen(v => !v)}
            aria-label="Изменить иконку"
            title="Изменить иконку"
            disabled={suggBusy}
            style={{
              position: 'absolute', right: -5, bottom: -5, width: 24, height: 24, borderRadius: R.full,
              border: `2.5px solid ${C.bgMain}`, background: C.accent, color: C.onAccent,
              cursor: suggBusy ? 'default' : 'pointer', padding: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              boxShadow: SHADOW.thumb, transition: 'background 0.15s',
            }}
          >
            <Pencil size={13} strokeWidth={2.4} style={{ flexShrink: 0 }} />
          </button>

          {menuOpen && (
            <Menu onClose={() => setMenuOpen(false)} align="left" top={64} minWidth={236}>
              <MenuItem
                label={MENU_LABEL_GLYPH}
                onClick={() => { setMenuOpen(false); setShowSuggest(true); setSuggErr(''); setSuggRouteDown(false); }}
              />
              <div style={{ fontSize: 11.5, color: C.textMuted, padding: '0 12px 6px', fontFamily: FONT.sans }}>
                {MENU_HINT_GLYPH}
              </div>
              <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '4px 2px' }} />
              {/* Палитра цвета: красит и инициалы, и плитку глифа (макет §"Композиция секции"). */}
              <div style={{ padding: '4px 8px 6px' }}>
                <div style={{ fontSize: 11.5, color: C.textMuted, marginBottom: 7, fontFamily: FONT.sans }}>
                  {MENU_LABEL_COLOR}
                </div>
                <div style={{ display: 'flex', gap: 7, flexWrap: 'wrap' }}>
                  {Object.keys(AGENT_COLORS).map(key => (
                    <button
                      key={key}
                      type="button"
                      title={key}
                      onClick={() => onColorChange(color === key ? null : key)}
                      style={{
                        width: 20, height: 20, borderRadius: '50%', cursor: 'pointer', flexShrink: 0,
                        background: agentDotColor(key),
                        border: color === key ? `2px solid ${C.textHeading}` : '2px solid transparent',
                      }}
                    />
                  ))}
                </div>
              </div>
              {/* «Вернуть инициалы» / «Вернуть значок» — режим переключения, файл не трогается. */}
              {!creating && isGlyphActive && (
                <>
                  <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '4px 2px' }} />
                  <MenuItem label={MENU_LABEL_RESET_TO_INITIALS} onClick={() => void setMode('initials')} />
                </>
              )}
              {!creating && !isGlyphActive && project.icon?.glyph && (
                <>
                  <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '4px 2px' }} />
                  <MenuItem label={MENU_LABEL_RESET_TO_GLYPH} onClick={() => void setMode('glyph')} />
                </>
              )}
            </Menu>
          )}
        </div>

        {/* Название проекта — крупный serif-ввод рядом с иконкой. */}
        <input
          value={name}
          onChange={e => onNameChange(e.target.value)}
          placeholder="Название проекта"
          autoFocus={creating}
          style={{
            flex: 1, minWidth: 0, boxSizing: 'border-box',
            border: 'none', outline: 'none', background: 'transparent',
            fontFamily: FONT.serif, fontSize: 22, fontWeight: 500,
            color: C.textHeading, padding: 0, lineHeight: 1.3,
          }}
        />
      </div>

      {/* Форма подбора значка: поле + кнопка; под ними грид кандидатов либо скелетоны. */}
      {showSuggest && (
        <div style={{ marginTop: SP.sm, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <div style={{ display: 'flex', gap: 6 }}>
            <input
              value={suggInput}
              onChange={e => setSuggInput(e.target.value)}
              placeholder={PLACEHOLDER_PROMPT}
              onKeyDown={e => { if (e.key === 'Enter' && !suggBusy) { e.preventDefault(); void suggest(); } }}
              disabled={suggBusy}
              style={{
                flex: 1, minWidth: 0, boxSizing: 'border-box', height: 32, padding: '0 10px',
                borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
                fontSize: 12.5, color: C.textPrimary, outline: 'none', fontFamily: FONT.sans,
              }}
            />
            <Button
              variant="secondary" size="sm" onClick={suggest}
              loading={suggBusy} disabled={suggBusy}
              style={{ flexShrink: 0 }}
            >
              {suggBusy ? SUGGESTING_LABEL : REST_PICKING_LABEL}
            </Button>
          </div>

          {/* Скелетоны во время подбора: 4 плитки с пульсом C.bgSelected ↔ C.borderLight. */}
          {suggBusy && (
            <div data-testid="glyph-skeleton" style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 }}>
              {[0, 1, 2, 3].map(i => (
                <div
                  key={i}
                  aria-hidden
                  className="glyph-skeleton"
                  style={{
                    aspectRatio: '1', borderRadius: 10,
                    border: `1px solid ${C.border}`, background: C.bgSelected,
                    animation: 'glyph-skeleton-pulse 1.2s ease-in-out infinite',
                  }}
                />
              ))}
              <style>{`@keyframes glyph-skeleton-pulse { 0%,100% { opacity: 1 } 50% { opacity: 0.45 } }`}</style>
            </div>
          )}

          {/* Грид кандидатов: до 4 плиток, плитка-подложка 60% от самой плитки, глиф в цвете проекта. */}
          {!suggBusy && suggestions && suggestions.length > 0 && (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 }}>
              <GlyphCandidates
                candidates={suggestions}
                selected={null}
                projectColor={projectMainColorCss(project, color)}
                onChoose={c => void choose(c)}
              />
            </div>
          )}

          {/* Отказ при явном действии (модель недоступна, JSON битый, 0 годных кандидатов). */}
          {!suggBusy && suggErr && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
              <div style={{ fontSize: 12.5, color: C.dangerText, flex: 1, minWidth: 0, fontFamily: FONT.sans }}>
                {GLYPH_FAIL_MSG}
              </div>
              <Button variant="secondary" size="sm" onClick={() => void suggest()}>
                Попробовать снова
              </Button>
            </div>
          )}

          {/* Техническая недоступность маршрута подбора (HTTP 405): отдельный текст,
              без кнопки «Попробовать снова» — ретрай в отсутствующий маршрут бесполезен. */}
          {!suggBusy && suggRouteDown && (
            <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>
              {GLYPH_ROUTE_UNAVAILABLE_MSG}
            </div>
          )}

          {/* Текстовая «Отмена» под гридом — закрывает подбор без выбора. */}
          {!suggBusy && (suggestions && suggestions.length > 0) && (
            <button
              type="button"
              onClick={() => { setSuggestions(null); setShowSuggest(false); }}
              style={{
                marginTop: 0, background: 'none', border: 'none',
                color: C.textMuted, fontSize: 12, cursor: 'pointer',
                fontFamily: FONT.sans, alignSelf: 'flex-start',
              }}
            >
              Отмена
            </button>
          )}
        </div>
      )}
    </div>
  );
}

// Цвет плитки-подложки для грида кандидатов: явно выбранный пользователем цвет
// (creating) или записанный в Icon.Color (editing). Дефолт agentDotColor — для
// проектов без явного цвета.
function projectMainColorCss(project: Project, color: string | null): string {
  return agentDotColor(color ?? project.icon?.color);
}

// Плитка кандидата: плитка-подложка 60% от плитки, на ней — миниатюрный глиф в
// цвете проекта. Кольцо выбора — 2px C.accent + 18px бейдж с галочкой.
function GlyphCandidates({ candidates, selected, projectColor, onChoose }: {
  candidates: GlyphCandidate[];
  selected: string | null | undefined;
  projectColor: string;
  onChoose: (c: GlyphCandidate) => void;
}) {
  return (
    <>
      {candidates.map((c, i) => {
        const key = c.name ?? `paths-${i}`;
        const isSelected = selected != null && selected === key;
        return (
          <button
            key={key}
            type="button"
            onClick={() => onChoose(c)}
            aria-label={c.name ? `Значок ${c.name}` : 'Нарисованный значок'}
            style={{
              position: 'relative',
              padding: 0,
              border: isSelected ? `2px solid ${C.accent}` : `1px solid ${C.border}`,
              background: C.bgWhite,
              cursor: 'pointer',
              borderRadius: 10,
              aspectRatio: '1',
              overflow: 'hidden',
              boxShadow: isSelected ? `0 0 0 1px ${C.accent}` : 'none',
            }}
          >
            {/* Плитка-подложка 60% от плитки кандидата, округлая — глиф живёт на ней. */}
            <div
              aria-hidden
              style={{
                position: 'absolute',
                left: '20%', top: '20%', width: '60%', height: '60%',
                borderRadius: '22%',
                background: projectColor,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                color: C.onDark,
              }}
            >
              <GlyphThumb candidate={c} />
            </div>
            {isSelected && (
              <span
                aria-hidden
                style={{
                  position: 'absolute', top: 4, right: 4,
                  width: 18, height: 18, borderRadius: '50%',
                  background: C.accent, color: C.onAccent,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  boxShadow: SHADOW.thumb,
                }}
              >
                <Check size={11} strokeWidth={2.4} />
              </span>
            )}
          </button>
        );
      })}
    </>
  );
}

// Миниатюра глифа в плитке-кандидате: тот же путь (paths значениями d, name из GLYPHS),
// но в крупном размере (60% от плитки-подложки, ~ 36% от плитки кандидата).
function GlyphThumb({ candidate }: { candidate: GlyphCandidate }) {
  const stroke = 2;
  if (candidate.paths && candidate.paths.length > 0) {
    return (
      <svg
        width="60%" height="60%" viewBox="0 0 24 24"
        fill="none" stroke="currentColor" strokeWidth={stroke}
        strokeLinecap="round" strokeLinejoin="round" aria-hidden
      >
        {candidate.paths.map((d, i) => <path key={i} d={d} />)}
      </svg>
    );
  }
  if (candidate.name) {
    const Named = GLYPHS[candidate.name as keyof typeof GLYPHS] as
      | ComponentType<SVGProps<SVGSVGElement> & { size?: number; strokeWidth?: number }>
      | undefined;
    if (Named) {
      return <Named size={Math.round(24 * 0.6)} strokeWidth={stroke} />;
    }
  }
  // Имени нет в карте — миниатюрная пустышка со Sparkles, чтобы плитка не схлопнулась.
  return <Sparkles size={Math.round(24 * 0.6)} strokeWidth={stroke} />;
}
