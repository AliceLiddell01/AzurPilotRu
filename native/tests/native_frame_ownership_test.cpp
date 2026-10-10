/* Внутренняя проверка декодера RGB8 и срока жизни кадра; экспорты C ABI отдельно проверяет native_abi_smoke. */

#include "native_frame.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

namespace {

int g_checks = 0;
int g_failures = 0;

void check(bool condition, const char* description) {
    ++g_checks;
    if (condition) {
        std::cout << "  [OK]   " << description << '\n';
    } else {
        ++g_failures;
        std::cout << "  [FAIL] " << description << '\n';
    }
}

bool read_fixture(const char* name, std::vector<std::uint8_t>& bytes) {
    const std::string path = std::string("tests/fixtures/") + name;
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        std::cout << "  [FAIL] Не удалось открыть тестовый файл " << path << '\n';
        ++g_failures;
        ++g_checks;
        return false;
    }
    bytes.assign(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
    return true;
}

bool snapshot_matches(azurpilot::native::detail::FrameOwnershipSnapshot expected) {
    const auto actual = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    return actual.live_frames == expected.live_frames
        && actual.live_rgb_bytes == expected.live_rgb_bytes;
}

void expect_failure(
    const std::uint8_t* bytes,
    std::uint32_t size,
    std::int32_t expected_status,
    const char* description) {
    const auto baseline = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    auto* frame = reinterpret_cast<AzurPilotNativeFrameOpaque*>(static_cast<std::uintptr_t>(1u));
    const std::int32_t status =
        azurpilot::native::detail::decode_png_frame(bytes, size, &frame);
    check(status == expected_status, description);
    check(frame == nullptr, "Отказ декодирования не возвращает пригодный дескриптор кадра");
    check(snapshot_matches(baseline), "Отказ декодирования не оставляет активных кадров или выделенной RGB-памяти");
}

void test_rgb_and_opaque_rgba() {
    std::vector<std::uint8_t> rgb_bytes;
    if (!read_fixture("rgb_red_blue.png", rgb_bytes)) {
        return;
    }

    const auto baseline = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    AzurPilotNativeFrameOpaque* frame = nullptr;
    const std::int32_t status = azurpilot::native::detail::decode_png_frame(
        rgb_bytes.data(), static_cast<std::uint32_t>(rgb_bytes.size()), &frame);
    check(status == AZURPILOT_NATIVE_OK && frame != nullptr,
          "Настоящий RGB PNG декодируется из памяти во внутренний кадр");
    if (frame == nullptr) {
        return;
    }

    check(frame->width == 2u && frame->height == 1u,
          "Метаданные RGB-кадра используют размеры тестового PNG");
    check(frame->stride_bytes == 6u && frame->byte_length == 6u,
          "Непрерывный RGB-кадр имеет шаг строки width*3 и точную длину данных");
    const std::uint8_t* pixels = frame->rgb.ptr<std::uint8_t>(0);
    check(pixels[0] == 255u && pixels[1] == 0u && pixels[2] == 0u,
          "Первый пиксель тестового PNG остаётся красным в RGB, без перестановки BGR");
    check(pixels[3] == 0u && pixels[4] == 0u && pixels[5] == 255u,
          "Второй пиксель тестового PNG остаётся синим в RGB, без перестановки BGR");

    std::fill(rgb_bytes.begin(), rgb_bytes.end(), 0u);
    check(
        frame->rgb.ptr<std::uint8_t>(0)[0] == 255u
            && frame->rgb.ptr<std::uint8_t>(0)[5] == 255u,
        "Кадр не удерживает и не читает байты PNG вызывающей стороны после декодирования");

    AzurPilotNativeFrameInfo info{};
    check(
        azurpilot::native::detail::get_frame_info(frame, &info) == AZURPILOT_NATIVE_OK
            && info.width == 2u && info.height == 1u
            && info.stride_bytes == 6u && info.byte_length == 6u
            && info.pixel_format == AZURPILOT_NATIVE_PIXEL_FORMAT_RGB8,
        "Метаданные кадра согласованы с буфером RGB8");

    delete frame;
    check(snapshot_matches(baseline), "Освобождение RGB-кадра возвращает учёт памяти к исходному уровню");

    std::vector<std::uint8_t> rgba_bytes;
    if (!read_fixture("rgba_opaque.png", rgba_bytes)) {
        return;
    }
    frame = nullptr;
    const std::int32_t rgba_status = azurpilot::native::detail::decode_png_frame(
        rgba_bytes.data(), static_cast<std::uint32_t>(rgba_bytes.size()), &frame);
    check(rgba_status == AZURPILOT_NATIVE_OK && frame != nullptr,
          "Полностью непрозрачный RGBA PNG преобразуется в RGB8");
    if (frame != nullptr) {
        const std::uint8_t* rgba_pixel = frame->rgb.ptr<std::uint8_t>(0);
        check(
            rgba_pixel[0] == 23u && rgba_pixel[1] == 97u && rgba_pixel[2] == 201u,
            "Тестовый непрозрачный RGBA PNG сохраняет порядок каналов RGB");
        delete frame;
    }
    check(snapshot_matches(baseline), "После освобождения RGB- и RGBA-кадров нет активных выделений памяти");
}

void test_rejected_inputs() {
    const std::array<std::uint8_t, 4> non_png{0u, 1u, 2u, 3u};
    expect_failure(
        non_png.data(), static_cast<std::uint32_t>(non_png.size()),
        AZURPILOT_NATIVE_ERROR_INVALID_PNG, "Вход, не являющийся PNG, получает INVALID_PNG");

    const std::array<std::uint8_t, 1> empty_input_storage{0u};
    expect_failure(
        empty_input_storage.data(), 0u,
        AZURPILOT_NATIVE_ERROR_INVALID_PNG, "Вход нулевой длины получает INVALID_PNG");

    std::vector<std::uint8_t> invalid_chunk_type;
    if (read_fixture("invalid_chunk_type.png", invalid_chunk_type)) {
        expect_failure(
            invalid_chunk_type.data(), static_cast<std::uint32_t>(invalid_chunk_type.size()),
            AZURPILOT_NATIVE_ERROR_INVALID_PNG,
            "PNG с некорректным зарезервированным битом типа блока получает INVALID_PNG");
    }

    std::vector<std::uint8_t> truncated;
    if (read_fixture("rgb_red_blue.png", truncated)) {
        truncated.resize(truncated.size() - 4u);
        expect_failure(
            truncated.data(), static_cast<std::uint32_t>(truncated.size()),
            AZURPILOT_NATIVE_ERROR_INVALID_PNG, "Усечённый PNG получает INVALID_PNG");

        std::vector<std::uint8_t> corrupted;
        if (read_fixture("rgb_red_blue.png", corrupted)) {
            corrupted[41u] ^= 1u;
            expect_failure(
                corrupted.data(), static_cast<std::uint32_t>(corrupted.size()),
                AZURPILOT_NATIVE_ERROR_INVALID_PNG, "PNG с повреждённым CRC получает INVALID_PNG");
        }

        std::vector<std::uint8_t> corrupt_idat;
        if (read_fixture("corrupt_idat_checksum.png", corrupt_idat)) {
            expect_failure(
                corrupt_idat.data(), static_cast<std::uint32_t>(corrupt_idat.size()),
                AZURPILOT_NATIVE_ERROR_INVALID_PNG,
                "PNG с корректной структурой/CRC и повреждённым IDAT получает INVALID_PNG");
        }
    }

    const std::array<const char*, 6> unsupported_names{
        "rgba_nonopaque.png",
        "unsupported_grayscale.png",
        "unsupported_palette.png",
        "unsupported_16bit.png",
        "unsupported_interlaced.png",
        "unsupported_apng.png"};
    for (const char* name : unsupported_names) {
        std::vector<std::uint8_t> bytes;
        if (read_fixture(name, bytes)) {
            expect_failure(
                bytes.data(), static_cast<std::uint32_t>(bytes.size()),
                AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG,
                "Неподдерживаемая форма PNG или политика обработки прозрачности получает UNSUPPORTED_PNG");
        }
    }

    std::vector<std::uint8_t> trns;
    if (read_fixture("unsupported_trns.png", trns)) {
        expect_failure(
            trns.data(), static_cast<std::uint32_t>(trns.size()),
            AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG,
            "PNG с прозрачностью tRNS получает UNSUPPORTED_PNG");
    }

    std::vector<std::uint8_t> oversized_dimensions;
    if (read_fixture("oversized_dimensions.png", oversized_dimensions)) {
        expect_failure(
            oversized_dimensions.data(),
            static_cast<std::uint32_t>(oversized_dimensions.size()),
            AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE,
            "Размеры IHDR выше пределов отклоняются до выделения памяти OpenCV");
    }

    std::vector<std::uint8_t> oversized_encoded(
        static_cast<std::size_t>(AZURPILOT_NATIVE_MAX_PNG_BYTES) + 1u, 0u);
    expect_failure(
        oversized_encoded.data(),
        static_cast<std::uint32_t>(oversized_encoded.size()),
        AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE,
        "PNG выше лимита размера получает IMAGE_TOO_LARGE до разбора структуры");
}

void test_argument_and_repeated_ownership() {
    const auto baseline = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    auto* frame = reinterpret_cast<AzurPilotNativeFrameOpaque*>(static_cast<std::uintptr_t>(1u));
    check(
        azurpilot::native::detail::decode_png_frame(nullptr, 1u, &frame)
            == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
            && frame == nullptr,
        "При NULL-указателе на PNG выходной дескриптор обнуляется");

    check(
        azurpilot::native::detail::decode_png_frame(
            reinterpret_cast<const std::uint8_t*>("x"), 1u, nullptr)
            == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL-указатель на выходной дескриптор получает INVALID_ARGUMENT");

    AzurPilotNativeFrameInfo dirty_info{};
    std::memset(&dirty_info, 0xA5, sizeof(dirty_info));
    check(
        azurpilot::native::detail::get_frame_info(nullptr, &dirty_info)
            == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL-дескриптор кадра получает INVALID_ARGUMENT");
    check(
        dirty_info.width == 0u && dirty_info.height == 0u
            && dirty_info.stride_bytes == 0u && dirty_info.byte_length == 0u
            && dirty_info.pixel_format == 0u,
        "Ошибка get_frame_info обнуляет всю выходную структуру метаданных");

    std::vector<std::uint8_t> rgb;
    if (!read_fixture("rgb_red_blue.png", rgb)) {
        return;
    }
    constexpr std::uint32_t frame_count = 64u;
    std::array<AzurPilotNativeFrameOpaque*, frame_count> frames{};
    std::uint32_t created_count = 0u;
    for (std::uint32_t index = 0u; index < frame_count; ++index) {
        const std::int32_t status = azurpilot::native::detail::decode_png_frame(
            rgb.data(), static_cast<std::uint32_t>(rgb.size()), &frames[index]);
        if (status == AZURPILOT_NATIVE_OK && frames[index] != nullptr) {
            ++created_count;
        } else {
            ++g_failures;
        }
    }
    check(
        created_count == frame_count,
        "Несколько независимых кадров native DLL создаются одновременно");
    const auto live = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    check(
        live.live_frames == baseline.live_frames + frame_count
            && live.live_rgb_bytes == baseline.live_rgb_bytes + (frame_count * 6u),
        "Счётчики активных кадров и пиксельных данных соответствуют числу кадров во владении DLL");

    for (std::uint32_t index = 0u; index < frame_count; ++index) {
        delete frames[index];
    }
    check(snapshot_matches(baseline), "Освобождение всех кадров возвращает счётчики к исходному уровню");

    for (std::uint32_t index = 0u; index < 1000u; ++index) {
        frame = nullptr;
        const std::int32_t status = azurpilot::native::detail::decode_png_frame(
            rgb.data(), static_cast<std::uint32_t>(rgb.size()), &frame);
        if (status == AZURPILOT_NATIVE_OK) {
            delete frame;
        } else {
            ++g_failures;
        }
    }
    check(snapshot_matches(baseline), "1000 циклов создания и освобождения не оставляют активных кадров и байтов RGB");
}

}  // namespace

int main() {
    std::cout << "Проверка декодирования PNG-кадров и управления владением\n";
    test_rgb_and_opaque_rgba();
    test_rejected_inputs();
    test_argument_and_repeated_ownership();

    const auto final_snapshot = azurpilot::native::detail::frame_ownership_snapshot_for_test();
    check(final_snapshot.live_frames == 0u && final_snapshot.live_rgb_bytes == 0u,
          "Итоговый снимок активных выделений памяти равен нулю");

    std::cout << "Итог: проверок " << g_checks << ", провалено " << g_failures << '\n';
    return g_failures == 0 ? 0 : 1;
}
