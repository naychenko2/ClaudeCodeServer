// Т4d «Предметов и репетиторов» (макет Д1, секция «Предметы на главной»): контролируемый список
// предметов. Состояния и запросов своих нет: черновик держит родитель (Т4f), каждое действие
// строит НОВЫЙ массив и отдаёт его целиком onChange(next); порядок массива = порядок плиток у
// ребёнка (И5). Пустой список — empty-state с подсказкой (макет «Предметов нет»).
//
// id рождается из slug названия (тот же разбор, что lib/slug — зеркало бэкового Slugify) в момент,
// когда название впервые становится непустым; с этого момента id от названия не зависит —
// «правка названия id не меняет» (контракт шага). Коллизия — суффикс -2, -3… с сохранением
// потолка ^[a-z0-9-]{1,32}$ (ChildProfileValidator.cs:201). Новая строка до названия id не имеет:
// в тело она не уйдёт (название обязано, subject_label_invalid), а до появления id строка
// ключуется draft-ключом — состояния по строкам у компонента нет, контролы управляемые,
// поэтому индексный ключ здесь не смешивает данные.
//
// Подсказки у полей — правила валидатора Т2a (название 1..40, глиф 1..3), до отправки;
// ввод обрезается по этим же длинам (TextField maxLength не пропускает — срез в onChange).
// Ошибка поля — когда значение точно не пройдёт сервер: у предмета с id (он уже назван)
// название или глиф очищены. Новая строка с пустым названием — не ошибка: человек её заполняет.
// Серверный 400 показывает родитель (Т4f), здесь своего «Сохранить» нет.
//
// Маршрут нового предмета — «маршрут Помощника (правила)»: его знает только родитель, поэтому
// проп defaultRoute (значение из его черновика); без него — первая пара из routes (тот же
// порядок, что отдаёт GET /api/admin/child-routes). Пустые routes — пустая пара: пикер сам
// скажет «Нет записей пула с API-ключом» (Т4c), список предметов при этом работает.
//
// Клик по строке (её текст) — onSelect(id): карточку репетитора открывает родитель (Т4f);
// selectedId — отмеченная строка. Строка без id не выбирается: открывать нечего.

import type { CSSProperties } from 'react';
import { BookOpen, ChevronDown, ChevronUp, Plus, Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { Field, TextField } from '../../components/ui/Field';
import { FieldLabel } from '../../components/ui/Field';
import { Toggle } from '../../components/ui/Toggle';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { slugify } from '../../lib/slug';
import { ChildRoutePicker, type ChildRouteValue } from './ChildRoutePicker';
import { subjectColors } from './subjectPalette';
import type { ChildRoute, ChildSubjectAdmin, ChildSubjectColor } from './types';

// Правила Т2a (ChildProfileValidator.cs:110-113) и потолок id (тот же файл, строка 201).
const LABEL_MAX = 40;
const GLYPH_MAX = 3;
const ID_MAX = 32;

// Палитра и её порядок — макет Д1, раздел 2 «Палитра предметов»: ровно эти пять ключей
// (тот же набор, что ChildSubjectColor на бэке), имена — подписи swatch из макета.
const COLOR_OPTIONS: { key: ChildSubjectColor; name: string }[] = [
  { key: 'info', name: 'Синий' },
  { key: 'rose', name: 'Розовый' },
  { key: 'success', name: 'Зелёный' },
  { key: 'warning', name: 'Янтарный' },
  { key: 'accent', name: 'Терракотовый' },
];

// Slug с суффиксом при коллизии: base остаётся внутри ID_MAX и с суффиксом —
// обрезаем base ровно настолько, чтобы «base-k» влез.
function uniqueId(label: string, taken: Set<string>): string {
  const base = slugify(label).slice(0, ID_MAX) || 'predmet';
  let id = base;
  for (let k = 2; taken.has(id); k++) {
    id = `${base.slice(0, ID_MAX - 1 - String(k).length)}-${k}`;
  }
  return id;
}

interface Props {
  // Черновик предмета из профиля (тот же элемент, что тело subjects): компонент его не держит.
  subjects: ChildSubjectAdmin[];
  // Элементы GET /api/admin/child-routes как есть (Т4c): пикеру и маршруту по умолчанию.
  routes: ChildRoute[];
  onChange: (next: ChildSubjectAdmin[]) => void;
  // Открытая сейчас карточка репетитора (Т4f): строка отмечена, клик по другой строке зовёт onSelect.
  selectedId: string | null;
  onSelect: (id: string) => void;
  // Маршрут «Помощника (правила)» из черновика родителя — им и начинается новый предмет.
  defaultRoute?: ChildRouteValue;
  // Гасит контролы на время сохранения (макет Д1 «Сохранение») — состояние родителя.
  disabled?: boolean;
}

export function ChildSubjectsEditor({ subjects, routes, onChange, selectedId, onSelect, defaultRoute, disabled }: Props) {
  const isMobile = useIsMobile();

  const takenIds = new Set(subjects.map((s) => s.id).filter(Boolean));
  const defaultRouteValue = (): ChildRouteValue =>
    defaultRoute ?? (routes.length > 0
      ? { providerKey: routes[0].providerKey, model: routes[0].models[0]?.id ?? '' }
      : { providerKey: '', model: '' });

  const patch = (i: number, fn: (s: ChildSubjectAdmin) => ChildSubjectAdmin) =>
    onChange(subjects.map((s, j) => (j === i ? fn(s) : s)));

  const addSubject = () => {
    const route = defaultRouteValue();
    onChange([
      ...subjects,
      { id: '', label: '', glyph: '', color: 'info', providerKey: route.providerKey, model: route.model, enabled: true },
    ]);
  };

  const move = (i: number, dir: -1 | 1) => {
    const j = i + dir;
    if (j < 0 || j >= subjects.length) return;
    const next = [...subjects];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  };

  const remove = (i: number) => onChange(subjects.filter((_, j) => j !== i));

  const rowGap = isMobile ? SP.sm : SP.md;
  const rootStyle: CSSProperties = { display: 'flex', flexDirection: 'column', gap: rowGap };
  const rowStyle = (selected: boolean): CSSProperties => ({
    border: `1px solid ${selected ? C.accent : C.borderLight}`,
    borderRadius: R.xl, background: C.bgWhite, overflow: 'hidden',
  });
  const headStyle: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: SP.sm,
    padding: `${SP.sm}px ${SP.xs}px ${SP.sm}px ${SP.sm}px`, minHeight: 56,
  };
  const bodyStyle: CSSProperties = {
    borderTop: `1px solid ${C.borderLight}`, background: C.bgCard,
    padding: isMobile ? SP.sm : SP.md, display: 'flex', flexDirection: 'column', gap: rowGap,
  };
  const glyphTile = (color: ChildSubjectColor, dim: boolean): CSSProperties => {
    const p = subjectColors(color);
    return {
      width: 40, height: 40, flexShrink: 0, borderRadius: R.lg, display: 'grid', placeItems: 'center',
      fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, lineHeight: 1, whiteSpace: 'nowrap',
      background: p.bg, color: p.fg, opacity: dim ? 0.55 : 1,
    };
  };
  const swatchStyle = (color: ChildSubjectColor, active: boolean): CSSProperties => {
    const p = subjectColors(color);
    return {
      width: 40, height: 40, borderRadius: R.lg, padding: 0, cursor: 'pointer',
      display: 'grid', placeItems: 'center', fontFamily: FONT.serif, fontWeight: 700, fontSize: FS.md,
      background: p.bg, color: p.fg, border: `2px solid ${active ? C.textHeading : 'transparent'}`,
    };
  };

  const subjectName = (s: ChildSubjectAdmin): string => s.label.trim() || 'без названия';

  const upButton = (s: ChildSubjectAdmin, i: number, mobile: boolean) => (
    <Button
      variant="ghost"
      // md — touch-размер ≥40 (спека мобиле); на десктопе тот же control остаётся читаемым
      size={mobile ? 'md' : 'sm'}
      fullWidth={mobile}
      ariaLabel={`Выше: ${subjectName(s)}`}
      disabled={i === 0 || disabled}
      onClick={() => move(i, -1)}
    >
      {mobile && <ChevronUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      {mobile ? 'Выше' : <ChevronUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
    </Button>
  );
  const downButton = (s: ChildSubjectAdmin, i: number, mobile: boolean) => (
    <Button
      variant="ghost"
      size={mobile ? 'md' : 'sm'}
      fullWidth={mobile}
      ariaLabel={`Ниже: ${subjectName(s)}`}
      disabled={i === subjects.length - 1 || disabled}
      onClick={() => move(i, 1)}
    >
      {mobile && <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      {mobile ? 'Ниже' : <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
    </Button>
  );

  const addBtn = (primary: boolean) => (
    <Button
      variant={primary ? 'primary' : 'dashed'}
      size="md"
      dataTestId="add-subject"
      leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      disabled={disabled}
      onClick={addSubject}
    >
      Добавить предмет
    </Button>
  );

  const subjectRow = (s: ChildSubjectAdmin, i: number) => {
    const selected = !!s.id && s.id === selectedId;
    // Пустое название у новой строки — не ошибка (её заполняют); у строки с id — очищено, сервер
    // такой не примет (subject_label_invalid).
    const badLabel = !!s.id && !s.label.trim();
    const badGlyph = !!s.id && !s.glyph.trim();
    return (
      <div key={s.id || `draft-${i}`} data-testid="subject-row" style={rowStyle(selected)}>
        <div style={headStyle}>
          <span style={glyphTile(s.color, !s.enabled)} aria-hidden="true">{s.glyph || '?'}</span>
          <button
            data-testid="subject-open"
            aria-label={`Открыть: ${subjectName(s)}`}
            disabled={!s.id || disabled}
            onClick={() => s.id && onSelect(s.id)}
            style={{
              flex: 1, minWidth: 0, minHeight: 40, display: 'flex', flexDirection: 'column', gap: SP.xxs,
              background: 'none', border: 'none', padding: 0, textAlign: 'left', color: 'inherit',
              cursor: s.id ? 'pointer' : 'default',
            }}
          >
            <span style={{
              fontSize: FS.md, fontWeight: 600, color: C.textHeading,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              display: 'flex', alignItems: 'center', gap: SP.sm, opacity: s.enabled ? 1 : 0.55,
            }}>
              {s.label || 'Без названия'}
              {!s.id && <Badge tone="plan" size="xs">новый</Badge>}
              {!s.enabled && <Badge tone="neutral" size="xs">выключен</Badge>}
            </span>
          </button>
          <span style={{ minWidth: 44, minHeight: 40, display: 'grid', placeItems: 'center', flexShrink: 0 }}>
            <Toggle
              checked={s.enabled}
              ariaLabel={`Предмет «${subjectName(s)}» включён`}
              disabled={disabled}
              onChange={(v) => patch(i, (prev) => ({ ...prev, enabled: v }))}
            />
          </span>
          {!isMobile && (
            <span style={{ display: 'flex', gap: SP.xxs, flexShrink: 0 }}>
              {upButton(s, i, false)}
              {downButton(s, i, false)}
            </span>
          )}
        </div>

        <div style={bodyStyle}>
          <div style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
            <div style={{ flex: 2, minWidth: 0 }}>
              <Field label="Название" hint={`${s.label.length}/${LABEL_MAX}`} error={badLabel ? `Название — от 1 до ${LABEL_MAX} символов` : undefined}>
                <TextField
                  value={s.label}
                  invalid={badLabel}
                  placeholder="Математика"
                  disabled={disabled}
                  onChange={(v) =>
                    patch(i, (prev) => {
                      const next = { ...prev, label: v.slice(0, LABEL_MAX) };
                      // id рождается один раз — по первому непустому названию; дальше не меняется.
                      if (!prev.id && next.label.trim()) next.id = uniqueId(next.label, takenIds);
                      return next;
                    })}
                />
              </Field>
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="Глиф" hint={`1–${GLYPH_MAX} символа`} error={badGlyph ? `Глиф — от 1 до ${GLYPH_MAX} символов` : undefined}>
                <TextField
                  value={s.glyph}
                  invalid={badGlyph}
                  placeholder="Мат"
                  disabled={disabled}
                  onChange={(v) => patch(i, (prev) => ({ ...prev, glyph: v.slice(0, GLYPH_MAX) }))}
                />
              </Field>
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <FieldLabel>Цвет</FieldLabel>
            <div role="radiogroup" aria-label={`Цвет предмета «${subjectName(s)}»`} style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
              {COLOR_OPTIONS.map((c) => (
                <button
                  key={c.key}
                  data-testid={`subject-color-${c.key}`}
                  role="radio"
                  aria-checked={s.color === c.key}
                  aria-label={c.name}
                  title={c.name}
                  disabled={disabled}
                  onClick={() => patch(i, (prev) => ({ ...prev, color: c.key }))}
                  style={swatchStyle(c.key, s.color === c.key)}
                >
                  {s.glyph || '?'}
                </button>
              ))}
            </div>
          </div>

          <ChildRoutePicker
            routes={routes}
            value={{ providerKey: s.providerKey, model: s.model }}
            onChange={(v) => patch(i, (prev) => ({ ...prev, providerKey: v.providerKey, model: v.model }))}
            disabled={disabled}
          />

          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between' }}>
            {isMobile && (
              <span style={{ display: 'flex', gap: SP.sm, width: '100%' }}>
                {upButton(s, i, true)}
                {downButton(s, i, true)}
              </span>
            )}
            <Button
              variant="danger"
              size={isMobile ? 'md' : 'sm'}
              ariaLabel={`Удалить предмет «${subjectName(s)}»`}
              disabled={disabled}
              onClick={() => remove(i)}
            >
              <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              Удалить
            </Button>
          </div>
        </div>
      </div>
    );
  };

  return (
    <div data-testid="child-subjects-editor" style={rootStyle}>
      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        Предметы на главной
      </span>
      <span style={{ fontSize: FS.sm, color: C.textMuted, marginTop: -SP.sm }}>
        Порядок списка — порядок плиток у ребёнка. Выключенный предмет скрыт с главной, его чаты только для чтения.
      </span>
      {subjects.length === 0 ? (
        <EmptyState
          inline
          icon={<BookOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Предметов нет"
          subtitle="Главная у ребёнка будет пустой: уроки открыть будет нечем. Добавь хотя бы один предмет."
          action={addBtn(true)}
        />
      ) : (
        <>
          {subjects.map(subjectRow)}
          {addBtn(false)}
        </>
      )}
    </div>
  );
}
