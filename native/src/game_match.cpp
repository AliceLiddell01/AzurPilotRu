#include "native_frame.h"

#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <new>

static_assert(sizeof(AzurPilotNativeMatchResult) == 48 && alignof(AzurPilotNativeMatchResult) == 8
    && offsetof(AzurPilotNativeMatchResult, score) == 16
    && offsetof(AzurPilotNativeMatchResult, second_score) == 24
    && offsetof(AzurPilotNativeMatchResult, mean_absolute_error) == 32
    && offsetof(AzurPilotNativeMatchResult, template_stddev) == 40,
    "Неверная раскладка match ABI");

int32_t azurpilot_native_frame_match(
    AzurPilotNativeFrameHandle frame, const AzurPilotNativeRegion* search,
    const uint8_t* template_png, uint32_t template_png_size,
    int32_t template_width, int32_t template_height, uint32_t mode,
    AzurPilotNativeMatchResult* out_result) noexcept {
    if (out_result == nullptr) return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    std::memset(out_result, 0, sizeof(*out_result));
    try {
        if (frame == nullptr || search == nullptr || template_png == nullptr
            || template_width < 3 || template_height < 3 || mode > AZURPILOT_NATIVE_MATCH_LUMA)
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        AzurPilotNativeFrameInfo info{};
        const auto status = azurpilot::native::detail::get_frame_info(frame, &info);
        if (status != AZURPILOT_NATIVE_OK) return status;
        if (search->left < 0 || search->top < 0 || search->right <= search->left || search->bottom <= search->top
            || search->right > static_cast<int>(info.width) || search->bottom > static_cast<int>(info.height))
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        const int width = search->right - search->left;
        const int height = search->bottom - search->top;
        if (template_png_size > AZURPILOT_NATIVE_MAX_TEMPLATE_PNG_BYTES
            || static_cast<uint64_t>(template_width) * template_height > AZURPILOT_NATIVE_MAX_TEMPLATE_PIXELS
            || static_cast<uint64_t>(width) * height > AZURPILOT_NATIVE_MAX_SEARCH_PIXELS)
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        if (template_width > width || template_height > height) return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;

        AzurPilotNativeFrameOpaque* decoded = nullptr;
        const auto decoded_status = azurpilot::native::detail::decode_png_frame(template_png, template_png_size, &decoded,
            AZURPILOT_NATIVE_MAX_TEMPLATE_PIXELS);
        std::unique_ptr<AzurPilotNativeFrameOpaque> owner(decoded);
        if (decoded_status != AZURPILOT_NATIVE_OK) return decoded_status;
        if (static_cast<uint64_t>(decoded->width) * decoded->height > AZURPILOT_NATIVE_MAX_TEMPLATE_PIXELS)
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        const cv::Mat source = frame->rgb(cv::Rect(search->left, search->top, width, height));
        cv::Mat templ;
        cv::resize(decoded->rgb, templ, cv::Size(template_width, template_height), 0, 0,
            template_width <= decoded->rgb.cols && template_height <= decoded->rgb.rows ? cv::INTER_AREA : cv::INTER_CUBIC);
        cv::Mat image = source;
        if (mode == AZURPILOT_NATIVE_MATCH_LUMA) {
            cv::cvtColor(source, image, cv::COLOR_RGB2GRAY);
            cv::cvtColor(templ, templ, cv::COLOR_RGB2GRAY);
        }
        cv::Scalar mean, deviation;
        cv::meanStdDev(templ, mean, deviation);
        double texture = 0;
        for (int c = 0; c < templ.channels(); ++c) texture += deviation[c];
        texture /= templ.channels();
        if (texture < AZURPILOT_NATIVE_MIN_TEMPLATE_STDDEV) return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        cv::Mat scores;
        cv::matchTemplate(image, templ, scores, cv::TM_CCOEFF_NORMED);
        double score = 0;
        cv::Point point;
        cv::minMaxLoc(scores, nullptr, &score, nullptr, &point);
        cv::Mat difference;
        cv::absdiff(image(cv::Rect(point.x, point.y, templ.cols, templ.rows)), templ, difference);
        const cv::Scalar error = cv::mean(difference);
        double absolute_error = 0;
        for (int c = 0; c < templ.channels(); ++c) absolute_error += error[c];
        absolute_error /= templ.channels();
        const int left = std::max(0, point.x - templ.cols / 2);
        const int top = std::max(0, point.y - templ.rows / 2);
        const int right = std::min(scores.cols, point.x + templ.cols / 2 + 1);
        const int bottom = std::min(scores.rows, point.y + templ.rows / 2 + 1);
        scores(cv::Rect(left, top, right - left, bottom - top)).setTo(-1.0f);
        double second = -1;
        cv::minMaxLoc(scores, nullptr, &second);
        if (!std::isfinite(score) || !std::isfinite(second) || !std::isfinite(absolute_error))
            return AZURPILOT_NATIVE_ERROR_INTERNAL;
        *out_result = {{search->left + point.x, search->top + point.y,
            search->left + point.x + templ.cols, search->top + point.y + templ.rows},
            std::clamp(score, -1.0, 1.0), std::clamp(second, -1.0, 1.0), absolute_error, texture};
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (const cv::Exception& exception) {
        return exception.code == cv::Error::StsNoMem ? AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY : AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}
