# ────────────────────────────────────────────────────────────────────────
# libhartonomous codegen
#
# Self-contained C tools that read canonical Unicode / ISO / CLDR sources
# and emit static `lh_*.{h,c}` table files into ../src/generated/.
# These tools are NOT part of the runtime library — they are build-time
# utilities. The committed contents of src/generated/ are the spec.
#
# Targets:
#   codegen   — builds every gen_* executable (default OFF — opt-in build).
#   generate  — runs every gen_* executable, writing into ../src/generated/.
#   gen_check — runs into a scratch dir and `cmp`-diffs against committed.
# ────────────────────────────────────────────────────────────────────────

option(HARTONOMOUS_BUILD_CODEGEN
    "Build libhartonomous codegen utilities (developer-only)"
    OFF)

if(NOT HARTONOMOUS_BUILD_CODEGEN)
    return()
endif()

# Directory where generators emit lh_*.{h,c}.
set(LH_GENERATED_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../src/generated")

# Where unzipped UCD XML lives at codegen time. CMake unzips on demand so
# the .gitignored `_unicode_extracted/` directory carries no source.
set(LH_UCD_ROOT "/vault/Data/Unicode/Public/UCD/latest"
    CACHE PATH "Root of the canonical UCD/UCA/security/idna data tree")
set(LH_UCD_EXTRACT_DIR "${CMAKE_BINARY_DIR}/_unicode_extracted")

# Shared codegen support objects used by every gen_* tool.
set(LH_CODEGEN_SUPPORT_SOURCES
    lh_input.c
    lh_emit.c
    xml_pull.c
)
add_library(lh_codegen_support STATIC ${LH_CODEGEN_SUPPORT_SOURCES})
target_include_directories(lh_codegen_support PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(lh_codegen_support PRIVATE -Wall -Wextra -Wpedantic)

# Aggregate "build all generators" + "run all generators" targets.
add_custom_target(codegen)
add_custom_target(generate)

# Macro that registers a gen_* tool: builds it, attaches to `codegen`,
# wires a custom command into `generate`.
function(lh_add_generator NAME)
    cmake_parse_arguments(GEN "" "" "OUTPUTS;ARGS" ${ARGN})
    add_executable(${NAME} ${NAME}.c)
    target_link_libraries(${NAME} PRIVATE lh_codegen_support blake3)
    target_compile_options(${NAME} PRIVATE -Wall -Wextra -Wpedantic)
    add_dependencies(codegen ${NAME})

    if(GEN_OUTPUTS)
        add_custom_command(
            OUTPUT ${GEN_OUTPUTS}
            COMMAND $<TARGET_FILE:${NAME}> ${GEN_ARGS}
            DEPENDS ${NAME}
            WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
            COMMENT "[codegen] ${NAME}"
            VERBATIM)
        add_custom_target(${NAME}_run DEPENDS ${GEN_OUTPUTS})
        add_dependencies(generate ${NAME}_run)
    endif()
endfunction()

# ── Generators ─────────────────────────────────────────────────────────

lh_add_generator(gen_codepoint_hashes
    OUTPUTS
        ${LH_GENERATED_DIR}/lh_codepoint_hashes.h
        ${LH_GENERATED_DIR}/lh_codepoint_hashes.c
    ARGS
        ${LH_GENERATED_DIR})

# ── UCD grouped XML — extract on demand ────────────────────────────────
set(LH_UCD_ALL_GROUPED_XML "${LH_UCD_EXTRACT_DIR}/ucd.all.grouped.xml")
add_custom_command(
    OUTPUT ${LH_UCD_ALL_GROUPED_XML}
    COMMAND ${CMAKE_COMMAND} -E make_directory ${LH_UCD_EXTRACT_DIR}
    COMMAND ${CMAKE_COMMAND} -E tar xf
        ${LH_UCD_ROOT}/ucdxml/ucd.all.grouped.zip
    WORKING_DIRECTORY ${LH_UCD_EXTRACT_DIR}
    COMMENT "[codegen] extracting ucd.all.grouped.zip"
    VERBATIM)

lh_add_generator(gen_ucd_grouped
    OUTPUTS
        ${LH_GENERATED_DIR}/lh_ucd_props.h
        ${LH_GENERATED_DIR}/lh_ucd_props.c
    ARGS
        ${LH_UCD_ALL_GROUPED_XML} ${LH_GENERATED_DIR})

# Add the extracted-XML dependency to gen_ucd_grouped's run target so the
# unzip happens before the generator runs.
add_custom_target(gen_ucd_grouped_extract DEPENDS ${LH_UCD_ALL_GROUPED_XML})
add_dependencies(gen_ucd_grouped_run gen_ucd_grouped_extract)
