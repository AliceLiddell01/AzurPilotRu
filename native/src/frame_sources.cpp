/* Источники кадров захвата ABI v6: сырые дисплейные пиксели, aScreenCap BMZ1/LZ4 и JPEG.
 * Каждый источник даёт тот же непрерывный RGB8-кадр DLL, что и PNG-декодер. */

#include "native_frame.h"

#include <opencv2/core/utility.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace {

std::uint32_t bytes_per_pixel(std::uint32_t format) noexcept {
    switch (format) {
    case AZURPILOT_NATIVE_SOURCE_RGBA8888:
    case AZURPILOT_NATIVE_SOURCE_BGRA8888:
        return 4u;
    case AZURPILOT_NATIVE_SOURCE_RGB565_LE:
        return 2u;
    case AZURPILOT_NATIVE_SOURCE_RGB888:
    case AZURPILOT_NATIVE_SOURCE_BGR888:
        return 3u;
    default:
        return 0u;
    }
}

std::uint32_t read_u32_le(const std::uint8_t* bytes) noexcept {
    return static_cast<std::uint32_t>(bytes[0]) | (static_cast<std::uint32_t>(bytes[1]) << 8u)
        | (static_cast<std::uint32_t>(bytes[2]) << 16u) | (static_cast<std::uint32_t>(bytes[3]) << 24u);
}

std::uint32_t byte_swap(std::uint32_t value) noexcept {
    return ((value & 0x000000FFu) << 24u) | ((value & 0x0000FF00u) << 8u)
        | ((value & 0x00FF0000u) >> 8u) | ((value & 0xFF000000u) >> 24u);
}

// Строгая распаковка блока LZ4: вход и выход должны быть исчерпаны ровно, ссылки только назад.
bool lz4_decompress_block(const std::uint8_t* source, std::size_t source_size,
                          std::uint8_t* destination, std::size_t destination_size) noexcept {
    std::size_t input = 0u;
    std::size_t output = 0u;
    while (input < source_size) {
        const std::uint8_t token = source[input++];
        std::size_t literal_length = token >> 4u;
        if (literal_length == 15u) {
            std::uint8_t extra = 255u;
            while (extra == 255u) {
                if (input >= source_size) {
                    return false;
                }
                extra = source[input++];
                literal_length += extra;
                if (literal_length > destination_size) {
                    return false;
                }
            }
        }
        if (literal_length > source_size - input || literal_length > destination_size - output) {
            return false;
        }
        std::memcpy(destination + output, source + input, literal_length);
        input += literal_length;
        output += literal_length;
        if (input == source_size) {
            // Последняя последовательность состоит только из литералов.
            break;
        }

        if (source_size - input < 2u) {
            return false;
        }
        const std::size_t offset = static_cast<std::size_t>(source[input])
            | (static_cast<std::size_t>(source[input + 1u]) << 8u);
        input += 2u;
        if (offset == 0u || offset > output) {
            return false;
        }

        std::size_t match_length = token & 0x0Fu;
        if (match_length == 15u) {
            std::uint8_t extra = 255u;
            while (extra == 255u) {
                if (input >= source_size) {
                    return false;
                }
                extra = source[input++];
                match_length += extra;
                if (match_length > destination_size) {
                    return false;
                }
            }
        }
        match_length += 4u;
        if (match_length > destination_size - output) {
            return false;
        }
        // Перекрывающееся копирование допустимо и выполняется побайтно.
        for (std::size_t index = 0u; index < match_length; ++index) {
            destination[output + index] = destination[output - offset + index];
        }
        output += match_length;
    }
    return input == source_size && output == destination_size;
}

// Размеры из первого маркера SOF; true только при корректной цепочке сегментов до SOF.
bool read_jpeg_dimensions(const std::uint8_t* bytes, std::uint32_t size,
                          std::uint32_t& width, std::uint32_t& height) noexcept {
    if (size < 4u || bytes[0] != 0xFFu || bytes[1] != 0xD8u
        || bytes[size - 2u] != 0xFFu || bytes[size - 1u] != 0xD9u) {
        return false;
    }
    std::size_t offset = 2u;
    while (offset + 4u <= size) {
        if (bytes[offset] != 0xFFu) {
            return false;
        }
        const std::uint8_t marker = bytes[offset + 1u];
        if (marker == 0xFFu) {
            ++offset;
            continue;
        }
        if (marker == 0xD8u || marker == 0xD9u || (marker >= 0xD0u && marker <= 0xD7u) || marker == 0x01u) {
            return false;
        }
        const std::size_t length = (static_cast<std::size_t>(bytes[offset + 2u]) << 8u) | bytes[offset + 3u];
        if (length < 2u || offset + 2u + length > size) {
            return false;
        }
        const bool start_of_frame = marker >= 0xC0u && marker <= 0xCFu
            && marker != 0xC4u && marker != 0xC8u && marker != 0xCCu;
        if (start_of_frame) {
            if (length < 8u) {
                return false;
            }
            height = (static_cast<std::uint32_t>(bytes[offset + 5u]) << 8u) | bytes[offset + 6u];
            width = (static_cast<std::uint32_t>(bytes[offset + 7u]) << 8u) | bytes[offset + 8u];
            return width > 0u && height > 0u;
        }
        offset += 2u + length;
    }
    return false;
}

std::int32_t exception_status(const cv::Exception& exception) noexcept {
    return exception.code == cv::Error::StsNoMem
        ? AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY
        : AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE;
}

template <typename Body>
std::int32_t guarded_frame_call(AzurPilotNativeFrameHandle* out_frame, Body body) noexcept {
    if (out_frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;
    try {
        AzurPilotNativeFrameOpaque* frame = nullptr;
        const std::int32_t status = body(&frame);
        if (status != AZURPILOT_NATIVE_OK) {
            delete frame;
            return status;
        }
        *out_frame = frame;
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (const cv::Exception& exception) {
        return exception_status(exception);
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

}  // namespace

namespace azurpilot::native::detail {

std::int32_t convert_pixels_to_frame(
    const std::uint8_t* pixels,
    std::uint32_t size,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride_bytes,
    std::uint32_t source_format,
    std::uint32_t flags,
    AzurPilotNativeFrameOpaque** out_frame) {
    if (out_frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;
    const std::uint32_t pixel_bytes = bytes_per_pixel(source_format);
    if (pixels == nullptr || pixel_bytes == 0u
        || (flags & ~AZURPILOT_NATIVE_SOURCE_FLAG_FLIP_VERTICAL) != 0u) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    std::uint32_t rgb_stride = 0u;
    std::uint32_t rgb_bytes = 0u;
    if (width == 0u || height == 0u) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }
    if (size > AZURPILOT_NATIVE_MAX_RAW_INPUT_BYTES || !rgb8_size_is_bounded(width, height, rgb_stride, rgb_bytes)) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }
    const std::uint64_t row_bytes = static_cast<std::uint64_t>(width) * pixel_bytes;
    const std::uint64_t required = static_cast<std::uint64_t>(stride_bytes) * (height - 1u) + row_bytes;
    if (stride_bytes < row_bytes || required > size) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }

    const int rows = static_cast<int>(height);
    const int columns = static_cast<int>(width);
    cv::Mat rgb(rows, columns, CV_8UC3);
    auto* source = const_cast<std::uint8_t*>(pixels);
    switch (source_format) {
    case AZURPILOT_NATIVE_SOURCE_RGBA8888:
        cv::cvtColor(cv::Mat(rows, columns, CV_8UC4, source, stride_bytes), rgb, cv::COLOR_RGBA2RGB);
        break;
    case AZURPILOT_NATIVE_SOURCE_BGRA8888:
        cv::cvtColor(cv::Mat(rows, columns, CV_8UC4, source, stride_bytes), rgb, cv::COLOR_BGRA2RGB);
        break;
    case AZURPILOT_NATIVE_SOURCE_RGB888:
        cv::Mat(rows, columns, CV_8UC3, source, stride_bytes).copyTo(rgb);
        break;
    case AZURPILOT_NATIVE_SOURCE_BGR888:
        cv::cvtColor(cv::Mat(rows, columns, CV_8UC3, source, stride_bytes), rgb, cv::COLOR_BGR2RGB);
        break;
    case AZURPILOT_NATIVE_SOURCE_RGB565_LE:
        // Повтор старших битов даёт полный диапазон 0..255 каждого канала, без масштабирования float.
        cv::parallel_for_(cv::Range(0, rows), [&](const cv::Range& range) {
            for (int row = range.start; row < range.end; ++row) {
                const std::uint8_t* input = pixels + static_cast<std::size_t>(stride_bytes) * static_cast<std::size_t>(row);
                std::uint8_t* output = rgb.ptr<std::uint8_t>(row);
                for (int column = 0; column < columns; ++column) {
                    const std::uint32_t value = static_cast<std::uint32_t>(input[column * 2])
                        | (static_cast<std::uint32_t>(input[(column * 2) + 1]) << 8u);
                    const std::uint32_t red = (value >> 11u) & 0x1Fu;
                    const std::uint32_t green = (value >> 5u) & 0x3Fu;
                    const std::uint32_t blue = value & 0x1Fu;
                    output[(column * 3) + 0] = static_cast<std::uint8_t>((red << 3u) | (red >> 2u));
                    output[(column * 3) + 1] = static_cast<std::uint8_t>((green << 2u) | (green >> 4u));
                    output[(column * 3) + 2] = static_cast<std::uint8_t>((blue << 3u) | (blue >> 2u));
                }
            }
        });
        break;
    default:
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    if ((flags & AZURPILOT_NATIVE_SOURCE_FLAG_FLIP_VERTICAL) != 0u) {
        cv::flip(rgb, rgb, 0);
    }
    return adopt_rgb_frame(std::move(rgb), out_frame);
}

}  // namespace azurpilot::native::detail

namespace {

std::int32_t decode_bmz1(const std::uint8_t* data, std::uint32_t size, AzurPilotNativeFrameOpaque** out_frame) {
    if (data == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    if (size > AZURPILOT_NATIVE_MAX_RAW_INPUT_BYTES) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }
    if (size < AZURPILOT_NATIVE_BMZ1_HEADER_BYTES) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }

    std::uint32_t header[5];
    for (std::size_t index = 0u; index < 5u; ++index) {
        header[index] = read_u32_le(data + (index * 4u));
    }
    if (header[0] != AZURPILOT_NATIVE_BMZ1_MAGIC) {
        for (std::uint32_t& value : header) {
            value = byte_swap(value);
        }
        if (header[0] != AZURPILOT_NATIVE_BMZ1_MAGIC) {
            return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
        }
    }

    const std::uint32_t uncompressed_size = header[1];
    const std::uint32_t compressed_size = header[2];
    const std::uint32_t width = header[3];
    const std::uint32_t height = header[4];
    std::uint32_t rgb_stride = 0u;
    std::uint32_t rgb_bytes = 0u;
    if (width == 0u || height == 0u || compressed_size != size - AZURPILOT_NATIVE_BMZ1_HEADER_BYTES) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }
    if (!azurpilot::native::detail::rgb8_size_is_bounded(width, height, rgb_stride, rgb_bytes)
        || uncompressed_size > AZURPILOT_NATIVE_MAX_RAW_INPUT_BYTES) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }
    // Распакованные данные — BMP: небольшой заголовок, затем строки BGR снизу вверх без выравнивающих байтов.
    const std::uint64_t pixel_bytes = static_cast<std::uint64_t>(width) * height * 3u;
    if (uncompressed_size < pixel_bytes || uncompressed_size - pixel_bytes > 1024u || (width * 3u) % 4u != 0u) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }

    std::vector<std::uint8_t> unpacked(uncompressed_size);
    if (!lz4_decompress_block(data + AZURPILOT_NATIVE_BMZ1_HEADER_BYTES, compressed_size,
                              unpacked.data(), unpacked.size())) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }
    const std::uint8_t* pixels = unpacked.data() + (uncompressed_size - pixel_bytes);
    return azurpilot::native::detail::convert_pixels_to_frame(
        pixels, static_cast<std::uint32_t>(pixel_bytes), width, height, width * 3u,
        AZURPILOT_NATIVE_SOURCE_BGR888, AZURPILOT_NATIVE_SOURCE_FLAG_FLIP_VERTICAL, out_frame);
}

std::int32_t decode_jpeg(const std::uint8_t* bytes, std::uint32_t size, AzurPilotNativeFrameOpaque** out_frame) {
    if (bytes == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    if (size > AZURPILOT_NATIVE_MAX_JPEG_INPUT_BYTES) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }
    std::uint32_t width = 0u;
    std::uint32_t height = 0u;
    if (!read_jpeg_dimensions(bytes, size, width, height)) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }
    std::uint32_t rgb_stride = 0u;
    std::uint32_t rgb_bytes = 0u;
    if (!azurpilot::native::detail::rgb8_size_is_bounded(width, height, rgb_stride, rgb_bytes)) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }

    cv::Mat encoded(1, static_cast<int>(size), CV_8UC1, const_cast<std::uint8_t*>(bytes));
    cv::Mat rgb = cv::imdecode(encoded, cv::IMREAD_COLOR_RGB);
    if (rgb.empty() || rgb.type() != CV_8UC3
        || static_cast<std::uint32_t>(rgb.cols) != width || static_cast<std::uint32_t>(rgb.rows) != height) {
        return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
    }
    if (!rgb.isContinuous()) {
        rgb = rgb.clone();
    }
    return azurpilot::native::detail::adopt_rgb_frame(std::move(rgb), out_frame);
}

}  // namespace

int32_t azurpilot_native_frame_create_from_pixels(
    const uint8_t* pixels, uint32_t size, uint32_t width, uint32_t height, uint32_t stride_bytes,
    uint32_t source_format, uint32_t flags, AzurPilotNativeFrameHandle* out_frame) AZURPILOT_NATIVE_NOEXCEPT {
    return guarded_frame_call(out_frame, [&](AzurPilotNativeFrameOpaque** frame) {
        return azurpilot::native::detail::convert_pixels_to_frame(
            pixels, size, width, height, stride_bytes, source_format, flags, frame);
    });
}

int32_t azurpilot_native_frame_decode_bmz1(
    const uint8_t* data, uint32_t size, AzurPilotNativeFrameHandle* out_frame) AZURPILOT_NATIVE_NOEXCEPT {
    return guarded_frame_call(out_frame, [&](AzurPilotNativeFrameOpaque** frame) {
        return decode_bmz1(data, size, frame);
    });
}

int32_t azurpilot_native_frame_decode_jpeg(
    const uint8_t* jpeg_bytes, uint32_t size, AzurPilotNativeFrameHandle* out_frame) AZURPILOT_NATIVE_NOEXCEPT {
    return guarded_frame_call(out_frame, [&](AzurPilotNativeFrameOpaque** frame) {
        return decode_jpeg(jpeg_bytes, size, frame);
    });
}
