/* =============================================================================
 * azurpilot_native_abi.h — замороженный C ABI между managed (C#) и native (C++) частями AzurPilot.
 *
 * НАЗНАЧЕНИЕ
 *   Единственный владелец формы C ABI версии 5. Managed сторона и native реализация обязаны
 *   совпадать побайтово: любое расхождение — ошибка совместимости, а не повод продолжать работу.
 *   Изменение формы (поля, порядок, типы, коды возврата, смысл битов) требует нового номера ABI
 *   и согласованного обновления обеих сторон; обратная совместимость формой не гарантируется.
 *
 * АРТЕФАКТ
 *   Native часть собирается в динамическую библиотеку `AzurPilot.Native.dll` (Windows, x64). Managed сторона
 *   загружает её по имени `AzurPilot.Native` через source-generated LibraryImport. Имя артефакта
 *   заморожено этим контрактом: native build обязан выпускать ровно такой файл, managed сторона
 *   не имеет права подставлять другое имя.
 *
 * ГРАНИЦЫ
 *   - Через границу проходят только C-совместимые типы: uint32_t, uint64_t, int32_t, double,
 *     char, POD-структуры и непрозрачный дескриптор.
 *   - Запрещено: cv::Mat, любые STL-типы (std::string, std::vector, ...), C++-классы кроме непрозрачного
 *     дескриптора, ссылки, C++-исключения и указатели на пиксельные буферы.
 *   - Входные буферы и метаданные принадлежат вызывающей стороне. Native-кадр принадлежит DLL и
 *     освобождается только парным экспортом azurpilot_native_frame_release.
 *   - Соглашение вызова — по умолчанию. На x64 Windows существует одно соглашение вызова, поэтому
 *     явные __cdecl/__stdcall не указываются; extern "C" исключает декорацию имён.
 *   - Ни одна функция не выпускает C++-исключения через границу: любое исключение перехватывается
 *     внутри native-стороны и превращается в int32-код возврата. Наружу идёт только код.
 *   - Функции не бросают исключений C++ (см. AZURPILOT_NATIVE_NOEXCEPT), не требуют инициализации
 *     и не имеют разделяемого изменяемого глобального состояния; владение задаётся каждым дескриптором.
 *
 * ИСПОЛЬЗОВАНИЕ ИЗ NATIVE-КОДА
 *   - Реализация DLL включает этот заголовок без дополнительных макросов: объявления разворачиваются
 *     в __declspec(dllexport).
 *   - Native-потребитель, линкующийся с DLL через import library (например проверка C ABI),
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
#define AZURPILOT_NATIVE_ABI_VERSION 5
#define AZURPILOT_NATIVE_ABI_VERSION_STRING "5"

/* Бюджет одного bounded template search. Полный кадр не масштабируется. */
#define AZURPILOT_NATIVE_MAX_TEMPLATE_PNG_BYTES UINT32_C(262144)
#define AZURPILOT_NATIVE_MAX_TEMPLATE_PIXELS UINT32_C(262144)
#define AZURPILOT_NATIVE_MAX_SEARCH_PIXELS UINT32_C(2097152)
#define AZURPILOT_NATIVE_MIN_TEMPLATE_STDDEV 5.0
#define AZURPILOT_NATIVE_MATCH_RGB UINT32_C(0)
#define AZURPILOT_NATIVE_MATCH_LUMA UINT32_C(1)

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
/* PNG корректен, но его вариант или политика обработки прозрачности не поддерживается этой границей. */
#define AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG 6
/* Закодированный вход или декодированные размеры превышают фиксированные безопасные пределы. */
#define AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE 7
/* Не удалось выделить память для декодирования или native-кадра. */
#define AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY 8

/* Защитные пределы PNG-кадра. Единственный нормативный владелец значений — этот ABI-заголовок.
 * Предел входного PNG ограничивает размер данных для разбора; предел стороны — габариты кадра;
 * предел пикселей — объём декодирования; предел RGB8 — размер выходного буфера. Это отдельные
 * границы ресурсов: проверяй каждую напрямую и не выводи её значение из другой. */
#define AZURPILOT_NATIVE_MAX_PNG_BYTES UINT32_C(16777216)
#define AZURPILOT_NATIVE_MAX_FRAME_DIMENSION UINT32_C(8192)
#define AZURPILOT_NATIVE_MAX_FRAME_PIXELS UINT32_C(16777216)
#define AZURPILOT_NATIVE_MAX_RGB8_BYTES UINT32_C(50331648)

/* Диагностическое превью (ABI v5). Превью — отдельный уменьшенный RGB8-кадр DLL, а не новый владелец
 * исходного кадра; JPEG кодируется только из кадра, обе стороны которого не больше предела стороны
 * превью: кадр больше предела (например, 4K) этой границей не экспортируется. Аннотации рисуются на
 * внутренней копии кадра. */
#define AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION UINT32_C(1920)
#define AZURPILOT_NATIVE_MAX_ANNOTATIONS UINT32_C(512)
#define AZURPILOT_NATIVE_ANNOTATION_TEXT_BYTES 64
#define AZURPILOT_NATIVE_MAX_JPEG_BYTES UINT32_C(8388608)
#define AZURPILOT_NATIVE_MIN_JPEG_QUALITY UINT32_C(50)
#define AZURPILOT_NATIVE_MAX_JPEG_QUALITY UINT32_C(95)
/* Предел координат аннотации по модулю: рисование вне превью отсекается OpenCV, но арифметика остаётся ограниченной. */
#define AZURPILOT_NATIVE_MAX_ANNOTATION_COORDINATE INT32_C(16384)
/* Пределы параметров аннотаций: радиус (поле right у кругов), масштаб текста в процентах и толщина линии. */
#define AZURPILOT_NATIVE_MAX_ANNOTATION_RADIUS INT32_C(1024)
#define AZURPILOT_NATIVE_MIN_ANNOTATION_TEXT_SCALE INT32_C(25)
#define AZURPILOT_NATIVE_MAX_ANNOTATION_TEXT_SCALE INT32_C(300)
#define AZURPILOT_NATIVE_MAX_ANNOTATION_THICKNESS UINT32_C(8)

/* Виды аннотаций. RECT/FILLED_RECT: region — полуоткрытый прямоугольник превью.
 * CIRCLE/FILLED_CIRCLE/CROSS: центр (left, top), радиус right (1..AZURPILOT_NATIVE_MAX_ANNOTATION_RADIUS), bottom == 0.
 * TEXT: начало базовой линии (left, top), масштаб шрифта right в процентах (AZURPILOT_NATIVE_MIN_ANNOTATION_TEXT_SCALE..AZURPILOT_NATIVE_MAX_ANNOTATION_TEXT_SCALE), bottom == 0;
 * text — печатный ASCII 0x20..0x7E с завершающим NUL. У остальных видов text пуст. */
#define AZURPILOT_NATIVE_ANNOTATION_RECT UINT32_C(1)
#define AZURPILOT_NATIVE_ANNOTATION_FILLED_RECT UINT32_C(2)
#define AZURPILOT_NATIVE_ANNOTATION_CIRCLE UINT32_C(3)
#define AZURPILOT_NATIVE_ANNOTATION_FILLED_CIRCLE UINT32_C(4)
#define AZURPILOT_NATIVE_ANNOTATION_CROSS UINT32_C(5)
#define AZURPILOT_NATIVE_ANNOTATION_TEXT UINT32_C(6)
/* Стиль линии: сплошная либо штриховая (только RECT). */
#define AZURPILOT_NATIVE_ANNOTATION_SOLID UINT32_C(0)
#define AZURPILOT_NATIVE_ANNOTATION_DASHED UINT32_C(1)

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
/* imgproc: cv::matchTemplate реально исполнен при query. */
#define AZURPILOT_NATIVE_CAPABILITY_IMGPROC (1u << 2)

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

/* Сведения о native-границе. Раскладка заморожена: смещения 0, 4, 8, 12, 16, 20 и 24..55,
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

/* Непрозрачный дескриптор: его содержимое и выделение памяти принадлежат DLL. Нельзя разыменовывать,
 * сохранять после вызова azurpilot_native_frame_release или освобождать средствами CLR/CRT. */
typedef struct AzurPilotNativeFrameOpaque* AzurPilotNativeFrameHandle;

/* Неизменяемые метаданные конкретного непрерывного кадра RGB8, которым владеет DLL.
 * Раскладка ABI v2: пять uint32_t, смещения 0, 4, 8, 12, 16; выравнивание 4; размер 20. */
typedef struct AzurPilotNativeFrameInfo {
    uint32_t width;
    uint32_t height;
    uint32_t stride_bytes;
    uint32_t byte_length;
    uint32_t pixel_format;
} AzurPilotNativeFrameInfo;

/* Физические координаты полуоткрытой области [left, right) × [top, bottom).
 * Размер 16, выравнивание 4, смещения 0, 4, 8, 12. Область не обрезается границей. */
typedef struct AzurPilotNativeRegion {
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
} AzurPilotNativeRegion;

/* Статистика RGB8 в исходном диапазоне 0..255. stddev — стандартное отклонение
 * генеральной совокупности (делитель N); для одного пикселя равно нулю.
 * Размер 56, выравнивание 8; смещения 0, 8, 16, 24, 32, 40, 48. */
typedef struct AzurPilotNativeRegionStatistics {
    uint64_t pixel_count;
    double mean_red;
    double mean_green;
    double mean_blue;
    double stddev_red;
    double stddev_green;
    double stddev_blue;
} AzurPilotNativeRegionStatistics;

/* Одна диагностическая аннотация превью; цвет задаётся в RGB. thickness 1..8 (для TEXT — толщина штриха).
 * Размер 92, выравнивание 4; смещения kind 0, style 4, region 8, red 24, green 25, blue 26, thickness 27, text 28. */
typedef struct AzurPilotNativeAnnotation {
    uint32_t kind;
    uint32_t style;
    AzurPilotNativeRegion region;
    uint8_t red;
    uint8_t green;
    uint8_t blue;
    uint8_t thickness;
    char text[AZURPILOT_NATIVE_ANNOTATION_TEXT_BYTES];
} AzurPilotNativeAnnotation;

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
static_assert(sizeof(AzurPilotNativeRegion) == 16 && alignof(AzurPilotNativeRegion) == 4 &&
                  offsetof(AzurPilotNativeRegion, left) == 0 &&
                  offsetof(AzurPilotNativeRegion, top) == 4 &&
                  offsetof(AzurPilotNativeRegion, right) == 8 &&
                  offsetof(AzurPilotNativeRegion, bottom) == 12,
              "AzurPilotNativeRegion: требуется раскладка ABI v3 size=16 alignment=4");
static_assert(sizeof(AzurPilotNativeRegionStatistics) == 56 &&
                  alignof(AzurPilotNativeRegionStatistics) == 8 &&
                  offsetof(AzurPilotNativeRegionStatistics, pixel_count) == 0 &&
                  offsetof(AzurPilotNativeRegionStatistics, mean_red) == 8 &&
                  offsetof(AzurPilotNativeRegionStatistics, mean_green) == 16 &&
                  offsetof(AzurPilotNativeRegionStatistics, mean_blue) == 24 &&
                  offsetof(AzurPilotNativeRegionStatistics, stddev_red) == 32 &&
                  offsetof(AzurPilotNativeRegionStatistics, stddev_green) == 40 &&
                  offsetof(AzurPilotNativeRegionStatistics, stddev_blue) == 48,
              "AzurPilotNativeRegionStatistics: требуется раскладка ABI v3 size=56 alignment=8");
static_assert(sizeof(AzurPilotNativeAnnotation) == 92 && alignof(AzurPilotNativeAnnotation) == 4 &&
                  offsetof(AzurPilotNativeAnnotation, style) == 4 &&
                  offsetof(AzurPilotNativeAnnotation, region) == 8 &&
                  offsetof(AzurPilotNativeAnnotation, red) == 24 &&
                  offsetof(AzurPilotNativeAnnotation, thickness) == 27 &&
                  offsetof(AzurPilotNativeAnnotation, text) == 28,
              "AzurPilotNativeAnnotation: требуется раскладка ABI v5 size=92 alignment=4");
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
_Static_assert(sizeof(AzurPilotNativeRegion) == 16 && _Alignof(AzurPilotNativeRegion) == 4 &&
                   offsetof(AzurPilotNativeRegion, left) == 0 &&
                   offsetof(AzurPilotNativeRegion, top) == 4 &&
                   offsetof(AzurPilotNativeRegion, right) == 8 &&
                   offsetof(AzurPilotNativeRegion, bottom) == 12,
               "AzurPilotNativeRegion: требуется раскладка ABI v3 size=16 alignment=4");
_Static_assert(sizeof(AzurPilotNativeRegionStatistics) == 56 &&
                   _Alignof(AzurPilotNativeRegionStatistics) == 8 &&
                   offsetof(AzurPilotNativeRegionStatistics, pixel_count) == 0 &&
                   offsetof(AzurPilotNativeRegionStatistics, mean_red) == 8 &&
                   offsetof(AzurPilotNativeRegionStatistics, mean_green) == 16 &&
                   offsetof(AzurPilotNativeRegionStatistics, mean_blue) == 24 &&
                   offsetof(AzurPilotNativeRegionStatistics, stddev_red) == 32 &&
                   offsetof(AzurPilotNativeRegionStatistics, stddev_green) == 40 &&
                   offsetof(AzurPilotNativeRegionStatistics, stddev_blue) == 48,
               "AzurPilotNativeRegionStatistics: требуется раскладка ABI v3 size=56 alignment=8");
_Static_assert(sizeof(AzurPilotNativeAnnotation) == 92 && _Alignof(AzurPilotNativeAnnotation) == 4 &&
                   offsetof(AzurPilotNativeAnnotation, region) == 8 &&
                   offsetof(AzurPilotNativeAnnotation, text) == 28,
               "AzurPilotNativeAnnotation: требуется раскладка ABI v5 size=92 alignment=4");
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* Результат CCOEFF_NORMED и независимой проверки ошибки пикселей (0..255).
 * second_score — максимум вне окрестности первого кандидата (половина шаблона по каждой оси).
 * Низкотекстурный шаблон отвергается; оценки сами по себе не означают подтверждённого match.
 * Раскладка: region 0..15, score 16, second_score 24, mean_absolute_error 32, stddev 40; size=48. */
typedef struct AzurPilotNativeMatchResult {
    AzurPilotNativeRegion region;
    double score;
    double second_score;
    double mean_absolute_error;
    double template_stddev;
} AzurPilotNativeMatchResult;

/* Синхронный поиск small PNG template в ROI native RGB8. Размер шаблона задаётся physical pixels;
 * mode = MATCH_RGB либо MATCH_LUMA (RGB2GRAY). Меняется только размер шаблона через INTER_AREA
 * при уменьшении, INTER_CUBIC при увеличении. Маски/повороты и letterbox transform не предполагаются.
 * При ошибке out_result обнуляется; NULL — INVALID_ARGUMENT, бюджеты — IMAGE_TOO_LARGE,
 * низкая текстура/невалидный mode/ROI/размер — INVALID_ARGUMENT; decoder statuses сохраняются.
 * Lifetime frame и PNG буфера покрывает весь вызов. Пиксели и handle через границу не выдаются. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_match(
    AzurPilotNativeFrameHandle frame, const AzurPilotNativeRegion* search,
    const uint8_t* template_png, uint32_t template_png_size,
    int32_t template_width, int32_t template_height, uint32_t mode,
    AzurPilotNativeMatchResult* out_result) AZURPILOT_NATIVE_NOEXCEPT;

/* Возвращает AZURPILOT_NATIVE_ABI_VERSION.
 * Никогда не падает, не обращается к OpenCV и не требует инициализации. Managed сторона обязана
 * сравнить результат с ожидаемой версией ABI до любого другого вызова; несовпадение — ошибка
 * совместимости, а не повод продолжать. */
AZURPILOT_NATIVE_API uint32_t azurpilot_native_abi_version(void) AZURPILOT_NATIVE_NOEXCEPT;

/* Возвращает нормативный лимит закодированного PNG из этого заголовка без OpenCV
 * и выделения памяти. Managed capture использует его для ограничения входного потока. */
AZURPILOT_NATIVE_API uint32_t azurpilot_native_max_png_bytes(void) AZURPILOT_NATIVE_NOEXCEPT;

/* Заполняет структуру сведениями о native-границе.
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

/* Декодирует PNG непосредственно из памяти вызывающей стороны и создаёт один кадр RGB8,
 * которым владеет DLL. При успехе возвращается дескриптор, который нужно освободить вызовом
 * azurpilot_native_frame_release.
 * Входные байты не сохраняются и не используются после возврата.
 *
 * При ненулевом out_frame он сначала устанавливается в NULL. Любой отказ оставляет его NULL.
 * NULL png_bytes/out_frame — INVALID_ARGUMENT; пустой/повреждённый/не-PNG вход — INVALID_PNG;
 * неподдерживаемая PNG-форма — UNSUPPORTED_PNG; нарушение лимита — IMAGE_TOO_LARGE;
 * нехватка памяти — OUT_OF_MEMORY; непредвиденная ошибка — INTERNAL. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_decode_png(
    const uint8_t* png_bytes, uint32_t png_size, AzurPilotNativeFrameHandle* out_frame)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Возвращает проверенные width/height/stride/byte_length и формат RGB8.
 * out_info обнуляется до проверки дескриптора. При любом отказе метаданные не содержат частичных
 * данных. NULL-дескриптор или NULL out_info — INVALID_ARGUMENT; недостоверное внутреннее состояние — INTERNAL. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_get_info(
    AzurPilotNativeFrameHandle frame, AzurPilotNativeFrameInfo* out_info)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Вычисляет число пикселей, среднее и population stddev RGB по physical ROI.
 * Требуется 0 <= left < right <= width и 0 <= top < bottom <= height;
 * отрицательная, пустая, перевёрнутая или выходящая за кадр область — INVALID_ARGUMENT.
 * NULL frame/region/out_statistics — INVALID_ARGUMENT. Ненулевой out_statistics полностью
 * обнуляется при любом отказе. Внутренне недостоверный кадр — INTERNAL; ошибка OpenCV —
 * OPENCV_FAILURE; нехватка памяти — OUT_OF_MEMORY. Пиксели не копируются через ABI,
 * кадр не изменяется. Вызывающая сторона удерживает живой frame до возврата и исключает
 * конкурентный frame_release; чужой или освобождённый дескриптор недопустим. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_scan_region(
    AzurPilotNativeFrameHandle frame,
    const AzurPilotNativeRegion* region,
    AzurPilotNativeRegionStatistics* out_statistics)
    AZURPILOT_NATIVE_NOEXCEPT;

/* Создаёт новый кадр RGB8 заданного размера уменьшением исходного кадра (INTER_AREA) для диагностики.
 * Исходный кадр не изменяется и может быть освобождён независимо; превью освобождается тем же
 * azurpilot_native_frame_release. Требуется 1 <= width <= исходной ширины, 1 <= height <= исходной высоты,
 * обе стороны <= AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION и точное совпадение пропорций
 * (width * source_height == height * source_width): нестандартная геометрия не подгоняется.
 * При ненулевом out_preview он сначала устанавливается в NULL. NULL frame/out_preview и нарушение размеров —
 * INVALID_ARGUMENT; недостоверный кадр — INTERNAL; ошибка OpenCV — OPENCV_FAILURE; нехватка памяти —
 * OUT_OF_MEMORY. Вызывающая сторона удерживает живой исходный frame до возврата. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_create_preview(
    AzurPilotNativeFrameHandle frame, uint32_t width, uint32_t height,
    AzurPilotNativeFrameHandle* out_preview) AZURPILOT_NATIVE_NOEXCEPT;

/* Рисует аннотации на внутренней копии кадра и кодирует её в baseline JPEG без EXIF/ICC metadata.
 * Кадр не изменяется. Кадр со стороной больше AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION — IMAGE_TOO_LARGE.
 * quality — MIN..MAX_JPEG_QUALITY; annotation_count <= MAX_ANNOTATIONS; annotations может быть NULL
 * только при нулевом count. Невалидные вид/стиль/толщина/координаты/текст — INVALID_ARGUMENT.
 * written_size обязателен (NULL — INVALID_ARGUMENT) и обнуляется в начале. Если закодированный JPEG
 * больше buffer_size (включая размер-запрос buffer == NULL, buffer_size == 0), возвращается
 * BUFFER_TOO_SMALL, буфер не изменяется, *written_size получает требуемый размер. JPEG больше
 * AZURPILOT_NATIVE_MAX_JPEG_BYTES — IMAGE_TOO_LARGE. При OK *written_size — длина JPEG в buffer.
 * Порядок каналов входа — RGB, JPEG содержит те же цвета. Пиксели и handle через границу не выдаются. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_encode_jpeg(
    AzurPilotNativeFrameHandle frame, uint32_t quality,
    const AzurPilotNativeAnnotation* annotations, uint32_t annotation_count,
    uint8_t* buffer, uint32_t buffer_size, uint32_t* written_size) AZURPILOT_NATIVE_NOEXCEPT;

/* Освобождает дескриптор через механизм выделения памяти native DLL. NULL-дескриптор возвращает INVALID_ARGUMENT.
 * Произвольный устаревший или чужой дескриптор недействителен и не обязан безопасно обнаруживаться;
 * managed-потребитель исключает повторное освобождение посредством SafeHandle. */
AZURPILOT_NATIVE_API int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame)
    AZURPILOT_NATIVE_NOEXCEPT;

#if defined(__cplusplus)
}
#endif

#endif /* AZURPILOT_NATIVE_ABI_H */
