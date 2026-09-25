find_package(benchmark CONFIG REQUIRED)

add_executable(qe_bench bs_bench.cpp)
target_link_libraries(qe_bench PRIVATE qe qe_impl benchmark::benchmark)
target_include_directories(qe_bench PRIVATE "${PROJECT_SOURCE_DIR}/native/src")
qe_set_warnings(qe_bench)
