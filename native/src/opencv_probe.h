/* =============================================================================
 * opencv_probe.h — внутренний C++ интерфейс native boundary (не часть C ABI).
 *
 * Зачем отдельный заголовок: единица трансляции, реализующая C ABI, не включает
 * заголовки OpenCV вообще. Тогда нарушение границы (протаскивание cv::Mat,
 * STL-типов, ссылок или владеющих указателей в C ABI) невозможно даже случайно —
 * компилятор его не пропустит. Через этот внутренний интерфейс ходят только
 * POD-значения: статус, биты и номера версии.
 *
 * Исключения: функции объявлены noexcept и перехватывают внутри всё, что может
 * выбросить OpenCV или стандартная библиотека. Исключение не покидает native
 * boundary ни при каком исходе.
 * =============================================================================
 */

#ifndef AZURPILOT_NATIVE_OPENCV_PROBE_H
#define AZURPILOT_NATIVE_OPENCV_PROBE_H

#include <stdint.h>

namespace azurpilot::native::detail {

/* Исход исполнения OpenCV-кода. Отображается на коды возврата C ABI:
 *   Ok              -> AZURPILOT_NATIVE_OK;
 *   OpencvFailure   -> AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE (OpenCV выбросил cv::Exception);
 *   InternalFailure -> AZURPILOT_NATIVE_ERROR_INTERNAL (любая другая ошибка native стороны). */
enum class ProbeStatus : int32_t {
    Ok = 0,
    OpencvFailure = 1,
    InternalFailure = 2
};

/* Результат исполнения OpenCV-кода. */
struct ProbeResult {
    /* Исход исполнения. */
    ProbeStatus status;
    /* Биты AZURPILOT_NATIVE_BUILD_FLAG_*: факт реального исполнения OpenCV-кода. */
    uint32_t build_flags;
    /* Биты AZURPILOT_NATIVE_CAPABILITY_*: модули, реально проверенные в этом вызове. */
    uint32_t capabilities;
};

/* Реально исполняет OpenCV-код: cv::Mat + cv::mean (core) и cv::imwrite/cv::imread
 * round-trip (imgcodecs). Возвращает факт исполнения, а не константу сборки.
 * Никогда не выбрасывает исключений: cv::Exception превращается в OpencvFailure,
 * любое другое исключение — в InternalFailure. */
ProbeResult run_opencv_probe() noexcept;

/* Версия OpenCV, с которой собран native код (макросы CV_VERSION_MAJOR/MINOR/REVISION). */
struct OpencvVersion {
    uint32_t major;
    uint32_t minor;
    uint32_t patch;
};

OpencvVersion opencv_compile_time_version() noexcept;

/* Версия OpenCV строкой (значение макроса CV_VERSION). Время жизни — статическое,
 * владение наружу не передаётся. */
const char* opencv_compile_time_version_string() noexcept;

}  // namespace azurpilot::native::detail

#endif /* AZURPILOT_NATIVE_OPENCV_PROBE_H */
