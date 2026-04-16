#include "hartonomous.h"

#define H_STR_(x) #x
#define H_STR(x) H_STR_(x)

static const char kVersion[] =
    H_STR(HARTONOMOUS_VERSION_MAJOR) "."
    H_STR(HARTONOMOUS_VERSION_MINOR) "."
    H_STR(HARTONOMOUS_VERSION_PATCH);

const char* hartonomous_version(void) {
    return kVersion;
}
