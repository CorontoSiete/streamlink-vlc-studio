/* VLC 3.0.23 normally supplies these through its generated config.h. */
#ifndef STUDIO_GDI_BUILD_CONFIG_H
#define STUDIO_GDI_BUILD_CONFIG_H
struct pollfd;
#ifdef __cplusplus
extern "C" {
#endif
int poll(struct pollfd *, unsigned, int);
#ifdef __cplusplus
}
#endif
#define N_(s) (s)
#define _(s) (s)
#define gettext_noop(s) (s)
#define PACKAGE_NAME "VLC"
#define PACKAGE_VERSION "3.0.23"
#define PACKAGE_STRING "VLC 3.0.23"
#define VLC_WINSTORE_APP 0
#endif
