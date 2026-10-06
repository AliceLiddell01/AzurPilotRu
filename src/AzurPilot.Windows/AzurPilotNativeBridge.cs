using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AzurPilot.Core;

namespace AzurPilot.Windows;

/// <summary>
/// Managed сторона замороженного C ABI: единственный канал вызова native библиотеки.
/// </summary>
/// <remarks>
/// <para>
/// Форма границы принадлежит <c>native/include/azurpilot_native_abi.h</c> и здесь только повторяется.
/// Marshalling выполняет source-generated <c>LibraryImport</c>; runtime marshalling в проекте
/// отключён (<c>DisableRuntimeMarshalling</c>), поэтому IL-стабы в границу не попадают.
/// </para>
/// <para>
/// Все вызовы синхронны и не имеют состояния: native функции не требуют инициализации и
/// потокобезопасны, поэтому экземпляр адаптера не нужен.
/// </para>
/// </remarks>
public static partial class AzurPilotNativeBridge
{
    /// <summary>Имя native библиотеки, замороженное контрактом ABI.</summary>
    public const string LibraryName = "AzurPilot.Native";

    /// <summary>Код возврата native стороны «успех».</summary>
    public const int StatusOk = 0;

    /// <summary>Код возврата native стороны «некорректные аргументы».</summary>
    public const int StatusInvalidArgument = 1;

    /// <summary>Код возврата native стороны «буфер меньше требуемого».</summary>
    public const int StatusBufferTooSmall = 2;

    /// <summary>Код возврата native стороны «код OpenCV выбросил исключение».</summary>
    public const int StatusOpencvFailure = 3;

    /// <summary>Код возврата native стороны «внутренняя ошибка native стороны».</summary>
    public const int StatusInternal = 4;

    /// <summary>
    /// Бит признака того, что код OpenCV реально исполнился при заполнении сведений.
    /// </summary>
    public const uint BuildFlagOpencvExecuted = 1u << 0;

    /// <summary>
    /// Бит capability «core»: доступны cv::Mat и cv::mean.
    /// </summary>
    public const uint CapabilityCore = 1u << 0;

    /// <summary>
    /// Бит capability «imgcodecs»: доступны cv::imwrite и cv::imread.
    /// </summary>
    public const uint CapabilityImgcodecs = 1u << 1;

    /// <summary>
    /// Размер managed объявления структуры сведений о native boundary в байтах.
    /// </summary>
    /// <remarks>
    /// Значение принадлежит заголовку ABI (смещения 0, 4, 8, 12, 16, 20 и 24..55, выравнивание 4).
    /// Здесь оно повторяется только для того, чтобы тест мог доказать совпадение managed раскладки
    /// с замороженной, не открывая приватную структуру interop.
    /// </remarks>
    public const int NativeInfoSizeInBytes = 56;

    private const int MaxStringBytes = 4096;
    private const int MaxBuildInfoAttempts = 4;

    /// <summary>
    /// Запрашивает у native библиотеки сведения о native boundary и проверяет совместимость ABI.
    /// </summary>
    /// <returns>Сведения, полученные от native библиотеки.</returns>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотеку не удалось загрузить.</exception>
    /// <exception cref="NativeAbiMismatchException">Версия ABI не совпадает с ожидаемой.</exception>
    /// <exception cref="AzurPilotNativeBoundaryException">Native сторона вернула код ошибки.</exception>
    public static NativeBoundaryInfo Query()
    {
        _ = VerifyStructLayout();

        int status;
        NativeInfo nativeInfo;
        try
        {
            status = QueryNative(out nativeInfo);
        }
        catch (DllNotFoundException exception)
        {
            throw Unavailable(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw Unavailable(exception);
        }

        if (status != StatusOk)
        {
            throw new AzurPilotNativeBoundaryException(
                $"Native функция azurpilot_native_query завершилась с кодом {status}: "
                + $"{DescribeStatus(status)}. Сведения о native boundary использовать нельзя.");
        }

        uint abiVersion = nativeInfo.AbiVersion;
        if (abiVersion != NativeBoundaryContract.ExpectedAbiVersion)
        {
            throw new NativeAbiMismatchException(
                $"Версия ABI native библиотеки «{LibraryName}»: {abiVersion}; ожидается: "
                + $"{NativeBoundaryContract.ExpectedAbiVersion}. Native библиотека несовместима с "
                + "managed фундаментом: пересоберите native часть или обновите контракт ABI.");
        }

        Version opencvVersion = new(
            ToVersionComponent(nativeInfo.OpencvMajor),
            ToVersionComponent(nativeInfo.OpencvMinor),
            ToVersionComponent(nativeInfo.OpencvPatch));

        return new NativeBoundaryInfo(
            abiVersion,
            opencvVersion,
            DescribeCapabilities(nativeInfo.Capabilities),
            nativeInfo.BuildFlags,
            BuildInfo());
    }

    /// <summary>
    /// Запрашивает у native библиотеки строку сведений о её сборке.
    /// </summary>
    /// <returns>Строка в формате <c>key=value</c>, разделитель <c>;</c>, без завершающего перевода строки.</returns>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотеку не удалось загрузить.</exception>
    /// <exception cref="AzurPilotNativeBoundaryException">Native сторона вернула код ошибки.</exception>
    public static string BuildInfo()
    {
        int status = RequestBuildInfoSize(out uint requiredSize);
        if (status != StatusBufferTooSmall)
        {
            throw new AzurPilotNativeBoundaryException(
                $"Native функция azurpilot_native_build_info на запросе размера завершилась с кодом "
                + $"{status}: {DescribeStatus(status)}.");
        }

        if (requiredSize == 0)
        {
            throw new AzurPilotNativeBoundaryException(
                "Native функция azurpilot_native_build_info сообщила нулевой требуемый размер строки: "
                + "контракт ABI требует минимум один байт под завершающий NUL.");
        }

        if (requiredSize > MaxStringBytes)
        {
            throw new AzurPilotNativeBoundaryException(
                $"Native функция azurpilot_native_build_info требует {requiredSize} байт под строку "
                + $"сведений, что больше допустимых {MaxStringBytes}: строка не соответствует контракту ABI.");
        }

        byte[] buffer = new byte[requiredSize];

        // Размер строки может измениться между запросом размера и записью, поэтому вызов
        // повторяется с новым буфером; ограничение числа попыток исключает бесконечный цикл.
        for (int attempt = 1; attempt <= MaxBuildInfoAttempts; attempt++)
        {
            status = WriteBuildInfo(buffer, out requiredSize);
            if (status == StatusOk)
            {
                return DecodeString(buffer);
            }

            if (status != StatusBufferTooSmall)
            {
                throw new AzurPilotNativeBoundaryException(
                    $"Native функция azurpilot_native_build_info завершилась с кодом {status}: "
                    + $"{DescribeStatus(status)}.");
            }

            if (requiredSize == 0 || requiredSize > MaxStringBytes)
            {
                throw new AzurPilotNativeBoundaryException(
                    $"Native функция azurpilot_native_build_info требует {requiredSize} байт под строку "
                    + $"сведений, что не соответствует контракту ABI (допустимо 1..{MaxStringBytes}).");
            }

            buffer = new byte[requiredSize];
        }

        throw new AzurPilotNativeBoundaryException(
            $"Native функция azurpilot_native_build_info не записала строку сведений за "
            + $"{MaxBuildInfoAttempts} попытки: размер строки не стабилизировался.");
    }

    /// <summary>Версия ABI, которую сообщает сама native библиотека.</summary>
    /// <returns>Версия ABI native библиотеки.</returns>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотеку не удалось загрузить.</exception>
    public static uint NativeAbiVersion()
    {
        try
        {
            return AzurPilotNativeAbiVersion();
        }
        catch (DllNotFoundException exception)
        {
            throw Unavailable(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw Unavailable(exception);
        }
    }

    /// <summary>Преобразует код возврата native стороны в диагностический текст.</summary>
    /// <param name="status">Код возврата native функции.</param>
    /// <returns>Текст на русском языке.</returns>
    public static string DescribeStatus(int status) => status switch
    {
        StatusOk => "успех",
        StatusInvalidArgument => "некорректные аргументы вызова",
        StatusBufferTooSmall => "предоставленный буфер меньше требуемого размера",
        StatusOpencvFailure => "код OpenCV выбросил исключение",
        StatusInternal => "внутренняя ошибка native стороны",
        _ => "неизвестный код возврата",
    };

    /// <summary>
    /// Проверяет, что managed объявление структуры совпадает с раскладкой из заголовка ABI,
    /// и возвращает фактический размер managed структуры в байтах.
    /// </summary>
    /// <returns>Фактический размер managed структуры сведений о native boundary.</returns>
    /// <exception cref="AzurPilotNativeBoundaryException">Раскладка managed структуры не совпадает.</exception>
    public static int VerifyStructLayout()
    {
        // sizeof вместо Marshal.SizeOf: раскладка managed структуры не должна зависеть от
        // runtime marshalling, который в проекте выключен осознанно. Структура содержит только
        // blittable-поля, поэтому её managed размер совпадает с размером по заголовку ABI.
        int size;
        unsafe
        {
            size = sizeof(NativeInfo);
        }

        if (size != NativeInfoSizeInBytes)
        {
            throw new AzurPilotNativeBoundaryException(
                $"Раскладка managed структуры сведений о native boundary изменилась: размер {size} байт, "
                + $"а заголовок ABI требует {NativeInfoSizeInBytes} байт. Изменение формы ABI требует "
                + "нового номера версии ABI.");
        }

        return size;
    }

    private static NativeBoundaryUnavailableException Unavailable(Exception exception)
    {
        return new NativeBoundaryUnavailableException(
            $"Native библиотека «{LibraryName}» не загружена: {exception.Message} Соберите native часть "
            + "через CMake preset и повторите managed сборку: Directory.Build.targets автоматически "
            + "копирует native runtime в выход проекта.",
            exception);
    }

    /// <summary>
    /// Запрашивает у native стороны требуемый размер строки сведений о сборке.
    /// </summary>
    /// <param name="requiredSize">Требуемый размер строки в байтах вместе с завершающим NUL.</param>
    /// <returns>Код возврата native стороны.</returns>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотеку не удалось загрузить.</exception>
    private static int RequestBuildInfoSize(out uint requiredSize)
    {
        try
        {
            return BuildInfoNative(null, 0, out requiredSize);
        }
        catch (DllNotFoundException exception)
        {
            throw Unavailable(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw Unavailable(exception);
        }
    }

    /// <summary>
    /// Просит native сторону записать строку сведений о сборке в предоставленный буфер.
    /// </summary>
    /// <param name="buffer">Буфер вызывающей стороны.</param>
    /// <param name="requiredSize">Требуемый размер строки в байтах вместе с завершающим NUL.</param>
    /// <returns>Код возврата native стороны.</returns>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотеку не удалось загрузить.</exception>
    private static int WriteBuildInfo(byte[] buffer, out uint requiredSize)
    {
        try
        {
            return BuildInfoNative(buffer, (uint)buffer.Length, out requiredSize);
        }
        catch (DllNotFoundException exception)
        {
            throw Unavailable(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw Unavailable(exception);
        }
    }

    private static int ToVersionComponent(uint value)
    {
        if (value > int.MaxValue)
        {
            throw new AzurPilotNativeBoundaryException(
                $"Native библиотека сообщила компонент версии OpenCV {value}, который не представим "
                + "в managed типе Version: сведения о native boundary некорректны.");
        }

        return (int)value;
    }

    private static string[] DescribeCapabilities(uint capabilities)
    {
        List<string> names = [];
        if ((capabilities & CapabilityCore) != 0)
        {
            names.Add("core");
        }

        if ((capabilities & CapabilityImgcodecs) != 0)
        {
            names.Add("imgcodecs");
        }

        return [.. names];
    }

    private static string DecodeString(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            throw new AzurPilotNativeBoundaryException(
                "Native функция azurpilot_native_build_info вернула успех, но не завершила строку "
                + "завершающим NUL: контракт ABI нарушен.");
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_abi_version")]
    private static partial uint AzurPilotNativeAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_query")]
    private static partial int QueryNative(out NativeInfo outInfo);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_build_info")]
    private static partial int BuildInfoNative(byte[]? buffer, uint bufferSize, out uint requiredSize);

    /// <summary>
    /// Managed объявление <c>AzurPilotNativeInfo</c>. Раскладка заморожена заголовком ABI:
    /// смещения 0, 4, 8, 12, 16, 20 и 24..55, выравнивание 4, полный размер 56 байт.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        /// <summary>Размер буфера строки версии OpenCV по заголовку ABI.</summary>
        private const int VersionStringCapacity = 32;

        /// <summary>Версия ABI native библиотеки.</summary>
        internal uint AbiVersion;

        /// <summary>Major версии OpenCV, с которой собрана native библиотека.</summary>
        internal uint OpencvMajor;

        /// <summary>Minor версии OpenCV, с которой собрана native библиотека.</summary>
        internal uint OpencvMinor;

        /// <summary>Patch версии OpenCV, с которой собрана native библиотека.</summary>
        internal uint OpencvPatch;

        /// <summary>Биты признаков сборки native библиотеки.</summary>
        internal uint BuildFlags;

        /// <summary>Биты доступных capability.</summary>
        internal uint Capabilities;

        /// <summary>Версия OpenCV строкой; поле существует для побайтового совпадения с заголовком ABI.</summary>
        internal FixedVersionString VersionString;

        [InlineArray(VersionStringCapacity)]
        internal struct FixedVersionString
        {
            private byte _element0;
        }
    }
}
