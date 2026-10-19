/* Диагностическое превью и JPEG-кодирование кадра (ABI v5). Production распознавание его не использует. */

#include "native_frame.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <new>
#include <vector>

namespace {

constexpr std::int32_t kMaximumRadius = 1024;
constexpr std::int32_t kMinimumTextScale = 25;
constexpr std::int32_t kMaximumTextScale = 300;
constexpr std::uint8_t kMaximumThickness = 8;
constexpr int kDashLength = 8;
constexpr int kDashGap = 6;

std::int32_t exception_status(const cv::Exception& exception) noexcept {
    return exception.code == cv::Error::StsNoMem
        ? AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY
        : AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE;
}

bool coordinate_is_bounded(std::int32_t value) noexcept {
    return value >= -AZURPILOT_NATIVE_MAX_ANNOTATION_COORDINATE
        && value <= AZURPILOT_NATIVE_MAX_ANNOTATION_COORDINATE;
}

// Длина текста до NUL либо -1, если NUL отсутствует или встречен непечатный/не-ASCII байт.
int text_length(const char (&text)[AZURPILOT_NATIVE_ANNOTATION_TEXT_BYTES]) noexcept {
    for (int index = 0; index < AZURPILOT_NATIVE_ANNOTATION_TEXT_BYTES; ++index) {
        const auto byte = static_cast<unsigned char>(text[index]);
        if (byte == 0u) {
            return index;
        }
        if (byte < 0x20u || byte > 0x7Eu) {
            return -1;
        }
    }
    return -1;
}

bool annotation_is_valid(const AzurPilotNativeAnnotation& annotation) noexcept {
    const AzurPilotNativeRegion& region = annotation.region;
    if (annotation.thickness == 0u || annotation.thickness > kMaximumThickness
        || !coordinate_is_bounded(region.left) || !coordinate_is_bounded(region.top)
        || !coordinate_is_bounded(region.right) || !coordinate_is_bounded(region.bottom)) {
        return false;
    }

    const int length = text_length(annotation.text);
    if (length < 0) {
        return false;
    }

    if (annotation.style != AZURPILOT_NATIVE_ANNOTATION_SOLID
        && !(annotation.style == AZURPILOT_NATIVE_ANNOTATION_DASHED
            && annotation.kind == AZURPILOT_NATIVE_ANNOTATION_RECT)) {
        return false;
    }

    switch (annotation.kind) {
    case AZURPILOT_NATIVE_ANNOTATION_RECT:
    case AZURPILOT_NATIVE_ANNOTATION_FILLED_RECT:
        return length == 0 && region.left < region.right && region.top < region.bottom;
    case AZURPILOT_NATIVE_ANNOTATION_CIRCLE:
    case AZURPILOT_NATIVE_ANNOTATION_FILLED_CIRCLE:
    case AZURPILOT_NATIVE_ANNOTATION_CROSS:
        return length == 0 && region.right >= 1 && region.right <= kMaximumRadius && region.bottom == 0;
    case AZURPILOT_NATIVE_ANNOTATION_TEXT:
        return length > 0 && region.right >= kMinimumTextScale && region.right <= kMaximumTextScale
            && region.bottom == 0;
    default:
        return false;
    }
}

void draw_dashed_line(cv::Mat& canvas, cv::Point from, cv::Point to, const cv::Scalar& color, int thickness) {
    const int dx = to.x - from.x;
    const int dy = to.y - from.y;
    const int length = std::max(std::abs(dx), std::abs(dy));
    if (length == 0) {
        cv::line(canvas, from, to, color, thickness, cv::LINE_8);
        return;
    }

    for (int start = 0; start <= length; start += kDashLength + kDashGap) {
        const int end = std::min(length, start + kDashLength);
        const cv::Point a{from.x + (dx * start / length), from.y + (dy * start / length)};
        const cv::Point b{from.x + (dx * end / length), from.y + (dy * end / length)};
        cv::line(canvas, a, b, color, thickness, cv::LINE_8);
    }
}

// Холст хранит RGB, поэтому Scalar задаётся в том же порядке каналов, что и вход.
void draw(cv::Mat& canvas, const AzurPilotNativeAnnotation& annotation) {
    const AzurPilotNativeRegion& region = annotation.region;
    const cv::Scalar color{
        static_cast<double>(annotation.red),
        static_cast<double>(annotation.green),
        static_cast<double>(annotation.blue)};
    const int thickness = static_cast<int>(annotation.thickness);
    const cv::Point center{region.left, region.top};
    switch (annotation.kind) {
    case AZURPILOT_NATIVE_ANNOTATION_RECT: {
        // Полуоткрытая область: последний закрашиваемый пиксель — right-1/bottom-1.
        const cv::Point top_left{region.left, region.top};
        const cv::Point bottom_right{region.right - 1, region.bottom - 1};
        if (annotation.style == AZURPILOT_NATIVE_ANNOTATION_DASHED) {
            draw_dashed_line(canvas, top_left, {bottom_right.x, top_left.y}, color, thickness);
            draw_dashed_line(canvas, {bottom_right.x, top_left.y}, bottom_right, color, thickness);
            draw_dashed_line(canvas, bottom_right, {top_left.x, bottom_right.y}, color, thickness);
            draw_dashed_line(canvas, {top_left.x, bottom_right.y}, top_left, color, thickness);
        } else {
            cv::rectangle(canvas, top_left, bottom_right, color, thickness, cv::LINE_8);
        }
        break;
    }
    case AZURPILOT_NATIVE_ANNOTATION_FILLED_RECT:
        cv::rectangle(canvas, cv::Point{region.left, region.top}, cv::Point{region.right - 1, region.bottom - 1},
            color, cv::FILLED, cv::LINE_8);
        break;
    case AZURPILOT_NATIVE_ANNOTATION_CIRCLE:
        cv::circle(canvas, center, region.right, color, thickness, cv::LINE_AA);
        break;
    case AZURPILOT_NATIVE_ANNOTATION_FILLED_CIRCLE:
        cv::circle(canvas, center, region.right, color, cv::FILLED, cv::LINE_AA);
        break;
    case AZURPILOT_NATIVE_ANNOTATION_CROSS:
        cv::line(canvas, {center.x - region.right, center.y - region.right},
            {center.x + region.right, center.y + region.right}, color, thickness, cv::LINE_AA);
        cv::line(canvas, {center.x - region.right, center.y + region.right},
            {center.x + region.right, center.y - region.right}, color, thickness, cv::LINE_AA);
        break;
    case AZURPILOT_NATIVE_ANNOTATION_TEXT:
        cv::putText(canvas, annotation.text, center, cv::FONT_HERSHEY_SIMPLEX,
            static_cast<double>(region.right) / 100.0, color, thickness, cv::LINE_AA);
        break;
    default:
        break;
    }
}

}  // namespace

int32_t azurpilot_native_frame_create_preview(
    AzurPilotNativeFrameHandle frame, uint32_t width, uint32_t height,
    AzurPilotNativeFrameHandle* out_preview) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_preview == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_preview = nullptr;
    try {
        if (frame == nullptr) {
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        }

        AzurPilotNativeFrameInfo info{};
        const std::int32_t status = azurpilot::native::detail::get_frame_info(frame, &info);
        if (status != AZURPILOT_NATIVE_OK) {
            return status;
        }

        if (width == 0u || height == 0u
            || width > AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION || height > AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION
            || width > info.width || height > info.height
            || static_cast<std::uint64_t>(width) * info.height != static_cast<std::uint64_t>(height) * info.width) {
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        }

        cv::Mat resized;
        cv::resize(frame->rgb, resized, cv::Size(static_cast<int>(width), static_cast<int>(height)),
            0, 0, cv::INTER_AREA);
        const std::uint32_t stride = width * 3u;
        if (resized.type() != CV_8UC3 || !resized.isContinuous()
            || resized.cols != static_cast<int>(width) || resized.rows != static_cast<int>(height)
            || resized.step[0] != stride) {
            return AZURPILOT_NATIVE_ERROR_INTERNAL;
        }

        auto preview = std::make_unique<AzurPilotNativeFrameOpaque>(
            std::move(resized), width, height, stride, stride * height);
        *out_preview = preview.release();
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (const cv::Exception& exception) {
        return exception_status(exception);
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_frame_encode_jpeg(
    AzurPilotNativeFrameHandle frame, uint32_t quality,
    const AzurPilotNativeAnnotation* annotations, uint32_t annotation_count,
    uint8_t* buffer, uint32_t buffer_size, uint32_t* written_size) AZURPILOT_NATIVE_NOEXCEPT {
    if (written_size == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *written_size = 0u;
    try {
        if (frame == nullptr || (annotations == nullptr && annotation_count != 0u)
            || (buffer == nullptr && buffer_size != 0u)
            || quality < AZURPILOT_NATIVE_MIN_JPEG_QUALITY || quality > AZURPILOT_NATIVE_MAX_JPEG_QUALITY
            || annotation_count > AZURPILOT_NATIVE_MAX_ANNOTATIONS) {
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        }

        AzurPilotNativeFrameInfo info{};
        const std::int32_t status = azurpilot::native::detail::get_frame_info(frame, &info);
        if (status != AZURPILOT_NATIVE_OK) {
            return status;
        }
        if (info.width > AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION || info.height > AZURPILOT_NATIVE_MAX_PREVIEW_DIMENSION) {
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        }

        for (std::uint32_t index = 0u; index < annotation_count; ++index) {
            if (!annotation_is_valid(annotations[index])) {
                return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
            }
        }

        cv::Mat canvas = frame->rgb.clone();
        for (std::uint32_t index = 0u; index < annotation_count; ++index) {
            draw(canvas, annotations[index]);
        }

        // Кодер JPEG OpenCV ожидает BGR; преобразование сохраняет исходные цвета RGB-кадра.
        cv::Mat bgr;
        cv::cvtColor(canvas, bgr, cv::COLOR_RGB2BGR);
        std::vector<std::uint8_t> encoded;
        const std::vector<int> parameters{
            cv::IMWRITE_JPEG_QUALITY, static_cast<int>(quality),
            cv::IMWRITE_JPEG_PROGRESSIVE, 0,
            cv::IMWRITE_JPEG_OPTIMIZE, 0};
        if (!cv::imencode(".jpg", bgr, encoded, parameters) || encoded.empty()) {
            return AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE;
        }
        if (encoded.size() > AZURPILOT_NATIVE_MAX_JPEG_BYTES) {
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        }

        *written_size = static_cast<std::uint32_t>(encoded.size());
        if (encoded.size() > buffer_size) {
            return AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL;
        }

        std::memcpy(buffer, encoded.data(), encoded.size());
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        *written_size = 0u;
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (const cv::Exception& exception) {
        *written_size = 0u;
        return exception_status(exception);
    } catch (...) {
        *written_size = 0u;
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}
