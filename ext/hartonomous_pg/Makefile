EXTENSION    = hartonomous
EXTVERSION   = 1.0

MODULE_big   = hartonomous
OBJS         = src/hartonomous.o src/pg_blake3.o src/pg_geometry.o src/pg_traversal.o

DATA         = sql/hartonomous--$(EXTVERSION).sql
PG_CPPFLAGS  = -I$(srcdir)/../libhartonomous/include
SHLIB_LINK   = -L/usr/local/lib -lhartonomous

REGRESS      = hartonomous_test
REGRESS_OPTS = --inputdir=test

PG_CONFIG   ?= pg_config
PGXS        := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
