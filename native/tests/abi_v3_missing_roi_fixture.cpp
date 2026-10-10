/* Изолированная DLL текущего ABI без обязательного ROI-экспорта.
 * Декодирование не создаёт ресурс и возвращает отличимый INVALID_PNG:
 * managed проверка должна обнаружить неполный API до вызова декодирования. */

#include "azurpilot_native_abi.h"

uint32_t azurpilot_native_abi_version(void) noexcept {
    return AZURPILOT_NATIVE_ABI_VERSION;
}

uint32_t azurpilot_native_max_png_bytes(void) noexcept {
    return AZURPILOT_NATIVE_MAX_PNG_BYTES;
}

int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame) noexcept {
    return frame == nullptr
        ? AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
        : AZURPILOT_NATIVE_ERROR_INTERNAL;
}

int32_t azurpilot_native_frame_decode_png(
    const uint8_t* png_bytes,
    uint32_t png_size,
    AzurPilotNativeFrameHandle* out_frame) noexcept {
    if (out_frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;
    if (png_bytes == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    (void)png_size;
    return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
}
