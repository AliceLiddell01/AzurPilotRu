/* Проверка источников кадров захвата ABI v6 через C ABI.
 * sources: сырые пиксели всех форматов с различимыми R/B, переворот, отрицательные размеры и данные,
 *          aScreenCap BMZ1/LZ4 и JPEG.
 * video:   H.264, закодированный системным MFT в самом тесте и декодированный production декодером. */
#include "azurpilot_native_abi.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <codecapi.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>

#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
int failures = 0;
void check(bool value, const std::string& description) {
    if (!value) { ++failures; std::cerr << "[FAIL] " << description << '\n'; }
}

struct Rgb final { double red; double green; double blue; };

Rgb pixel(AzurPilotNativeFrameHandle frame, int x, int y) {
    const AzurPilotNativeRegion region{x, y, x + 1, y + 1};
    AzurPilotNativeRegionStatistics statistics{};
    check(azurpilot_native_frame_scan_region(frame, &region, &statistics) == AZURPILOT_NATIVE_OK, "Чтение пикселя кадра");
    return {statistics.mean_red, statistics.mean_green, statistics.mean_blue};
}

bool close_to(const Rgb& actual, double red, double green, double blue, double tolerance) {
    return std::abs(actual.red - red) <= tolerance && std::abs(actual.green - green) <= tolerance
        && std::abs(actual.blue - blue) <= tolerance;
}

AzurPilotNativeFrameInfo info(AzurPilotNativeFrameHandle frame) {
    AzurPilotNativeFrameInfo value{};
    check(azurpilot_native_frame_get_info(frame, &value) == AZURPILOT_NATIVE_OK, "Метаданные кадра");
    return value;
}

// Кадр 4×2: верхняя строка красная, нижняя — синяя; столбец 3 верхней строки зелёный.
std::vector<uint8_t> raw(uint32_t format, uint32_t stride) {
    const uint32_t width = 4u;
    std::vector<uint8_t> bytes(static_cast<std::size_t>(stride) * 2u, 0xEEu);
    for (uint32_t row = 0; row < 2u; ++row) {
        for (uint32_t column = 0; column < width; ++column) {
            uint8_t red = row == 0u ? 250u : 5u;
            uint8_t green = 10u;
            uint8_t blue = row == 0u ? 5u : 240u;
            if (row == 0u && column == 3u) { red = 0u; green = 255u; blue = 0u; }
            uint8_t* p = bytes.data() + static_cast<std::size_t>(row) * stride;
            switch (format) {
            case AZURPILOT_NATIVE_SOURCE_RGBA8888: p[column * 4] = red; p[column * 4 + 1] = green; p[column * 4 + 2] = blue; p[column * 4 + 3] = 0; break;
            case AZURPILOT_NATIVE_SOURCE_BGRA8888: p[column * 4] = blue; p[column * 4 + 1] = green; p[column * 4 + 2] = red; p[column * 4 + 3] = 255; break;
            case AZURPILOT_NATIVE_SOURCE_RGB888: p[column * 3] = red; p[column * 3 + 1] = green; p[column * 3 + 2] = blue; break;
            case AZURPILOT_NATIVE_SOURCE_BGR888: p[column * 3] = blue; p[column * 3 + 1] = green; p[column * 3 + 2] = red; break;
            case AZURPILOT_NATIVE_SOURCE_RGB565_LE: {
                const uint16_t value = static_cast<uint16_t>(((red >> 3) << 11) | ((green >> 2) << 5) | (blue >> 3));
                p[column * 2] = static_cast<uint8_t>(value & 0xFFu);
                p[column * 2 + 1] = static_cast<uint8_t>(value >> 8);
                break;
            }
            default: break;
            }
        }
    }
    return bytes;
}

void test_raw_formats() {
    struct Case { uint32_t format; uint32_t bpp; double tolerance; };
    const std::array<Case, 5> cases{{
        {AZURPILOT_NATIVE_SOURCE_RGBA8888, 4u, 0.0}, {AZURPILOT_NATIVE_SOURCE_BGRA8888, 4u, 0.0},
        {AZURPILOT_NATIVE_SOURCE_RGB888, 3u, 0.0}, {AZURPILOT_NATIVE_SOURCE_BGR888, 3u, 0.0},
        {AZURPILOT_NATIVE_SOURCE_RGB565_LE, 2u, 8.0}}};
    for (const Case& value : cases) {
        const std::string name = "формат " + std::to_string(value.format);
        // Шаг строки с выравнивающими байтами проверяет, что stride не путается с шириной.
        const uint32_t stride = value.bpp * 4u + 3u;
        const std::vector<uint8_t> bytes = raw(value.format, stride);
        for (uint32_t flip = 0; flip < 2u; ++flip) {
            AzurPilotNativeFrameHandle frame = nullptr;
            const int32_t status = azurpilot_native_frame_create_from_pixels(bytes.data(), static_cast<uint32_t>(bytes.size()),
                4u, 2u, stride, value.format, flip, &frame);
            check(status == AZURPILOT_NATIVE_OK && frame != nullptr, name + ": создание кадра");
            if (frame == nullptr) {
                continue;
            }
            const AzurPilotNativeFrameInfo meta = info(frame);
            check(meta.width == 4u && meta.height == 2u && meta.stride_bytes == 12u && meta.byte_length == 24u
                && meta.pixel_format == AZURPILOT_NATIVE_PIXEL_FORMAT_RGB8, name + ": геометрия RGB8");
            const int top = flip == 0u ? 0 : 1;
            const int bottom = 1 - top;
            // Красный и синий различимы: перестановка каналов дала бы обратный результат.
            check(close_to(pixel(frame, 0, top), 250, 10, 5, value.tolerance), name + ": красная строка сверху");
            check(close_to(pixel(frame, 0, bottom), 5, 10, 240, value.tolerance), name + ": синяя строка снизу");
            check(close_to(pixel(frame, 3, top), 0, 255, 0, value.tolerance), name + ": зелёный столбец");
            check(azurpilot_native_frame_release(frame) == AZURPILOT_NATIVE_OK, name + ": release");
        }
    }

    const std::vector<uint8_t> rgba = raw(AZURPILOT_NATIVE_SOURCE_RGBA8888, 16u);
    AzurPilotNativeFrameHandle frame = reinterpret_cast<AzurPilotNativeFrameHandle>(1);
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 31u, 4u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_DATA && frame == nullptr,
        "Буфер короче stride×(h-1)+строка отвергается и обнуляет out_frame");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 4u, 2u, 15u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_DATA,
        "Stride меньше ширины строки отвергается");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 4u, 2u, 16u, 99u, 0u, &frame)
        == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Неизвестный формат отвергается");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 4u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 4u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Неизвестный флаг отвергается");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 0u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_DATA,
        "Нулевая ширина отвергается");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 9000u, 2u, 36000u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame) == AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE,
        "Сторона больше предела кадра отвергается");
    check(azurpilot_native_frame_create_from_pixels(nullptr, 32u, 4u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL пиксели отвергаются");
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 4u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "NULL out_frame отвергается");
}

void test_rotate() {
    const std::vector<uint8_t> rgba = raw(AZURPILOT_NATIVE_SOURCE_RGBA8888, 16u);
    AzurPilotNativeFrameHandle source = nullptr;
    check(azurpilot_native_frame_create_from_pixels(rgba.data(), 32u, 4u, 2u, 16u,
        AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &source) == AZURPILOT_NATIVE_OK, "Поворот: исходный кадр");
    if (source == nullptr) {
        return;
    }
    // Зелёный пиксель (3,0) исходного кадра 4×2 после поворота по часовой оказывается в (1,3),
    // после 180° — в (0,1), после 270° — в (0,0); красная верхняя строка задаёт направление.
    struct Case { uint32_t turns; uint32_t width; uint32_t height; int green_x; int green_y; int red_x; int red_y; };
    const std::array<Case, 3> cases{{{1u, 2u, 4u, 1, 3, 1, 0}, {2u, 4u, 2u, 0, 1, 3, 1}, {3u, 2u, 4u, 0, 0, 0, 3}}};
    for (const Case& value : cases) {
        const std::string name = "поворот " + std::to_string(value.turns);
        AzurPilotNativeFrameHandle rotated = nullptr;
        check(azurpilot_native_frame_rotate(source, value.turns, &rotated) == AZURPILOT_NATIVE_OK && rotated != nullptr, name);
        if (rotated == nullptr) {
            continue;
        }
        const AzurPilotNativeFrameInfo meta = info(rotated);
        check(meta.width == value.width && meta.height == value.height && meta.stride_bytes == value.width * 3u, name + ": геометрия");
        check(close_to(pixel(rotated, value.green_x, value.green_y), 0, 255, 0, 0.0), name + ": зелёный пиксель");
        check(close_to(pixel(rotated, value.red_x, value.red_y), 250, 10, 5, 0.0), name + ": красный угол");
        check(azurpilot_native_frame_release(rotated) == AZURPILOT_NATIVE_OK, name + ": release");
    }

    AzurPilotNativeFrameHandle rejected = reinterpret_cast<AzurPilotNativeFrameHandle>(1);
    check(azurpilot_native_frame_rotate(source, 0u, &rejected) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT && rejected == nullptr,
        "Поворот 0 отвергается и обнуляет out_frame");
    check(azurpilot_native_frame_rotate(source, 4u, &rejected) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "Поворот 4 отвергается");
    check(azurpilot_native_frame_rotate(nullptr, 1u, &rejected) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "NULL кадр отвергается");
    check(azurpilot_native_frame_rotate(source, 1u, nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "NULL out_frame отвергается");
    check(azurpilot_native_frame_release(source) == AZURPILOT_NATIVE_OK, "Поворот: release исходного кадра");
}
void append_u32(std::vector<uint8_t>& bytes, uint32_t value) {
    for (int shift = 0; shift < 32; shift += 8) bytes.push_back(static_cast<uint8_t>(value >> shift));
}

// BMP-подобные данные aScreenCap: 54 байта заголовка и BGR снизу вверх; кадр 4×2.
std::vector<uint8_t> bmz1_payload() {
    std::vector<uint8_t> unpacked(54u, 0x42u);
    // Нижняя строка источника (первая в данных) синяя, верхняя — красная.
    for (int column = 0; column < 4; ++column) { unpacked.push_back(240); unpacked.push_back(10); unpacked.push_back(5); }
    for (int column = 0; column < 4; ++column) { unpacked.push_back(5); unpacked.push_back(10); unpacked.push_back(250); }
    return unpacked;
}

// LZ4: литералы заголовка, затем совпадение-повтор строки (offset 3) и финальные литералы.
std::vector<uint8_t> bmz1(bool swap_header, bool corrupt) {
    const std::vector<uint8_t> unpacked = bmz1_payload();
    std::vector<uint8_t> block;
    // Последовательность 1: 57 литералов (заголовок + первый пиксель), match длиной 9 с offset 3.
    block.push_back(static_cast<uint8_t>((15u << 4) | 5u));
    block.push_back(57u - 15u);
    block.insert(block.end(), unpacked.begin(), unpacked.begin() + 57);
    block.push_back(corrupt ? 0xFFu : 3u);
    block.push_back(corrupt ? 0xFFu : 0u);
    // Последовательность 2: оставшиеся 12 байт верхней строки литералами без match.
    block.push_back(static_cast<uint8_t>(12u << 4));
    block.insert(block.end(), unpacked.begin() + 66, unpacked.end());

    std::vector<uint8_t> bytes;
    const std::array<uint32_t, 5> header{AZURPILOT_NATIVE_BMZ1_MAGIC, static_cast<uint32_t>(unpacked.size()),
        static_cast<uint32_t>(block.size()), 4u, 2u};
    for (uint32_t value : header) {
        if (swap_header) {
            value = ((value & 0xFFu) << 24) | ((value & 0xFF00u) << 8) | ((value >> 8) & 0xFF00u) | (value >> 24);
        }
        append_u32(bytes, value);
    }
    bytes.insert(bytes.end(), block.begin(), block.end());
    return bytes;
}

void test_bmz1() {
    for (int swap = 0; swap < 2; ++swap) {
        const std::vector<uint8_t> bytes = bmz1(swap == 1, false);
        AzurPilotNativeFrameHandle frame = nullptr;
        check(azurpilot_native_frame_decode_bmz1(bytes.data(), static_cast<uint32_t>(bytes.size()), &frame)
            == AZURPILOT_NATIVE_OK && frame != nullptr, "BMZ1 декодируется");
        if (frame != nullptr) {
            const AzurPilotNativeFrameInfo meta = info(frame);
            check(meta.width == 4u && meta.height == 2u, "BMZ1: геометрия");
            check(close_to(pixel(frame, 2, 0), 250, 10, 5, 0), "BMZ1: строки снизу вверх и BGR→RGB, верх красный");
            check(close_to(pixel(frame, 2, 1), 5, 10, 240, 0), "BMZ1: низ синий");
            azurpilot_native_frame_release(frame);
        }
    }

    std::vector<uint8_t> corrupt = bmz1(false, true);
    AzurPilotNativeFrameHandle frame = nullptr;
    check(azurpilot_native_frame_decode_bmz1(corrupt.data(), static_cast<uint32_t>(corrupt.size()), &frame)
        == AZURPILOT_NATIVE_ERROR_INVALID_DATA && frame == nullptr, "BMZ1: ссылка LZ4 за начало буфера отвергается");
    std::vector<uint8_t> truncated = bmz1(false, false);
    truncated.pop_back();
    check(azurpilot_native_frame_decode_bmz1(truncated.data(), static_cast<uint32_t>(truncated.size()), &frame)
        == AZURPILOT_NATIVE_ERROR_INVALID_DATA, "BMZ1: усечённый блок отвергается");
    std::vector<uint8_t> magic = bmz1(false, false);
    magic[0] = 'X';
    check(azurpilot_native_frame_decode_bmz1(magic.data(), static_cast<uint32_t>(magic.size()), &frame)
        == AZURPILOT_NATIVE_ERROR_INVALID_DATA, "BMZ1: неверная сигнатура отвергается");
    check(azurpilot_native_frame_decode_bmz1(magic.data(), 10u, &frame) == AZURPILOT_NATIVE_ERROR_INVALID_DATA,
        "BMZ1: короче заголовка отвергается");
}

void test_jpeg() {
    cv::Mat bgr(16, 32, CV_8UC3, cv::Scalar(5, 10, 250));
    bgr(cv::Rect(16, 0, 16, 16)).setTo(cv::Scalar(240, 10, 5));
    std::vector<uint8_t> bytes;
    check(cv::imencode(".jpg", bgr, bytes, {cv::IMWRITE_JPEG_QUALITY, 100}), "Кодирование JPEG fixture");
    AzurPilotNativeFrameHandle frame = nullptr;
    check(azurpilot_native_frame_decode_jpeg(bytes.data(), static_cast<uint32_t>(bytes.size()), &frame)
        == AZURPILOT_NATIVE_OK && frame != nullptr, "JPEG декодируется");
    if (frame != nullptr) {
        const AzurPilotNativeFrameInfo meta = info(frame);
        check(meta.width == 32u && meta.height == 16u, "JPEG: геометрия");
        check(close_to(pixel(frame, 4, 8), 250, 10, 5, 6), "JPEG: левая половина красная (RGB)");
        check(close_to(pixel(frame, 27, 8), 5, 10, 240, 6), "JPEG: правая половина синяя (RGB)");
        azurpilot_native_frame_release(frame);
    }
    std::vector<uint8_t> broken(bytes.begin(), bytes.end() - 2);
    check(azurpilot_native_frame_decode_jpeg(broken.data(), static_cast<uint32_t>(broken.size()), &frame)
        == AZURPILOT_NATIVE_ERROR_INVALID_DATA, "JPEG без EOI отвергается");
    const uint8_t png[] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A};
    check(azurpilot_native_frame_decode_jpeg(png, sizeof(png), &frame) == AZURPILOT_NATIVE_ERROR_INVALID_DATA,
        "Не-JPEG отвергается");
}

void test_nemu_arguments() {
    AzurPilotNativeNemuHandle session = reinterpret_cast<AzurPilotNativeNemuHandle>(1);
    // Каталог строится от временного каталога пользователя: в исходниках нет machine-specific путей.
    wchar_t temp[MAX_PATH]{};
    const DWORD tempLength = GetTempPathW(MAX_PATH, temp);
    const std::wstring folder = std::wstring(temp, tempLength) + L"azurpilot-missing-" + std::to_wstring(GetCurrentProcessId());
    const std::wstring missing = folder + L"\\external_renderer_ipc.dll";
    check(azurpilot_native_nemu_open(reinterpret_cast<const uint16_t*>(missing.c_str()),
        reinterpret_cast<const uint16_t*>(folder.c_str()), 0, &session) == AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE
        && session == nullptr, "nemu_ipc: отсутствующая DLL — PROVIDER_UNAVAILABLE без сессии");
    // Системная DLL без экспортов nemu_* загружается, но отвергается до вызова кода эмулятора.
    wchar_t system[MAX_PATH]{};
    const UINT length = GetSystemDirectoryW(system, MAX_PATH);
    const std::wstring kernel = std::wstring(system, length) + L"\\kernel32.dll";
    check(azurpilot_native_nemu_open(reinterpret_cast<const uint16_t*>(kernel.c_str()),
        reinterpret_cast<const uint16_t*>(folder.c_str()), 0, &session) == AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE,
        "nemu_ipc: DLL без экспортов отвергается");
    check(azurpilot_native_nemu_open(reinterpret_cast<const uint16_t*>(kernel.c_str()),
        reinterpret_cast<const uint16_t*>(folder.c_str()), -1, &session) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "nemu_ipc: отрицательный индекс отвергается");
    AzurPilotNativeFrameHandle frame = nullptr;
    int32_t provider = 7;
    check(azurpilot_native_nemu_capture(nullptr, 0u, AZURPILOT_NATIVE_SOURCE_RGBA8888, 0u, &frame, &provider)
        == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT && provider == 0, "nemu_ipc: NULL сессия отвергается");
    check(azurpilot_native_nemu_release(nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "nemu_ipc: release NULL");
}

// ---- H.264: системный MFT-кодер даёт Annex-B, production декодер возвращает RGB8. ----

void throw_if(HRESULT hr, const char* what) {
    if (FAILED(hr)) throw std::runtime_error(what);
}

std::vector<std::vector<uint8_t>> encode_h264(int width, int height, int frames, bool& available) {
    available = false;
    std::vector<std::vector<uint8_t>> packets;
    ComPtr<IMFTransform> encoder;
    if (FAILED(CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&encoder)))) {
        return packets;
    }
    available = true;
    ComPtr<IMFMediaType> output;
    throw_if(MFCreateMediaType(&output), "output type");
    throw_if(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "major");
    throw_if(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), "subtype");
    throw_if(output->SetUINT32(MF_MT_AVG_BITRATE, 4000000), "bitrate");
    throw_if(MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, width, height), "size");
    throw_if(MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, 30, 1), "rate");
    throw_if(output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "interlace");
    throw_if(encoder->SetOutputType(0, output.Get(), 0), "set output");
    ComPtr<IMFMediaType> input;
    throw_if(MFCreateMediaType(&input), "input type");
    throw_if(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "major");
    throw_if(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12), "subtype");
    throw_if(MFSetAttributeSize(input.Get(), MF_MT_FRAME_SIZE, width, height), "size");
    throw_if(MFSetAttributeRatio(input.Get(), MF_MT_FRAME_RATE, 30, 1), "rate");
    throw_if(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "interlace");
    throw_if(encoder->SetInputType(0, input.Get(), 0), "set input");
    throw_if(encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin");

    // Левая половина красная, правая синяя: BT.601 limited range.
    const auto yuv = [](int r, int g, int b) {
        return std::array<int, 3>{
            static_cast<int>(std::lround(16 + 0.257 * r + 0.504 * g + 0.098 * b)),
            static_cast<int>(std::lround(128 - 0.148 * r - 0.291 * g + 0.439 * b)),
            static_cast<int>(std::lround(128 + 0.439 * r - 0.368 * g - 0.071 * b))};
    };
    const auto red = yuv(230, 20, 20);
    const auto blue = yuv(20, 20, 230);
    const DWORD size = static_cast<DWORD>(width * height * 3 / 2);
    MFT_OUTPUT_STREAM_INFO stream{};
    throw_if(encoder->GetOutputStreamInfo(0, &stream), "stream info");

    const auto collect = [&]() {
        for (;;) {
            ComPtr<IMFSample> sample;
            ComPtr<IMFMediaBuffer> buffer;
            MFT_OUTPUT_DATA_BUFFER data{};
            if ((stream.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0) {
                throw_if(MFCreateSample(&sample), "sample");
                throw_if(MFCreateMemoryBuffer(std::max<DWORD>(stream.cbSize, size * 2), &buffer), "buffer");
                throw_if(sample->AddBuffer(buffer.Get()), "add");
                data.pSample = sample.Get();
            }
            DWORD status = 0;
            const HRESULT hr = encoder->ProcessOutput(0, 1, &data, &status);
            if (data.pEvents != nullptr) data.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            throw_if(hr, "process output");
            ComPtr<IMFMediaBuffer> contiguous;
            throw_if(data.pSample->ConvertToContiguousBuffer(&contiguous), "contiguous");
            BYTE* bytes = nullptr;
            DWORD length = 0;
            throw_if(contiguous->Lock(&bytes, nullptr, &length), "lock");
            packets.emplace_back(bytes, bytes + length);
            contiguous->Unlock();
            if ((stream.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0) data.pSample->Release();
        }
    };

    for (int index = 0; index < frames; ++index) {
        ComPtr<IMFMediaBuffer> buffer;
        throw_if(MFCreateMemoryBuffer(size, &buffer), "input buffer");
        BYTE* bytes = nullptr;
        throw_if(buffer->Lock(&bytes, nullptr, nullptr), "lock input");
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x) {
                bytes[y * width + x] = static_cast<BYTE>((x < width / 2 ? red : blue)[0]);
            }
        }
        BYTE* uv = bytes + width * height;
        for (int y = 0; y < height / 2; ++y) {
            for (int x = 0; x < width; x += 2) {
                const auto& c = x < width / 2 ? red : blue;
                uv[y * width + x] = static_cast<BYTE>(c[1]);
                uv[y * width + x + 1] = static_cast<BYTE>(c[2]);
            }
        }
        buffer->Unlock();
        throw_if(buffer->SetCurrentLength(size), "length");
        ComPtr<IMFSample> sample;
        throw_if(MFCreateSample(&sample), "input sample");
        throw_if(sample->AddBuffer(buffer.Get()), "add input");
        throw_if(sample->SetSampleTime(static_cast<LONGLONG>(index) * 333333), "time");
        throw_if(sample->SetSampleDuration(333333), "duration");
        HRESULT hr = encoder->ProcessInput(0, sample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING) {
            collect();
            hr = encoder->ProcessInput(0, sample.Get(), 0);
        }
        throw_if(hr, "process input");
        collect();
    }
    throw_if(encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0), "drain");
    collect();
    return packets;
}

int test_video() {
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) || FAILED(MFStartup(MF_VERSION, MFSTARTUP_LITE))) {
        std::cout << "[SKIP] Media Foundation недоступен в этом образе Windows\n";
        return 77;
    }
    AzurPilotNativeVideoDecoderHandle decoder = nullptr;
    check(azurpilot_native_video_decoder_create(99u, &decoder) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT,
        "Видеодекодер: неизвестный кодек отвергается");
    const int32_t created = azurpilot_native_video_decoder_create(AZURPILOT_NATIVE_VIDEO_CODEC_H264, &decoder);
    bool encoder_available = false;
    std::vector<std::vector<uint8_t>> packets;
    try {
        packets = encode_h264(256, 144, 8, encoder_available);
    } catch (const std::exception& exception) {
        check(false, std::string("Кодирование H.264 fixture: ") + exception.what());
    }
    if (created == AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE || !encoder_available) {
        std::cout << "[SKIP] Системные H.264 MFT недоступны: декодер=" << created << '\n';
        if (decoder != nullptr) azurpilot_native_video_decoder_release(decoder);
        return failures == 0 ? 77 : 1;
    }
    check(created == AZURPILOT_NATIVE_OK && decoder != nullptr, "Видеодекодер создаётся");
    check(!packets.empty(), "Кодер выдал пакеты");

    AzurPilotNativeFrameHandle frame = nullptr;
    int64_t pts = -1;
    uint64_t sequence = 7;
    check(azurpilot_native_video_decoder_latest_frame(decoder, 0u, 256u, 144u, AZURPILOT_NATIVE_YUV_BT601, &frame,
        &pts, &sequence) == AZURPILOT_NATIVE_ERROR_NOT_READY && sequence == 0u, "До первого кадра — NOT_READY");

    uint32_t decoded_total = 0;
    int64_t time = 0;
    for (const auto& packet : packets) {
        uint32_t decoded = 0;
        int32_t provider = 0;
        const int32_t status = azurpilot_native_video_decoder_push(decoder, packet.data(),
            static_cast<uint32_t>(packet.size()), time, 0u, &decoded, &provider);
        check(status == AZURPILOT_NATIVE_OK, "Пакет H.264 принят, HRESULT=" + std::to_string(provider));
        decoded_total += decoded;
        time += 33333;
    }
    check(decoded_total > 0u, "Декодер выдал кадры");

    check(azurpilot_native_video_decoder_latest_frame(decoder, 0u, 256u, 144u, AZURPILOT_NATIVE_YUV_BT601, &frame,
        &pts, &sequence) == AZURPILOT_NATIVE_OK && frame != nullptr, "Последний кадр создаётся");
    if (frame != nullptr) {
        const AzurPilotNativeFrameInfo meta = info(frame);
        check(meta.width == 256u && meta.height == 144u, "Обрезка до display размера");
        check(close_to(pixel(frame, 40, 72), 230, 20, 20, 24), "H.264: левая половина красная (порядок каналов RGB)");
        check(close_to(pixel(frame, 215, 72), 20, 20, 230, 24), "H.264: правая половина синяя");
        azurpilot_native_frame_release(frame);
        AzurPilotNativeFrameHandle same = nullptr;
        check(azurpilot_native_video_decoder_latest_frame(decoder, sequence, 256u, 144u, AZURPILOT_NATIVE_YUV_BT601,
            &same, &pts, &sequence) == AZURPILOT_NATIVE_ERROR_NOT_READY,
            "Тот же номер кадра не выдаётся повторно как новый");
    }
    check(azurpilot_native_video_decoder_latest_frame(decoder, 0u, 258u, 144u, AZURPILOT_NATIVE_YUV_BT601, &frame,
        &pts, &sequence) == AZURPILOT_NATIVE_ERROR_INVALID_DATA, "Display шире кадра отвергается");
    uint32_t decoded = 0;
    int32_t provider = 0;
    check(azurpilot_native_video_decoder_push(decoder, nullptr, 1u, 0, 0u, &decoded, &provider)
        == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "NULL пакет отвергается");
    check(azurpilot_native_video_decoder_release(decoder) == AZURPILOT_NATIVE_OK, "Декодер освобождается");
    check(azurpilot_native_video_decoder_release(nullptr) == AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT, "release NULL");
    MFShutdown();
    return failures == 0 ? 0 : 1;
}
}  // namespace

int main(int argc, char** argv) {
    const std::string mode = argc > 1 ? argv[1] : "sources";
    if (mode == "video") {
        const int result = test_video();
        if (result == 0) std::cout << "[PASS] H.264 видеодекодер ABI v6\n";
        return result;
    }
    test_raw_formats();
    test_bmz1();
    test_jpeg();
    test_rotate();
    test_nemu_arguments();
    if (failures != 0) return 1;
    std::cout << "[PASS] Источники кадров ABI v6\n";
    return 0;
}
