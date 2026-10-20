# Токены короткой карточки плана в ленте

Новых значений в шкалах нет. Глиф «простаивает» — новый вид `FeatureGlyph` из существующих цветов.

| Элемент | Токены |
|---|---|
| строка А | высота 36 (на 360 — 44, перенос на 2 строки), `R` 10, рамка `C.borderLight`, фон `C.bgCard`; текст 12.5, слово этапа 700 `C.textHeading`, остальное `C.textSecondary` |
| тон `info` | фон `C.infoBg`, полоса слева 3 px `C.info` (inset-тень), рамки нет |
| тон `danger` | фон `C.dangerBg`, рамка `C.dangerBorder`, полоса `C.danger`, слово `C.dangerText` |
| тон `success` | фон `C.successBg`, слово `C.successText` |
| нейтральный | фон `C.bgCard`, слово `C.textSecondary` |
| время, «этап k из 7», «2 из 5» | `FONT.mono` 11, `C.textSecondary` / `C.textMuted` |
| чип «кто сейчас» | высота 24 (на 360 — 40), `R` full, фон `C.bgWhite`, рамка `C.borderLight`, hover `C.accentLight` + `C.accentMuted`; роль `C.textMuted` |
| аватар 18 | пары `h-*` из страницы фичи (`C.accentLight`/`C.accent` у Киры и т. д.) |
| «Подробнее ›» | 12/600 `C.accent`, высота 28 (на 360 — 40), hover `C.accentLight` |
| прогресс плана | дорожка `C.progressTrack`, заливка `C.accent`, 44 × 4 |
| Б: точки этапов | 6 px, пройденные `C.success`, текущая 14 px цветом тона, впереди `C.progressTrack` |
| В: чип в итоге хода | высота 26 (на 360 — 40), `R` 8, фон `C.bgSelected` или фон тона, mono 11 |
| снимок прошлого хода | высота 28 (на 360 — 40), 11.5 `C.textMuted`, hover `C.bgSelected`; раскрытый чек-лист — линия `C.divider` |
| глиф «простаивает» | кольцо и две паузы `C.textSecondary`, 14 px |
| попап (заглушка) | `C.bgWhite`, рамка `C.border`, `SHADOW.dropdown`; на 360 — шторка `SHADOW.sheet`, затемнение `C.overlay` |
| пометка нового | пунктир 1.5px `C.accentMuted` |
