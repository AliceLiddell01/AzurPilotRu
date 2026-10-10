/* Внутренняя реализация native-owned PNG frame. Этот заголовок не входит в публичный ABI. */
#ifndef AZURPILOT_NATIVE_FRAME_H
#define AZURPILOT_NATIVE_FRAME_H

#include "azurpilot_native_abi.h"

#include <opencv2/core.hpp>

#include <cstdint>

/* Единственная concrete реализация opaque C handle; наружу виден только incomplete type. */
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

/* Фабрика общая для export boundary и внутренних ownership tests.
 * При ожидаемой ошибке возвращает status и оставляет *out_frame == nullptr.
 * Не перехватывает C++ исключения: это делает C ABI export boundary. */
std::int32_t decode_png_frame(
    const std::uint8_t* png_bytes,
    std::uint32_t png_size,
    AzurPilotNativeFrameOpaque** out_frame);

/* Возвращает OK только при согласованности pixels и immutable metadata. */
std::int32_t get_frame_info(
    AzurPilotNativeFrameHandle frame,
    AzurPilotNativeFrameInfo* out_info) noexcept;

#if defined(AZURPILOT_NATIVE_TESTING)
struct FrameOwnershipSnapshot final {
    std::uint64_t live_frames;
    std::uint64_t live_rgb_bytes;
};

FrameOwnershipSnapshot frame_ownership_snapshot_for_test() noexcept;
#endif

}  // namespace azurpilot::native::detail

#endif  // AZURPILOT_NATIVE_FRAME_H
