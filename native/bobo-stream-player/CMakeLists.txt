cmake_minimum_required(VERSION 3.16)
project(BoBoStreamPlayer LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(BOBO_WITH_MOONLIGHT_COMMON_C "Link moonlight-common-c (GPL-3.0) for live GameStream RTP/RTSP playback" OFF)

add_executable(BoBoStreamPlayer
    src/main.cpp
    src/ipc_protocol.cpp
    src/d3d11_renderer.cpp
    src/mf_decoder.cpp
    src/moonlight_bridge.cpp
)

if(MSVC)
    target_compile_options(BoBoStreamPlayer PRIVATE /W4 /WX /utf-8)
endif()

target_link_libraries(BoBoStreamPlayer PRIVATE
    d3d11
    dxgi
    mfplat
    mfuuid
    mf
    strmiids
    ole32
    user32
    ws2_32
    winmm
)

if(BOBO_WITH_MOONLIGHT_COMMON_C)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/third_party/moonlight-common-c/CMakeLists.txt")
        set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
        add_subdirectory(third_party/moonlight-common-c)
        target_link_libraries(BoBoStreamPlayer PRIVATE moonlight-common-c)
        target_compile_definitions(BoBoStreamPlayer PRIVATE BOBO_WITH_MOONLIGHT_COMMON_C=1)
    else()
        message(FATAL_ERROR "moonlight-common-c not found under third_party/moonlight-common-c")
    endif()
endif()
