#include "azurpilot_native_abi.h"
#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <cmath>
#include <iostream>
#include <vector>

namespace {
int failures = 0;
void check(bool value, const char* description) {
    if (!value) { ++failures; std::cerr << "[FAIL] " << description << '\n'; }
}
std::vector<uint8_t> png(const cv::Mat& rgb) {
    cv::Mat bgr;
    cv::cvtColor(rgb, bgr, cv::COLOR_RGB2BGR);
    std::vector<uint8_t> bytes;
    cv::imencode(".png", bgr, bytes);
    return bytes;
}
AzurPilotNativeFrameHandle decode(const cv::Mat& image) {
    const auto bytes = png(image);
    AzurPilotNativeFrameHandle frame = nullptr;
    check(azurpilot_native_frame_decode_png(bytes.data(), static_cast<uint32_t>(bytes.size()), &frame) == AZURPILOT_NATIVE_OK,
        "Декодирование fixture через production ABI");
    return frame;
}
}

int main() {
    check(azurpilot_native_abi_version() == AZURPILOT_NATIVE_ABI_VERSION, "Версия ABI match");
    AzurPilotNativeMatchResult result{};
    check(azurpilot_native_frame_match(nullptr, nullptr, nullptr, 0, 0, 0, 0, &result) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL match отвергается");
    cv::Mat templ(17, 23, CV_8UC3);
    cv::RNG random(76213);
    random.fill(templ, cv::RNG::UNIFORM, 0, 255);
    const auto template_png = png(templ);
    for (const int height : {720, 1080, 1440, 2160}) {
        const int width = height * 16 / 9;
        const int tw = 23 * height / 720;
        const int th = 17 * height / 720;
        cv::Mat resized;
        cv::resize(templ, resized, cv::Size(tw, th), 0, 0, cv::INTER_CUBIC);
        cv::Mat image(height, width, CV_8UC3, cv::Scalar(13, 29, 47));
        const int x = 37 + height / 13;
        const int y = 41 + height / 17;
        resized.copyTo(image(cv::Rect(x, y, tw, th)));
        auto frame = decode(image);
        const AzurPilotNativeRegion search{x - 7, y - 9, x + tw + 20, y + th + 30};
        check(azurpilot_native_frame_match(frame, &search, template_png.data(), static_cast<uint32_t>(template_png.size()),
            tw, th, AZURPILOT_NATIVE_MATCH_RGB, &result) == AZURPILOT_NATIVE_OK, "Match на полном native кадре каждого разрешения");
        check(result.region.left == x && result.region.top == y && result.score > .999 && result.mean_absolute_error < .01,
            "Найден смещённый шаблон без уменьшения кадра");
        check(azurpilot_native_frame_release(frame) == AZURPILOT_NATIVE_OK, "Освобождение physical frame");
    }
    cv::Mat duplicate(80, 130, CV_8UC3, cv::Scalar(0, 0, 0));
    templ.copyTo(duplicate(cv::Rect(10, 15, templ.cols, templ.rows)));
    templ.copyTo(duplicate(cv::Rect(80, 40, templ.cols, templ.rows)));
    auto frame = decode(duplicate);
    AzurPilotNativeRegion search{0, 0, 130, 80};
    check(azurpilot_native_frame_match(frame, &search, template_png.data(), static_cast<uint32_t>(template_png.size()),
        templ.cols, templ.rows, AZURPILOT_NATIVE_MATCH_RGB, &result) == AZURPILOT_NATIVE_OK && result.second_score > .999,
        "Два независимых кандидата сохраняются как ambiguity evidence");
    search.left = -1;
    check(azurpilot_native_frame_match(frame, &search, template_png.data(), static_cast<uint32_t>(template_png.size()),
        templ.cols, templ.rows, 0, &result) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT && result.score == 0,
        "Неверный ROI очищает результат");
    search.left = 0;
    const auto solid = png(cv::Mat(8, 8, CV_8UC3, cv::Scalar(22, 33, 44)));
    check(azurpilot_native_frame_match(frame, &search, solid.data(), static_cast<uint32_t>(solid.size()),
        8, 8, 0, &result) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Однотонный шаблон не становится match=1");
    check(azurpilot_native_frame_match(frame, &search, template_png.data(), static_cast<uint32_t>(template_png.size()),
        600, 600, 0, &result) == AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE, "Template budget проверяется до выделения score map");
    check(azurpilot_native_frame_release(frame) == AZURPILOT_NATIVE_OK, "Освобождение duplicate frame");
    std::cout << "Проверки native match завершены; ошибок: " << failures << '\n';
    return failures == 0 ? 0 : 1;
}
