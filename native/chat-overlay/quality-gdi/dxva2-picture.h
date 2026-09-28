/* VLC 3.0.23 DXVA2 picture layout, extracted from:
 * modules/video_chroma/d3d9_fmt.h and modules/codec/avcodec/va_surface.h.
 * Copyright (C) 2009 Geoffroy Couprie and Laurent Aimar.
 * Copyright (C) 2015-2017 Steve Lhomme, VLC authors, VideoLAN and VideoLabs.
 * SPDX-License-Identifier: LGPL-2.1-or-later
 * See COPYING.LIB. Do not use this private layout with an unverified VLC ABI.
 */
#ifndef STUDIO_GDI_DXVA2_PICTURE_H
#define STUDIO_GDI_DXVA2_PICTURE_H
#include <vlc_picture.h>
#define COBJMACROS
#include <initguid.h>
#include <d3d9.h>
#include <dxva2api.h>

struct picture_sys_t {
    IDirect3DSurface9 *surface;
    IDirectXVideoDecoder *decoder;
    HINSTANCE dxva2_dll;
};

struct studio_va_picture_context {
    picture_context_t context;
    struct vlc_va_surface_t *surface_owner;
    picture_sys_t picture;
};

static inline picture_sys_t *HardwarePictureSystem(picture_t *picture)
{
    return picture->context ?
        &((struct studio_va_picture_context *)picture->context)->picture : picture->p_sys;
}
#endif
