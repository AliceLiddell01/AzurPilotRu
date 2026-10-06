/* Изолированная DLL для негативной managed проверки. Она сообщает несовместимый
 * номер ABI и отклоняет azurpilot_native_query. Поэтому успешная проба доказывает,
 * что managed сторона проверила версию до вызова query. */

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
