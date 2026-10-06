/* =============================================================================
 * native_smoke_test.cpp — project-owned native smoke test замороженного C ABI v1.
 *
 * Тест линкуется с AzurPilot.Native.dll через import library и вызывает ровно те
 * экспорты, которые объявлены в native/include/azurpilot_native_abi.h. Заголовки
 * OpenCV здесь не подключаются: единственный канал к native-коду — C ABI, и это
 * доказывается тем, что тест физически не может воспользоваться ничем другим.
 *
 * Проверяется:
 *   1. замороженная раскладка AzurPilotNativeInfo (полный размер 56 байт);
 *   2. совпадение версии ABI с нормативным заголовком границы;
 *   3. реальное исполнение OpenCV через azurpilot_native_query: версия OpenCV из
 *      пина, подтверждённый факт исполнения в build_flags, capability core и imgcodecs;
 *   4. корректная обработка слишком маленького буфера в azurpilot_native_build_info:
 *      возвращается требуемый размер, буфер не переполняется и не изменяется ни на байт;
 *   5. коды возврата на некорректных аргументах.
 *
 * Тест не является заглушкой: без собранной native DLL и без реально исполнившегося
 * OpenCV-кода он падает.
 * =============================================================================
 */

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

#include "azurpilot_native_abi.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

/* Ожидаемая версия OpenCV приходит из native/opencv.json через CMake: копии пина в тесте нет. */
#if !defined(AZURPILOT_NATIVE_EXPECTED_OPENCV_MAJOR) || \
    !defined(AZURPILOT_NATIVE_EXPECTED_OPENCV_MINOR) || !defined(AZURPILOT_NATIVE_EXPECTED_OPENCV_PATCH)
#error "Не задана ожидаемая версия OpenCV: тест собирается только через CMake-проект native/."
#endif

#define AZURPILOT_TEST_STRINGIFY_IMPL(value) #value
#define AZURPILOT_TEST_STRINGIFY(value) AZURPILOT_TEST_STRINGIFY_IMPL(value)

namespace {

int g_checks = 0;
int g_failures = 0;

/* Ожидаемая строка версии OpenCV, собранная из чисел пина на этапе компиляции. */
const char* const kExpectedOpencvVersion = AZURPILOT_TEST_STRINGIFY(AZURPILOT_NATIVE_EXPECTED_OPENCV_MAJOR) "." AZURPILOT_TEST_STRINGIFY(
    AZURPILOT_NATIVE_EXPECTED_OPENCV_MINOR) "." AZURPILOT_TEST_STRINGIFY(AZURPILOT_NATIVE_EXPECTED_OPENCV_PATCH);

void check(bool condition, const char* description) {
    ++g_checks;
    if (condition) {
        printf("  [OK]   %s\n", description);
    } else {
        ++g_failures;
        printf("  [FAIL] %s\n", description);
    }
}

void check_uint32(uint32_t actual, uint32_t expected, const char* description) {
    ++g_checks;
    if (actual == expected) {
        printf("  [OK]   %s (значение=%u)\n", description, static_cast<unsigned>(actual));
    } else {
        ++g_failures;
        printf("  [FAIL] %s: получено %u, ожидалось %u\n", description, static_cast<unsigned>(actual),
               static_cast<unsigned>(expected));
    }
}

void check_string(const char* actual, const char* expected, const char* description) {
    ++g_checks;
    if (actual != nullptr && strcmp(actual, expected) == 0) {
        printf("  [OK]   %s (\"%s\")\n", description, actual);
    } else {
        ++g_failures;
        printf("  [FAIL] %s: получено \"%s\", ожидалось \"%s\"\n", description,
               actual != nullptr ? actual : "<NULL>", expected);
    }
}

/* Проверяет, что байты за пределами записанной строки не изменены. */
void check_bytes_untouched(const unsigned char* actual, const unsigned char* snapshot, size_t size,
                           const char* description) {
    check(memcmp(actual, snapshot, size) == 0, description);
}

}  // namespace

int main() {
#if defined(_WIN32)
    // Русская диагностика выводится в UTF-8: консоль переключается на UTF-8, чтобы
    // вывод читался и при ручном запуске, и в логе CTest.
    (void)SetConsoleOutputCP(CP_UTF8);
#endif

    printf("native smoke test: C ABI v%d, ожидаемая версия OpenCV %s\n",
           static_cast<int>(AZURPILOT_NATIVE_ABI_VERSION), kExpectedOpencvVersion);

    // --- 1. Раскладка структуры -------------------------------------------------
    printf("\n[1/6] Замороженная раскладка AzurPilotNativeInfo\n");
    check_uint32(static_cast<uint32_t>(sizeof(AzurPilotNativeInfo)), 56u,
                 "sizeof(AzurPilotNativeInfo) == 56 (владелец раскладки — заголовок ABI)");

    // --- 2. Версия ABI ----------------------------------------------------------
    printf("\n[2/6] Версия ABI\n");
    check_uint32(azurpilot_native_abi_version(), static_cast<uint32_t>(AZURPILOT_NATIVE_ABI_VERSION),
                 "azurpilot_native_abi_version() возвращает версию ABI из нормативного заголовка");

    // --- 3. Реальное исполнение OpenCV через C ABI ------------------------------
    printf("\n[3/6] azurpilot_native_query: реальное исполнение OpenCV\n");
    check_uint32(static_cast<uint32_t>(azurpilot_native_query(nullptr)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT),
                 "query(NULL) возвращает INVALID_ARGUMENT");

    AzurPilotNativeInfo info{};
    check_uint32(static_cast<uint32_t>(azurpilot_native_query(&info)), static_cast<uint32_t>(AZURPILOT_NATIVE_OK),
                 "query(&info) возвращает OK");
    check_uint32(info.abi_version, static_cast<uint32_t>(AZURPILOT_NATIVE_ABI_VERSION),
                 "abi_version в структуре совпадает с нормативным заголовком");
    check_uint32(info.opencv_major, static_cast<uint32_t>(AZURPILOT_NATIVE_EXPECTED_OPENCV_MAJOR),
                 "opencv_major совпадает с закреплённой линией OpenCV");
    check_uint32(info.opencv_minor, static_cast<uint32_t>(AZURPILOT_NATIVE_EXPECTED_OPENCV_MINOR),
                 "opencv_minor совпадает с закреплённой линией OpenCV");
    check_uint32(info.opencv_patch, static_cast<uint32_t>(AZURPILOT_NATIVE_EXPECTED_OPENCV_PATCH),
                 "opencv_patch совпадает с закреплённой линией OpenCV");
    check_string(info.opencv_version_string, kExpectedOpencvVersion,
                 "opencv_version_string совпадает с закреплённой версией OpenCV");
    check(info.opencv_version_string[0] != '\0', "opencv_version_string непустая");
    check(memchr(info.opencv_version_string, '\0', sizeof(info.opencv_version_string)) != nullptr,
          "opencv_version_string NUL-терминирована внутри массива");

    check((info.build_flags & AZURPILOT_NATIVE_BUILD_FLAG_OPENCV_EXECUTED) != 0u,
          "build_flags подтверждает фактическое исполнение OpenCV-кода в этом вызове");
    check((info.build_flags & ~AZURPILOT_NATIVE_BUILD_FLAG_OPENCV_EXECUTED) == 0u,
          "build_flags не содержит неизвестных битов");
    check((info.capabilities & AZURPILOT_NATIVE_CAPABILITY_CORE) != 0u, "capabilities содержит core");
    check((info.capabilities & AZURPILOT_NATIVE_CAPABILITY_IMGCODECS) != 0u, "capabilities содержит imgcodecs");
    check((info.capabilities & ~(AZURPILOT_NATIVE_CAPABILITY_CORE | AZURPILOT_NATIVE_CAPABILITY_IMGCODECS)) == 0u,
          "capabilities не содержит неизвестных битов");

    // --- 4. Повторный вызов: нет изменяемого состояния ---------------------------
    printf("\n[4/6] Повторный вызов: отсутствие изменяемого состояния\n");
    AzurPilotNativeInfo second{};
    check_uint32(static_cast<uint32_t>(azurpilot_native_query(&second)), static_cast<uint32_t>(AZURPILOT_NATIVE_OK),
                 "повторный query(&info) возвращает OK");
    check(memcmp(&info, &second, sizeof(info)) == 0,
          "повторный вызов даёт побайтово идентичный результат (вызовы не зависят от порядка и состояния)");

    // --- 5. build_info: размер-запрос и слишком маленький буфер ------------------
    printf("\n[5/6] azurpilot_native_build_info: размер-запрос и слишком маленький буфер\n");
    uint32_t required = 0u;
    check_uint32(static_cast<uint32_t>(azurpilot_native_build_info(nullptr, 0u, &required)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL),
                 "вызов (NULL, 0) — это размер-запрос, возвращается BUFFER_TOO_SMALL");
    check(required > 1u, "required_size заполнен и включает завершающий NUL");
    const uint32_t queried_required = required;

    unsigned char small_buffer[4];
    unsigned char small_snapshot[4];
    memset(small_buffer, 0xAB, sizeof(small_buffer));
    memcpy(small_snapshot, small_buffer, sizeof(small_buffer));

    uint32_t small_required = 0u;
    check_uint32(static_cast<uint32_t>(azurpilot_native_build_info(reinterpret_cast<char*>(small_buffer),
                                                                   static_cast<uint32_t>(sizeof(small_buffer)),
                                                                   &small_required)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_ERROR_BUFFER_TOO_SMALL),
                 "слишком маленький буфер возвращает BUFFER_TOO_SMALL");
    check_bytes_untouched(small_buffer, small_snapshot, sizeof(small_buffer),
                          "при BUFFER_TOO_SMALL в буфер не записан ни один байт (нет частичной записи)");
    check_uint32(small_required, queried_required, "required_size не зависит от размера буфера");

    // --- 6. build_info: точный и увеличенный буфер, некорректные аргументы --------
    printf("\n[6/6] azurpilot_native_build_info: точный буфер, увеличенный буфер, аргументы\n");
    char expected_info[128];
    const int expected_written =
        snprintf(expected_info, sizeof(expected_info), "abi_version=%d;opencv_version=%s",
                 static_cast<int>(AZURPILOT_NATIVE_ABI_VERSION), kExpectedOpencvVersion);
    check(expected_written > 0 && static_cast<size_t>(expected_written) < sizeof(expected_info),
          "ожидаемая строка build_info собрана");

    char exact_buffer[512];
    char exact_snapshot[512];
    memset(exact_buffer, 0x5A, sizeof(exact_buffer));
    memcpy(exact_snapshot, exact_buffer, sizeof(exact_buffer));

    uint32_t exact_required = 0u;
    check(queried_required <= sizeof(exact_buffer), "требуемый размер укладывается в тестовый буфер");
    check_uint32(static_cast<uint32_t>(azurpilot_native_build_info(exact_buffer, queried_required, &exact_required)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_OK), "буфер точного размера возвращает OK");
    check_uint32(exact_required, queried_required, "required_size равен значению размер-запроса");
    check(exact_buffer[queried_required - 1u] == '\0', "строка завершена NUL на последнем байте точного буфера");
    check_bytes_untouched(reinterpret_cast<const unsigned char*>(exact_buffer) + queried_required,
                          reinterpret_cast<const unsigned char*>(exact_snapshot) + queried_required,
                          sizeof(exact_buffer) - queried_required,
                          "за пределами строки буфер точного размера не изменён");
    check_string(exact_buffer, expected_info, "строка build_info содержит abi_version и opencv_version из пина");
    check(strchr(exact_buffer, '\n') == nullptr, "строка не содержит завершающего перевода строки");

    char large_buffer[512];
    char large_snapshot[512];
    memset(large_buffer, 0x5A, sizeof(large_buffer));
    memcpy(large_snapshot, large_buffer, sizeof(large_buffer));

    uint32_t large_required = 0u;
    check_uint32(static_cast<uint32_t>(azurpilot_native_build_info(large_buffer,
                                                                   static_cast<uint32_t>(sizeof(large_buffer)),
                                                                   &large_required)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_OK), "буфер больше требуемого возвращает OK");
    check_uint32(large_required, queried_required, "required_size одинаков для обоих размеров буфера");
    check_string(large_buffer, expected_info, "строка в увеличенном буфере совпадает с ожидаемой");
    check_bytes_untouched(reinterpret_cast<const unsigned char*>(large_buffer) + queried_required,
                          reinterpret_cast<const unsigned char*>(large_snapshot) + queried_required,
                          sizeof(large_buffer) - queried_required,
                          "за пределами строки увеличенный буфер не изменён");

    unsigned char guard_buffer[8];
    unsigned char guard_snapshot[8];
    memset(guard_buffer, 0x11, sizeof(guard_buffer));
    memcpy(guard_snapshot, guard_buffer, sizeof(guard_buffer));
    check_uint32(static_cast<uint32_t>(azurpilot_native_build_info(reinterpret_cast<char*>(guard_buffer),
                                                                   static_cast<uint32_t>(sizeof(guard_buffer)),
                                                                   nullptr)),
                 static_cast<uint32_t>(AZURPILOT_NATIVE_ERROR_INVALID_ARGUMENT),
                 "required_size == NULL возвращает INVALID_ARGUMENT");
    check_bytes_untouched(guard_buffer, guard_snapshot, sizeof(guard_buffer),
                          "при INVALID_ARGUMENT буфер не изменён");

    // --- Итог -------------------------------------------------------------------
    printf("\nИтог: проверок %d, провалено %d\n", g_checks, g_failures);
    if (g_failures != 0) {
        printf("native smoke test: ПРОВАЛ\n");
        return 1;
    }
    printf("native smoke test: УСПЕХ — project-owned native код исполнен через C ABI, OpenCV реально работал\n");
    return 0;
}
