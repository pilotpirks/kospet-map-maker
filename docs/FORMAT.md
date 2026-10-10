# Формат карт Kospet

Спецификация восстановлена по стоковым картам часов Kospet T4  (файлы от 2024 г.). Все числа — **little-endian**, если
не сказано иное.

## 1. Структура на карте памяти

Одна карта = две папки:

```
<name>/                  векторная карта региона
    roads.mlp  roads.idx
    water.mlp  water.idx
    landuse.mlp landuse.idx landuse.db
<name>-cm/               рельеф (contour map), квадраты 1°×1°
    N55E037/  N55E037.mlp  N55E037.idx  N55E037.db
    N55E038/  ...
```

| Расширение | Аналог в ESRI Shapefile | Содержимое |
|---|---|---|
| `.mlp` | `.shp` | геометрия |
| `.idx` | пространственный индекс (`.qix`-подобный) | R-дерево + класс объекта |
| `.db`  | `.dbf` | атрибуты (dBase III) |

Набор слоёв фиксированный. Тип геометрии файлом не хранится и задаётся именем слоя:
`roads` — линии, `water` и `landuse` — полигоны, рельеф — линии. У `roads` и `water` нет `.db`:
всё, что нужно для отрисовки (класс), лежит в `.idx`.

Имя квадрата рельефа — юго-западный угол в формате SRTM: `N|S` + 2 цифры широты, `E|W` +
3 цифры долготы. `N55E037` — от 55° до 56° с. ш., от 37° до 38° в. д. (Москва). В папку попадают только
квадраты, где есть изолинии.

## 2. Контейнер YZL

Каждый из файлов `.mlp`, `.idx`, `.db` обёрнут в 32-байтный заголовок:

| Смещение | Размер | Значение |
|---|---|---|
| 0  | 4  | сигнатура `59 5A 4C 00` (`"YZL\0"`) |
| 4  | 4  | `u32` длина тела (= размер файла − 32) |
| 8  | 4  | `u32` версия, всегда `2` |
| 12 | 4  | `u32` `0` |
| 16 | 16 | **MD5 тела** (байты 32…конец) |
| 32 | …  | тело |

Тело не шифруется и не сжимается. MD5 часы, вероятно, проверяют — при любом изменении тела его
нужно пересчитать.

## 3. `.mlp` — геометрия

Тело — подряд идущие записи, без общего заголовка (в отличие от `.shp`).

```
запись:
  u32 BE  recno          номер записи, с 1; у стоковых изолиний бывают пропуски
  u32 LE  contentLen     длина содержимого в байтах
  содержимое:
    i32 xmin, ymin, xmax, ymax      bbox, градусы × 10⁶
    u32 nParts
    u32 nPoints
    i32 parts[nParts]               индекс первой точки каждой части
    i32 xy[nPoints*2]               lon, lat, lon, lat …  (градусы × 10⁶)
```

- Обратите внимание: `recno` — **big-endian**, длина — little-endian.
- Координаты — WGS 84, целые микроградусы (~11 см на экваторе). Округление — к ближайшему.
- `contentLen = 24 + 4·nParts + 8·nPoints`.
- Полигоны — как в Shapefile: внешние кольца по часовой стрелке, дыры — против, кольца замкнуты
  (последняя точка = первой), дыры идут сразу после своего внешнего кольца.
- Код типа фигуры (как в `.shp`) отсутствует.

## 4. `.idx` — пространственный индекс

Упакованное R-дерево с узлами по 7…15 потомков. Помимо поиска по области, именно здесь хранится
**класс объекта** (`code`), по которому часы выбирают стиль.

### Заголовок тела (16 байт)

| Смещение | Тип | Значение |
|---|---|---|
| 0  | 4 байта | `53 51 54 01` (`"SQT\x01"`) |
| 4  | i32 | `1` (версия) |
| 8  | i32 | число уровней дерева (листья — уровень 0) |
| 12 | i32 | число узлов верхнего уровня (единого корня нет) |

Дальше подряд идут узлы верхнего уровня, каждый вместе со всем своим поддеревом.

### Узел

```
i32  size        длина узла в байтах, считая от поля level до конца поддерева
f32  xmin, ymin, xmax, ymax     bbox узла (объединение потомков), float32 градусы
i32  level       0 = лист
i32  n           число потомков (у листа — число записей)
...  n дочерних узлов  или  n записей
```

`size = 8 + длина всех потомков` (то есть узел занимает `20 + size` байт).

### Запись листа (28 байт)

| Смещение | Тип | Значение |
|---|---|---|
| 0  | f32 ×4 | bbox объекта |
| 16 | i32 | `code` — класс объекта (см. §6) |
| 20 | i32 | `offset` — позиция **содержимого** записи в теле `.mlp` (= позиция её заголовка + 8) |
| 24 | i32 | `recno` — номер записи в `.mlp`; он же номер строки в `.db` (строка `recno − 1`) |

### Наблюдения по стоковым файлам

| Файл | Объектов | Уровней | Верхних узлов | Потомков в узле |
|---|---|---|---|---|
| `roads` | 28 801 | 4 | 2 | 7–15, в среднем 10 |
| `water` | 3 191 | 3 | 3 | 7–15 |
| `landuse` | 51 596 | 4 | 4 | 7–15 |
| квадрат рельефа | 9 764 | 3 | 12 | 7–15 |

Точный алгоритм построения дерева у оригинала неизвестен (bbox соседних листьев перекрываются,
регулярной сетки нет). Kospet Map Maker строит дерево методом STR (Sort-Tile-Recursive)
с ~10 потомками на узел и ограничением 7…15.

## 5. `.db` — атрибуты

Обычный dBase III (как `.dbf` из GDAL/shapelib: дата в заголовке `95-07-26`), строки в UTF-8.
Номер строки = `recno − 1`.

**`landuse.db`** — схема Geofabrik:

| Поле | Тип | Длина |
|---|---|---|
| `osm_id` | C | 12 |
| `code` | N | 4 |
| `fclass` | C | 28 |
| `name` | C | 100 |

**`<квадрат>.db`** (рельеф):

| Поле | Тип | Длина |
|---|---|---|
| `ID` | N | 8 |
| `elevation` | N | 12.3 |

В стоковом рельефе `.db` содержит все исходные изолинии (шаг 25 м), а в `.mlp`/`.idx` оставлены
только кратные 100 м — отсюда пропуски в `recno`. Kospet Map Maker пишет только реально
используемые строки и нумерует записи подряд.

## 6. Коды классов

Используется схема [Geofabrik «OSM data in GIS formats (free)»](https://download.geofabrik.de/osm-data-in-gis-formats-free.pdf).

### Дороги (`roads.idx`)

| Код | fclass | OSM |
|---|---|---|
| 5111 | motorway | `highway=motorway` |
| 5112 | trunk | `highway=trunk` |
| 5113 | primary | `highway=primary` |
| 5114 | secondary | `highway=secondary` |
| 5115 | tertiary | `highway=tertiary` |
| 5121 | unclassified | `highway=unclassified` |
| 5122 | residential | `highway=residential` |
| 5123 | living_street | `highway=living_street` |
| 5124 | pedestrian | `highway=pedestrian` |
| 5131–5135 | *_link | съезды motorway…tertiary |
| 5141 | service | `highway=service` |
| 5142 | track | `highway=track` без `tracktype` |
| 5143–5147 | track_grade1…5 | `highway=track` + `tracktype=gradeN` |
| 5151 | bridleway | `highway=bridleway` |
| 5152 | cycleway | `highway=cycleway` |
| 5153 | footway | `highway=footway` |
| 5154 | path | `highway=path` |
| 5155 | steps | `highway=steps` |
| 5199 | unknown | `highway=road` |

### Вода (`water.idx`)

| Код | fclass | OSM |
|---|---|---|
| 8200 | water | `natural=water` |
| 8201 | reservoir | `landuse=reservoir`, `water=reservoir` |
| 8202 | river / riverbank | `water=river`, `waterway=riverbank` |
| 8203 | dock | `waterway=dock` |
| 8211 | glacier | `natural=glacier` |
| 8221 | wetland | `natural=wetland` |

### Землепользование (`landuse.idx` / `landuse.db`)

| Код | fclass | OSM |
|---|---|---|
| 7201 | forest | `landuse=forest`, `natural=wood` |
| 7202 | park | `leisure=park`, `leisure=common` |
| 7203 | residential | `landuse=residential` |
| 7204 | industrial | `landuse=industrial` |
| 7206 | cemetery | `landuse=cemetery` |
| 7207 | allotments | `landuse=allotments` |
| 7208 | meadow | `landuse=meadow` |
| 7209 | commercial | `landuse=commercial` |
| 7210 | nature_reserve | `leisure=nature_reserve` |
| 7211 | recreation_ground | `landuse=`/`leisure=recreation_ground` |
| 7212 | retail | `landuse=retail` |
| 7213 | military | `landuse=military` |
| 7214 | quarry | `landuse=quarry` |
| 7215 | orchard | `landuse=orchard` |
| 7216 | vineyard | `landuse=vineyard` |
| 7217 | scrub | `natural=scrub` |
| 7218 | grass | `landuse=grass` |
| 7219 | heath | `natural=heath` |
| 7228 | farmyard | `landuse=farmyard` |
| 7229 | farmland | `landuse=farmland` |

### Рельеф (`<квадрат>.idx`)

`code` = высота изолинии в метрах (100, 200, …).

### Есть ли у часов другие стили

Неизвестно. В стоковых картах встречаются только перечисленные коды. Чтобы выяснить, рисуют ли
часы что-то ещё, можно собрать тестовую карту с сеткой квадратов разных кодов.

## 7. Проверка

Минимальная проверка корректности файла:

1. Сигнатура `YZL\0`, длина тела = размер − 32, MD5 тела совпадает с байтами 16…31.
2. `.idx` разбирается рекурсивно, и для каждой записи `size` узлов сходятся до байта.
3. Для каждой записи `.idx`: `u32 BE` по адресу `offset − 8` в теле `.mlp` равен `recno`.
4. bbox записи `.idx` совпадает с bbox записи `.mlp` (с точностью float32).
