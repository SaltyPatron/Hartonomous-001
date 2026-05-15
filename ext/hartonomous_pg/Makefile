EXTENSION    = hartonomous
EXTVERSION   = 1.0

MODULE_big   = hartonomous
# 4D type surface (P1a.2/.3) replaces the prior pg_geometry.c PostGIS bridge.
OBJS         = src/hartonomous.o \
               src/pg_blake3.o \
               src/pg_traversal.o \
               src/pg_similarity_topk.o \
               src/pg_recompose_walk.o \
               src/pg_point4d.o \
               src/pg_box4d.o \
               src/pg_distance.o \
               src/pg_scalar.o \
               src/pg_aggregates.o \
               src/pg_gist_point4d.o \
               src/pg_spgist_point4d.o \
               src/pg_linestring4d.o \
               src/pg_trajectory.o \
               src/pg_glicko_bulk.o \
               src/pg_casts.o \
               src/pg_geometry4d.o \
               src/pg_gist_geometry4d.o \
               src/pg_spgist_geometry4d.o \
               src/pg_text_decompose.o \
               src/pg_codepoint_atoms_pg.o \
               src/pg_ucd_atoms_blob.o \
               src/pg_ucd_sequences_pg.o \
               src/generated/pg_ucd_segmentation.o \
               src/generated/pg_ucd_classification.o \
               src/generated/pg_ucd_casing.o \
               src/generated/pg_ucd_pictographic.o \
               src/generated/pg_ucd_decomp.o \
               src/generated/pg_ucd_fcf.o \
               src/generated/pg_ucd_uca.o \
               src/generated/pg_ucd_names.o \
               src/generated/pg_ucd_inventory.o \
               src/generated/pg_ucd_tier1.o \
               src/generated/pg_ucd_named_sequences.o \
               src/generated/pg_ucd_emoji_seq.o \
               src/generated/pg_ucd_emoji_zwj_seq.o \
               src/generated/pg_ucd_standardized_variants.o \
               src/generated/pg_ucd_confusables.o \
               src/generated/pg_ucd_idna_mapping.o \
               src/generated/pg_ucd_cjk_radicals.o

DATA         = sql/hartonomous--$(EXTVERSION).sql

# UCD atoms blob — math-derived per-codepoint data, shipped under
# $share/extension/hartonomous-ucd/ for runtime mmap. The loader in
# pg_ucd_atoms_blob.c looks here by default; HARTONOMOUS_UCD_BLOB_DIR
# env var overrides.
UCD_BLOB_DIR = $(DESTDIR)$(datadir)/extension/hartonomous-ucd
UCD_BLOB_SRC = src/generated
PG_CPPFLAGS  = -I$(srcdir)/../libhartonomous/include
# Disable LTO: PG's default CFLAGS on Debian/Ubuntu enable -flto which triggers
# a GCC internal compiler error (try_forward_edges, cfgcleanup.cc) on the
# pg_traverse_astar nested-SPI control flow. -fno-lto on both compile and link
# overrides the inherited PG flags. The extension is small (3 .c files) so
# losing LTO costs nothing measurable.
#
# Diagnostic flags (debug-mode build):
#   -g3                          — full DWARF debug info: locals, file:line,
#                                   macro definitions. -g alone strips macros.
#   -O0                          — no optimization. Without it, gcc folds locals
#                                   ("optimized out" in gdb) and inlines functions
#                                   so backtrace() loses frames. For diagnostic
#                                   builds. Production-mode rebuild can use -O2 -g.
#   -fno-inline                  — every function gets its own frame, even tiny
#                                   helpers — backtrace + gdb walk reliably.
#   -fno-omit-frame-pointer      — backtrace() needs frame pointers to walk the
#                                   stack reliably across signal-handler boundaries.
#   -rdynamic                    — export ALL symbols (not just PG_FUNCTION_INFO_V1
#                                   entry points) so backtrace_symbols can resolve
#                                   internal helper names instead of "??".
PG_CFLAGS   += -fno-lto -g3 -O0 -fno-inline -fno-omit-frame-pointer
SHLIB_LINK   = -L$(shell $(PG_CONFIG) --pkglibdir) -L/usr/local/lib \
               -Wl,--no-as-needed -lhartonomous -latomic -Wl,--as-needed \
               -fno-lto -Wl,--export-dynamic \
               -Wl,-rpath,$(shell $(PG_CONFIG) --pkglibdir)

REGRESS      = hartonomous_test
REGRESS_OPTS = --inputdir=test

PG_CONFIG   ?= pg_config
PGXS        := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)

# Install UCD atoms blob (idx + reverse + per-block files) alongside
# the extension. PGXS hooks 'install' via the 'all' target — we tack
# the blob deploy on as a post-install action.
.PHONY: install-ucd-blob
install-ucd-blob:
	@if test -f "$(UCD_BLOB_SRC)/hartonomous-ucd-17.0.0.idx" && \
	    test -f "$(UCD_BLOB_SRC)/hartonomous-ucd-17.0.0.reverse.bin" && \
	    test -d "$(UCD_BLOB_SRC)/blocks"; then \
		mkdir -p "$(UCD_BLOB_DIR)/blocks"; \
		cp -f "$(UCD_BLOB_SRC)/hartonomous-ucd-17.0.0.idx" "$(UCD_BLOB_DIR)/"; \
		cp -f "$(UCD_BLOB_SRC)/hartonomous-ucd-17.0.0.reverse.bin" "$(UCD_BLOB_DIR)/"; \
		cp -f "$(UCD_BLOB_SRC)"/blocks/*.bin "$(UCD_BLOB_DIR)/blocks/"; \
		echo "hartonomous-ucd: deployed $(UCD_BLOB_DIR)"; \
	else \
		echo "hartonomous-ucd: blob artifacts absent; libhartonomous embedded catalog fallback remains active"; \
	fi
install: install-ucd-blob
