#ifndef LDP_PNG_DECODE_H
#define LDP_PNG_DECODE_H
#include <stdint.h>
#include <stddef.h>

typedef struct {
    uint32_t width, height;
    int has_alpha;
    uint8_t *bgra;   // width*height*4，BGRA8888
} ldp_png_image;

int32_t ldp_png_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t error_cap);
void ldp_png_free(ldp_png_image *img);

#endif
