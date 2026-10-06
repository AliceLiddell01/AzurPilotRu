/* =============================================================================
 * azurpilot_native.cpp — реализация замороженного C ABI v1.
 *
 * Форма ABI (структура, экспорты, коды возврата, семантика required_size) описана
 * и заморожена в native/include/azurpilot_native_abi.h — этот файл только исполняет
 * её и не вводит собственных решений о форме.
 *
 * Этот файл сознательно НЕ включает заголовки OpenCV: OpenCV-код живёт в
 * opencv_probe.cpp за внутренним C++ интерфейсом. Благодаря этому протащить
 * cv::Mat, STL-тип или владеющий указатель в C ABI невозможно даже случайно.
 *
 * Ни одно исключение не покидает экспорты: все три функции объявлены noexcept, и
 * исключение, вылетевшее из функции, вызвало бы std::terminate в процессе-хосте.
 * Поэтому тело каждой функции обёрнуто в try/catch и любой исход превращается в
 * код возврата.
 * =============================================================================
 */

#include "azurpilot_native_abi.h"

#include "opencv_probe.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <exception>

namespace {

/* Копирует строку в массив вызывающей стороны с гарантией NUL-терминации.
 * При усечении последний байт массива — NUL (требование заголовка ABI). */
void copy_string_truncated(char* destination, size_t capacity, const char* source) noexcept {
    if (capacity == 0u || destination == nullptr) {
        return;
    }
    size_t index = 0u;
    if (source != nullptr) {
        while ((index + 1u) < capacity && source[index] != '\0') {
            destination[index] = source[index];
            ++index;
        }
    }
    destination[index] = '\0';
}

}  // namespace

extern "C" {

/* Возвращает версию ABI. Функция тривиальна и не может выбросить исключение;
 * try/catch оставлен как защита от будущих правок, а 0 — заведомо недействительная
 * версия ABI, которую managed сторона обязана распознать как несовместимость. */
uint32_t azurpilot_native_abi_version(void) AZURPILOT_NATIVE_NOEXCEPT {
    try {
        return static_cast<uint32_t>(AZURPILOT_NATIVE_ABI_VERSION);
    } catch (...) {
        return 0u;
    }
}

int32_t azurpilot_native_query(AzurPilotNativeInfo* out_info) AZURPILOT_NATIVE_NOEXCEPT {
    try {
        if (out_info == nullptr) {
            // Структура не изменяется: изменять нечего.
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        }

        // Обнуление до любой работы: гарантия заголовка «при любом ненулевом коде
        // структура не содержит частично заполненных данных» выполняется на всех
        // путях, включая отказ OpenCV.
        memset(out_info, 0, sizeof(*out_info));

        const azurpilot::native::detail::ProbeResult probe = azurpilot::native::detail::run_opencv_probe();
        if (probe.status != azurpilot::native::detail::ProbeStatus::Ok) {
            return probe.status == azurpilot::native::detail::ProbeStatus::OpencvFailure
                       ? AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE
                       : AZURPILOT_NATIVE_ERROR_INTERNAL;
        }

        AzurPilotNativeInfo info{};
        info.abi_version = static_cast<uint32_t>(AZURPILOT_NATIVE_ABI_VERSION);

        const azurpilot::native::detail::OpencvVersion version =
            azurpilot::native::detail::opencv_compile_time_version();
        info.opencv_major = version.major;
        info.opencv_minor = version.minor;
        info.opencv_patch = version.patch;

        // Биты берутся из факта исполнения, а не из констант сборки.
        info.build_flags = probe.build_flags;
        info.capabilities = probe.capabilities;

        copy_string_truncated(info.opencv_version_string, sizeof(info.opencv_version_string),
                              azurpilot::native::detail::opencv_compile_time_version_string());

        *out_info = info;
        return AZURPILOT_NATIVE_OK;
    } catch (const std::exception&) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_build_info(char* buffer, uint32_t buffer_size,
                                   uint32_t* required_size) AZURPILOT_NATIVE_NOEXCEPT {
    try {
        if (required_size == nullptr) {
            // Буфер не изменяется: требуемый размер сообщить некуда.
            return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
        }

        // Строка собирается в локальном буфере: наружу владение не передаётся, и
        // память вызывающей стороны изменяется только при полном успехе.
        char text[128];
        const int written = snprintf(text, sizeof(text), "abi_version=%u;opencv_version=%s",
                                     static_cast<unsigned>(AZURPILOT_NATIVE_ABI_VERSION),
                                     azurpilot::native::detail::opencv_compile_time_version_string());
        if (written <= 0 || static_cast<size_t>(written) >= sizeof(text)) {
            return AZURPILOT_NATIVE_ERROR_INTERNAL;
        }

        // Полный размер строки вместе с завершающим NUL — при любом исходе, кроме
        // INVALID_ARGUMENT (требование заголовка ABI).
        const uint32_t required = static_cast<uint32_t>(written) + 1u;
        *required_size = required;

        // buffer == NULL или buffer_size == 0 — штатный размер-запрос;
        // недостаточный размер — тоже отказ без единого записанного байта.
        if (buffer == nullptr || buffer_size == 0u || buffer_size < required) {
            return AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL;
        }

        memcpy(buffer, text, static_cast<size_t>(required));
        return AZURPILOT_NATIVE_OK;
    } catch (const std::exception&) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

}  // extern "C"
