/* Внутренняя PNG-specific реализация frame decode и lifetime. */

#include "native_frame.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <utility>

namespace {

constexpr std::array<std::uint8_t, 8> kPngSignature{
    0x89u, 0x50u, 0x4Eu, 0x47u, 0x0Du, 0x0Au, 0x1Au, 0x0Au};

enum class PngPreflightStatus {
    Ok,
    Invalid,
    Unsupported,
    TooLarge,
};

struct PngHeader final {
    std::uint32_t width = 0u;
    std::uint32_t height = 0u;
    std::uint8_t color_type = 0u;
};

constexpr std::array<std::uint32_t, 256> make_png_crc_table() noexcept {
    std::array<std::uint32_t, 256> table{};
    for (std::uint32_t index = 0u; index < table.size(); ++index) {
        std::uint32_t crc = index;
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 1u) != 0u ? (crc >> 1u) ^ 0xEDB88320u : crc >> 1u;
        }
        table[index] = crc;
    }
    return table;
}

constexpr auto kPngCrcTable = make_png_crc_table();

std::uint32_t read_big_endian_u32(const std::uint8_t* bytes) noexcept {
    return (static_cast<std::uint32_t>(bytes[0]) << 24u)
        | (static_cast<std::uint32_t>(bytes[1]) << 16u)
        | (static_cast<std::uint32_t>(bytes[2]) << 8u)
        | static_cast<std::uint32_t>(bytes[3]);
}

std::uint32_t png_crc32(const std::uint8_t* bytes, std::size_t length) noexcept {
    std::uint32_t crc = 0xFFFFFFFFu;
    for (std::size_t index = 0u; index < length; ++index) {
        const std::uint8_t table_index =
            static_cast<std::uint8_t>((crc ^ bytes[index]) & 0xFFu);
        crc = (crc >> 8u) ^ kPngCrcTable[table_index];
    }
    return crc ^ 0xFFFFFFFFu;
}

bool chunk_is(const std::uint8_t* type, const char (&expected)[5]) noexcept {
    return std::memcmp(type, expected, 4u) == 0;
}

bool is_valid_chunk_type(const std::uint8_t* type) noexcept {
    for (std::size_t index = 0u; index < 4u; ++index) {
        const bool uppercase = type[index] >= 'A' && type[index] <= 'Z';
        const bool lowercase = type[index] >= 'a' && type[index] <= 'z';
        if (!uppercase && !lowercase) {
            return false;
        }
        if (index == 2u && !uppercase) {
            return false;
        }
    }
    return true;
}

bool is_known_critical_chunk(const std::uint8_t* type) noexcept {
    return chunk_is(type, "IHDR") || chunk_is(type, "PLTE")
        || chunk_is(type, "IDAT") || chunk_is(type, "IEND");
}

bool decoded_size_is_bounded(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t& stride_bytes,
    std::uint32_t& byte_length) noexcept {
    if (width == 0u || height == 0u
        || width > AZURPILOT_NATIVE_MAX_FRAME_DIMENSION
        || height > AZURPILOT_NATIVE_MAX_FRAME_DIMENSION) {
        return false;
    }

    const std::uint64_t pixels = static_cast<std::uint64_t>(width) * height;
    if (pixels > AZURPILOT_NATIVE_MAX_FRAME_PIXELS) {
        return false;
    }

    const std::uint64_t stride = static_cast<std::uint64_t>(width) * 3u;
    const std::uint64_t bytes = stride * height;
    if (stride > UINT32_MAX || bytes > UINT32_MAX
        || bytes > AZURPILOT_NATIVE_MAX_RGB8_BYTES) {
        return false;
    }

    stride_bytes = static_cast<std::uint32_t>(stride);
    byte_length = static_cast<std::uint32_t>(bytes);
    return true;
}

PngPreflightStatus inspect_png(
    const std::uint8_t* bytes,
    std::uint32_t size,
    PngHeader& header) noexcept {
    if (size == 0u || size < kPngSignature.size()
        || std::memcmp(bytes, kPngSignature.data(), kPngSignature.size()) != 0) {
        return PngPreflightStatus::Invalid;
    }

    std::size_t offset = kPngSignature.size();
    bool saw_header = false;
    bool saw_palette = false;
    bool saw_image_data = false;
    bool image_data_ended = false;
    bool saw_end = false;

    while (offset < size) {
        const std::size_t remaining = static_cast<std::size_t>(size) - offset;
        if (remaining < 12u) {
            return PngPreflightStatus::Invalid;
        }

        const std::uint32_t chunk_size = read_big_endian_u32(bytes + offset);
        if (static_cast<std::size_t>(chunk_size) > remaining - 12u) {
            return PngPreflightStatus::Invalid;
        }

        const std::uint8_t* type = bytes + offset + 4u;
        const std::uint8_t* chunk_data = bytes + offset + 8u;
        if (!is_valid_chunk_type(type)) {
            return PngPreflightStatus::Invalid;
        }
        const std::uint32_t expected_crc =
            read_big_endian_u32(chunk_data + static_cast<std::size_t>(chunk_size));
        const std::uint32_t actual_crc =
            png_crc32(type, static_cast<std::size_t>(chunk_size) + 4u);
        if (actual_crc != expected_crc) {
            return PngPreflightStatus::Invalid;
        }

        if (!saw_header) {
            if (!chunk_is(type, "IHDR") || chunk_size != 13u) {
                return PngPreflightStatus::Invalid;
            }

            header.width = read_big_endian_u32(chunk_data);
            header.height = read_big_endian_u32(chunk_data + 4u);
            header.color_type = chunk_data[9u];
            saw_header = true;

            std::uint32_t ignored_stride = 0u;
            std::uint32_t ignored_byte_length = 0u;
            if (header.width == 0u || header.height == 0u) {
                return PngPreflightStatus::Invalid;
            }
            if (!decoded_size_is_bounded(
                    header.width, header.height, ignored_stride, ignored_byte_length)) {
                return PngPreflightStatus::TooLarge;
            }

            if (chunk_data[8u] != 8u
                || (header.color_type != 2u && header.color_type != 6u)
                || chunk_data[10u] != 0u || chunk_data[11u] != 0u) {
                return PngPreflightStatus::Unsupported;
            }
            if (chunk_data[12u] != 0u) {
                return PngPreflightStatus::Unsupported;
            }
        } else if (chunk_is(type, "IHDR")) {
            return PngPreflightStatus::Invalid;
        } else if (chunk_is(type, "PLTE")) {
            if (saw_image_data || saw_palette || chunk_size == 0u
                || chunk_size > 768u || (chunk_size % 3u) != 0u) {
                return PngPreflightStatus::Invalid;
            }
            saw_palette = true;
        } else if (chunk_is(type, "IDAT")) {
            if (image_data_ended) {
                return PngPreflightStatus::Invalid;
            }
            saw_image_data = true;
        } else if (chunk_is(type, "IEND")) {
            if (chunk_size != 0u || !saw_image_data) {
                return PngPreflightStatus::Invalid;
            }
            offset += 12u;
            if (offset != size) {
                return PngPreflightStatus::Invalid;
            }
            saw_end = true;
            break;
        } else {
            if (saw_image_data) {
                image_data_ended = true;
            }
            if (chunk_is(type, "tRNS") || chunk_is(type, "acTL")
                || chunk_is(type, "fcTL") || chunk_is(type, "fdAT")) {
                return PngPreflightStatus::Unsupported;
            }
            if ((type[0] & 0x20u) == 0u && !is_known_critical_chunk(type)) {
                return PngPreflightStatus::Unsupported;
            }
        }

        offset += 12u + static_cast<std::size_t>(chunk_size);
    }

    if (!saw_header || !saw_image_data || !saw_end) {
        return PngPreflightStatus::Invalid;
    }
    return PngPreflightStatus::Ok;
}

std::int32_t preflight_status(PngPreflightStatus status) noexcept {
    switch (status) {
    case PngPreflightStatus::Ok:
        return AZURPILOT_NATIVE_OK;
    case PngPreflightStatus::Invalid:
        return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
    case PngPreflightStatus::Unsupported:
        return AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG;
    case PngPreflightStatus::TooLarge:
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }
    return AZURPILOT_NATIVE_ERROR_INTERNAL;
}

#if defined(AZURPILOT_NATIVE_TESTING)
std::atomic<std::uint64_t> g_live_frames{0u};
std::atomic<std::uint64_t> g_live_rgb_bytes{0u};
#endif

}  // namespace

AzurPilotNativeFrameOpaque::AzurPilotNativeFrameOpaque(
    cv::Mat rgb_pixels,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    std::uint32_t row_stride,
    std::uint32_t payload_size)
    : rgb(std::move(rgb_pixels)),
      width(frame_width),
      height(frame_height),
      stride_bytes(row_stride),
      byte_length(payload_size) {
#if defined(AZURPILOT_NATIVE_TESTING)
    g_live_frames.fetch_add(1u, std::memory_order_relaxed);
    g_live_rgb_bytes.fetch_add(byte_length, std::memory_order_relaxed);
#endif
}

AzurPilotNativeFrameOpaque::~AzurPilotNativeFrameOpaque() noexcept {
    rgb.release();
#if defined(AZURPILOT_NATIVE_TESTING)
    g_live_rgb_bytes.fetch_sub(byte_length, std::memory_order_relaxed);
    g_live_frames.fetch_sub(1u, std::memory_order_relaxed);
#endif
}

namespace azurpilot::native::detail {

std::int32_t decode_png_frame(
    const std::uint8_t* png_bytes,
    std::uint32_t png_size,
    AzurPilotNativeFrameOpaque** out_frame) {
    if (out_frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;

    if (png_bytes == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    if (png_size > AZURPILOT_NATIVE_MAX_PNG_BYTES) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }

    PngHeader png_header{};
    const std::int32_t validation_status =
        preflight_status(inspect_png(png_bytes, png_size, png_header));
    if (validation_status != AZURPILOT_NATIVE_OK) {
        return validation_status;
    }

    std::uint32_t expected_stride = 0u;
    std::uint32_t expected_byte_length = 0u;
    if (!decoded_size_is_bounded(
            png_header.width, png_header.height, expected_stride, expected_byte_length)) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }

    // imdecode читает синхронно из caller memory и не сохраняет этот временный Mat header.
    cv::Mat encoded(
        1,
        static_cast<int>(png_size),
        CV_8UC1,
        const_cast<std::uint8_t*>(png_bytes));
    cv::Mat decoded;
    cv::Mat rgb;

    if (png_header.color_type == 2u) {
        decoded = cv::imdecode(encoded, cv::IMREAD_COLOR_RGB);
        if (decoded.empty()) {
            return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
        }
        if (decoded.type() != CV_8UC3) {
            return AZURPILOT_NATIVE_ERROR_INTERNAL;
        }
        rgb = std::move(decoded);
    } else {
        decoded = cv::imdecode(encoded, cv::IMREAD_UNCHANGED);
        if (decoded.empty()) {
            return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
        }
        if (decoded.type() != CV_8UC4) {
            return AZURPILOT_NATIVE_ERROR_INTERNAL;
        }

        for (int row_index = 0; row_index < decoded.rows; ++row_index) {
            const std::uint8_t* row = decoded.ptr<std::uint8_t>(row_index);
            for (int column = 0; column < decoded.cols; ++column) {
                if (row[(static_cast<std::size_t>(column) * 4u) + 3u] != 255u) {
                    return AZURPILOT_NATIVE_ERROR_UNSUPPORTED_PNG;
                }
            }
        }

        // OpenCV PNG decode отдаёт RGBA как BGRA; normalize в канонический RGB8.
        cv::cvtColor(decoded, rgb, cv::COLOR_BGRA2RGB);
    }

    if (rgb.type() != CV_8UC3
        || rgb.cols != static_cast<int>(png_header.width)
        || rgb.rows != static_cast<int>(png_header.height)
        || !rgb.isContinuous()
        || rgb.step[0] != expected_stride
        || rgb.total() * rgb.elemSize() != expected_byte_length) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }

    auto frame = std::make_unique<AzurPilotNativeFrameOpaque>(
        std::move(rgb),
        png_header.width,
        png_header.height,
        expected_stride,
        expected_byte_length);
    *out_frame = frame.release();
    return AZURPILOT_NATIVE_OK;
}

std::int32_t get_frame_info(
    AzurPilotNativeFrameHandle frame,
    AzurPilotNativeFrameInfo* out_info) noexcept {
    if (out_info == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    std::memset(out_info, 0, sizeof(*out_info));
    if (frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    std::uint32_t expected_stride = 0u;
    std::uint32_t expected_byte_length = 0u;
    if (frame->rgb.empty()
        || frame->rgb.type() != CV_8UC3
        || frame->rgb.cols <= 0
        || frame->rgb.rows <= 0
        || static_cast<std::uint32_t>(frame->rgb.cols) != frame->width
        || static_cast<std::uint32_t>(frame->rgb.rows) != frame->height
        || !frame->rgb.isContinuous()
        || !decoded_size_is_bounded(
            frame->width, frame->height, expected_stride, expected_byte_length)
        || frame->stride_bytes != expected_stride
        || frame->byte_length != expected_byte_length
        || frame->rgb.step[0] != frame->stride_bytes
        || frame->rgb.total() * frame->rgb.elemSize() != frame->byte_length) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }

    const AzurPilotNativeFrameInfo info{
        frame->width,
        frame->height,
        frame->stride_bytes,
        frame->byte_length,
        AZURPILOT_NATIVE_PIXEL_FORMAT_RGB8};
    *out_info = info;
    return AZURPILOT_NATIVE_OK;
}

#if defined(AZURPILOT_NATIVE_TESTING)
FrameOwnershipSnapshot frame_ownership_snapshot_for_test() noexcept {
    return {
        g_live_frames.load(std::memory_order_relaxed),
        g_live_rgb_bytes.load(std::memory_order_relaxed),
    };
}
#endif

}  // namespace azurpilot::native::detail
