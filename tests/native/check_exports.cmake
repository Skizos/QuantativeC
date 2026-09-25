# Compares the exported (dynamic, defined) symbols of the qe shared library with the functions
# declared QE_API in qe_api.h. Fails on anything missing or unexpected.
# Usage: cmake -DNM=<nm> -DLIBRARY=<libqe.so> -DHEADER=<qe_api.h> -P check_exports.cmake
foreach(var NM LIBRARY HEADER)
    if(NOT DEFINED ${var})
        message(FATAL_ERROR "${var} is required")
    endif()
endforeach()

file(READ "${HEADER}" header_text)
string(REGEX MATCHALL "QE_API[ \t\r\n]+qe_status[ \t\r\n]+QE_CALL[ \t\r\n]+[a-z_0-9]+" decls "${header_text}")
set(expected "")
foreach(decl IN LISTS decls)
    string(REGEX REPLACE ".*QE_CALL[ \t\r\n]+" "" name "${decl}")
    list(APPEND expected "${name}")
endforeach()
list(SORT expected)
if(NOT expected)
    message(FATAL_ERROR "No QE_API declarations found in ${HEADER}")
endif()

execute_process(COMMAND "${NM}" -D --defined-only "${LIBRARY}"
    OUTPUT_VARIABLE nm_out RESULT_VARIABLE nm_rc)
if(NOT nm_rc EQUAL 0)
    message(FATAL_ERROR "nm failed (${nm_rc}) on ${LIBRARY}")
endif()

# Text (T), weak (W/V) and unique (u) symbols are real exports; linker-provided _init/_fini are allowed.
string(REPLACE "\n" ";" nm_lines "${nm_out}")
set(actual "")
foreach(line IN LISTS nm_lines)
    if(line MATCHES "^[0-9a-fA-F]+ [TWVu] (.+)$")
        set(sym "${CMAKE_MATCH_1}")
        if(NOT sym MATCHES "^(_init|_fini)$")
            list(APPEND actual "${sym}")
        endif()
    endif()
endforeach()
list(SORT actual)

set(missing ${expected})
list(REMOVE_ITEM missing ${actual})
set(unexpected ${actual})
list(REMOVE_ITEM unexpected ${expected})

message(STATUS "expected exports: ${expected}")
if(missing OR unexpected)
    message(FATAL_ERROR "Export mismatch.\n  missing: ${missing}\n  unexpected: ${unexpected}")
endif()
message(STATUS "qe exports match qe_api.h (${LIBRARY})")
