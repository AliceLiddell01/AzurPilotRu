/* Изолированная DLL для негативной managed проверки. Она сохраняет форму C ABI,
 * но сообщает несовместимый номер. OpenCV и production runtime она не подменяет:
 * fixture копируется только в отдельный процесс-пробу. Отсутствие build_info
 * дополнительно доказывает, что Query отвергает ABI до чтения остальных данных. */

#include "azurpilot_native_abi.h"

uint32_t azurpilot_native_abi_version(void) noexcept {
    return AZURPILOT_NATIVE_ABI_VERSION + 1u;
}

int32_t azurpilot_native_query(AzurPilotNativeInfo* out_info) noexcept {
    if (out_info == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    *out_info = {};
    out_info->abi_version = azurpilot_native_abi_version();
    return AZURPILOT_NATIVE_OK;
}
