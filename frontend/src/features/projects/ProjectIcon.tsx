import type { Project } from '../../types';
import { C, FONT } from '../../lib/design';
import { GlyphIcon, isLucideIconName } from '../../lib/projectGlyphs';
import { projectInitials, projectMainColor } from './projectUtil';

// Глиф живёт на своей плитке: 60% стороны плитки (поля 20% со всех сторон). Люк-стандарт
// требует strokeWidth = 2 в координатах viewBox 24 (≈8.3% от размера глифа), и 2.4
// при глифе <16px (≈10%), чтобы штрих не уходил под 1px на ретине.
const GLYPH_RATIO = 0.6;
const STROKE_BIG = 2;
const STROKE_SMALL = 2.4;

// Минимальный размер плитки, на которой глиф ещё читается. Док стены, WallPicker
// (< 20px) остаются на инициалах — значок превратился бы в кляксу, а буквы ещё
// читаются. Граница из макета §"Размеры по местам".
const GLYPH_MIN_PX = 20;

// Глиф берётся именем из ПОЛНОГО набора lucide (ADR-009 §5) через GlyphIcon: бэкенд
// подбирает имя из всех ~2000 имён пакета, и рукописная карта GLYPHS (89 имён) молча
// съедала остальные — плитка выходила пустой (ни значка, ни букв). Проверка имени —
// isLucideIconName по тому же набору, что у loader'ов DynamicIcon.
function ProjectGlyph({ project, size }: { project: Project; size: number }) {
  const glyph = project.icon?.glyph;
  const inner = size * GLYPH_RATIO;
  const offset = (size - inner) / 2;
  const stroke = size < 16 ? STROKE_SMALL : STROKE_BIG;
  if (!glyph?.name) return null;
  // Пока чанк значка едет — и если он не доедет вовсе (офлайн, промах кеша после
  // выкатки) — на плитке стоят инициалы: пустой плитки не бывает ни в одном состоянии
  // (ADR-009 §7). Пропсы в fallback не передаются: DynamicIcon зовёт createElement(Fallback).
  const Fallback = () => (
    <span style={{ fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.38), lineHeight: 1 }}>
      {projectInitials(project.name)}
    </span>
  );
  return (
    <GlyphIcon
      name={glyph.name}
      fallback={Fallback}
      size={inner}
      strokeWidth={stroke}
      style={{ position: 'absolute', left: offset, top: offset, color: 'currentColor' }}
    />
  );
}

// Единая иконка проекта (по образцу PersonaAvatar, но КВАДРАТНАЯ со скруглением —
// чтобы отличаться от круглых персон). Три состояния:
//   1. kind === 'glyph' и glyph валидный (name из карты)
//      → плитка projectMainColor + белый штриховой глиф (currentColor, значок сам
//      перекрашивается при смене цвета/темы, регенерации не нужно).
//   2. muted=true (спящий ряд) — плитка заменяется бледным контуром, глиф в C.textMuted.
//   3. иначе — инициалы на цветной плитке. То же в muted-режиме, только цвет бледный.
// Выбор показа значка — положительная проверка `kind === 'glyph'` (ADR-009 §7).
export function ProjectIcon({ project, size = 40, radius, muted }: { project: Project; size?: number; radius?: number; muted?: boolean }) {
  const br = radius ?? Math.round(size * 0.22);
  const base: React.CSSProperties = {
    width: size, height: size, borderRadius: br, flexShrink: 0, userSelect: 'none',
    position: 'relative',
  };

  // Условие показа значка — положительное (kind === 'glyph' И имя есть в наборе lucide).
  // НИКОГДА !== 'initials': старая запись с числовым Kind = 1 (бывший Image) должна
  // попасть в инициалы, а не в ветку значка, которого нет (ADR-009 §7). Имя вне набора
  // (левая строка, снятое из пакета имя) уходит той же веткой на инициалы.
  const showGlyph = size >= GLYPH_MIN_PX
    && project.icon?.kind === 'glyph'
    && !!project.icon.glyph
    && project.icon.glyph.name != null
    && project.icon.glyph.name !== ''
    && isLucideIconName(project.icon.glyph.name);

  if (showGlyph && muted) {
    return (
      <div
        aria-hidden
        style={{
          ...base,
          border: `1px solid ${C.border}`, boxSizing: 'border-box',
          color: C.textMuted,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        <ProjectGlyph project={project} size={size} />
      </div>
    );
  }

  if (showGlyph) {
    return (
      <div
        aria-hidden
        style={{
          ...base,
          background: projectMainColor(project), color: C.onDark,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        <ProjectGlyph project={project} size={size} />
      </div>
    );
  }

  // Muted-режим для инициалов: бледная рамка и инициалы вместо цветной плашки.
  // Спящий ряд держит вес, а не пестрит цветными плитками — выбор при этом
  // метится кольцом кнопки, а не заливкой этой иконки.
  if (muted) {
    return (
      <div
        aria-hidden
        style={{
          ...base,
          border: `1px solid ${C.border}`, boxSizing: 'border-box',
          color: C.textMuted,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontWeight: 600, fontSize: Math.round(size * 0.34),
          lineHeight: 1,
        }}
      >
        {projectInitials(project.name)}
      </div>
    );
  }

  const bg = projectMainColor(project);
  return (
    <div
      aria-hidden
      style={{
        ...base,
        background: bg, color: C.onDark,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.38),
        lineHeight: 1,
      }}
    >
      {projectInitials(project.name)}
    </div>
  );
}
