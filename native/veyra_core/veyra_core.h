#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

int32_t veyra_last_error_utf8(uint8_t *out, uint64_t out_len, uint64_t *written);

int veyra_blake3_hash(const uint8_t *data, int data_len, uint8_t *output, int output_len);

int veyra_blake3_hash_file(const char *path_ptr, uint8_t *output, int output_len);

int veyra_sha256_hash_file_utf8(const char *path_ptr, uint8_t *output, int output_len);

int32_t veyra_scan_directory_utf8(const char *root_path_ptr,
                                  const char *extensions_csv_ptr,
                                  uint8_t *out,
                                  uint64_t out_len,
                                  uint64_t *written);

int32_t veyra_scan_directory_limited_utf8(const char *root_path_ptr,
                                          const char *extensions_csv_ptr,
                                          uint64_t max_read_bytes_per_sec,
                                          uint8_t *out,
                                          uint64_t out_len,
                                          uint64_t *written);

int32_t veyra_scan_directory_limited_v2_utf8(const char *root_path_ptr,
                                             const char *extensions_csv_ptr,
                                             uint64_t max_read_bytes_per_sec,
                                             uint64_t max_file_ops_per_sec,
                                             uint8_t *out,
                                             uint64_t out_len,
                                             uint64_t *written);

int32_t veyra_store_file_blocks_utf8(const char *file_path_ptr,
                                     const char *store_root_ptr,
                                     uint32_t chunk_size,
                                     uint8_t *out,
                                     uint64_t out_len,
                                     uint64_t *written);

int64_t veyra_restore_file_blocks_utf8(const char *store_root_ptr,
                                       const char *blocks_json_ptr,
                                       const char *target_path_ptr,
                                       int overwrite_existing);

int veyra_zstd_compress(const uint8_t *data,
                        int data_len,
                        uint8_t *output,
                        int output_len,
                        int level);

int veyra_zstd_decompress(const uint8_t *data, int data_len, uint8_t *output, int output_len);

int32_t veyra_build_text_diff_utf8(const char *left_file_path_ptr,
                                   const char *right_file_path_ptr,
                                   uint32_t max_lines,
                                   uint8_t *out,
                                   uint64_t out_len,
                                   uint64_t *written);

int32_t veyra_render_image_diff_utf8(const char *baseline_path_ptr,
                                     const char *current_path_ptr,
                                     uint32_t sensitivity_percent,
                                     uint32_t mode,
                                     uint32_t split_percent,
                                     int show_region_boxes,
                                     uint8_t *out,
                                     uint64_t out_len,
                                     uint64_t *written);

int32_t veyra_compare_snapshot_links_utf8(const char *current_states_json_ptr,
                                          const char *previous_states_json_ptr,
                                          uint8_t *out,
                                          uint64_t out_len,
                                          uint64_t *written);

int32_t veyra_compare_repository_paths_utf8(const char *current_states_json_ptr,
                                            const char *baseline_states_json_ptr,
                                            uint32_t take,
                                            uint8_t *out,
                                            uint64_t out_len,
                                            uint64_t *written);

int32_t veyra_plan_repository_versions_utf8(const char *states_json_ptr,
                                            uint8_t *out,
                                            uint64_t out_len,
                                            uint64_t *written);

int64_t veyra_zstd_compress_file(const char *src_path_ptr, const char *dst_path_ptr, int level);

int64_t veyra_zstd_decompress_file(const char *src_path_ptr, const char *dst_path_ptr);

char *veyra_get_version(void);

void veyra_free_string(char *s);
