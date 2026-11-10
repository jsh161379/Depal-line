# 디팔 서버 관리 화면 (서버 UI) - Qt 6 Widgets, C++17
cmake_minimum_required(VERSION 3.21)
project(DepalServerUi VERSION 0.1 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Qt6 REQUIRED COMPONENTS Widgets Network)
qt_standard_project_setup()

qt_add_executable(depal_server_ui WIN32
    src/main.cpp
    src/AppConfig.h
    src/RunnerClient.h src/RunnerClient.cpp
    src/PortProbe.h src/PortProbe.cpp
    src/ProgramsPage.h src/ProgramsPage.cpp
    src/MainWindow.h src/MainWindow.cpp
)

target_link_libraries(depal_server_ui PRIVATE Qt6::Widgets Qt6::Network)

if(MSVC)
    # 소스 파일의 한글을 UTF-8로 읽게 함 (없으면 한글 문자열이 깨짐)
    target_compile_options(depal_server_ui PRIVATE /utf-8)
endif()
