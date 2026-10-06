/* =============================================================================
 * opencv_probe.cpp — реальное исполнение OpenCV для native boundary.
 *
 * Здесь и только здесь native код касается OpenCV. Результат отдаётся наружу как
 * POD-значения (см. opencv_probe.h), поэтому C ABI остаётся узким и не содержит ни
 * cv::Mat, ни STL-типов, ни владеющих указателей.
 *
 * Всё тело обёрнуто в try/catch: cv::imwrite/cv::imread/cv::mean при ошибке бросают
 * cv::Exception, и она обязана превратиться в код возврата, а не покинуть boundary.
 * =============================================================================
 */

#include "opencv_probe.h"

#include "azurpilot_native_abi.h"

#include <opencv2/core.hpp>
#include <opencv2/core/version.hpp>
#include <opencv2/imgcodecs.hpp>

#include <process.h>

#include <atomic>
#include <cmath>
#include <filesystem>
#include <string>
#include <system_error>
#include <utility>

namespace azurpilot::native::detail {
namespace {

/* Временный файл для round-trip imgcodecs. Имя собирается из системного каталога
 * временных файлов и идентификатора процесса: зашитых путей нет. Файл удаляется при
 * выходе из области видимости, в том числе при выброшенном исключении. */
class ScopedTempFile {
public:
    explicit ScopedTempFile(std::filesystem::path path) : path_(std::move(path)) {}
    ScopedTempFile(const ScopedTempFile&) = delete;
    ScopedTempFile& operator=(const ScopedTempFile&) = delete;
    ~ScopedTempFile() {
        std::error_code error;
        std::filesystem::remove(path_, error);
    }

    const std::filesystem::path& path() const noexcept { return path_; }

private:
    std::filesystem::path path_;
};

std::filesystem::path make_unique_temp_png_path() {
    static std::atomic<unsigned long long> counter{0u};
    const unsigned long long index = counter.fetch_add(1u, std::memory_order_relaxed);

    std::string name = "azurpilot_native_probe_";
    name += std::to_string(static_cast<long long>(::_getpid()));
    name += "_";
    name += std::to_string(index);
    name += ".png";

    return std::filesystem::temp_directory_path() / name;
}

}  // namespace

ProbeResult run_opencv_probe() noexcept {
    try {
        uint32_t capabilities = 0u;

        // --- core: cv::Mat + cv::mean -------------------------------------------------
        // Детерминированная матрица 4x4 со значениями 0..15: среднее известно точно
        // (7.5), поэтому проверяется реально посчитанный результат, а не факт вызова.
        cv::Mat plane(4, 4, CV_8UC1);
        for (int y = 0; y < plane.rows; ++y) {
            for (int x = 0; x < plane.cols; ++x) {
                plane.at<uchar>(y, x) = static_cast<uchar>((y * plane.cols) + x);
            }
        }
        const double mean = cv::mean(plane)[0];
        if (std::fabs(mean - 7.5) > 1e-9) {
            // OpenCV исполнился, но результат неверен: это внутренняя ошибка native
            // стороны, а не исключение OpenCV.
            return ProbeResult{ProbeStatus::InternalFailure, 0u, 0u};
        }
        capabilities |= AZURPILOT_NATIVE_CAPABILITY_CORE;

        // --- imgcodecs: cv::imwrite / cv::imread round-trip ---------------------------
        const ScopedTempFile temp_file(make_unique_temp_png_path());

        cv::Mat image(8, 8, CV_8UC3);
        for (int y = 0; y < image.rows; ++y) {
            for (int x = 0; x < image.cols; ++x) {
                image.at<cv::Vec3b>(y, x) =
                    cv::Vec3b(static_cast<uchar>(x * 8), static_cast<uchar>(y * 8), static_cast<uchar>(64));
            }
        }

        if (!cv::imwrite(temp_file.path().string(), image)) {
            return ProbeResult{ProbeStatus::OpencvFailure, 0u, 0u};
        }

        const cv::Mat decoded = cv::imread(temp_file.path().string(), cv::IMREAD_COLOR);
        if (decoded.empty() || decoded.size() != image.size() || decoded.type() != image.type()) {
            return ProbeResult{ProbeStatus::OpencvFailure, 0u, 0u};
        }
        // PNG — формат без потерь: расхождение означает, что round-trip не состоялся.
        if (cv::norm(decoded, image, cv::NORM_INF) != 0.0) {
            return ProbeResult{ProbeStatus::InternalFailure, 0u, 0u};
        }
        capabilities |= AZURPILOT_NATIVE_CAPABILITY_IMGCODECS;

        // Оба модуля реально исполнились в этом вызове: флаг — факт, а не константа.
        return ProbeResult{ProbeStatus::Ok, AZURPILOT_NATIVE_BUILD_FLAG_OPENCV_EXECUTED, capabilities};
    } catch (const cv::Exception&) {
        // Ожидаемый отказ OpenCV: наружу уходит код AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE.
        return ProbeResult{ProbeStatus::OpencvFailure, 0u, 0u};
    } catch (...) {
        return ProbeResult{ProbeStatus::InternalFailure, 0u, 0u};
    }
}

OpencvVersion opencv_compile_time_version() noexcept {
    return OpencvVersion{static_cast<uint32_t>(CV_VERSION_MAJOR), static_cast<uint32_t>(CV_VERSION_MINOR),
                         static_cast<uint32_t>(CV_VERSION_REVISION)};
}

const char* opencv_compile_time_version_string() noexcept {
    return CV_VERSION;
}

}  // namespace azurpilot::native::detail
