#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

/**
 * Computes BLAKE3 hash of the given data
 * Returns length of hash written to output buffer
 */
int veyra_blake3_hash(const unsigned char *data,
                      int data_len,
                      unsigned char *output,
                      int output_len);

/**
 * Compresses data using zstd
 * Returns length of compressed data, or -1 on error
 */
int veyra_zstd_compress(const unsigned char *data,
                        int data_len,
                        unsigned char *output,
                        int output_len,
                        int level);

/**
 * Decompresses zstd data
 * Returns length of decompressed data, or -1 on error
 */
int veyra_zstd_decompress(const unsigned char *data,
                          int data_len,
                          unsigned char *output,
                          int output_len);

/**
 * Gets version string
 */
const char *veyra_get_version(void);

/**
 * Frees a string returned by veyra_get_version
 */
void veyra_free_string(char *s);
