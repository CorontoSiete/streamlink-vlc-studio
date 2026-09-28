#ifndef STUDIO_CORE_CONFIG_H
#define STUDIO_CORE_CONFIG_H
#define WIN32 1
#define WIN32_LEAN_AND_MEAN 1
#define UNICODE 1
#define _UNICODE 1
#define _FILE_OFFSET_BITS 64
#define _REENTRANT 1
#define HAVE_THREAD_LOCAL 1
#define fdatasync fsync
#define SHUT_RD 0
#define SHUT_WR 1
#define SHUT_RDWR 2
#define LIBEXT ".dll"
#define COPYRIGHT_MESSAGE "Copyright (C) 1996-2026 the VideoLAN team"
#define CONFIGURE_LINE VLC_CONFIGURE_LINE
#define PACKAGE_VERSION_MAJOR 3
#define PACKAGE_VERSION_MINOR 0
#define PACKAGE_VERSION_REVISION 23
#define PACKAGE_VERSION_EXTRA 0
#define PACKAGE_VERSION_DEV ""
#define _WIN32_WINNT 0x0601
#define WINVER 0x0601
#define __MSVCRT_VERSION__ 0x700
#define VLC_WINSTORE_APP 0
#define __LIBVLC__ 1
#define MODULE_STRING "core"
#define PACKAGE "vlc"
#define PACKAGE_NAME "VLC"
#define PACKAGE_VERSION "3.0.23"
#define PACKAGE_STRING "VLC 3.0.23"
#define PACKAGE_BUGREPORT "vlc-devel@videolan.org"
#define PACKAGE_URL "https://www.videolan.org/"
#define VERSION "3.0.23"
#define VERSION_MAJOR 3
#define VERSION_MINOR 0
#define VERSION_REVISION 23
#define VERSION_EXTRA 0
#define VERSION_MESSAGE "3.0.23 Vetinari"
#define CODENAME "Vetinari"
#define VLC_COMPILE_BY "Stream Studio"
#define VLC_COMPILE_HOST "x86_64-w64-mingw32"
#define VLC_COMPILER "GCC 16.1.0 MSVCRT"
#define VLC_CONFIGURE_LINE "Windows x64 core with native address waits"
#define VLC_VLC_PACKAGE_CHANGESET "3.0.23"
#define LOCALEDIR "locale"
#define PKGDATADIR "share"
#define PKGLIBDIR "plugins"
#define ENABLE_SOUT 1
#define ENABLE_VLM 1
#define ENABLE_NLS 1
#define HAVE_GETTEXT 1
#define HAVE_DCGETTEXT 1
#define HAVE_ICONV 1
#define ICONV_CONST
#define HAVE_IDN 1
#define HAVE_LIBIDN 1
#define HAVE_DYNAMIC_PLUGINS 1
#define UPDATE_CHECK 1
#define HAVE_STRUCT_POLLFD 1
#define HAVE_STRUCT_TIMESPEC 1
#define HAVE_MAX_ALIGN_T 1
#define HAVE_LLDIV 1
#define HAVE_GETENV 1
#define HAVE_GETPID 1
#define HAVE_REWIND 1
#define HAVE_SWAB 1
#define HAVE_ATTRIBUTE_PACKED 1
#define HAVE_NANF 1
#define HAVE_STRDUP 1
#define HAVE_STRNLEN 1
#define HAVE_STRTOK_R 1
#define HAVE_STRCASECMP 1
#define HAVE_GETTIMEOFDAY 1
#define HAVE_INET_PTON 1
#define HAVE_ISATTY 1
#define HAVE_SEARCH_H 1
#define HAVE_TSEARCH 1
#define HAVE_IF_NAMETOINDEX 1
#define HAVE_PROCESS_H 1
#define HAVE_STRUCT_POLLFD 1
#define HAVE_SYS_STAT_H 1
#define HAVE_SYS_TYPES_H 1
#define HAVE_STDINT_H 1
#define HAVE_UNISTD_H 1
#define HAVE_GETADDRINFO 1
#define HAVE_GETNAMEINFO 1
#define HAVE_STATIC_ASSERT 1
#define HAVE_ATTRIBUTE_VISIBILITY 1
#define HAVE_ATTRIBUTE_DESTRUCTOR 1
#define HAVE_ATTRIBUTE_PACKED 1
#define CAN_COMPILE_MMX 1
#define CAN_COMPILE_SSE 1
#define CAN_COMPILE_SSE2 1
#define CAN_COMPILE_SSE3 1
#define CAN_COMPILE_SSSE3 1
#define CAN_COMPILE_SSE4_1 1
#define CAN_COMPILE_SSE4_2 1
#define CAN_COMPILE_AVX 1
#define CAN_COMPILE_AVX2 1
#define restrict __restrict
#include <winsock2.h>
#include <vlc_fixups.h>
struct pollfd;
int poll(struct pollfd *, unsigned, int);
#endif
