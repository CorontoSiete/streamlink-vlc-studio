/*****************************************************************************
 * wingdi.c : Win32 / WinCE GDI video output plugin for vlc
 *****************************************************************************
 * Copyright (C) 2002-2009 VLC authors and VideoLAN
 * $Id$
 *
 * Authors: Gildas Bazin <gbazin@videolan.org>
 *          Samuel Hocevar <sam@zoy.org>
 *
 * This program is free software; you can redistribute it and/or modify it
 * under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston MA 02110-1301, USA.
 *****************************************************************************/

/*****************************************************************************
 * Preamble
 *****************************************************************************/

#ifdef HAVE_CONFIG_H
# include "config.h"
#endif
#include <assert.h>

#include <vlc_common.h>
#include <vlc_plugin.h>
#include <vlc_vout_display.h>

#include <windows.h>

#include "common.h"
#include "compositor.h"

/*****************************************************************************
 * Module descriptor
 *****************************************************************************/
int  StudioGdiOpen (vlc_object_t *);
void StudioGdiClose(vlc_object_t *);

/* Registered as studio_gdi by myoverlay.c. */


/*****************************************************************************
 * Local prototypes
 *****************************************************************************/
struct vout_display_sys_t
{
    vout_display_sys_win32_t sys;

    int  i_depth;

    /* Our offscreen bitmap and its framebuffer */
    HDC        off_dc;
    HBITMAP    off_bitmap;
    HANDLE     replay_output_ready;

    /* Video and chat are composed at window resolution into one GDI bitmap. */
    studio_gdi_compositor_t compositor;

    struct
    {
        BITMAPINFO bitmapinfo;
        RGBQUAD    red;
        RGBQUAD    green;
        RGBQUAD    blue;
    };
};

static picture_pool_t *Pool  (vout_display_t *, unsigned);
static void           Display(vout_display_t *, picture_t *, subpicture_t *subpicture);
static int            Control(vout_display_t *, int, va_list);
static void           Manage (vout_display_t *);

static int            Init(vout_display_t *, video_format_t *, int, int);
static void           Clean(vout_display_t *);

/* */
int StudioGdiOpen(vlc_object_t *object)
{
    vout_display_t *vd = (vout_display_t *)object;
    vout_display_sys_t *sys;
    if (!module_exists("swscale")) return VLC_EGENERIC;

    if ( !vd->obj.force && vd->source.projection_mode != PROJECTION_MODE_RECTANGULAR)
        return VLC_EGENERIC; /* let a module who can handle it do it */

    vd->sys = sys = calloc(1, sizeof(*sys));
    if (!sys)
        return VLC_ENOMEM;

    if (CommonInit(vd))
        goto error;

    /* */
    video_format_t fmt = vd->fmt;
    if (Init(vd, &fmt, fmt.i_width, fmt.i_height))
        goto error;

    /* The player owns this per-input gate. It is installed before any vout can
     * exist (including prepared replay adoption), and signalled only after seek
     * confirmation. Missing/invalid named events must not expose preroll. */
    char *ready_name = var_InheritString(vd, "studio-replay-output-ready");
    if (ready_name) {
        sys->replay_output_ready = OpenEventA(SYNCHRONIZE, FALSE, ready_name);
        free(ready_name);
        if (!sys->replay_output_ready) goto error;
    }

    vout_display_info_t info = vd->info;
    info.is_slow              = false;
    info.has_double_click     = true;
    info.has_pictures_invalid = true;
    static const vlc_fourcc_t chromas[] = { VLC_CODEC_RGBA, 0 };
    info.subpicture_chromas = chromas;

    /* */
    vd->fmt  = fmt;
    vd->info = info;

    vd->pool    = Pool;
    vd->prepare = NULL;
    vd->display = Display;
    vd->manage  = Manage;
    vd->control = Control;
    return VLC_SUCCESS;

error:
    StudioGdiClose(VLC_OBJECT(vd));
    return VLC_EGENERIC;
}

/* */
void StudioGdiClose(vlc_object_t *object)
{
    vout_display_t *vd = (vout_display_t *)object;

    if (vd->sys->replay_output_ready) CloseHandle(vd->sys->replay_output_ready);

    Clean(vd);

    CommonClean(vd);

    free(vd->sys);
}

/* */
static picture_pool_t *Pool(vout_display_t *vd, unsigned count)
{
    VLC_UNUSED(count);
    return vd->sys->sys.pool;
}

static void Display(vout_display_t *vd, picture_t *picture, subpicture_t *subpicture)
{
    vout_display_sys_t *sys = vd->sys;

#define rect_src vd->sys->rect_src
#define rect_src_clipped vd->sys->sys.rect_src_clipped
#define rect_dest vd->sys->sys.rect_dest
#define rect_dest_clipped vd->sys->sys.rect_dest_clipped
    RECT rect_dst = rect_dest_clipped;
    HDC hdc = GetDC(sys->sys.hvideownd);

    OffsetRect(&rect_dst, -rect_dest.left, -rect_dest.top);

    if (sys->replay_output_ready) {
        if (WaitForSingleObject(sys->replay_output_ready, 0) != WAIT_OBJECT_0) {
            PatBlt(hdc, rect_dst.left, rect_dst.top,
                   rect_dst.right - rect_dst.left, rect_dst.bottom - rect_dst.top, BLACKNESS);
            goto displayed;
        }
        CloseHandle(sys->replay_output_ready);
        sys->replay_output_ready = NULL;
    }
    SelectObject(sys->off_dc, sys->off_bitmap);

    const int width = rect_dst.right - rect_dst.left;
    const int height = rect_dst.bottom - rect_dst.top;
    const bool buffered = EnsureCanvas(&sys->compositor, hdc, width, height);
    HDC target = buffered ? sys->compositor.dc : hdc;
    const int x = buffered ? 0 : rect_dst.left;
    const int y = buffered ? 0 : rect_dst.top;
    const int source_width = rect_src_clipped.right - rect_src_clipped.left;
    const int source_height = rect_src_clipped.bottom - rect_src_clipped.top;
    if (width != source_width || height != source_height) {
        /* Video retains VLC's fast original scaling. Chat is filtered separately. */
        SetStretchBltMode(target, COLORONCOLOR);
        StretchBlt(target, x, y, width, height, sys->off_dc,
                   rect_src_clipped.left, rect_src_clipped.top,
                   source_width, source_height, SRCCOPY);
    } else {
        BitBlt(target, x, y, width, height, sys->off_dc,
               rect_src_clipped.left, rect_src_clipped.top, SRCCOPY);
    }
    if (buffered) {
        ComposeChat(VLC_OBJECT(vd), &sys->compositor, subpicture, &rect_dest, &rect_dest_clipped);
        BitBlt(hdc, rect_dst.left, rect_dst.top, width, height,
               sys->compositor.dc, 0, 0, SRCCOPY);
    }
displayed:
    GdiFlush();

    ReleaseDC(sys->sys.hvideownd, hdc);
#undef rect_src
#undef rect_src_clipped
#undef rect_dest
#undef rect_dest_clipped
    /* TODO */
    picture_Release(picture);
    if (subpicture) subpicture_Delete(subpicture);

    CommonDisplay(vd);
}

static int Control(vout_display_t *vd, int query, va_list args)
{
    switch (query) {
    case VOUT_DISPLAY_RESET_PICTURES:
        vlc_assert_unreachable();
        return VLC_EGENERIC;
    default:
        return CommonControl(vd, query, args);
    }

}

static void Manage(vout_display_t *vd)
{
    CommonManage(vd);
}

static int Init(vout_display_t *vd,
                video_format_t *fmt, int width, int height)
{
    vout_display_sys_t *sys = vd->sys;

    /* */
    RECT *display = &sys->sys.rect_display;
    display->left   = 0;
    display->top    = 0;
    display->right  = GetSystemMetrics(SM_CXSCREEN);;
    display->bottom = GetSystemMetrics(SM_CYSCREEN);;

    /* Initialize an offscreen bitmap for direct buffer operations. */

    /* */
    HDC window_dc = GetDC(sys->sys.hvideownd);

    /* */
    sys->i_depth = GetDeviceCaps(window_dc, PLANES) *
                   GetDeviceCaps(window_dc, BITSPIXEL);

    /* */
    msg_Dbg(vd, "GDI depth is %i", sys->i_depth);
    switch (sys->i_depth) {
    case 8:
        fmt->i_chroma = VLC_CODEC_RGB8;
        break;
    case 15:
        fmt->i_chroma = VLC_CODEC_RGB15;
        fmt->i_rmask  = 0x7c00;
        fmt->i_gmask  = 0x03e0;
        fmt->i_bmask  = 0x001f;
        break;
    case 16:
        fmt->i_chroma = VLC_CODEC_RGB16;
        fmt->i_rmask  = 0xf800;
        fmt->i_gmask  = 0x07e0;
        fmt->i_bmask  = 0x001f;
        break;
    case 24:
        fmt->i_chroma = VLC_CODEC_RGB24;
        fmt->i_rmask  = 0x00ff0000;
        fmt->i_gmask  = 0x0000ff00;
        fmt->i_bmask  = 0x000000ff;
        break;
    case 32:
        fmt->i_chroma = VLC_CODEC_RGB32;
        fmt->i_rmask  = 0x00ff0000;
        fmt->i_gmask  = 0x0000ff00;
        fmt->i_bmask  = 0x000000ff;
        break;
    default:
        msg_Err(vd, "screen depth %i not supported", sys->i_depth);
        return VLC_EGENERIC;
    }
    fmt->i_width  = width;
    fmt->i_height = height;

    void *p_pic_buffer;
    int     i_pic_pitch;
    /* Initialize offscreen bitmap */
    BITMAPINFO *bi = &sys->bitmapinfo;
    memset(bi, 0, sizeof(BITMAPINFO) + 3 * sizeof(RGBQUAD));
    if (sys->i_depth > 8) {
        ((DWORD*)bi->bmiColors)[0] = fmt->i_rmask;
        ((DWORD*)bi->bmiColors)[1] = fmt->i_gmask;
        ((DWORD*)bi->bmiColors)[2] = fmt->i_bmask;;
    }

    BITMAPINFOHEADER *bih = &sys->bitmapinfo.bmiHeader;
    bih->biSize = sizeof(BITMAPINFOHEADER);
    bih->biSizeImage     = 0;
    bih->biPlanes        = 1;
    bih->biCompression   = (sys->i_depth == 15 ||
                            sys->i_depth == 16) ? BI_BITFIELDS : BI_RGB;
    bih->biBitCount      = sys->i_depth;
    bih->biWidth         = fmt->i_width;
    bih->biHeight        = -fmt->i_height;
    bih->biClrImportant  = 0;
    bih->biClrUsed       = 0;
    bih->biXPelsPerMeter = 0;
    bih->biYPelsPerMeter = 0;

    i_pic_pitch = bih->biBitCount * bih->biWidth / 8;
    sys->off_bitmap = CreateDIBSection(window_dc,
                                       (BITMAPINFO *)bih,
                                       DIB_RGB_COLORS,
                                       &p_pic_buffer, NULL, 0);

    sys->off_dc = CreateCompatibleDC(window_dc);

    SelectObject(sys->off_dc, sys->off_bitmap);
    ReleaseDC(sys->sys.hvideownd, window_dc);

    EventThreadUpdateTitle(sys->sys.event, VOUT_TITLE " (WinGDI output)");

    /* */
    picture_resource_t rsc;
    memset(&rsc, 0, sizeof(rsc));
    rsc.p[0].p_pixels = p_pic_buffer;
    rsc.p[0].i_lines  = fmt->i_height;
    rsc.p[0].i_pitch  = i_pic_pitch;;

    picture_t *picture = picture_NewFromResource(fmt, &rsc);
    if (picture != NULL)
        sys->sys.pool = picture_pool_New(1, &picture);
    else
        sys->sys.pool = NULL;

    UpdateRects(vd, NULL, true);

    return VLC_SUCCESS;
}

static void Clean(vout_display_t *vd)
{
    vout_display_sys_t *sys = vd->sys;
    CleanCompositor(&sys->compositor);

    if (sys->sys.pool)
        picture_pool_Release(sys->sys.pool);
    sys->sys.pool = NULL;

    if (sys->off_dc)
        DeleteDC(sys->off_dc);
    if (sys->off_bitmap)
        DeleteObject(sys->off_bitmap);
}
