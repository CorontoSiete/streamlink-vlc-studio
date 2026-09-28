"""Build the explicitly selected VLC 3.0.23 live replay module (Python 3.12+).

Requires Git, x64 MinGW-w64 GCC/G++ targeting MSVCRT, and VLC's libvlccore.a.
All corresponding VLC sources and the local patch are included with the app.
"""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import subprocess
import tarfile
import tempfile


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--gcc', required=True, type=Path)
    parser.add_argument('--vlc-library-directory', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=root / 'src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledAdaptiveReplay/libstudio_adaptive_plugin.dll')
    parser.add_argument('--build-directory', type=Path, default=root / 'artifacts/native/adaptive-replay')
    parser.add_argument('--jobs', type=int, default=min(6, os.cpu_count() or 1))
    args = parser.parse_args()
    compiler = args.gcc.resolve().parent
    sdk = args.vlc_library_directory.resolve()
    output = args.output.resolve()
    environment = dict(os.environ)
    environment['PATH'] = str(compiler) + os.pathsep + environment['PATH']
    target = subprocess.check_output([str(args.gcc), '-dumpmachine'], env=environment, text=True).strip()
    if target != 'x86_64-w64-mingw32':
        parser.error('Use x64 MinGW-w64 GCC/G++ targeting MSVCRT for VLC 3.0.23.')
    if not (sdk / 'libvlccore.a').is_file():
        parser.error('The VLC import library libvlccore.a is missing.')
    native = root / 'native/adaptive-replay'
    provenance = json.loads((native / 'provenance.json').read_text(encoding='utf-8'))
    bundle = native / provenance['sourceBundle']
    if hashlib.sha256(bundle.read_bytes()).hexdigest() != provenance['sourceBundleSha256']:
        raise RuntimeError('The bundled upstream source does not match its pinned SHA-256.')
    args.build_directory.mkdir(parents=True, exist_ok=True)
    # Every build has a fresh source/object directory; no stale native header cache.
    build = Path(tempfile.mkdtemp(prefix='build-', dir=args.build_directory.resolve()))
    with tarfile.open(bundle, 'r:gz') as archive:
        archive.extractall(build, filter='data')
    source = build / 'vlc-3.0.23'
    environment['GIT_CEILING_DIRECTORIES'] = str(build)
    subprocess.run(['git', 'apply', str(native / 'live-edge.patch')], cwd=source, env=environment, check=True)
    config = native / 'config.h'
    modules = source / 'modules'
    makefile = (modules / 'demux/Makefile.am').read_text(encoding='utf-8')
    makefile = makefile[makefile.index('libvlc_adaptive_la_SOURCES'):makefile.index('noinst_LTLIBRARIES += libvlc_adaptive.la')]
    paths = set(re.findall(r'\b(?:demux|mux|packetizer)/[\w/]+\.(?:cpp|c)\b', makefile))
    http = (modules / 'access/http/Makefile.am').read_text(encoding='utf-8').split('libvlc_http_la_CPPFLAGS')[0]
    paths.update(re.findall(r'\baccess/[\w/]+\.c\b', http[http.index('libvlc_http_la_SOURCES'):]))
    paths.add('demux/adaptive/adaptive.cpp')
    paths.update('../compat/' + name + '.c' for name in ['asprintf', 'strndup', 'strnstr', 'strcasestr', 'gmtime_r', 'localtime_r', 'timegm', 'poll'])
    flags = ['-O2', '-DNDEBUG', '-DHAVE_CONFIG_H', '-fno-strict-aliasing', '-fvisibility=hidden',
             '-ffile-prefix-map=' + str(source) + '=vlc-3.0.23',
             '-I' + str(native), '-I' + str(source / 'include'), '-I' + str(modules),
             '-I' + str(modules / 'demux'), '-I' + str(modules / 'demux/adaptive'), '-include', str(config)]

    def compile_one(path):
        cpp = path.endswith('.cpp')
        obj = build / (path.replace('/', '_') + '.o')
        result = subprocess.run([str(compiler / ('g++.exe' if cpp else 'gcc.exe')), *flags,
                                 '-std=gnu++17' if cpp else '-std=gnu11', '-c', str(modules / path), '-o', str(obj)],
                                capture_output=True, text=True, env=environment)
        if result.returncode:
            raise RuntimeError(path + '\n' + result.stdout + result.stderr)
        return obj

    print(f'Compiling {len(paths)} files in {build}', flush=True)
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        objects = list(pool.map(compile_one, sorted(paths)))
    response = build / 'objects.rsp'
    response.write_text('\n'.join('"' + obj.as_posix() + '"' for obj in objects), encoding='utf-8')
    candidate = build / output.name
    subprocess.run([str(compiler / 'g++.exe'), '-shared', '-static', '-static-libgcc', '-static-libstdc++',
                    '@' + str(response), '-L' + str(sdk), '-lvlccore', '-lws2_32',
                    '-Wl,--no-insert-timestamp,--image-base,0x180000000,--strip-all', '-o', str(candidate)], env=environment, check=True)
    imports = subprocess.check_output([str(compiler / 'objdump.exe'), '-p', str(candidate)], env=environment, text=True)
    dependencies = {name.lower() for name in re.findall(r'DLL Name:\s+(\S+)', imports)}
    if dependencies != {'kernel32.dll', 'msvcrt.dll', 'ws2_32.dll', 'libvlccore.dll'}:
        raise RuntimeError(f'Unexpected native dependencies: {sorted(dependencies)}')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(candidate.read_bytes())
    print(f'SHA256 {hashlib.sha256(output.read_bytes()).hexdigest()}  {output}')


if __name__ == '__main__':
    main()
