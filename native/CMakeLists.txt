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

set(AZURPILOT_NATIVE_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/bin")
set(AZURPILOT_NATIVE_RUNTIME_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/runtime")

function(azurpilot_native_apply_compile_options target)
  target_compile_options(${target} PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
  target_compile_features(${target} PRIVATE cxx_std_20)
endfunction()

add_library(AzurPilot.Native SHARED src/azurpilot_native.cpp src/opencv_probe.cpp)
target_include_directories(AzurPilot.Native
  PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_link_libraries(AzurPilot.Native PRIVATE "${AZURPILOT_OPENCV_WORLD_TARGET}")
azurpilot_native_apply_compile_options(AzurPilot.Native)
set_target_properties(AzurPilot.Native PROPERTIES
  OUTPUT_NAME "AzurPilot.Native"
  RUNTIME_OUTPUT_DIRECTORY "${AZURPILOT_NATIVE_OUTPUT_DIR}/$<CONFIG>")

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

# Изолированная несовместимая DLL нужна негативному managed тесту и никогда не
# участвует в staging production runtime. Отдельный каталог исключает конфликт import libraries.
add_library(AzurPilot.Native.AbiMismatchFixture SHARED tests/abi_mismatch_fixture.cpp)
target_include_directories(AzurPilot.Native.AbiMismatchFixture PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
azurpilot_native_apply_compile_options(AzurPilot.Native.AbiMismatchFixture)
set_target_properties(AzurPilot.Native.AbiMismatchFixture PROPERTIES
  OUTPUT_NAME "AzurPilot.Native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/abi-mismatch/$<CONFIG>"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/abi-mismatch/$<CONFIG>"
  PDB_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native/abi-mismatch/$<CONFIG>")
