# qe_set_warnings(<target>): strict warnings for our own targets only (never third-party code).
function(qe_set_warnings target)
    if(MSVC)
        target_compile_options(${target} PRIVATE
            /W4 /utf-8
            $<$<COMPILE_LANGUAGE:CXX>:/permissive- /Zc:__cplusplus>
            $<$<BOOL:${QE_WARNINGS_AS_ERRORS}>:/WX>)
    else()
        target_compile_options(${target} PRIVATE
            -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion
            -Wdouble-promotion -Wformat=2 -Wundef
            $<$<COMPILE_LANGUAGE:CXX>:-Wold-style-cast -Wnon-virtual-dtor -Woverloaded-virtual>
            $<$<BOOL:${QE_WARNINGS_AS_ERRORS}>:-Werror>)
        # Debug builds check std::span/vector bounds like MSVC's checked iterators do (no ABI change).
        target_compile_definitions(${target} PRIVATE $<$<CONFIG:Debug>:_GLIBCXX_ASSERTIONS>)
    endif()
endfunction()
