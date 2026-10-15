# Пользовательская конфигурация — agent-critical контракт

Подробное описание текущей схемы и загрузки находится в
`../../docs/reference/application-configuration.md`. Точные значения принадлежат production-коду.

## Основные инварианты

- Пользовательская конфигурация — один строгий типизированный snapshot, загружаемый один раз при startup.
  Канонический источник — YAML; legacy JSON нормализуется в памяти. Runtime использует
  `LoadRuntime`/`LoadFromSources`: одновременные источники запрещены, значения не смешиваются.
- Runtime-путь вычисляет `AzurPilotConfigurationPath`; абсолютный путь конкретной машины не хранится
  в репозитории.
- Отсутствующий файл/каталог означает успешное использование встроенных defaults.
- Существующий, но невалидный/нечитаемый файл не подменяется defaults: загрузка fail-closed.
- Hot reload, file watcher и auto-repair не поддерживаются. Явное сохранение через
  `AzurPilotConfigurationStore` валидирует YAML, оставляет backup и атомарно заменяет файл;
  уже внедрённый DI snapshot не меняется. Изменённый извне источник не перезаписывается устаревшим snapshot.
- Configuration providers (`appsettings`, env vars, user secrets и т. п.) не образуют второй слой
  пользовательской конфигурации.

## Schema ownership

- Номер текущей схемы принадлежит `AzurPilotConfiguration.CurrentSchemaVersion`.
- Defaults принадлежат `AzurPilotConfigurationDefaults`.
- JSON contract/converters принадлежат `AzurPilotConfigurationJson` и типам значений.
- YAML reader/writer принадлежат `AzurPilotYamlConfigurationReader` и `AzurPilotConfigurationYamlWriter`; wire-name UI mode — `OperatorUiModeNames`.
- Значения `mumu.instance` и `diagnostics.minimumLevel` проверяют `MuMuInstanceValue` и `LogLevelNames`.
- Не копируй допустимые значения/грамматику в новый prose-owner, если они выражены типом/конвертером.

Каноническая схема принадлежит корневому типу; legacy JSON v1/v2 нормализуется только в памяти.
Нормализация не ослабляет строгую проверку исходного документа и не переписывает файл.

## Граница текущей конфигурации

Секция `mumu` содержит выбор instance. Android/ADB endpoint и product identity Azur Lane Global/EN —
runtime facts, а не пользовательские настройки; ключи `adb.*`/`game.*` ради текущей capability не
добавляются.

Screenshot diagnostics не вводит пользовательских настроек. Не добавляй секции для input/OCR или
будущих продуктовых возможностей до появления реального пользовательского решения, которое должно
конфигурироваться.

## Failures и diagnostics

Configuration failures возвращаются как application failure values; их коды/details принадлежат
`application-failures.md`. Diagnostics сообщает bounded метаданные snapshot, а не полный JSON.
