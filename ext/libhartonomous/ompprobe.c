#include <stdint.h>
int main(void) {
    int64_t n = 100;
    int64_t s = 0;
    int64_t i;
    #pragma omp parallel for reduction(+:s)
    for (i = 0; i < n; ++i) s += i;
    return (int)s;
}
