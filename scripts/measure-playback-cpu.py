"""Opt-in Windows/VLC 3 benchmark. Opens local videos with deterministic chat.

Measures playback/plugin CPU only; no Streamlink, network or chat controller.
Run sequentially in fresh processes with identical media and window geometry.
"""
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import struct
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--media', required=True)
p.add_argument('--output', required=True)
p.add_argument('--vlc-directory', default=r'C:\Program Files\VideoLAN\VLC')
p.add_argument('--overlay-directory', required=True, help='Directory containing build/libmyoverlay_plugin.dll')
p.add_argument('--hw', choices=['none', 'any', 'dxva2'], required=True)
p.add_argument('--count', type=int, default=4, choices=range(1, 17))
p.add_argument('--seconds', type=float, default=12)
p.add_argument('--chat-hz', type=float, default=0, help='0 = static; otherwise animate at this rate (up to 60)')
a = p.parse_args()
if not 1 <= a.seconds <= 300 or not 0 <= a.chat_hz <= 60:
    p.error('seconds must be 1..300 and chat-hz must be 0..60')
media_path = Path(a.media).resolve(strict=True)
vlcdir = Path(a.vlc_directory).resolve(strict=True)
pluginroot = Path(a.overlay_directory).resolve(strict=True)
assert (pluginroot / 'build/libmyoverlay_plugin.dll').is_file()
os.environ['VLC_PLUGIN_PATH'] = str(pluginroot)
dlldir = os.add_dll_directory(str(vlcdir))

def bind(lib, name, result, *args):
    fn = getattr(lib, name)
    fn.restype, fn.argtypes = result, args
    return fn

# VLC uses MSVCRT, whose environment is separate from Python's UCRT environment.
putenv = bind(c.CDLL('msvcrt'), '_putenv_s', c.c_int, c.c_char_p, c.c_char_p)
assert putenv(b'VLC_PLUGIN_PATH', str(pluginroot).encode()) == 0
vlc = c.CDLL(str(vlcdir / 'libvlc.dll'))
core = c.CDLL(str(vlcdir / 'libvlccore.dll'))
user = c.WinDLL('user32', use_last_error=True)
kernel = c.WinDLL('kernel32', use_last_error=True)
psapi = c.WinDLL('psapi', use_last_error=True)
ptr = c.c_void_p

class MemoryCounters(c.Structure):
    _fields_ = [('cb', w.DWORD), ('PageFaultCount', w.DWORD)] + [
        (name, c.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
        'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
        'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]

current_process = bind(kernel, 'GetCurrentProcess', ptr)()
get_memory = bind(psapi, 'GetProcessMemoryInfo', w.BOOL, ptr, c.POINTER(MemoryCounters), w.DWORD)

def memory():
    counters = MemoryCounters()
    counters.cb = c.sizeof(counters)
    assert get_memory(current_process, c.byref(counters), counters.cb)
    return dict(private_bytes=counters.PrivateUsage, working_set_bytes=counters.WorkingSetSize)
version = bind(vlc, 'libvlc_get_version', c.c_char_p)().decode()
assert version.startswith('3.'), 'The checked variable ABI below requires VLC 3.'
new = bind(vlc, 'libvlc_new', ptr, c.c_int, c.POINTER(c.c_char_p))
release = bind(vlc, 'libvlc_release', None, ptr)
media_new = bind(vlc, 'libvlc_media_new_path', ptr, ptr, c.c_char_p)
media_release = bind(vlc, 'libvlc_media_release', None, ptr)
player_new = bind(vlc, 'libvlc_media_player_new_from_media', ptr, ptr)
player_release = bind(vlc, 'libvlc_media_player_release', None, ptr)
set_hwnd = bind(vlc, 'libvlc_media_player_set_hwnd', None, ptr, ptr)
play = bind(vlc, 'libvlc_media_player_play', c.c_int, ptr)
stop = bind(vlc, 'libvlc_media_player_stop', None, ptr)
get_size = bind(vlc, 'libvlc_video_get_size', c.c_int, ptr, c.c_uint, c.POINTER(c.c_uint), c.POINTER(c.c_uint))
set_checked = bind(core, 'var_SetChecked', c.c_int, ptr, c.c_char_p, c.c_int, ptr)
create_window = bind(user, 'CreateWindowExW', ptr, w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD,
                     c.c_int, c.c_int, c.c_int, c.c_int, ptr, ptr, ptr, ptr)
destroy_window = bind(user, 'DestroyWindow', w.BOOL, ptr)
peek = bind(user, 'PeekMessageW', w.BOOL, c.POINTER(w.MSG), ptr, w.UINT, w.UINT, w.UINT)
translate = bind(user, 'TranslateMessage', w.BOOL, c.POINTER(w.MSG))
dispatch = bind(user, 'DispatchMessageW', c.c_ssize_t, c.POINTER(w.MSG))

class Stats(c.Structure):
    _fields_ = [(name, typ) for name, typ in (
        ('read_bytes', c.c_int), ('input_bitrate', c.c_float), ('demux_read_bytes', c.c_int),
        ('demux_bitrate', c.c_float), ('demux_corrupted', c.c_int), ('demux_discontinuity', c.c_int),
        ('decoded_video', c.c_int), ('decoded_audio', c.c_int), ('displayed_pictures', c.c_int),
        ('lost_pictures', c.c_int), ('played_abuffers', c.c_int), ('lost_abuffers', c.c_int),
        ('sent_packets', c.c_int), ('sent_bytes', c.c_int), ('send_bitrate', c.c_float))]

get_stats = bind(vlc, 'libvlc_media_get_stats', c.c_int, ptr, c.POINTER(Stats))
def stats(media):
    result = Stats()
    assert get_stats(media, c.byref(result))
    return {name: getattr(result, name) for name in ('decoded_video', 'displayed_pictures', 'lost_pictures')}

# Alternating, partially transparent stripes exercise the real pipe and scaler.
frames = []
for phase in range(2):
    pixels = bytes(v for y in range(292) for x in range(340)
                   for v in ((255, 255, 255, 192) if (x + phase) % 2 else (0, 128, 255, 128)))
    frames.append(struct.pack('<IIIB3xiiIIB3x', 0x564C4F56, 1, len(pixels), 1, 32, 32, 340, 292, 255) + pixels)
pipes = []
def pump(seconds, animate=False):
    end = time.perf_counter() + seconds
    next_frame, phase = time.perf_counter(), 0
    msg = w.MSG()
    while time.perf_counter() < end:
        while peek(c.byref(msg), None, 0, 0, 1):
            translate(c.byref(msg))
            dispatch(c.byref(msg))
        if animate and a.chat_hz and time.perf_counter() >= next_frame:
            phase = 1 - phase
            for pipe in pipes:
                assert os.write(pipe, frames[phase]) == len(frames[phase])
            next_frame += 1 / a.chat_hz
        time.sleep(.002)

columns = 2 if a.count <= 4 else 4
cell_width, cell_height = 1280 // columns, 720 // columns
root = create_window(0, 'STATIC', 'Stream Studio CPU benchmark', 0x10CF0000, 50, 50,
                     1310, 50 + cell_height * ((a.count + columns - 1) // columns), None, None, None, None)
assert root
engines = []
try:
    for i in range(a.count):
        child = create_window(0, 'STATIC', '', 0x50000000, i % columns * cell_width,
                              i // columns * cell_height, cell_width, cell_height, root, None, None, None)
        pipe_name = 'svs_cpu_' + str(os.getpid()) + '_' + str(i)
        options = ['--no-video-title-show', '--quiet', '--vout=studio_gdi', '--no-audio',
                   '--no-volume-save', '--avcodec-hw=' + a.hw, '--network-caching=500',
                   '--live-caching=300', '--drop-late-frames', '--skip-frames',
                   '--sub-source=myoverlay{pipe=' + pipe_name + ',show-placeholder=0}']
        opts = [x.encode() for x in options]
        inst = new(len(opts), (c.c_char_p * len(opts))(*opts))
        assert inst
        media = media_new(inst, str(media_path).encode())
        assert media
        player = player_new(media)
        assert player
        engines.append((inst, media, player))
        set_hwnd(player, child)
        for name, value in [('vout', 'studio_gdi'), ('avcodec-hw', a.hw)]:
            assert set_checked(player, name.encode(), 0x40, c.cast(c.c_char_p(value.encode()), ptr)) == 0
        assert play(player) == 0
    pump(4)
    for i in range(a.count):
        pipe = os.open(r'\\.\pipe\svs_cpu_' + str(os.getpid()) + '_' + str(i), os.O_WRONLY | os.O_BINARY)
        pipes.append(pipe)
        assert os.write(pipe, frames[0]) == len(frames[0])
    pump(2, animate=True)
    sizes = []
    for _, _, player in engines:
        width, height = c.c_uint(), c.c_uint()
        assert get_size(player, 0, c.byref(width), c.byref(height)) == 0
        sizes.append([width.value, height.value])
    before = [stats(m) for _, m, _ in engines]
    memory_before = memory()
    start, cpu = time.perf_counter(), time.process_time()
    pump(a.seconds, animate=True)
    elapsed, used = time.perf_counter() - start, time.process_time() - cpu
    after = [stats(m) for _, m, _ in engines]
    counts = [{key: end[key] - begin[key] for key in begin} for begin, end in zip(before, after)]
    continuing_video = all(f['displayed_pictures'] > a.seconds * 10 for f in counts)
    result = dict(vlc=version, hardware=a.hw, count=a.count, chat_hz=a.chat_hz, sizes=sizes,
                  memory_before=memory_before, memory_after=memory(),
                  continuing_video=continuing_video,
                  elapsed=elapsed, cpu_seconds=used, cpu_cores=used/elapsed, logical_cpus=os.cpu_count(), frames=counts)
    Path(a.output).write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
    assert continuing_video, 'Video did not keep displaying; check frame counters and fixture duration in the output.'
finally:
    for pipe in pipes:
        os.close(pipe)
    for inst, media, player in engines:
        stop(player)
        player_release(player)
        media_release(media)
        release(inst)
    destroy_window(root)
