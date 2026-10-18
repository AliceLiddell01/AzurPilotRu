/* Неполная DLL текущего ABI: decode не должен вызываться без match-экспорта. */
#include "azurpilot_native_abi.h"
#include <cstring>

uint32_t azurpilot_native_abi_version(void) noexcept { return AZURPILOT_NATIVE_ABI_VERSION; }
uint32_t azurpilot_native_max_png_bytes(void) noexcept { return AZURPILOT_NATIVE_MAX_PNG_BYTES; }
int32_t azurpilot_native_frame_release(AzurPilotNativeFrameHandle frame) noexcept {
    return frame == nullptr ? AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT : AZURPILOT_NATIVE_ERROR_INTERNAL;
}
int32_t azurpilot_native_frame_scan_region(AzurPilotNativeFrameHandle,
    const AzurPilotNativeRegion*, AzurPilotNativeRegionStatistics* statistics) noexcept {
    if (statistics != nullptr) std::memset(statistics, 0, sizeof(*statistics));
    return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
}
int32_t azurpilot_native_frame_decode_png(const uint8_t*, uint32_t, AzurPilotNativeFrameHandle* frame) noexcept {
    if (frame != nullptr) *frame = nullptr;
    return AZURPILOT_NATIVE_ERROR_INVALID_PNG;
}
