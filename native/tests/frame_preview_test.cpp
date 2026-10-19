/* Проверка диагностического превью и JPEG через C ABI; JPEG декодируется независимым OpenCV. */
#include "azurpilot_native_abi.h"
#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

namespace {
int failures = 0;
void check(bool value, const char* description) {
    if (!value) { ++failures; std::cerr << "[FAIL] " << description << '\n'; }
}

AzurPilotNativeFrameHandle decode(const cv::Mat& rgb) {
    cv::Mat bgr;
    cv::cvtColor(rgb, bgr, cv::COLOR_RGB2BGR);
    std::vector<uint8_t> bytes;
    cv::imencode(".png", bgr, bytes);
    AzurPilotNativeFrameHandle frame = nullptr;
    check(azurpilot_native_frame_decode_png(bytes.data(), static_cast<uint32_t>(bytes.size()), &frame) == AZURPILOT_NATIVE_OK,
        "Декодирование fixture через production ABI");
    return frame;
}

std::vector<uint8_t> encode(AzurPilotNativeFrameHandle frame, const std::vector<AzurPilotNativeAnnotation>& annotations,
    int32_t* status = nullptr) {
    uint32_t required = 0;
    const auto* data = annotations.empty() ? nullptr : annotations.data();
    const auto count = static_cast<uint32_t>(annotations.size());
    int32_t query = azurpilot_native_frame_encode_jpeg(frame, 82u, data, count, nullptr, 0u, &required);
    if (query != AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL) {
        if (status != nullptr) *status = query;
        return {};
    }
    std::vector<uint8_t> buffer(required);
    uint32_t written = 0;
    const int32_t result = azurpilot_native_frame_encode_jpeg(frame, 82u, data, count, buffer.data(),
        static_cast<uint32_t>(buffer.size()), &written);
    if (status != nullptr) *status = result;
    buffer.resize(result == AZURPILOT_NATIVE_OK ? written : 0u);
    return buffer;
}

AzurPilotNativeAnnotation annotation(uint32_t kind, AzurPilotNativeRegion region, const char* text = "") {
    AzurPilotNativeAnnotation value{};
    value.kind = kind;
    value.style = AZURPILOT_NATIVE_ANNOTATION_SOLID;
    value.region = region;
    value.red = 255;
    value.green = 255;
    value.blue = 255;
    value.thickness = 2;
    for (std::size_t index = 0; index + 1 < sizeof(value.text) && text[index] != 0; ++index) {
        value.text[index] = text[index];
    }
    return value;
}

int32_t encode_status(AzurPilotNativeFrameHandle frame, const AzurPilotNativeAnnotation& value) {
    uint32_t written = 7;
    return azurpilot_native_frame_encode_jpeg(frame, 82u, &value, 1u, nullptr, 0u, &written);
}

cv::Vec3b pixel(const cv::Mat& bgr, int x, int y) {
    return bgr.at<cv::Vec3b>(y, x);
}
}

int main() {
    check(azurpilot_native_abi_version() == AZURPILOT_NATIVE_ABI_VERSION, "Версия ABI превью");
    AzurPilotNativeFrameHandle preview = reinterpret_cast<AzurPilotNativeFrameHandle>(1);
    check(azurpilot_native_frame_create_preview(nullptr, 1280u, 720u, &preview) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
        && preview == nullptr, "NULL кадр превью отвергается и обнуляет out");
    check(azurpilot_native_frame_create_preview(nullptr, 1280u, 720u, nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL out превью отвергается");
    check(azurpilot_native_frame_encode_jpeg(nullptr, 82u, nullptr, 0u, nullptr, 0u, nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL written_size отвергается");
    uint32_t written = 99;
    check(azurpilot_native_frame_encode_jpeg(nullptr, 82u, nullptr, 0u, nullptr, 0u, &written) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
        && written == 0u, "NULL кадр JPEG отвергается и обнуляет размер");

    // Красная левая половина, зелёная правая верхняя и синяя правая нижняя четверти 4K кадра.
    cv::Mat source(2160, 3840, CV_8UC3, cv::Scalar(255, 0, 0));
    source(cv::Rect(1920, 0, 1920, 1080)).setTo(cv::Scalar(0, 255, 0));
    source(cv::Rect(1920, 1080, 1920, 1080)).setTo(cv::Scalar(0, 0, 255));
    AzurPilotNativeFrameHandle frame = decode(source);
    for (const auto& size : std::vector<std::pair<uint32_t, uint32_t>>{{1280u, 721u}, {3841u, 2161u}, {2560u, 1440u}, {0u, 0u}}) {
        AzurPilotNativeFrameHandle rejected = nullptr;
        check(azurpilot_native_frame_create_preview(frame, size.first, size.second, &rejected) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
            && rejected == nullptr, "Неверные размеры/пропорции/увеличение превью отвергаются без подгонки");
    }
    check(azurpilot_native_frame_encode_jpeg(frame, 82u, nullptr, 0u, nullptr, 0u, &written) == AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE,
        "Полный 4K кадр не экспортируется JPEG-границей");
    check(azurpilot_native_frame_create_preview(frame, 1280u, 720u, &preview) == AZURPILOT_NATIVE_OK && preview != nullptr,
        "Превью логического размера создаётся из 4K");
    check(azurpilot_native_frame_release(frame) == AZURPILOT_NATIVE_OK, "Исходный кадр освобождается до кодирования превью");
    AzurPilotNativeFrameInfo info{};
    check(azurpilot_native_frame_get_info(preview, &info) == AZURPILOT_NATIVE_OK && info.width == 1280u && info.height == 720u
        && info.stride_bytes == 3840u && info.pixel_format == AZURPILOT_NATIVE_PIXEL_FORMAT_RGB8, "Метаданные превью RGB8 логического размера");

    uint32_t required = 0;
    check(azurpilot_native_frame_encode_jpeg(preview, 82u, nullptr, 0u, nullptr, 0u, &required) == AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL
        && required > 0u, "Размер-запрос JPEG сообщает требуемый размер");
    std::vector<uint8_t> small(required - 1u, 0xA5u);
    uint32_t small_written = 0;
    check(azurpilot_native_frame_encode_jpeg(preview, 82u, nullptr, 0u, small.data(), static_cast<uint32_t>(small.size()), &small_written)
        == AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL && small_written == required
        && std::all_of(small.begin(), small.end(), [](uint8_t b) { return b == 0xA5u; }), "Малый буфер не изменяется");
    check(azurpilot_native_frame_encode_jpeg(preview, 82u, nullptr, 1u, nullptr, 0u, &written) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL annotations при ненулевом count отвергается");
    check(azurpilot_native_frame_encode_jpeg(preview, 49u, nullptr, 0u, nullptr, 0u, &written) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
        && azurpilot_native_frame_encode_jpeg(preview, 96u, nullptr, 0u, nullptr, 0u, &written) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Качество вне 50..95 отвергается");

    int32_t status = -1;
    const std::vector<uint8_t> plain = encode(preview, {}, &status);
    check(status == AZURPILOT_NATIVE_OK && plain.size() == required && plain.size() > 4u
        && plain[0] == 0xFFu && plain[1] == 0xD8u && plain[plain.size() - 2u] == 0xFFu && plain.back() == 0xD9u,
        "JPEG ограничен маркерами SOI/EOI");
    const std::string text(plain.begin(), plain.end());
    check(text.find("Exif") == std::string::npos && text.find("ICC_PROFILE") == std::string::npos, "JPEG без EXIF/ICC metadata");
    const cv::Mat decoded = cv::imdecode(plain, cv::IMREAD_COLOR);
    check(decoded.cols == 1280 && decoded.rows == 720, "Независимый декодер видит логический размер превью");
    const cv::Vec3b red = pixel(decoded, 320, 360);
    const cv::Vec3b green = pixel(decoded, 960, 180);
    const cv::Vec3b blue = pixel(decoded, 960, 540);
    check(red[2] > 220 && red[1] < 40 && red[0] < 40, "Красный RGB остаётся красным (порядок каналов)");
    check(green[1] > 220 && green[2] < 40 && green[0] < 40, "Зелёная четверть сохраняет положение");
    check(blue[0] > 220 && blue[2] < 40 && blue[1] < 40, "Синяя четверть сохраняет положение");
    const cv::Vec3b left_edge = pixel(decoded, 636, 360);
    const cv::Vec3b right_edge = pixel(decoded, 644, 180);
    check(left_edge[2] > 200 && right_edge[1] > 200, "Граница 1920 физических пикселей переходит в 640 превью");

    const std::vector<uint8_t> annotated = encode(preview, {
        annotation(AZURPILOT_NATIVE_ANNOTATION_FILLED_RECT, {100, 100, 200, 200}),
        annotation(AZURPILOT_NATIVE_ANNOTATION_TEXT, {10, 700, 50, 0}, "tap issued #1"),
        annotation(AZURPILOT_NATIVE_ANNOTATION_CROSS, {900, 300, 12, 0}),
        annotation(AZURPILOT_NATIVE_ANNOTATION_FILLED_CIRCLE, {1000, 600, 9, 0}),
    }, &status);
    const cv::Mat with_box = cv::imdecode(annotated, cv::IMREAD_COLOR);
    const cv::Vec3b box = pixel(with_box, 150, 150);
    const cv::Vec3b outside = pixel(with_box, 250, 150);
    check(status == AZURPILOT_NATIVE_OK && box[0] > 220 && box[1] > 220 && box[2] > 220
        && outside[2] > 220 && outside[1] < 40, "Аннотация рисуется в ожидаемой области превью");
    const cv::Mat again = cv::imdecode(encode(preview, {}), cv::IMREAD_COLOR);
    const cv::Vec3b clean = pixel(again, 150, 150);
    check(clean[2] > 220 && clean[1] < 40, "Аннотации не изменяют пиксели превью");

    AzurPilotNativeAnnotation dashed = annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {10, 10, 50, 50});
    dashed.style = AZURPILOT_NATIVE_ANNOTATION_DASHED;
    check(encode_status(preview, dashed) == AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL, "Штриховой прямоугольник допустим");
    AzurPilotNativeAnnotation invalid = annotation(AZURPILOT_NATIVE_ANNOTATION_CIRCLE, {10, 10, 5, 0});
    invalid.style = AZURPILOT_NATIVE_ANNOTATION_DASHED;
    check(encode_status(preview, invalid) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Штрих допустим только для RECT");
    check(encode_status(preview, annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {50, 10, 50, 20})) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Пустой прямоугольник отвергается");
    check(encode_status(preview, annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {0, 0, 10, 10}, "x")) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Текст у прямоугольника отвергается");
    check(encode_status(preview, annotation(AZURPILOT_NATIVE_ANNOTATION_TEXT, {0, 20, 50, 0}, "\xD0\xB0")) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Не-ASCII текст отвергается");
    AzurPilotNativeAnnotation unterminated = annotation(AZURPILOT_NATIVE_ANNOTATION_TEXT, {0, 20, 50, 0}, "a");
    std::memset(unterminated.text, 'a', sizeof(unterminated.text));
    check(encode_status(preview, unterminated) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Текст без NUL отвергается");
    AzurPilotNativeAnnotation thin = annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {0, 0, 10, 10});
    thin.thickness = 0;
    check(encode_status(preview, thin) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Нулевая толщина отвергается");
    check(encode_status(preview, annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {0, 0, 16385, 10})) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Координаты вне предела отвергаются");
    check(encode_status(preview, annotation(7u, {0, 0, 10, 10})) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Неизвестный вид отвергается");
    std::vector<AzurPilotNativeAnnotation> many(AZURPILOT_NATIVE_MAX_ANNOTATIONS + 1u,
        annotation(AZURPILOT_NATIVE_ANNOTATION_RECT, {0, 0, 10, 10}));
    check(azurpilot_native_frame_encode_jpeg(preview, 82u, many.data(), static_cast<uint32_t>(many.size()), nullptr, 0u, &written)
        == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Число аннотаций ограничено");
    check(azurpilot_native_frame_release(preview) == AZURPILOT_NATIVE_OK, "Превью освобождается");
    std::cout << "Проверки native превью завершены; ошибок: " << failures << '\n';
    return failures == 0 ? 0 : 1;
}
