/* libhartonomous — version.h
 *
 * Version macros, API export macro, common types.
 */

#ifndef HARTONOMOUS_VERSION_H
#define HARTONOMOUS_VERSION_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef HARTONOMOUS_BUILD
    #define HARTONOMOUS_API __declspec(dllexport)
  #else
    #define HARTONOMOUS_API __declspec(dllimport)
  #endif
#else
  #define HARTONOMOUS_API __attribute__((visibility("default")))
#endif

#define HARTONOMOUS_VERSION_MAJOR 0
#define HARTONOMOUS_VERSION_MINOR 1
#define HARTONOMOUS_VERSION_PATCH 0

HARTONOMOUS_API const char* hartonomous_version(void);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_VERSION_H */
