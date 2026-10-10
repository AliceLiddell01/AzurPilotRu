/* Изолированная DLL для негативной managed проверки. Она сообщает несовместимую версию ABI,
 * но экспортирует query, decode и release. Функция декодирования возвращает INVALID_PNG,
 * поэтому её вызов отличается от раннего отказа по несовместимой версии. */

#include "azurpilot_native_abi.h"

uint32_t azurpilot_native_abi_version(void) noexcept {
    return AZURPILOT_NATIVE_ABI_VERSION + 1u;
}

int32_t azurpilot_native_query(AzurPilotNativeInfo* out_info) noexcept {
    if (out_info == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    *out_info = {};
    return AZURPILOT_NATIVE_ERROR_INTERNAL;
}

int32_t azurpilot_native_frame_decode_png(
    const uint8_t* png_bytes,
    uint32_t png_size,
    AzurPilotNativeFrameHandle* out_frame) noexcept {
    if (png_bytes == nullptr || out_frame == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    (void)png_size;
    *out_frame = nullptr;
    return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
}

int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame) noexcept {
    return frame == nullptr
        ? AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT
        : AZURPILOT_NATIVE_ERROR_INTERNAL;
}
