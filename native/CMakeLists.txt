# =============================================================================
# native/CMakeLists.txt — CMake boundary native части AzurPilotRu.
#
# Ответственность этой boundary: собрать shared library AzurPilot.Native (Windows
# x64, C++20), реализующую замороженный C ABI из include/azurpilot_native_abi.h, и
# native smoke test, который исполняет этот ABI под управлением CTest.
#
# Правила boundary:
#   - CMake строго target-based: глобальных include_directories/link_libraries/
#     add_definitions и глобальных CMAKE_CXX_FLAGS нет — всё живёт на targets.
#   - OpenCV приходит извне через cache variable OpenCV_DIR; владелец значений —
#     eng/versions.json, значение передаёт eng/build.ps1. Если внешний источник не
#     задан, каталог закреплённого пакета вычисляется из eng/versions.json
#     (artifacts/opencv/<version>/<cmakeDir>). Абсолютных machine-specific путей и
#     хардкодов путей в этом файле нет.
#   - Все build outputs уходят в одну ignored boundary artifacts/; source tree не
#     засоряется.
#   - Комментарии и диагностика — по-русски (.codex/context/language.md);
#     идентификаторы, имена targets и ключи — латиницей.
# =============================================================================

# Минимум CMake. Нормативный владелец значения — eng/versions.json
# (toolchain.cmakeMinimumVersion). CMake требует литерал именно в этой позиции,
# поэтому значение продублировано здесь и сразу ниже сверяется с источником:
# расхождение валит configure, а не остаётся тихим дрейфом версии.
cmake_minimum_required(VERSION 4.4.2)

project(AzurPilot.Native LANGUAGES CXX)

# -----------------------------------------------------------------------------
# Закреплённые значения: eng/versions.json — единственный источник версий
# -----------------------------------------------------------------------------

set(AZURPILOT_VERSIONS_FILE "${CMAKE_CURRENT_SOURCE_DIR}/../eng/versions.json")
if(NOT EXISTS "${AZURPILOT_VERSIONS_FILE}")
  message(FATAL_ERROR
    "Не найден eng/versions.json — единственный источник закреплённых версий.\n"
    "Ожидался файл: ${AZURPILOT_VERSIONS_FILE}\n"
    "native boundary — часть репозитория AzurPilotRu; конфигурировать её вне репозитория нельзя.")
endif()
file(READ "${AZURPILOT_VERSIONS_FILE}" AZURPILOT_VERSIONS_JSON)

string(JSON AZURPILOT_CMAKE_MINIMUM_VERSION GET "${AZURPILOT_VERSIONS_JSON}" toolchain cmakeMinimumVersion)
string(JSON AZURPILOT_CMAKE_GENERATOR GET "${AZURPILOT_VERSIONS_JSON}" toolchain cmakeGenerator)
string(JSON AZURPILOT_CMAKE_GENERATOR_ALTERNATIVE GET "${AZURPILOT_VERSIONS_JSON}" toolchain cmakeGeneratorAlternative)
string(JSON AZURPILOT_OPENCV_VERSION GET "${AZURPILOT_VERSIONS_JSON}" opencv version)
string(JSON AZURPILOT_OPENCV_CMAKE_DIR GET "${AZURPILOT_VERSIONS_JSON}" opencv cmakeDir)
string(JSON AZURPILOT_ABI_VERSION GET "${AZURPILOT_VERSIONS_JSON}" nativeAbi version)

# Числовые части закреплённой версии OpenCV: ожидания теста берутся из пина, а не
# из найденного пакета, чтобы проверялось именно закреплённое значение.
string(REGEX REPLACE "^([0-9]+)\\..*$" "\\1" AZURPILOT_OPENCV_VERSION_MAJOR "${AZURPILOT_OPENCV_VERSION}")
string(REGEX REPLACE "^[0-9]+\\.([0-9]+).*$" "\\1" AZURPILOT_OPENCV_VERSION_MINOR "${AZURPILOT_OPENCV_VERSION}")
string(REGEX REPLACE "^[0-9]+\\.[0-9]+\\.([0-9]+).*$" "\\1" AZURPILOT_OPENCV_VERSION_PATCH "${AZURPILOT_OPENCV_VERSION}")
if(NOT AZURPILOT_OPENCV_VERSION_MAJOR MATCHES "^[0-9]+$" OR
   NOT AZURPILOT_OPENCV_VERSION_MINOR MATCHES "^[0-9]+$" OR
   NOT AZURPILOT_OPENCV_VERSION_PATCH MATCHES "^[0-9]+$")
  message(FATAL_ERROR
    "Не удалось разобрать версию OpenCV из eng/versions.json: opencv.version='${AZURPILOT_OPENCV_VERSION}'.\n"
    "Ожидается формат major.minor.patch.")
endif()

# -----------------------------------------------------------------------------
# Проверки окружения. Молчаливый fallback запрещён: несоответствие — ошибка
# configure с русской диагностикой, а не тихая сборка другим toolchain.
# -----------------------------------------------------------------------------

if(NOT CMAKE_MINIMUM_REQUIRED_VERSION VERSION_EQUAL AZURPILOT_CMAKE_MINIMUM_VERSION)
  message(FATAL_ERROR
    "Литерал cmake_minimum_required (${CMAKE_MINIMUM_REQUIRED_VERSION}) разошёлся с eng/versions.json "
    "(toolchain.cmakeMinimumVersion=${AZURPILOT_CMAKE_MINIMUM_VERSION}).\n"
    "Владелец значения — eng/versions.json; исправьте литерал в native/CMakeLists.txt.")
endif()

if(CMAKE_VERSION VERSION_LESS AZURPILOT_CMAKE_MINIMUM_VERSION)
  message(FATAL_ERROR
    "CMake ${CMAKE_VERSION} старше требуемого минимума ${AZURPILOT_CMAKE_MINIMUM_VERSION} "
    "(eng/versions.json: toolchain.cmakeMinimumVersion).\n"
    "CMake, поставляемый внутри Visual Studio, требованию не удовлетворяет — нужен standalone CMake. "
    "Канонический путь сборки — pwsh ./eng/build.ps1 -Configuration Release.")
endif()
message(STATUS "CMake ${CMAKE_VERSION} удовлетворяет минимуму ${AZURPILOT_CMAKE_MINIMUM_VERSION} (eng/versions.json)")

if(CMAKE_GENERATOR STREQUAL AZURPILOT_CMAKE_GENERATOR)
  message(STATUS "CMake generator: ${CMAKE_GENERATOR} (канонический, eng/versions.json)")
elseif(CMAKE_GENERATOR STREQUAL AZURPILOT_CMAKE_GENERATOR_ALTERNATIVE)
  message(STATUS "CMake generator: ${CMAKE_GENERATOR} (альтернативный путь; требуется активированный toolchain environment Visual Studio)")
else()
  message(STATUS "CMake generator: ${CMAKE_GENERATOR} — не канонический (${AZURPILOT_CMAKE_GENERATOR}) и не альтернативный (${AZURPILOT_CMAKE_GENERATOR_ALTERNATIVE})")
endif()

# Проект поддерживает только Windows x64 (см. .codex/context/architecture.md).
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR
    "native boundary собирается только для x64, а configure выполнен для 32-битной платформы "
    "(CMAKE_SIZEOF_VOID_P=${CMAKE_SIZEOF_VOID_P}).\n"
    "Для канонического generator задайте архитектуру: -A x64.")
endif()

# -----------------------------------------------------------------------------
# Каталоги вывода: одна ignored boundary artifacts/, source tree не засоряется
# -----------------------------------------------------------------------------

set(AZURPILOT_NATIVE_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/bin"
    CACHE PATH "Каталог runtime-выходов native targets (ignored boundary artifacts/).")

# -----------------------------------------------------------------------------
# OpenCV: строго target-based, без абсолютных путей и хардкодов в этом файле
# -----------------------------------------------------------------------------

if(NOT DEFINED OpenCV_DIR)
  # Внешний источник (eng/build.ps1) не задал OpenCV_DIR: подставляем каноническое
  # место распаковки закреплённого пакета — artifacts/opencv/<version>/<cmakeDir>.
  # Это не machine-specific путь и не хардкод: путь собран из владельца значений
  # eng/versions.json и работает из чистого checkout после получения зависимости.
  # Переменная не пишется в кэш: значение пересчитывается на каждом configure, чтобы
  # перемещение или удаление artifacts/ не оставляло устаревший путь в кэше.
  set(__azurpilot_opencv_dir
      "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/opencv/${AZURPILOT_OPENCV_VERSION}/${AZURPILOT_OPENCV_CMAKE_DIR}")
  if(EXISTS "${__azurpilot_opencv_dir}/OpenCVConfig.cmake")
    set(OpenCV_DIR "${__azurpilot_opencv_dir}")
  endif()
  unset(__azurpilot_opencv_dir)
endif()

if(NOT OpenCV_DIR OR NOT EXISTS "${OpenCV_DIR}/OpenCVConfig.cmake")
  message(FATAL_ERROR
    "Не найден OpenCVConfig.cmake — конфигурация закреплённого OpenCV не получена.\n"
    "OpenCV_DIR: '${OpenCV_DIR}'\n"
    "Ожидаемый каталог: artifacts/opencv/<version>/<cmakeDir> относительно корня репозитория "
    "(значения opencv.version и opencv.cmakeDir — в eng/versions.json).\n"
    "Как исправить: получить закреплённый пакет через канонический путь "
    "pwsh ./eng/build.ps1 -Configuration Release или передать OpenCV_DIR явно через cache variable.")
endif()

# Требуемая версия — линия закреплённого релиза OpenCV из eng/versions.json
# (для текущего пина это ровно find_package(OpenCV 5.0 REQUIRED CONFIG)).
find_package(OpenCV 5.0 REQUIRED CONFIG)

# Закреплён именно один релиз: найденная версия обязана совпасть с пином, иначе
# сборка доказывала бы работу с другой линией OpenCV, чем заявлено.
if(NOT OpenCV_VERSION VERSION_EQUAL AZURPILOT_OPENCV_VERSION)
  message(FATAL_ERROR
    "Найден OpenCV ${OpenCV_VERSION}, а eng/versions.json закрепляет ${AZURPILOT_OPENCV_VERSION}.\n"
    "OpenCV_DIR: ${OpenCV_DIR}\n"
    "Тихое использование другой версии запрещено: обновите пин в eng/versions.json "
    "или укажите OpenCV_DIR на каталог закреплённого релиза.")
endif()
message(STATUS "OpenCV ${OpenCV_VERSION} найден через CONFIG-пакет: ${OpenCV_DIR}")

# OpenCVConfig.cmake предоставляет target world-библиотеки. Берём его имя, а не
# имя файла: имена артефактов внутри пакета — не забота этого файла.
if(TARGET OpenCV::opencv_world)
  set(AZURPILOT_OPENCV_WORLD_TARGET OpenCV::opencv_world)
elseif(TARGET opencv_world)
  set(AZURPILOT_OPENCV_WORLD_TARGET opencv_world)
else()
  message(FATAL_ERROR
    "OpenCVConfig.cmake не предоставил target world-библиотеки OpenCV (ожидался OpenCV::opencv_world или opencv_world).\n"
    "OpenCV_DIR: ${OpenCV_DIR}")
endif()
message(STATUS "OpenCV target для линковки: ${AZURPILOT_OPENCV_WORLD_TARGET}")

# -----------------------------------------------------------------------------
# Общие compile-настройки: применяются на targets, глобальных CMAKE_CXX_FLAGS нет
# -----------------------------------------------------------------------------

function(azurpilot_native_apply_compile_options target)
  if(MSVC)
    target_compile_options(${target} PRIVATE
      /W4          # все значимые предупреждения
      /WX          # предупреждение project-owned кода = ошибка сборки
      /permissive- # строгий режим соответствия стандарту
      /utf-8       # исходники и литералы читаются как UTF-8 (русские тексты)
      /EHsc        # C++-исключения внутри native кода; через C ABI они не проходят
    )
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Werror)
  endif()
endfunction()

# -----------------------------------------------------------------------------
# Target 1: shared library AzurPilot.Native — реализация замороженного C ABI
# -----------------------------------------------------------------------------

add_library(AzurPilot.Native SHARED
  src/azurpilot_native.cpp
  src/opencv_probe.cpp
)

# Публичный include — только каталог с замороженным заголовком ABI: это и есть
# единственная разрешённая поверхность для потребителя.
target_include_directories(AzurPilot.Native
  PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

target_compile_features(AzurPilot.Native PRIVATE cxx_std_20)
target_link_libraries(AzurPilot.Native PRIVATE "${AZURPILOT_OPENCV_WORLD_TARGET}")
azurpilot_native_apply_compile_options(AzurPilot.Native)

set_target_properties(AzurPilot.Native PROPERTIES
  OUTPUT_NAME "AzurPilot.Native"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>"
)

# -----------------------------------------------------------------------------
# Target 2: native smoke test — исполняет C ABI и проверяет реальную работу OpenCV
# -----------------------------------------------------------------------------

add_executable(AzurPilot.Native.SmokeTest
  tests/native_smoke_test.cpp
)

# Заголовок ABI разворачивается в dllimport для потребителя import library
# (см. инструкцию в native/include/azurpilot_native_abi.h).
# Ожидаемые значения передаются из eng/versions.json: тест не хранит копию пина.
target_compile_definitions(AzurPilot.Native.SmokeTest PRIVATE
  AZURPILOT_NATIVE_IMPORT
  "AZURPILOT_NATIVE_EXPECTED_ABI_VERSION=${AZURPILOT_ABI_VERSION}"
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_MAJOR=${AZURPILOT_OPENCV_VERSION_MAJOR}"
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_MINOR=${AZURPILOT_OPENCV_VERSION_MINOR}"
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_PATCH=${AZURPILOT_OPENCV_VERSION_PATCH}"
)

# Единственная связь теста с native кодом — target библиотеки; OpenCV тест напрямую
# не линкует и его заголовки не включает.
target_link_libraries(AzurPilot.Native.SmokeTest PRIVATE AzurPilot.Native)
target_compile_features(AzurPilot.Native.SmokeTest PRIVATE cxx_std_20)
azurpilot_native_apply_compile_options(AzurPilot.Native.SmokeTest)

set_target_properties(AzurPilot.Native.SmokeTest PROPERTIES
  OUTPUT_NAME "AzurPilot.Native.SmokeTest"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>"
)

# Runtime DLL OpenCV кладётся рядом с тестом, чтобы CTest запускал его без ручной
# правки PATH. Источник DLL — target OpenCV, а не путь, прописанный в этом файле.
add_custom_command(TARGET AzurPilot.Native.SmokeTest POST_BUILD
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different
          "$<TARGET_FILE:${AZURPILOT_OPENCV_WORLD_TARGET}>"
          "$<TARGET_FILE_DIR:AzurPilot.Native.SmokeTest>"
  COMMENT "Staging runtime DLL OpenCV рядом с native smoke test"
  VERBATIM
)

# -----------------------------------------------------------------------------
# CTest: project-owned native тест реально исполняет project-owned native код
# -----------------------------------------------------------------------------

enable_testing()

add_test(NAME native_abi_smoke COMMAND AzurPilot.Native.SmokeTest)

set_tests_properties(native_abi_smoke PROPERTIES
  TIMEOUT 300
  # Дополнительная страховка к exit code: проваленная проверка печатает метку FAIL.
  FAIL_REGULAR_EXPRESSION "\\[FAIL\\]"
)
