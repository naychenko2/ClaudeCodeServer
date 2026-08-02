// Цвет проекта как оформительский тон: рамки и подпалы карточек чата.
//
// Формула одна на весь продукт — та же, что у ProjectIcon: цвет из палитры
// (project.icon.color), иначе детерминированный projectColor(id). Отдельный
// модуль нужен, чтобы «цвет проекта» не считался по-своему в каждом месте.
import type { Project } from '../types';
import { agentDotColor } from '../components/AgentSelector';
import { projectColor } from './tasks';

export function projectTone(project?: Project | null): string | null {
  if (!project) return null;
  return project.icon?.color ? agentDotColor(project.icon.color) : projectColor(project.id).main;
}

// Тот же цвет с прозрачностью. Палитровые цвета — hex (альфа приклеивается
// суффиксом), фолбэк может быть CSS-переменной, к которой альфу не приклеить —
// для неё color-mix (приём из цветовой вуали персон).
export function fadeTone(color: string, alpha: number): string {
  return /^#[0-9a-f]{6}$/i.test(color)
    ? color + Math.round(alpha * 255).toString(16).padStart(2, '0')
    : `color-mix(in srgb, ${color} ${Math.round(alpha * 100)}%, transparent)`;
}

// Подпал цветом проекта под верхом карточки чата: подкрашивает шапку и тает к
// ленте. Мягкий (active) — у той карточки, в которой сейчас работают.
export function projectTopWash(project: Project | null | undefined, active = true): string | undefined {
  const tone = projectTone(project);
  if (!tone) return undefined;
  return `linear-gradient(180deg, ${fadeTone(tone, active ? 0.16 : 0.07)} 0, transparent 96px)`;
}
