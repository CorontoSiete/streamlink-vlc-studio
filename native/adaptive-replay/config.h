/* Local Windows build configuration for the VLC 3.0.23 live replay module.
 * Unencrypted MPEG-TS EVENT inputs only: no gcrypt or compressed MP4 support.
 * The application validates the playlist before selecting this module.
 */
#ifndef STUDIO_ADAPTIVE_CONFIG_H
#define STUDIO_ADAPTIVE_CONFIG_H
#define MODULE_STRING "studio_adaptive"
#define __PLUGIN__ 1
#define WIN32 1
#define _WIN32_WINNT 0x0601
#define VLC_WINSTORE_APP 0
#define PACKAGE_NAME "VLC"
#define PACKAGE_VERSION "3.0.23"
#define PACKAGE_STRING "VLC 3.0.23"
#define HAVE_STRUCT_POLLFD 1
#define HAVE_STRUCT_TIMESPEC 1
#define HAVE_MAX_ALIGN_T 1
#define HAVE_LLDIV 1
#define HAVE_GETENV 1
#define HAVE_GETPID 1
#define HAVE_SWAB 1
#define HAVE_ATTRIBUTE_PACKED 1
#define HAVE_NANF 1
#define HAVE_STRDUP 1
#define HAVE_STRNLEN 1
#define HAVE_STRTOK_R 1
#define HAVE_STRCASECMP 1
#define HAVE_GETTIMEOFDAY 1
#define HAVE_INET_PTON 1
#define restrict __restrict
#include <winsock2.h>
#include <vlc_fixups.h>
struct pollfd;
#ifdef __cplusplus
extern "C" {
#endif
int poll(struct pollfd *, unsigned, int);
#ifdef __cplusplus
}
#endif
#endif
