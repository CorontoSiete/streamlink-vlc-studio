/*
 * protocol.h -- wire protocol shared between the VLC plugin (myoverlay.c)
 *               and any controller process (controller.c is the reference).
 *
 * Single source of truth for the named-pipe name, magic, version, message
 * types, and the on-the-wire header struct. Bumping VERSION requires both
 * sides to be rebuilt; the plugin will reject mismatched headers.
 */

#ifndef MYOVERLAY_PROTOCOL_H
#define MYOVERLAY_PROTOCOL_H

#include <stdint.h>

#define MYO_PIPE_NAME     "\\\\.\\pipe\\vlc_overlay" /* default; VLC plugin can override suffix with MYOVERLAY_PIPE_NAME */
#define MYO_MAGIC         0x564C4F56u            /* 'VLOV' little-endian */
#define MYO_VERSION       1u

#define MYO_TYPE_FRAME    1   /* RGBA frame: updates pixels + size + alpha. x/y ignored. */
#define MYO_TYPE_CLEAR    2   /* hide overlay until next FRAME */
#define MYO_TYPE_POSITION 3   /* explicit x/y/alpha update */
#define MYO_TYPE_SCROLL_STATE 4 /* x=offset, y=max_offset, w=visible, h=total */

#define MYO_EVENT_SCROLL    1 /* plugin -> controller: value > 0 older, value < 0 newer */
#define MYO_EVENT_SCROLL_TO 2 /* plugin -> controller: value = absolute offset */
#define MYO_EVENT_RESIZE    3 /* plugin -> controller: value packs width:height as 16:16 */
#define MYO_EVENT_CHAT_INPUT_FOCUS 4 /* plugin -> controller: value 1 focus, 0 blur */
#define MYO_EVENT_CHAT_INPUT_HOVER 5 /* plugin -> controller: value 1 while mouse is over chat input */
#define MYO_EVENT_SHUTDOWN  6 /* orchestrator -> controller: clear overlay and exit */
#define MYO_EVENT_UI_SCALE 8 /* render scale in source pixels; independent of app window size */
#define MYO_EVENT_VIDEO_SIZE 7 /* plugin -> controller: value packs source video width:height */

#define MYO_PACK_SIZE_EVENT(w, h) \
    ((int32_t)(((((uint32_t)(w)) & 0xffffu) << 16) | (((uint32_t)(h)) & 0xffffu)))
#define MYO_UNPACK_SIZE_W(value) ((int)((((uint32_t)(value)) >> 16) & 0xffffu))
#define MYO_UNPACK_SIZE_H(value) ((int)(((uint32_t)(value)) & 0xffffu))

#define MYO_MAX_PAYLOAD   (32u * 1024u * 1024u)  /* 32 MiB hard cap */

#pragma pack(push, 1)
typedef struct
{
    uint32_t magic;
    uint32_t version;
    uint32_t payload_size;
    uint8_t  type;
    uint8_t  reserved[3];
    int32_t  x;
    int32_t  y;
    uint32_t w;
    uint32_t h;
    uint8_t  alpha;
    uint8_t  reserved2[3];
} overlay_msg_v1;
#pragma pack(pop)

#pragma pack(push, 1)
typedef struct
{
    uint32_t magic;
    uint32_t version;
    uint32_t type;
    int32_t  value;
} overlay_event_v1;
#pragma pack(pop)

#endif /* MYOVERLAY_PROTOCOL_H */
