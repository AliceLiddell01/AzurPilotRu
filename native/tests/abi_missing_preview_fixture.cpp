/* Неполная DLL текущего ABI без диагностического превью: production decode возвращает служебный
 * дескриптор, а managed превью обязано отказать по отсутствующему экспорту до его вызова. */
#include "azurpilot_native_abi.h"
#include <cstring>

namespace {
int g_fixture_frame = 0;
}

uint32_t azurpilot_native_abi_version(void) noexcept { return AZURPILOT_NATIVE_ABI_VERSION; }
uint32_t azurpilot_native_max_png_bytes(void) noexcept { return AZURPILOT_NATIVE_MAX_PNG_BYTES; }
int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame) noexcept {
    return frame == nullptr ? AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT : AZURPILOT_NATIVE_OK;
}
int32_t azurpilot_native_frame_scan_region(AzurPilotNativeFrameHandle,
    const AzurPilotNativeRegion*, AzurPilotNativeRegionStatistics* statistics) noexcept {
    if (statistics != nullptr) std::memset(statistics, 0, sizeof(*statistics));
    return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
}
int32_t azurpilot_native_frame_match(AzurPilotNativeFrameHandle, const AzurPilotNativeRegion*,
    const uint8_t*, uint32_t, int32_t, int32_t, uint32_t, AzurPilotNativeMatchResult* result) noexcept {
    if (result != nullptr) std::memset(result, 0, sizeof(*result));
    return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
}
int32_t azurpilot_native_frame_decode_png(const uint8_t* png, uint32_t, AzurPilotNativeFrameHandle* frame) noexcept {
    if (frame == nullptr) return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    *frame = nullptr;
    if (png == nullptr) return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    *frame = reinterpret_cast<AzurPilotNativeFrameHandle>(&g_fixture_frame);
    return AZURPILOT_NATIVE_OK;
}
