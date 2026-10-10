/* =============================================================================
 * azurpilot_native_abi.h — замороженный C ABI между managed (C#) и native (C++) частями AzurPilot.
 *
 * НАЗНАЧЕНИЕ
 *   Единственный владелец формы C ABI версии 2. Managed сторона и native реализация обязаны
 *   совпадать побайтово: любое расхождение — ошибка совместимости, а не повод продолжать работу.
 *   Изменение формы (поля, порядок, типы, коды возврата, смысл битов) требует нового номера ABI
 *   и согласованного обновления обеих сторон; обратная совместимость формой не гарантируется.
 *
 * АРТЕФАКТ
 *   Native часть собирается в shared library `AzurPilot.Native.dll` (Windows, x64). Managed сторона
 *   загружает её по имени `AzurPilot.Native` через source-generated LibraryImport. Имя артефакта
 *   заморожено этим контрактом: native build обязан выпускать ровно такой файл, managed сторона
 *   не имеет права подставлять другое имя.
 *
 * ГРАНИЦЫ
 *   - Через границу проходят только C-совместимые типы: uint32_t, int32_t, char, opaque handle.
 *   - Запрещено: cv::Mat, любые STL-типы (std::string, std::vector, ...), C++-классы кроме opaque
 *     handle, ссылки, C++-исключения и pixel-buffer pointers.
 *   - Буферы-входы и metadata принадлежат вызывающей стороне. Native frame принадлежит DLL и
 *     освобождается только парным export-ом azurpilot_native_frame_release.
 *   - Соглашение вызова — по умолчанию. На x64 Windows существует одно соглашение вызова, поэтому
 *     явные __cdecl/__stdcall не указываются; extern "C" исключает декорацию имён.
 *   - Ни одна функция не выпускает C++-исключения через границу: любое исключение перехватывается
 *     внутри native стороны и превращается в int32-код возврата. Наружу идёт только код.
 *   - Функции не бросают исключений C++ (см. AZURPILOT_NATIVE_NOEXCEPT), не требуют инициализации
 *     и не имеют разделяемого изменяемого глобального состояния; ownership задаётся каждым handle.
 *
 * ИСПОЛЬЗОВАНИЕ ИЗ NATIVE-КОДА
 *   - Реализация DLL включает этот заголовок без дополнительных макросов: объявления разворачиваются
 *     в __declspec(dllexport).
 *   - Native-потребитель, линкующийся с DLL через import library (например native smoke test),
 *     обязан определить AZURPILOT_NATIVE_IMPORT до включения заголовка
 *     (например target_compile_definitions(<target> PRIVATE AZURPILOT_NATIVE_IMPORT)).
 * =============================================================================
 */

#ifndef AZURPILOT_NATIVE_ABI_H
#define AZURPILOT_NATIVE_ABI_H

#include <stdint.h>
#include <stddef.h>

/* Версия ABI. Единственный нормативный владелец значения — этот заголовок.
 * Managed и native стороны проверяют совместимость границы до использования данных. */
#define AZURPILOT_NATIVE_ABI_VERSION 2
#define AZURPILOT_NATIVE_ABI_VERSION_STRING "2"

/* Коды возврата. 0 — единственный успешный код; любой ненулевой код означает, что результат
 * недействителен и использовать его нельзя. */
#define AZURPILOT_NATIVE_OK 0
/* Некорректные аргументы вызова (например NULL там, где указатель обязателен). */
#define AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT 1
/* Предоставленный буфер меньше требуемого размера. Буфер не изменяется; требуемый размер
 * возвращается через out-параметр required_size. */
#define AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL 2
/* Код OpenCV исполнился, но выбросил исключение (например cv::Exception). */
#define AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE 3
/* Любая другая ошибка native стороны, включая неожиданное исключение C++. */
#define AZURPILOT_NATIVE_ERROR_INTERNAL 4
/* Вход не является целым PNG либо его структура/сжатые данные повреждены. */
#define AZURPILOT_NATIVE_ERROR_INVALID_PNG 5
/* PNG корректен, но его вариант/alpha policy не поддерживается этим boundary. */
#define AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG 6
/* Закодированный вход или декодированные размеры превышают фиксированные безопасные пределы. */
#define AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE 7
/* Не удалось выделить память для декодирования или native frame. */
#define AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY 8

/* Защитные пределы PNG-frame. Нормативный владелец чисел — этот ABI header. */
#define AZURPILOT_NATIVE_MAX_PNG_BYTES UINT32_C(16777216)
#define AZURPILOT_NATIVE_MAX_FRAME_DIMENSION UINT32_C(8192)
#define AZURPILOT_NATIVE_MAX_FRAME_PIXELS UINT32_C(16777216)
#define AZURPILOT_NATIVE_MAX_RGB8_BYTES UINT32_C(50331648)

/* Идентификатор формата поля pixel_format в AzurPilotNativeFrameInfo. */
#define AZURPILOT_NATIVE_PIXEL_FORMAT_RGB8 UINT32_C(1)

/* Биты поля build_flags структуры AzurPilotNativeInfo. */
/* Установлен, только если код OpenCV реально исполнился при заполнении структуры в этом вызове.
 * Это факт исполнения, а не константа сборки. */
#define AZURPILOT_NATIVE_BUILD_FLAG_OPENCV_EXECUTED (1u << 0)

/* Биты поля capabilities структуры AzurPilotNativeInfo: какие модули OpenCV реально доступны
 * в этой сборке native библиотеки. */
/* core: cv::Mat и cv::mean. */
#define AZURPILOT_NATIVE_CAPABILITY_CORE (1u << 0)
/* imgcodecs: cv::imwrite и cv::imread. */
#define AZURPILOT_NATIVE_CAPABILITY_IMGCODECS (1u << 1)

/* Разворачивается в __declspec(dllexport) при сборке DLL и в __declspec(dllimport) для
 * native-потребителя import library. */
#if defined(_WIN32)
#if defined(AZURPILOT_NATIVE_IMPORT)
#define AZURPILOT_NATIVE_API __declspec(dllimport)
#else
#define AZURPILOT_NATIVE_API __declspec(dllexport)
#endif
#else
#define AZURPILOT_NATIVE_API
#endif

/* Документирует на уровне типа, что исключения не пересекают границу: нарушение этой гарантии
 * внутри реализации приводит к аварийному завершению, а не к неопределённому поведению на границе. */
#if defined(__cplusplus)
#define AZURPILOT_NATIVE_NOEXCEPT noexcept
#else
#define AZURPILOT_NATIVE_NOEXCEPT
#endif

/* Сведения о native boundary. Раскладка заморожена: смещения 0, 4, 8, 12, 16, 20 и 24..55,
 * выравнивание 4, полный размер 56 байт. Managed сторона обязана объявлять структуру
 * с последовательной раскладкой и проверять её размер. */
typedef struct AzurPilotNativeInfo {
    /* Версия ABI native библиотеки: всегда AZURPILOT_NATIVE_ABI_VERSION при успехе. */
    uint32_t abi_version;
    /* Версия OpenCV, с которой собрана native библиотека (CV_VERSION_MAJOR/MINOR/REVISION). */
    uint32_t opencv_major;
    uint32_t opencv_minor;
    uint32_t opencv_patch;
    /* Биты AZURPILOT_NATIVE_BUILD_FLAG_*. */
    uint32_t build_flags;
    /* Биты AZURPILOT_NATIVE_CAPABILITY_*. */
    uint32_t capabilities;
    /* Версия OpenCV строкой в формате "major.minor.patch" (pin — native/opencv.json).
     * Всегда NUL-терминирована; при усечении последний байт массива — NUL.
     * Кодировка ASCII/UTF-8 без BOM. */
    char opencv_version_string[32];
} AzurPilotNativeInfo;

/* Opaque handle: его содержимое и allocator принадлежат DLL. Нельзя разыменовывать,
 * сохранять после release или освобождать через CLR/CRT allocator. */
typedef struct AzurPilotNativeFrameOpaque* AzurPilotNativeFrameHandle;

/* Неизменяемые metadata конкретного contiguous native-owned RGB8 frame.
 * Раскладка ABI v2: пять uint32_t, смещения 0, 4, 8, 12, 16; alignment 4; size 20. */
typedef struct AzurPilotNativeFrameInfo {
    uint32_t width;
    uint32_t height;
    uint32_t stride_bytes;
    uint32_t byte_length;
    uint32_t pixel_format;
} AzurPilotNativeFrameInfo;

#if defined(__cplusplus)
static_assert(sizeof(AzurPilotNativeInfo) == 56,
              "AzurPilotNativeInfo: раскладка ABI изменена — это ломает managed/native совместимость");
static_assert(sizeof(AzurPilotNativeFrameInfo) == 20 && alignof(AzurPilotNativeFrameInfo) == 4,
              "AzurPilotNativeFrameInfo: требуется раскладка ABI v2 size=20 alignment=4");
static_assert(offsetof(AzurPilotNativeFrameInfo, width) == 0 &&
                  offsetof(AzurPilotNativeFrameInfo, height) == 4 &&
                  offsetof(AzurPilotNativeFrameInfo, stride_bytes) == 8 &&
                  offsetof(AzurPilotNativeFrameInfo, byte_length) == 12 &&
                  offsetof(AzurPilotNativeFrameInfo, pixel_format) == 16,
              "AzurPilotNativeFrameInfo: поля ABI v2 перемещены");
#endif

#if !defined(__cplusplus) && defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(AzurPilotNativeFrameInfo) == 20 &&
                   _Alignof(AzurPilotNativeFrameInfo) == 4,
               "AzurPilotNativeFrameInfo: требуется раскладка ABI v2 size=20 alignment=4");
_Static_assert(offsetof(AzurPilotNativeFrameInfo, width) == 0 &&
                   offsetof(AzurPilotNativeFrameInfo, height) == 4 &&
                   offsetof(AzurPilotNativeFrameInfo, stride_bytes) == 8 &&
                   offsetof(AzurPilotNativeFrameInfo, byte_length) == 12 &&
                   offsetof(AzurPilotNativeFrameInfo, pixel_format) == 16,
               "AzurPilotNativeFrameInfo: поля ABI v2 перемещены");
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* Возвращает AZURPILOT_NATIVE_ABI_VERSION.
 * Никогда не падает, не обращается к OpenCV и не требует инициализации. Managed сторона обязана
 * сравнить результат с ожидаемой версией ABI до любого другого вызова; несовпадение — ошибка
 * совместимости, а не повод продолжать. */
AZURPILOT_NATIVE_API uint32_t azurpilot_native_abi_version(void) AZURPILOT_NATIVE_NOEXCEPT;

/* Заполняет структуру сведениями о native boundary.
 * out_info — память вызывающей стороны; владение остаётся у вызывающей стороны.
 *
 * Возврат:
 *   AZURPILOT_NATIVE_OK                        — структура заполнена полностью и корректно.
 *   AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT    — out_info == NULL; структура не изменяется.
 *   AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE      — код OpenCV исполнился, но выбросил исключение;
 *                                                структура полностью обнулена, build_flags == 0.
 *   AZURPILOT_NATIVE_ERROR_INTERNAL            — любая другая ошибка native стороны;
 *                                                структура полностью обнулена, build_flags == 0.
 *
 * Гарантии при AZURPILOT_NATIVE_OK:
 *   abi_version == AZURPILOT_NATIVE_ABI_VERSION;
 *   opencv_major/minor/patch — версия OpenCV, с которой собрана библиотека;
 *   opencv_version_string — непустая NUL-терминированная строка версии OpenCV;
 *   build_flags содержит AZURPILOT_NATIVE_BUILD_FLAG_OPENCV_EXECUTED только если код OpenCV
 *     реально исполнился в этом вызове;
 *   capabilities содержит биты модулей, реально доступных в сборке (core и imgcodecs).
 *
 * При любом ненулевом коде структура не содержит частично заполненных данных. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_query(AzurPilotNativeInfo* out_info)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Возвращает строку сведений о сборке native библиотеки в буфер вызывающей стороны.
 *
 * Требования к строке: одна строка, пары key=value, разделитель ';', кодировка ASCII/UTF-8 без BOM,
 * без завершающего перевода строки. Строка обязана содержать как минимум abi_version=<номер> и
 * opencv_version=<x.y.z>; дополнительные пары допустимы. Сведения о версии OpenCV берутся из
 * заголовков на этапе компиляции, поэтому функция не обязана исполнять код OpenCV.
 *
 * Возврат:
 *   AZURPILOT_NATIVE_OK                        — строка целиком записана в buffer и завершена NUL.
 *   AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL    — в буфер не записано ни одного байта (никаких
 *                                                частичных записей), *required_size заполнен.
 *   AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT    — required_size == NULL.
 *   AZURPILOT_NATIVE_ERROR_INTERNAL            — внутренняя ошибка native стороны.
 *
 * Семантика required_size:
 *   - required_size обязателен и не может быть NULL; при NULL буфер не изменяется и возвращается
 *     AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
 *   - при любом исходе, кроме INVALID_ARGUMENT, *required_size получает полный размер строки
 *     в байтах вместе с завершающим NUL (то есть минимум 1) — независимо от размера буфера;
 *   - buffer == NULL или buffer_size == 0 — это размер-запрос: буфер не изменяется, возвращается
 *     BUFFER_TOO_SMALL и заполненный *required_size. Штатная идиома двух вызовов: сначала
 *     (NULL, 0) для получения размера, затем буфер нужного размера;
 *   - buffer_size < *required_size — буфер не переполняется и не изменяется ни на байт;
 *   - при OK гарантировано buffer_size >= *required_size, строка завершена NUL. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_build_info(char* buffer, uint32_t buffer_size,
                                                         uint32_t* required_size)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Декодирует PNG непосредственно из памяти вызывающей стороны и создаёт один native-owned RGB8
 * frame. Успех передаёт handle, который обязан быть освобождён azurpilot_native_frame_release.
 * Входные байты не сохраняются и не используются после возврата.
 *
 * При ненулевом out_frame он сначала устанавливается в NULL. Любой отказ оставляет его NULL.
 * NULL png_bytes/out_frame — INVALID_ARGUMENT; пустой/повреждённый/не-PNG вход — INVALID_PNG;
 * неподдерживаемая PNG-форма — UNSUPPORTED_PNG; нарушение лимита — IMAGE_TOO_LARGE;
 * нехватка памяти — OUT_OF_MEMORY; непредвиденная ошибка — INTERNAL. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_decode_png(
    const uint8_t* png_bytes, uint32_t png_size, AzurPilotNativeFrameHandle* out_frame)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Возвращает проверенные width/height/stride/byte_length и RGB8 format.
 * out_info обнуляется до проверки handle. При любом отказе metadata не содержит частичных данных.
 * NULL handle или out_info — INVALID_ARGUMENT; недостоверное внутреннее состояние — INTERNAL. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_get_info(
    AzurPilotNativeFrameHandle frame, AzurPilotNativeFrameInfo* out_info)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Освобождает handle через allocator native DLL. NULL handle возвращает INVALID_ARGUMENT.
 * Произвольный stale/чужой handle недействителен и не обязан безопасно обнаруживаться; managed
 * consumer исключает повторный release посредством SafeHandle. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame)
    AZURPILOT_NATIVE_NOEXCEPT;

#if defined(__cplusplus)
}
#endif

#endif /* AZURPILOT_NATIVE_ABI_H */
