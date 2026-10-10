/* Изолированная DLL с ABI v3 без обязательного экспорта освобождения кадра.
 * Функция декодирования возвращает INVALID_PNG и не создаёт handle, чтобы проверить ранний отказ
 * при отсутствии функции освобождения без передачи владения и выделения памяти. */

#include "azurpilot_native_abi.h"

uint32_t azurpilot_native_abi_version(void) noexcept {
    return AZURPILOT_NATIVE_ABI_VERSION;
}

uint32_t azurpilot_native_max_png_bytes(void) noexcept {
    return AZURPILOT_NATIVE_MAX_PNG_BYTES;
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
