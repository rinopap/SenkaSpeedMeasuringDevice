cmake_minimum_required(VERSION 3.21)
project(SenkaSpeedMeasuringDevice VERSION 2.0.0 LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "This application targets Windows 10/11.")
endif()

find_package(Qt6 6.8 REQUIRED COMPONENTS Widgets)

add_executable(SenkaSpeedMeasuringDevice WIN32 main.cpp)
target_compile_features(SenkaSpeedMeasuringDevice PRIVATE cxx_std_20)
target_link_libraries(SenkaSpeedMeasuringDevice PRIVATE Qt6::Widgets Advapi32)
if(MSVC)
  target_compile_options(SenkaSpeedMeasuringDevice PRIVATE /W4 /utf-8 /permissive-)
endif()

