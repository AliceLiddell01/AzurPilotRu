/* Видеодекодер потока захвата (ABI v6): H.264 Annex-B → NV12 системным Media Foundation MFT Windows.
 * Последний декодированный кадр хранится как сэмпл MFT и преобразуется в RGB8 только по запросу. */

#include "native_frame.h"

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
#include <opencv2/core/utility.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

struct DecodedFrame final {
    ComPtr<IMFSample> sample;
    std::uint32_t width = 0u;
    std::uint32_t height = 0u;
    std::uint32_t pitch = 0u;
    std::uint32_t matrix = AZURPILOT_NATIVE_YUV_BT601;
    bool full_range = false;
    std::int64_t pts_us = 0;
    std::uint64_t sequence = 0u;
};

class HresultFailure final {
public:
    explicit HresultFailure(HRESULT value) noexcept : hr(value) {}
    HRESULT hr;
};

void check(HRESULT hr) {
    if (FAILED(hr)) {
        throw HresultFailure(hr);
    }
}

// Коэффициенты Y'CbCr → R'G'B' в фиксированной точке ×1024.
struct YuvCoefficients final {
    int y_scale;
    int y_offset;
    int red_v;
    int green_u;
    int green_v;
    int blue_u;
};

YuvCoefficients coefficients(std::uint32_t matrix, bool full_range) noexcept {
    if (full_range) {
        return matrix == AZURPILOT_NATIVE_YUV_BT709
            ? YuvCoefficients{1024, 0, 1613, 192, 479, 1900}
            : YuvCoefficients{1024, 0, 1436, 352, 731, 1815};
    }
    return matrix == AZURPILOT_NATIVE_YUV_BT709
        ? YuvCoefficients{1192, 16, 1836, 218, 546, 2163}
        : YuvCoefficients{1192, 16, 1634, 401, 833, 2066};
}

std::uint8_t clamp_channel(int value) noexcept {
    return static_cast<std::uint8_t>(std::clamp((value + 512) >> 10, 0, 255));
}

}  // namespace

struct AzurPilotNativeVideoDecoderOpaque final {
    std::mutex gate;
    ComPtr<IMFTransform> transform;
    bool provides_samples = false;
    std::uint32_t output_buffer_size = 0u;
    std::uint32_t frame_width = 0u;
    std::uint32_t frame_height = 0u;
    std::uint32_t frame_pitch = 0u;
    std::uint32_t frame_matrix = AZURPILOT_NATIVE_YUV_BT601;
    bool frame_full_range = false;
    std::vector<std::uint8_t> pending_config;
    DecodedFrame latest;
    std::uint64_t sequence = 0u;
    bool mta_usage = false;
    CO_MTA_USAGE_COOKIE mta_cookie = nullptr;
    bool media_foundation = false;

    ~AzurPilotNativeVideoDecoderOpaque() {
        latest.sample.Reset();
        transform.Reset();
        if (media_foundation) {
            MFShutdown();
        }
        if (mta_usage) {
            CoDecrementMTAUsage(mta_cookie);
        }
    }

    void select_output_type() {
        for (DWORD index = 0;; ++index) {
            ComPtr<IMFMediaType> type;
            const HRESULT hr = transform->GetOutputAvailableType(0, index, &type);
            if (hr == MF_E_NO_MORE_TYPES) {
                throw HresultFailure(MF_E_INVALIDMEDIATYPE);
            }
            check(hr);
            GUID subtype{};
            check(type->GetGUID(MF_MT_SUBTYPE, &subtype));
            if (subtype != MFVideoFormat_NV12) {
                continue;
            }
            check(transform->SetOutputType(0, type.Get(), 0));
            UINT32 width = 0;
            UINT32 height = 0;
            check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
            UINT32 stride = 0;
            if (FAILED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride)) || static_cast<INT32>(stride) <= 0) {
                stride = width;
            }
            frame_width = width;
            frame_height = height;
            frame_pitch = stride;
            UINT32 matrix = 0;
            frame_matrix = SUCCEEDED(type->GetUINT32(MF_MT_YUV_MATRIX, &matrix))
                    && matrix == MFVideoTransferMatrix_BT709
                ? AZURPILOT_NATIVE_YUV_BT709
                : AZURPILOT_NATIVE_YUV_BT601;
            UINT32 range = 0;
            frame_full_range = SUCCEEDED(type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range))
                && range == MFNominalRange_0_255;
            MFT_OUTPUT_STREAM_INFO info{};
            check(transform->GetOutputStreamInfo(0, &info));
            provides_samples = (info.dwFlags
                & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            output_buffer_size = info.cbSize;
            return;
        }
    }

    // Забирает все готовые кадры; возвращает их число.
    std::uint32_t drain() {
        std::uint32_t decoded = 0u;
        for (;;) {
            MFT_OUTPUT_DATA_BUFFER output{};
            ComPtr<IMFSample> allocated;
            if (!provides_samples) {
                if (output_buffer_size == 0u) {
                    output_buffer_size = frame_pitch * frame_height * 3u / 2u;
                }
                ComPtr<IMFMediaBuffer> buffer;
                check(MFCreateMemoryBuffer(output_buffer_size, &buffer));
                check(MFCreateSample(&allocated));
                check(allocated->AddBuffer(buffer.Get()));
                output.pSample = allocated.Get();
            }

            DWORD status = 0;
            const HRESULT hr = transform->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents != nullptr) {
                output.pEvents->Release();
            }
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
                if (provides_samples && output.pSample != nullptr) {
                    output.pSample->Release();
                }
                return decoded;
            }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
                if (provides_samples && output.pSample != nullptr) {
                    output.pSample->Release();
                }
                select_output_type();
                continue;
            }
            check(hr);

            ComPtr<IMFSample> sample;
            if (provides_samples) {
                sample.Attach(output.pSample);
            } else {
                sample = allocated;
            }
            LONGLONG time = 0;
            const std::int64_t pts = SUCCEEDED(sample->GetSampleTime(&time)) ? time / 10 : 0;
            latest.sample = sample;
            latest.width = frame_width;
            latest.height = frame_height;
            latest.pitch = frame_pitch;
            latest.matrix = frame_matrix;
            latest.full_range = frame_full_range;
            latest.pts_us = pts;
            latest.sequence = ++sequence;
            ++decoded;
        }
    }
};

int32_t azurpilot_native_video_decoder_create(
    uint32_t codec, AzurPilotNativeVideoDecoderHandle* out_decoder) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_decoder == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_decoder = nullptr;
    if (codec != AZURPILOT_NATIVE_VIDEO_CODEC_H264) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    try {
        auto decoder = std::make_unique<AzurPilotNativeVideoDecoderOpaque>();
        // MTA удерживается на всё время жизни декодера: объекты MFT используются с потоков пула.
        if (FAILED(CoIncrementMTAUsage(&decoder->mta_cookie))) {
            return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
        }
        decoder->mta_usage = true;
        if (FAILED(MFStartup(MF_VERSION, MFSTARTUP_LITE))) {
            return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
        }
        decoder->media_foundation = true;
        if (FAILED(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&decoder->transform)))) {
            return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
        }

        ComPtr<IMFAttributes> attributes;
        if (SUCCEEDED(decoder->transform->GetAttributes(&attributes))) {
            // Минимальная задержка: кадр выдаётся без накопления очереди переупорядочивания.
            attributes->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);
        }

        ComPtr<IMFMediaType> input;
        check(MFCreateMediaType(&input));
        check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        check(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        check(decoder->transform->SetInputType(0, input.Get(), 0));
        decoder->select_output_type();
        check(decoder->transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        check(decoder->transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
        *out_decoder = decoder.release();
        return AZURPILOT_NATIVE_OK;
    } catch (const HresultFailure&) {
        return AZURPILOT_NATIVE_ERROR_PROVIDER_UNAVAILABLE;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_video_decoder_push(
    AzurPilotNativeVideoDecoderHandle decoder, const uint8_t* data, uint32_t size, int64_t pts_us,
    uint32_t flags, uint32_t* out_decoded, int32_t* out_provider_status) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_decoded == nullptr || out_provider_status == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_decoded = 0u;
    *out_provider_status = 0;
    if (decoder == nullptr || data == nullptr || size == 0u
        || (flags & ~(AZURPILOT_NATIVE_VIDEO_PACKET_CONFIG | AZURPILOT_NATIVE_VIDEO_PACKET_KEY_FRAME)) != 0u
        || pts_us < 0) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    if (size > AZURPILOT_NATIVE_MAX_VIDEO_PACKET_BYTES) {
        return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
    }

    try {
        std::lock_guard<std::mutex> lock(decoder->gate);
        if ((flags & AZURPILOT_NATIVE_VIDEO_PACKET_CONFIG) != 0u) {
            // SPS/PPS не является кадром: он передаётся вместе со следующим пакетом, как в клиенте scrcpy.
            if (decoder->pending_config.size() + size > AZURPILOT_NATIVE_MAX_VIDEO_PACKET_BYTES) {
                return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
            }
            decoder->pending_config.insert(decoder->pending_config.end(), data, data + size);
            return AZURPILOT_NATIVE_OK;
        }

        const std::size_t total = decoder->pending_config.size() + size;
        if (total > AZURPILOT_NATIVE_MAX_VIDEO_PACKET_BYTES) {
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        }
        ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateMemoryBuffer(static_cast<DWORD>(total), &buffer));
        BYTE* destination = nullptr;
        check(buffer->Lock(&destination, nullptr, nullptr));
        if (!decoder->pending_config.empty()) {
            std::memcpy(destination, decoder->pending_config.data(), decoder->pending_config.size());
        }
        std::memcpy(destination + decoder->pending_config.size(), data, size);
        check(buffer->Unlock());
        check(buffer->SetCurrentLength(static_cast<DWORD>(total)));
        decoder->pending_config.clear();

        ComPtr<IMFSample> sample;
        check(MFCreateSample(&sample));
        check(sample->AddBuffer(buffer.Get()));
        check(sample->SetSampleTime(pts_us * 10));
        if ((flags & AZURPILOT_NATIVE_VIDEO_PACKET_KEY_FRAME) != 0u) {
            check(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE));
        }

        std::uint32_t decoded = 0u;
        HRESULT hr = decoder->transform->ProcessInput(0, sample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING) {
            decoded += decoder->drain();
            hr = decoder->transform->ProcessInput(0, sample.Get(), 0);
        }
        if (FAILED(hr)) {
            *out_provider_status = static_cast<int32_t>(hr);
            return hr == MF_E_INVALIDMEDIATYPE ? AZURPILOT_NATIVE_ERROR_INVALID_DATA
                                               : AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
        }
        decoded += decoder->drain();
        *out_decoded = decoded;
        return AZURPILOT_NATIVE_OK;
    } catch (const HresultFailure& failure) {
        *out_provider_status = static_cast<int32_t>(failure.hr);
        return AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
    } catch (const std::bad_alloc&) {
        return AZURPILOT_NATIVE_ERROR_OUT_OF_MEMORY;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}

int32_t azurpilot_native_video_decoder_latest_frame(
    AzurPilotNativeVideoDecoderHandle decoder, uint64_t after_sequence, uint32_t display_width,
    uint32_t display_height, uint32_t yuv_matrix, AzurPilotNativeFrameHandle* out_frame, int64_t* out_pts_us,
    uint64_t* out_sequence) AZURPILOT_NATIVE_NOEXCEPT {
    if (out_frame == nullptr || out_pts_us == nullptr || out_sequence == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    *out_frame = nullptr;
    *out_pts_us = 0;
    *out_sequence = 0u;
    if (decoder == nullptr || display_width == 0u || display_height == 0u
        || yuv_matrix > AZURPILOT_NATIVE_YUV_BT709) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }

    try {
        DecodedFrame frame;
        {
            std::lock_guard<std::mutex> lock(decoder->gate);
            if (decoder->latest.sample == nullptr || decoder->latest.sequence <= after_sequence) {
                return AZURPILOT_NATIVE_ERROR_NOT_READY;
            }
            frame = decoder->latest;
        }
        if (display_width > frame.width || display_height > frame.height || frame.pitch < frame.width
            || (display_width % 2u) != 0u || (display_height % 2u) != 0u) {
            return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
        }
        std::uint32_t rgb_stride = 0u;
        std::uint32_t rgb_bytes = 0u;
        if (!azurpilot::native::detail::rgb8_size_is_bounded(display_width, display_height, rgb_stride, rgb_bytes)) {
            return AZURPILOT_NATIVE_ERROR_IMAGE_TOO_LARGE;
        }

        ComPtr<IMFMediaBuffer> buffer;
        check(frame.sample->ConvertToContiguousBuffer(&buffer));
        BYTE* data = nullptr;
        DWORD length = 0;
        check(buffer->Lock(&data, nullptr, &length));
        struct Unlock final {
            IMFMediaBuffer* buffer;
            ~Unlock() { buffer->Unlock(); }
        } unlock{buffer.Get()};
        const std::uint64_t luma = static_cast<std::uint64_t>(frame.pitch) * frame.height;
        if (static_cast<std::uint64_t>(length) < luma + (luma / 2u)) {
            return AZURPILOT_NATIVE_ERROR_INVALID_DATA;
        }

        const std::uint32_t matrix = yuv_matrix == AZURPILOT_NATIVE_YUV_AUTO ? frame.matrix : yuv_matrix;
        const YuvCoefficients k = coefficients(matrix, frame.full_range);
        const std::uint8_t* y_plane = data;
        const std::uint8_t* uv_plane = data + luma;
        const std::uint32_t pitch = frame.pitch;
        cv::Mat rgb(static_cast<int>(display_height), static_cast<int>(display_width), CV_8UC3);
        cv::parallel_for_(cv::Range(0, static_cast<int>(display_height)), [&](const cv::Range& range) {
            for (int row = range.start; row < range.end; ++row) {
                const std::uint8_t* y_row = y_plane + static_cast<std::size_t>(pitch) * static_cast<std::size_t>(row);
                const std::uint8_t* uv_row = uv_plane + static_cast<std::size_t>(pitch) * static_cast<std::size_t>(row / 2);
                std::uint8_t* out = rgb.ptr<std::uint8_t>(row);
                for (std::uint32_t column = 0u; column < display_width; ++column) {
                    const int y = (static_cast<int>(y_row[column]) - k.y_offset) * k.y_scale;
                    const int u = static_cast<int>(uv_row[column & ~1u]) - 128;
                    const int v = static_cast<int>(uv_row[(column & ~1u) + 1u]) - 128;
                    out[(column * 3u) + 0u] = clamp_channel(y + (k.red_v * v));
                    out[(column * 3u) + 1u] = clamp_channel(y - (k.green_u * u) - (k.green_v * v));
                    out[(column * 3u) + 2u] = clamp_channel(y + (k.blue_u * u));
                }
            }
        });

        AzurPilotNativeFrameOpaque* created = nullptr;
        const std::int32_t status = azurpilot::native::detail::adopt_rgb_frame(std::move(rgb), &created);
        if (status != AZURPILOT_NATIVE_OK) {
            delete created;
            return status;
        }
        *out_frame = created;
        *out_pts_us = frame.pts_us;
        *out_sequence = frame.sequence;
        return AZURPILOT_NATIVE_OK;
    } catch (const HresultFailure&) {
        return AZURPILOT_NATIVE_ERROR_PROVIDER_FAILURE;
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

int32_t azurpilot_native_video_decoder_release(AzurPilotNativeVideoDecoderHandle decoder) AZURPILOT_NATIVE_NOEXCEPT {
    if (decoder == nullptr) {
        return AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT;
    }
    try {
        delete decoder;
        return AZURPILOT_NATIVE_OK;
    } catch (...) {
        return AZURPILOT_NATIVE_ERROR_INTERNAL;
    }
}
