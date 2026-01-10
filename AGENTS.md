# Инструкции для нейросетей

- Этот файл охватывает весь репозиторий. Любые изменения в проекте должны учитывать требования ниже.
- Все нейросети обязаны вести раздел «Прогресс», фиксируя каждую веху и важное действие, связанное с реализацией CacheFork.
- Комментарии и документация пишутся только на русском языке.

## Прогресс
- [2026-01-09] Прогресс обнулён; базовая линия переносится на проверенную сборку BepInEx 5.4.23.3 для дальнейшей работы над CacheFork.
- [2026-01-09] В репозиторий перенесён чистый BepInEx (core + базовый config, пустые plugins/patchers) из рабочей установки Valheim для дальнейшей работы.
- [2026-01-09] Добавлены файлы Doorstop (winhttp.dll, doorstop_config.ini), создан шаблон BepInEx/cache.cfg и каркас каталогов BepInEx/cache/* для старта реализации ТЗ.
- [2026-01-09] Импортированы исходники BepInEx v5.4.23.3 из официального репозитория в рабочее дерево для дальнейшей разработки CacheFork.
- [2026-01-09] Рантайм BepInEx перенесён в runtime/ для разведения с исходниками; исходная папка BepInEx восстановлена под кодовую базу.
- [2026-01-09] Добавлен проект BepInEx.Cache.Core с чтением cache.cfg и базовой логикой построения хеша окружения; проект подключён к решению.
- [2026-01-09] Обновлены README, CONTRIBUTING, CODE_OF_CONDUCT и LICENSE под цели BepInEx.CacheFork.
- [2026-01-09] Реализован манифест кеша и базовая валидация хеша окружения; CacheManager подключён к Chainloader для проверки и записи манифеста.
- [2026-01-09] В сборку добавлена публикация BepInEx.Cache.Core для упаковки в core-дистрибутив. Готовность: 10%.
- [2026-01-09] Добавлена ранняя проверка манифеста кеша в Preloader через reflection для будущей интеграции кеша патчей. Готовность: 15%.
- [2026-01-09] Перенесён дамп сборок в BepInEx/cache/assemblies, добавлено использование кеша патченных DLL в Preloader и запись кеша при включённом CacheFork. Готовность: 25%.
- [2026-01-10] Сборка MakeDist остановилась на загрузке Doorstop (ошибка SSL); скомпилированные DLL перенесены в runtime/BepInEx/core для проверки в Valheim. Готовность: 30%.
- [2026-01-10] Загрузка Doorstop проверена через отдельный запуск задачи DownloadDoorstop; архивы успешно скачаны в bin/doorstop. Готовность: 32%.
- [2026-01-10] Добавлено логирование cache hit/miss и очистка кеша при невалидном манифесте (ValidateStrict). Готовность: 35%.
- [2026-01-10] Реализован базовый кеш ассетов: скан AssetBundles в plugins, копирование в BepInEx/cache/assets и валидация по манифесту. Готовность: 45%.
- [2026-01-10] Выполнена сборка Build и обновлены DLL в runtime/BepInEx/core и в установке Valheim для проверки кеша ассетов. Готовность: 50%.
- [2026-01-10] Добавлено распознавание AssetBundle без расширения по сигнатурам UnityFS/UnityWeb, чтобы кеш ассетов покрывал больше модов. Готовность: 55%.
- [2026-01-10] Пересобраны DLL и обновлены runtime/BepInEx/core и Valheim/BepInEx/core после улучшения распознавания AssetBundle. Готовность: 58%.
- [2026-01-10] Добавлен базовый кеш локализации (Translation): копирование файлов, манифест и проверка готовности, участие в fingerprint. Готовность: 65%.
- [2026-01-10] Исправлена сборка net35 (Path.Combine), кеш локализации теперь активируется только при наличии XUnity.AutoTranslator; сборка Build проходит, обновлены DLL в runtime/BepInEx/core. Готовность: 68%.
- [2026-01-10] Кеш локализации переведён на постоянную работу, добавлено опциональное кеширование AutoTranslatorConfig.ini только при наличии XUnity.AutoTranslator; обновлены DLL в runtime и установке Valheim. Готовность: 70%.
- [2026-01-10] Исправлена причина ложной инвалидации кеша по версии Unity: манифест теперь хранит версию exe и принимает старый формат без сброса. Готовность: 72%.
- [2026-01-10] Добавлен runtime-кеш локализации: патч Localization.SetupLanguage, сохранение и загрузка словаря переводов из бинарного кеша; обновлены DLL в runtime и установке Valheim. Готовность: 78%.
- [2026-01-10] Исправлено повторное перезаписывание кешированных сборок при cache-hit; пересобраны и развёрнуты DLL. Готовность: 80%.
- [2026-01-10] Добавлено кеширование локализаций модов Jotunn на уровне AddFileByPath (дифф словаря, бинарный кеш по файлу). Готовность: 83%.
- [2026-01-10] Добавлен state-cache Jotunn локализаций (агрегированный кеш по модам с валидацией источников), патчи конструкторов CustomLocalization; обновлены DLL. Готовность: 86%.
- [2026-01-10] Исправлена отложенная инициализация Jotunn-кеша (патч после загрузки сборки Jotunn), устранено предупреждение HarmonyX. Готовность: 88%.
- [2026-01-10] Добавлен каркас state-cache для registries Jotunn: запись объектов при AddCustom* и попытка восстановления через сериализацию (с фоллбеком), подключены патчи и логирование. Готовность: 92%.
- [2026-01-10] Адаптирован state-cache под Jotunn 2.27.0: переход на AddItem/AddRecipe/AddStatusEffect/AddPiece/AddPieceTable, исправлен поиск AddTranslation и убраны ложные ошибки при патче перегрузок. Готовность: 94%.
- [2026-01-10] Исправлена совместимость с Jotunn 2.27.0 и модами: патчи registries переведены на универсальный __args, добавлено поле DumpedAssembliesPath для обратной совместимости плагинов. Готовность: 95%.
- [2026-01-10] Переподключены патчи registries строго по типам Custom* и добавлен Jotunn compatibility‑патч для GetSourceModMetadata, чтобы убрать падения ItemManager; подготовлены логи по ошибочным патчам. Готовность: 96%.
- [2026-01-10] Убраны Harmony‑патчи registries для Jotunn, кеш состояния переведён на события OnItemsRegistered/OnPiecesRegistered/OnPrefabsRegistered; добавлен prefix/finalizer для GetSourceModMetadata с фоллбеком по stacktrace. Готовность: 97%.
- [2026-01-10] GetSourceModMetadata теперь полностью переопределён (возвращает безопасный stub/метадату без вызова оригинала), чтобы исключить NRE в менеджерах Jotunn; пересобраны и развернуты DLL. Готовность: 98%.
- [2026-01-10] Усилен патч GetSourceModMetadata: добавлена проверка наличия патча, лог первого перехвата и финализатор на исключения для защиты от NRE. Готовность: 98%.
- [2026-01-10] Выполнена сборка Build и обновлён BepInEx.Cache.Core в Valheim/BepInEx/core для проверки Jotunn-патча. Готовность: 98%.
## Техническое задание (ТЗ) на разработку мод-инжектора "BepInEx.CacheFork" для Valheim

1. **Общая информация**

Название проекта: BepInEx.CacheFork (форк BepInEx с persistent caching для ускорения загрузки модов).
Версия ТЗ: 1.0 (от 06.01.2026).
Заказчик/Разработчик: Комьюнити Valheim (open-source на GitHub).
Базовый репозиторий: Форк https://github.com/BepInEx/BepInEx (stable 6.x или bleeding edge на момент форка).
Целевая платформа: Valheim (Unity 2021.3.x Mono, Windows/Linux/macOS). Расширяемо на другие Unity Mono игры.
Язык разработки: C# (.NET 6+ / Mono 2.0+).
Лицензия: MIT (как BepInEx).

2. **Цели проекта**

Основная цель: Ускорить startup/load модов в 5–10 раз (с 10 мин → 1–2 мин на 100+ модах) за счёт persistent кеша патчей, ассетов и init-state.
Дополнительные цели:
Полная обратная совместимость: Все моды BepInEx (plugins в BepInEx/plugins/, patchers в BepInEx/patchers/) работают без изменений.
Автоматическая валидация/обновление кеша при смене модов/игры.
Минимальные изменения в API (добавление config для cache control).

Проблемы, решаемые:
Runtime recompilation DLL (Harmony/MonoMod).
Preload AssetBundles/текстур (Jotunn, content-моды).
Localization loading.
Mod init (Awake/Start/OnEnable с регистрацией items/biomes).
GC spikes/UnloadUnusedAssets во время load.

3. **Функциональные требования**

№ Требование | Описание | Приоритет
--- | --- | ---
FR-1 | Форк BepInEx. Взять stable release, интегрировать все upstream изменения via git subtree/PR. | Высокий
FR-2 | Cache Manager. Новый модуль (BepInEx.Cache.Core): генерация hash (SHA256) набора модов + game exe + Unity version; persistent storage: BepInEx/cache/ (subdirs: assemblies/, assets/, localization/, state/); load cache если valid, fallback на full init. | Высокий
FR-3 | Assemblies Cache. Кешировать patched DLL (MonoMod/Harmony): preloader применяет все patchers → dump serialized Assembly (custom BinaryFormatter или Roslyn emit); load: dynamic load из кеша via Assembly.LoadFrom. | Высокий
FR-4 | Assets Preload Cache. Для AssetBundles (моды вроде EpicValheim, new items): авто-scan bundles в BepInEx/plugins/; preload → сериализовать Textures/Models в custom format (Unity Serializer + LZ4 compress); Unity Addressables integration (если Valheim supports). | Средний
FR-5 | Localization Cache. Интегрировать/улучшить MSchmoecker/LocalizationCache: кеш всех токенов в JSON/binary. | Низкий
FR-6 | Mod Init State Cache. Runtime: после ChainLoader.Init() → сериализовать registries (Jotunn.ItemManager, PieceManager и т.д.) в state.reg; restore: inject cached data в registries (reflection/hooks). | Высокий (сложно)
FR-7 | Config BepInEx/cache.cfg: EnableCache=true; CacheDir=auto; ValidateStrict=true (invalidate on any change); MaxCacheSize=16GB. | Высокий
FR-8 | Fallback & Cleanup: invalid cache → delete + full rebuild; manual: BepInEx.console "cache.clear". | Средний
FR-9 | Logging/Metrics: расширенные логи: "Cache hit: 85% speedup", timings per stage. | Низкий

4. **Нефункциональные требования**

№ Требование | Описание
--- | ---
NFR-1 | Performance: Startup <2 мин (100 модов, i7+SSD). Cache build <5 мин первый раз.
NFR-2 | Совместимость: 100% с BepInEx-модами (Jotunn, ValheimLib, CreatureLevel). Тест на 200+ популярных (Thunderstore top).
NFR-3 | Размер: Cache ≤20% от unpacked модов (compress).
NFR-4 | Платформы: Win x64, Linux x64 (Proton/Steam). macOS ARM опционально.
NFR-5 | Безопасность: Cache signed (RSA) по hash. No-exec на cache files.
NFR-6 | Upstream Sync: авто-PR merge из BepInEx monthly.

5. **Архитектура (High-Level)**

Форк структура:
```
BepInEx.CacheFork/
├── BepInEx.Core/          # Не трогать (ChainLoader, HarmonyX)
├── BepInEx.Preloader.Core/ # Добавить CachePreloader.dll
├── BepInEx.Cache.Core/     # Новый: CacheManager, AssemblySerializer, AssetDumper
├── docs/                   # Cache guide
└── pack/                   # Valheim pack (doorstop + cache-enabled)
```

Startup Sequence (модифицированная):

Preloader: Inject → Check cache hash → Load cached pre-patched assemblies (skip MonoMod).
ChainLoader: Load plugins → Если cache hit: Restore state (registries) → Skip Awake/OnEnable.
Post-init: Validate runtime (hooks) → Dump new cache.

Ключевые классы для модов:

- CacheManager: Singleton, TryLoadCache(), BuildAndDump().
- PatchedAssemblyCache: Harmony-aware serializer (patch graphs).
- ModStateSerializer: Reflection для Valheim-specific (ZNetScene, ObjectDB).

Интеграция с существующими: Embed StartupAccelerator patches.

6. **Этапы разработки (Roadmap)**

Этап | Задачи | Срок (чел.-мес.)
--- | --- | ---
1. Setup | Форк, build/test vanilla Valheim. | 0.5
2. Cache Basics | Hashing + configs. Assemblies cache (simple DLL copy). | 1.0
3. Assets & Loc | Bundle preload/dump. Integrate LocalizationCache. | 1.5
4. State Cache | Valheim registries serialize/restore. | 2.0
5. Polish | Config, logging, cleanup. | 0.5
6. Test/Release | 100+ модов тест (r2modman profiles). Thunderstore pack. | 1.0
Итого |  | 6.5

7. **Тестирование**

- Unit: NUnit для CacheManager (mock assemblies).
- Integration: Valheim test worlds (empty/full builds).
- Load Tests: 50/100/200 модов (Thunderstore profiles). Metrics: timings, RAM, CPU.
- Compatibility: Smoke-test top 100 модов (CreatureLevel, EpicLoot, etc.).
- CI/CD: GitHub Actions (Win/Linux), auto-publish packs.

8. **Риски & Зависимости**

Риск | Вероятность | Митигация
--- | --- | ---
Harmony state несериализуем | Высокая | Fallback: Partial cache + optimize init order.
Unity updates ломают | Средняя | Version pinning + auto-invalidate.
Upstream conflicts | Низкая | Modular Cache.Core.

9. **Доставка**

- GitHub repo: username/BepInEx.CacheFork.
- Releases: BepInEx.CacheFork.Valheim.v1.0.zip (doorstop + pack).
- Docs: README + https://docs.cachefork.dev.
- Комьюнити: r/ModdedValheim, Thunderstore.io (как mod/pack).

Одобрение ТЗ: [ ] Разработчик
