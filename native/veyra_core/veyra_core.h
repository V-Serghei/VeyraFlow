#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

const char *veyra_get_version(void);

int32_t veyra_last_error_utf8(uint8_t *out, uint64_t out_len, uint64_t *written);

int32_t veyra_file_size_utf8(const char *path, uint64_t *size_out);

int32_t veyra_read_file_utf8(const char *path, uint8_t *out, uint64_t out_len, uint64_t *written);
