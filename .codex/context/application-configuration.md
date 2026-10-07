# Пользовательская конфигурация — agent-critical контракт

Подробное описание текущей схемы и загрузки находится в
`../../docs/reference/application-configuration.md`. Точные значения принадлежат production-коду.

## Основные инварианты

- Пользовательская конфигурация — один строгий JSON snapshot, загружаемый один раз при startup.
- Runtime-путь вычисляет `AzurPilotConfigurationPath`; абсолютный путь конкретной машины не хранится
  в репозитории.
- Отсутствующий файл/каталог означает успешное использование встроенных defaults.
- Существующий, но невалидный/нечитаемый файл не подменяется defaults: загрузка fail-closed.
- Hot reload, file watcher, auto-repair и запись исправленной конфигурации не поддерживаются.
- Configuration providers (`appsettings`, env vars, user secrets и т. п.) не образуют второй слой
  пользовательской конфигурации.

## Schema ownership

- Номер текущей схемы принадлежит `AzurPilotConfiguration.CurrentSchemaVersion`.
- Defaults принадлежат `AzurPilotConfigurationDefaults`.
- JSON contract/converters принадлежат `AzurPilotConfigurationJson` и типам значений.
- Не копируй допустимые значения/грамматику в новый prose-owner, если они выражены типом/конвертером.

Поддерживается strict schema v2 и legacy input v1, который нормализуется только в памяти.
Нормализация не ослабляет строгую проверку исходного документа и не переписывает файл.

## Граница текущей конфигурации

Секция `mumu` содержит выбор instance. Android/ADB endpoint и product identity Azur Lane Global/EN —
runtime facts, а не пользовательские настройки; ключи `adb.*`/`game.*` ради текущей capability не
добавляются.

Не добавляй секции будущих input/vision/OCR/UI capability до появления реального пользовательского
решения, которое должно конфигурироваться.

## Failures и diagnostics

Configuration failures возвращаются как application failure values; их коды/details принадлежат
`application-failures.md`. Diagnostics сообщает bounded метаданные snapshot, а не полный JSON.
