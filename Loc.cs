namespace KospetMapMaker;

/// <summary>Tiny two-language string table.</summary>
public static class Loc
{
    public static bool En;

    private static readonly Dictionary<string, (string ru, string en)> S = new()
    {
        ["title"] = ("Kospet Map Maker — карты для часов", "Kospet Map Maker — watch maps"),
        ["lang"] = ("Язык", "Language"),
        ["source"] = ("Источник данных", "Data source"),
        ["srcRegion"] = ("Регион Geofabrik (скачать)", "Geofabrik region (download)"),
        ["srcFile"] = ("Свой файл .osm.pbf", "Local .osm.pbf file"),
        ["search"] = ("Поиск страны или региона…", "Search country or region…"),
        ["reload"] = ("Обновить список", "Reload list"),
        ["browse"] = ("Обзор…", "Browse…"),
        ["refresh"] = ("Скачать заново (игнорировать кэш)", "Re-download (ignore cache)"),
        ["layers"] = ("Слои", "Layers"),
        ["roads"] = ("Дороги", "Roads"),
        ["paths"] = ("тропы, тротуары, велодорожки, лестницы", "paths, footways, cycleways, steps"),
        ["service"] = ("проезды, дворы, парковки", "service roads, driveways, parking"),
        ["tracks"] = ("грунтовки (track)", "tracks"),
        ["water"] = ("Вода (озёра, водохранилища, болота, ледники)", "Water (lakes, reservoirs, wetlands, glaciers)"),
        ["rivers"] = ("реки и ручьи (линии → узкие полигоны)", "rivers and streams (lines → thin polygons)"),
        ["landuse"] = ("Землепользование (леса, парки, поля, кварталы)", "Land use (forests, parks, fields, districts)"),
        ["extra"] = ("доп. классы: луга, тундра, скалы, пляжи, плантации", "extra classes: grassland, rock, beach, plantations"),
        ["contours"] = ("Рельеф (изолинии SRTM)", "Relief (SRTM contour lines)"),
        ["step"] = ("шаг, м", "step, m"),
        ["output"] = ("Куда сохранить", "Output"),
        ["outDir"] = ("Папка", "Folder"),
        ["name"] = ("Имя карты (папка на часах)", "Map name (folder on the watch)"),
        ["build"] = ("Собрать карту", "Build map"),
        ["cancel"] = ("Отмена", "Cancel"),
        ["openFolder"] = ("Открыть папку", "Open folder"),
        ["ready"] = ("Готово к работе", "Ready"),
        ["loadingIndex"] = ("Загружаю список регионов…", "Loading region list…"),
        ["indexLoaded"] = ("Регионов в списке: {0}", "Regions available: {0}"),
        ["indexError"] = ("Не удалось загрузить список регионов: {0}", "Could not load region list: {0}"),
        ["noRegion"] = ("Выберите регион или файл .pbf", "Choose a region or a .pbf file"),
        ["noOut"] = ("Укажите папку для сохранения", "Choose an output folder"),
        ["done"] = ("Готово: {0}", "Done: {0}"),
        ["cancelled"] = ("Отменено", "Cancelled"),
        ["error"] = ("Ошибка: {0}", "Error: {0}"),
        ["preview"] = ("Превью (колесо — масштаб, перетаскивание — сдвиг)", "Preview (wheel = zoom, drag = pan)"),
        ["log"] = ("Журнал", "Log"),
        ["st_download"] = ("Скачивание OSM", "Downloading OSM"),
        ["st_osm"] = ("Обработка OSM", "Processing OSM"),
        ["st_write"] = ("Запись файлов", "Writing files"),
        ["st_contours"] = ("Рельеф", "Contours"),
        ["st_done"] = ("Готово", "Done"),
        ["hint"] = ("Скопируйте обе папки (карта и «-cm») на часы так же, как стоковые карты.",
                    "Copy both folders (map and “-cm”) to the watch the same way as stock maps."),
    };

    public static string T(string key) => S.TryGetValue(key, out var v) ? (En ? v.en : v.ru) : key;
    public static string T(string key, object arg) => string.Format(T(key), arg);
}
