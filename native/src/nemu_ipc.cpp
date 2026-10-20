/* Сессия nemu_ipc MuMu (ABI v6): кадр дисплея экземпляра читается в буфер DLL и сразу
 * преобразуется в RGB8-кадр, без выдачи пикселей managed стороне. */

#include "native_frame.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <opencv2/core.hpp>

#include <cstddef>
#include <cstdint>
#include <cwchar>
#include <memory>
#include <mutex>
#include <new>
#include <vector>

namespace {

// Сигнатуры экспортов external_renderer_ipc.dll MuMu (cdecl; на x64 единственное соглашение вызова).
using NemuConnect = int(__cdecl*)(const wchar_t* install_folder, int instance_index);
using NemuDisconnect = int(__cdecl*)(int connection);
using NemuCaptureDisplay = int(__cdecl*)(
    int connection, unsigned int display_id, int buffer_size, int* width, int* height, unsigned char* pixels);

constexpr std::size_t kMaximumPathCharacters = 32767u;

bool bounded_wide_string(const std::uint16_t* value) noexcept {
    if (value == nullptr) {
        return false;
    }
    for (std::size_t index = 0u; index <= kMaximumPathCharacters; ++index) {
        if (value[index] == 0u) {
            return index > 0u;
        }
    }
    return false;
}

}  // namespace

struct AzurPilotNativeNemuOpaque final {
    HMODULE module = nullptr;
    NemuDisconnect disconnect = nullptr;
    NemuCaptureDisplay capture = nullptr;
    int connection = 0;
    std::mutex gate;
    std::vector<std::uint8_t> pixels;
};

int32_t azurpilot_native_nemu_open(
    const uint16_t* dll_path, const uint16_t* install_folder, int32_t instance_index,
    AzurPilotNativeNemuHandle* out_session) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_session == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_session = nullptr;
    if (!bounded_wide_string(dll_path) || !bounded_wide_string(install_folder) || instance_index < 0) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    try {
        // Только полный путь из обнаруженной установки; зависимости ищутся в её каталоге и System32.
        const auto* path = reinterpret_cast<const wchar_t*>(dll_path);
        HMODULE module = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (module == nullptr) {
            return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
        }

        const auto connect = reinterpret_cast<NemuConnect>(GetProcAddress(module, "nemu_connect"));
        const auto disconnect = reinterpret_cast<NemuDisconnect>(GetProcAddress(module, "nemu_disconnect"));
        const auto capture = reinterpret_cast<NemuCaptureDisplay>(GetProcAddress(module, "nemu_capture_display"));
        if (connect == nullptr || disconnect == nullptr || capture == nullptr) {
            // Код DLL ещё не исполнялся потоками сессии: выгрузка безопасна.
            FreeLibrary(module);
            return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
        }

        auto session = std::make_unique<AzurPilotNativeNemuOpaque>();
        session->module = module;
        session->disconnect = disconnect;
        session->capture = capture;
        session->connection = connect(reinterpret_cast<const wchar_t*>(install_folder), instance_index);
        if (session->connection == 0) {
            return AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
        }

        *out_session = session.release();
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_nemu_capture(
    AzurPilotNativeNemuHandle session, uint32_t display_id, uint32_t source_format, uint32_t flags,
    AzurPilotNativeFrameHandle* out_frame, int32_t* out_provider_status) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_frame == nullptr || out_provider_status == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;
    *out_provider_status = 0;
    if (session == nullptr || display_id > 64u) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    try {
        std::lock_guard<std::mutex> lock(session->gate);
        int width = 0;
        int height = 0;
        int status = session->capture(session->connection, display_id, 0, &width, &height, nullptr);
        if (status != 0) {
            *out_provider_status = status;
            return AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
        }

        std::uint32_t rgb_stride = 0u;
        std::uint32_t rgb_bytes = 0u;
        if (width <= 0 || height <= 0) {
            return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
        }
        if (!azurpilot::native::detail::rgb8_size_is_bounded(
                static_cast<std::uint32_t>(width), static_cast<std::uint32_t>(height), rgb_stride, rgb_bytes)) {
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        }

        const std::size_t length = static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 4u;
        session->pixels.resize(length);
        int captured_width = width;
        int captured_height = height;
        status = session->capture(session->connection, display_id, static_cast<int>(length),
            &captured_width, &captured_height, session->pixels.data());
        if (status != 0) {
            *out_provider_status = status;
            return AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
        }
        if (captured_width != width || captured_height != height) {
            // Разрешение сменилось между запросом размера и пикселями: кадр недостоверен.
            return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
        }

        AzurPilotNativeFrameOpaque* frame = nullptr;
        const std::int32_t converted = azurpilot::native::detail::convert_pixels_to_frame(
            session->pixels.data(), static_cast<std::uint32_t>(length), static_cast<std::uint32_t>(width),
            static_cast<std::uint32_t>(height), static_cast<std::uint32_t>(width) * 4u, source_format, flags, &frame);
        if (converted != AZURPILOT_NATIVE_OK) {
            delete frame;
            return converted;
        }
        *out_frame = frame;
        return AZURPILOT_NATIVE_OK;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (const cv::Exception& exception) {
        return exception.code == cv::Error::StsNoMem
            ? AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY
            : AZURPILOT_NATIVE_ERROR_OPENCV_FAILURE;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_nemu_release(AzurPilotNativeNemuHandle session) AZURPILOT_NATIVE_NOEXCEPT {
    if (session == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    try {
        {
            std::lock_guard<std::mutex> lock(session->gate);
            if (session->connection != 0) {
                session->disconnect(session->connection);
                session->connection = 0;
            }
        }
        // Модуль не выгружается: поток, оставленный managed стороной после тайм-аута, мог остаться в его коде.
        delete session;
        return AZURPILOT_NATIVE_OK;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}
