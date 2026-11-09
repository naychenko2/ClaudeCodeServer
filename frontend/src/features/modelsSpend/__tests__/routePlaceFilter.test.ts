import { describe, expect, it } from 'vitest';
import { filterPlaceRoutes, FACE_POSE_ROUTE, type PlaceRouteContext, type RouteCandidate } from '../../../lib/routePlaceFilter';

// Фильтр маршрутов пикера «Поставщики моделей» по месту (ADR-027 §2). Чистая функция:
// проверяем три мутации — место с картинками теряет слепые маршруты, face-pose живёт
// только у ракурса, обычное место не трогается. hasVision считает вызывающий, функция
// лишь фильтрует по готовому признаку, поэтому кандидаты в тестах уже несут его.

// Маршруты, которые пикер мог бы показать: уровни, локаль, модели.
const VISION_MODEL: RouteCandidate = { route: 'claude-sonnet', hasVision: true };
const BLIND_MODEL: RouteCandidate = { route: 'glm-5.2', hasVision: false };
const LOCAL: RouteCandidate = { route: 'local', hasVision: false };
const TIER_STRONG: RouteCandidate = { route: 'tier:strong', hasVision: true };
const TIER_WEAK: RouteCandidate = { route: 'tier:weak', hasVision: false };

const IMAGE: PlaceRouteContext = { input: 'images', facePose: false };
const IMAGE_ANGLE: PlaceRouteContext = { input: 'images', facePose: true };
const TEXT: PlaceRouteContext = { input: 'text', facePose: false };

const routes = (list: RouteCandidate[]) => list.map(c => c.route);

describe('filterPlaceRoutes', () => {
  it('место с картинками — без маршрутов без зрения', () => {
    const out = filterPlaceRoutes(IMAGE, [TIER_STRONG, TIER_WEAK, LOCAL, VISION_MODEL, BLIND_MODEL]);
    // Сильный слот (vision) и vision-модель остаются; слабый слот, локаль и слепая модель — нет.
    expect(routes(out)).toEqual(['tier:strong', 'claude-sonnet']);
    // Ни один оставшийся маршрут не слепой.
    for (const c of out) expect(c.hasVision).toBe(true);
  });

  it('face-pose добавляется только на месте ракурса', () => {
    // Место ракурса (facePose) — пункт face-pose есть.
    const angle = filterPlaceRoutes(IMAGE_ANGLE, [VISION_MODEL]);
    expect(routes(angle)).toContain(FACE_POSE_ROUTE);
    // Обычное место с картинками (без facePose) — face-pose нет.
    const noAngle = filterPlaceRoutes(IMAGE, [VISION_MODEL]);
    expect(routes(noAngle)).not.toContain(FACE_POSE_ROUTE);
    // Текстовое место, даже с facePose, face-pose не получает: это маршрут зрения.
    const textWithFlag = filterPlaceRoutes({ input: 'text', facePose: true }, [VISION_MODEL]);
    expect(routes(textWithFlag)).not.toContain(FACE_POSE_ROUTE);
  });

  it('обычное место — список без изменений', () => {
    const all = [TIER_STRONG, TIER_WEAK, LOCAL, VISION_MODEL, BLIND_MODEL];
    const out = filterPlaceRoutes(TEXT, all);
    // Порядок и состав совпадают с входом: ни один слепой маршрут не вырезан.
    expect(routes(out)).toEqual(routes(all));
    // face-pose на текстовом месте не появляется.
    expect(routes(out)).not.toContain(FACE_POSE_ROUTE);
  });

  it('face-pose на месте ракурса помечен как vision', () => {
    const out = filterPlaceRoutes(IMAGE_ANGLE, [BLIND_MODEL]);
    const face = out.find(c => c.route === FACE_POSE_ROUTE);
    expect(face).toBeDefined();
    expect(face?.hasVision).toBe(true);
  });
});
