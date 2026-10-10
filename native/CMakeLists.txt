# Native boundary: Windows x64, C++20, OpenCV и замороженный C ABI.
# CMake владеет native build graph; стандартные presets задают generator и конфигурации.
cmake_minimum_required(VERSION 4.4.2)

project(AzurPilot.Native LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Native boundary поддерживает только Windows x64 с MSVC. Используйте native-x64 preset.")
endif()
if(MSVC_TOOLSET_VERSION LESS 145)
  message(FATAL_ERROR "MSVC toolset ${MSVC_TOOLSET_VERSION} не удовлетворяет минимуму v145 (native/CMakeLists.txt). Установите Visual Studio 2026 с C++ tools.")
endif()
if(NOT CMAKE_CXX_COMPILER_VERSION VERSION_GREATER_EQUAL 19.51)
  message(FATAL_ERROR "MSVC ${CMAKE_CXX_COMPILER_VERSION} не удовлетворяет минимуму 19.51 (native/CMakeLists.txt). Обновите C++ toolset Visual Studio.")
endif()

option(AZURPILOT_NATIVE_ENABLE_ASAN "Собрать изолированные native targets с MSVC AddressSanitizer" OFF)
set(AZURPILOT_NATIVE_ARTIFACT_DIR
  "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native"
  CACHE PATH "Корень артефактов native сборки")
if(AZURPILOT_NATIVE_ENABLE_ASAN AND NOT MSVC)
  message(FATAL_ERROR "AddressSanitizer для native boundary поддерживается только MSVC.")
endif()

# Dependency acquisition — узкий native контракт: manifest, SHA256, распаковка.
# Каждый configure проверяет hash, в том числе для кэшированного CI артефакта.
include(cmake/OpenCvDependency.cmake)
find_package(OpenCV "${AZURPILOT_OPENCV_VERSION}" EXACT REQUIRED CONFIG)

if(TARGET OpenCV::opencv_world)
  set(AZURPILOT_OPENCV_WORLD_TARGET OpenCV::opencv_world)
elseif(TARGET opencv_world)
  set(AZURPILOT_OPENCV_WORLD_TARGET opencv_world)
else()
  message(FATAL_ERROR "OpenCVConfig.cmake не предоставил world target (OpenCV::opencv_world или opencv_world): ${OpenCV_DIR}")
endif()

string(REPLACE "." ";" AZURPILOT_OPENCV_VERSION_PARTS "${AZURPILOT_OPENCV_VERSION}")
list(GET AZURPILOT_OPENCV_VERSION_PARTS 0 AZURPILOT_OPENCV_VERSION_MAJOR)
list(GET AZURPILOT_OPENCV_VERSION_PARTS 1 AZURPILOT_OPENCV_VERSION_MINOR)
list(GET AZURPILOT_OPENCV_VERSION_PARTS 2 AZURPILOT_OPENCV_VERSION_PATCH)

set(AZURPILOT_NATIVE_OUTPUT_DIR "${AZURPILOT_NATIVE_ARTIFACT_DIR}/bin")
set(AZURPILOT_NATIVE_RUNTIME_DIR "${AZURPILOT_NATIVE_ARTIFACT_DIR}/runtime")

function(azurpilot_native_apply_compile_options target)
  target_compile_options(${target} PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
  target_compile_features(${target} PRIVATE cxx_std_20)
  if(AZURPILOT_NATIVE_ENABLE_ASAN)
    target_compile_options(${target} PRIVATE /fsanitize=address /Zi)
    target_link_options(${target} PRIVATE /DEBUG /INCREMENTAL:NO)
  endif()
endfunction()

add_library(AzurPilot.Native SHARED src/azurpilot_native.cpp src/native_frame.cpp src/opencv_probe.cpp)
target_include_directories(AzurPilot.Native
  PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_link_libraries(AzurPilot.Native PRIVATE "${AZURPILOT_OPENCV_WORLD_TARGET}")
azurpilot_native_apply_compile_options(AzurPilot.Native)
set_target_properties(AzurPilot.Native PROPERTIES
  OUTPUT_NAME "AzurPilot.Native"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>")
if(AZURPILOT_NATIVE_ENABLE_ASAN)
  get_filename_component(AZURPILOT_MSVC_COMPILER_DIR "${CMAKE_CXX_COMPILER}" DIRECTORY)
  set(AZURPILOT_ASAN_RUNTIME_FILE
    "${AZURPILOT_MSVC_COMPILER_DIR}/clang_rt.asan_dynamic-x86_64.dll")
  if(NOT EXISTS "${AZURPILOT_ASAN_RUNTIME_FILE}")
    message(FATAL_ERROR "MSVC AddressSanitizer runtime не найден рядом с компилятором: ${AZURPILOT_ASAN_RUNTIME_FILE}")
  endif()
  # Тестовые EXE и DLL находятся в одном изолированном каталоге; runtime не попадает в обычный staging.
  add_custom_command(TARGET AzurPilot.Native POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
      "${AZURPILOT_ASAN_RUNTIME_FILE}" "$<TARGET_FILE_DIR:AzurPilot.Native>"
    COMMENT "Staging MSVC AddressSanitizer runtime в изолированный каталог"
    VERBATIM)
endif()

# Staging входит в native build graph и исполняется также при инкрементальной сборке.
# Выбирается DLL импортированного target для текущей конфигурации, без wildcard по пакету.
add_custom_target(AzurPilot.Native.Runtime ALL
  COMMAND "${CMAKE_COMMAND}"
    "-DNATIVE_FILE=$<TARGET_FILE:AzurPilot.Native>"
    "-DNATIVE_PDB=$<TARGET_PDB_FILE:AzurPilot.Native>"
    "-DOPENCV_FILE=$<TARGET_FILE:${AZURPILOT_OPENCV_WORLD_TARGET}>"
    "-DOPENCV_RUNTIME_DIR=${AZURPILOT_OPENCV_RUNTIME_DIR}"
    "-DRUNTIME_DIR=${AZURPILOT_NATIVE_RUNTIME_DIR}/$<CONFIG>"
    -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/StageNativeRuntime.cmake"
  DEPENDS AzurPilot.Native
  COMMENT "Staging native runtime для managed проектов и CTest"
  VERBATIM)

add_executable(AzurPilot.Native.SmokeTest tests/native_smoke_test.cpp)
target_compile_definitions(AzurPilot.Native.SmokeTest PRIVATE
  AZURPILOT_NATIVE_IMPORT
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_MAJOR=${AZURPILOT_OPENCV_VERSION_MAJOR}"
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_MINOR=${AZURPILOT_OPENCV_VERSION_MINOR}"
  "AZURPILOT_NATIVE_EXPECTED_OPENCV_PATCH=${AZURPILOT_OPENCV_VERSION_PATCH}")
target_link_libraries(AzurPilot.Native.SmokeTest PRIVATE AzurPilot.Native)
azurpilot_native_apply_compile_options(AzurPilot.Native.SmokeTest)
add_dependencies(AzurPilot.Native.SmokeTest AzurPilot.Native.Runtime)
set_target_properties(AzurPilot.Native.SmokeTest PROPERTIES
  OUTPUT_NAME "AzurPilot.Native.SmokeTest"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>")

enable_testing()
add_test(NAME native_abi_smoke COMMAND AzurPilot.Native.SmokeTest)
set_tests_properties(native_abi_smoke PROPERTIES TIMEOUT 300 FAIL_REGULAR_EXPRESSION "\\[FAIL\\]")
set_tests_properties(native_abi_smoke PROPERTIES WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}")

# Тест внутреннего decoder/ownership-модуля: counters компилируются только в этом test executable.
add_executable(AzurPilot.Native.FrameOwnershipTest
  tests/native_frame_ownership_test.cpp
  src/native_frame.cpp)
target_include_directories(AzurPilot.Native.FrameOwnershipTest PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_link_libraries(AzurPilot.Native.FrameOwnershipTest PRIVATE "${AZURPILOT_OPENCV_WORLD_TARGET}")
target_compile_definitions(AzurPilot.Native.FrameOwnershipTest PRIVATE AZURPILOT_NATIVE_TESTING)
azurpilot_native_apply_compile_options(AzurPilot.Native.FrameOwnershipTest)
add_dependencies(AzurPilot.Native.FrameOwnershipTest AzurPilot.Native.Runtime)
set_target_properties(AzurPilot.Native.FrameOwnershipTest PROPERTIES
  OUTPUT_NAME "AzurPilot.Native.FrameOwnershipTest"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>")
add_test(NAME native_frame_ownership COMMAND AzurPilot.Native.FrameOwnershipTest)
set_tests_properties(native_frame_ownership PROPERTIES
  TIMEOUT 300
  FAIL_REGULAR_EXPRESSION "\\[FAIL\\]"
  WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}")

# Изолированная несовместимая DLL нужна негативному managed тесту и никогда не
# участвует в staging production runtime. Отдельный каталог исключает конфликт import libraries.
add_library(AzurPilot.Native.AbiMismatchFixture SHARED tests/abi_mismatch_fixture.cpp)
target_include_directories(AzurPilot.Native.AbiMismatchFixture PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
azurpilot_native_apply_compile_options(AzurPilot.Native.AbiMismatchFixture)
set_target_properties(AzurPilot.Native.AbiMismatchFixture PROPERTIES
  OUTPUT_NAME "AzurPilot.Native"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_ARTIFACT_DIR}/abi-mismatch/$<CONFIG>"
  ARCHIVE_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_ARTIFACT_DIR}/abi-mismatch/$<CONFIG>"
  PDB_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_ARTIFACT_DIR}/abi-mismatch/$<CONFIG>")
