/* Внутренняя реализация PNG-кадра, которым владеет native DLL. Этот заголовок не входит в публичный ABI. */
#ifndef AZURPILOT_NATIVE_FRAME_H
#define AZURPILOT_NATIVE_FRAME_H

#include "azurpilot_native_abi.h"

#include <opencv2/core.hpp>

#include <cstdint>

/* Единственная конкретная реализация непрозрачного C-дескриптора; снаружи виден только неполный тип. */
struct AzurPilotNativeFrameOpaque final {
    AzurPilotNativeFrameOpaque(
        cv::Mat rgb_pixels,
        std::uint32_t frame_width,
        std::uint32_t frame_height,
        std::uint32_t row_stride,
        std::uint32_t payload_size);
    ~AzurPilotNativeFrameOpaque() noexcept;

    AzurPilotNativeFrameOpaque(const AzurPilotNativeFrameOpaque&) = delete;
    AzurPilotNativeFrameOpaque& operator=(const AzurPilotNativeFrameOpaque&) = delete;
    AzurPilotNativeFrameOpaque(AzurPilotNativeFrameOpaque&&) = delete;
    AzurPilotNativeFrameOpaque& operator=(AzurPilotNativeFrameOpaque&&) = delete;

    cv::Mat rgb;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride_bytes;
    std::uint32_t byte_length;
};

namespace azurpilot::native::detail {

/* Фабрика используется границей C ABI и внутренними проверками владения.
 * При ожидаемой ошибке возвращает код состояния и оставляет *out_frame == nullptr.
 * Не перехватывает исключения C++: это делает граница C ABI. */
std::int32_t decode_png_frame(
    const std::uint8_t* png_bytes,
    std::uint32_t png_size,
    AzurPilotNativeFrameOpaque** out_frame,
    std::uint64_t maximum_pixels = AZURPILOT_NATIVE_MAX_FRAME_PIXELS);

/* Проверяет пределы кадра RGB8 и вычисляет его шаг строки и длину буфера. */
bool rgb8_size_is_bounded(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t& stride_bytes,
    std::uint32_t& byte_length) noexcept;

/* Передаёт непрерывный CV_8UC3 RGB в новый кадр DLL после проверки размеров и пределов.
 * Не перехватывает исключения C++: это делает граница C ABI. */
std::int32_t adopt_rgb_frame(cv::Mat rgb, AzurPilotNativeFrameOpaque** out_frame);

/* Преобразует сырые пиксели AZURPILOT_NATIVE_SOURCE_* в новый кадр RGB8 (frame_sources.cpp). */
std::int32_t convert_pixels_to_frame(
    const std::uint8_t* pixels,
    std::uint32_t size,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride_bytes,
    std::uint32_t source_format,
    std::uint32_t flags,
    AzurPilotNativeFrameOpaque** out_frame);

/* Возвращает OK только при согласованности пикселей и неизменяемых метаданных. */
std::int32_t get_frame_info(
    AzurPilotNativeFrameHandle frame,
    AzurPilotNativeFrameInfo* out_info) noexcept;

/* Синхронно читает ROI неизменяемого живого кадра; исключения перехватывает C ABI. */
std::int32_t scan_frame_region(
    AzurPilotNativeFrameHandle frame,
    const AzurPilotNativeRegion* region,
    AzurPilotNativeRegionStatistics* out_statistics);

#if defined(AZURPILOT_NATIVE_TESTING)
struct FrameOwnershipSnapshot final {
    std::uint64_t live_frames;
    std::uint64_t live_rgb_bytes;
};

FrameOwnershipSnapshot frame_ownership_snapshot_for_test() noexcept;
#endif

}  // namespace azurpilot::native::detail

#endif  // AZURPILOT_NATIVE_FRAME_H
