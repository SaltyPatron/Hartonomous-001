EXTENSION    = hartonomous
EXTVERSION   = 1.0

MODULE_big   = hartonomous
# 4D type surface (P1a.2/.3) replaces the prior pg_geometry.c PostGIS bridge.
OBJS         = src/hartonomous.o \
               src/pg_blake3.o \
               src/pg_traversal.o \
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
               src/pg_spgist_geometry4d.o

DATA         = sql/hartonomous--$(EXTVERSION).sql
PG_CPPFLAGS  = -I$(srcdir)/../libhartonomous/include
# Disable LTO: PG's default CFLAGS on Debian/Ubuntu enable -flto which triggers
# a GCC internal compiler error (try_forward_edges, cfgcleanup.cc) on the
# pg_traverse_astar nested-SPI control flow. -fno-lto on both compile and link
# overrides the inherited PG flags. The extension is small (3 .c files) so
# losing LTO costs nothing measurable.
PG_CFLAGS   += -fno-lto
SHLIB_LINK   = -L/usr/local/lib -lhartonomous -fno-lto

REGRESS      = hartonomous_test
REGRESS_OPTS = --inputdir=test

PG_CONFIG   ?= pg_config
PGXS        := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
