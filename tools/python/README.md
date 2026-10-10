# Python-прототип

Исходная реализация конвертера на Python.

| Скрипт | Что делает |
|---|---|
| `kospet_maps.py` | формат часов (YZL, `.mlp`, `.idx`, `.db`), шейп-файл → слой, SRTM → квадрат рельефа (`make_contour_tile`, нужен `contourpy`) |
| `osm2kospet.py` | `.osm.pbf` → `roads` / `water` / `landuse` по схеме Geofabrik: `python3 osm2kospet.py kaliningrad-latest.osm.pbf out/kaliningrad` |
| `idxparse.py` | разбор и проверка `.idx`: `python3 idxparse.py roads.idx` |

Нужны Python 3.10+ и numpy.
