"""Read-only VPK index/extractor; validates paths and CRC on extraction."""
import argparse, json, pathlib, struct, zlib

class Vpk:
    def __init__(self, path):
        self.path = pathlib.Path(path)
        self.entries = {}
        with self.path.open('rb') as f:
            signature, version, size = struct.unpack('<III', f.read(12))
            if signature != 0x55AA1234 or version not in (1, 2):
                raise ValueError('Unsupported VPK')
            header = 28 if version == 2 else 12
            f.seek(header)
            tree = f.read(size)
        self.data_start = header + size
        p = 0
        def string():
            nonlocal p
            end = tree.index(b'\0', p)
            value = tree[p:end].decode('utf-8'); p = end + 1
            return value
        while (ext := string()):
            while (directory := string()):
                while (name := string()):
                    crc, preload, archive, offset, length, terminator = struct.unpack_from('<IHHIIH', tree, p)
                    p += 18
                    if terminator != 65535: raise ValueError('Bad VPK entry')
                    prefix = tree[p:p+preload]; p += preload
                    key = ((directory + '/') if directory != ' ' else '') + name + ('.' + ext if ext != ' ' else '')
                    self.entries[key] = (crc, archive, offset, length, prefix)

    def read(self, key):
        crc, archive, offset, length, prefix = self.entries[key]
        if archive == 0x7FFF:
            archive_path = self.path; offset += self.data_start
        else:
            archive_path = self.path.with_name(self.path.name.removesuffix('_dir.vpk') + f'_{archive:03}.vpk')
        with archive_path.open('rb') as f:
            f.seek(offset); data = prefix + f.read(length)
        if zlib.crc32(data) != crc: raise ValueError('CRC mismatch: ' + key)
        return data

    def extract(self, key, output):
        root = pathlib.Path(output).resolve(); target = (root / key).resolve()
        if not target.is_relative_to(root): raise ValueError('Unsafe archive path')
        target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(self.read(key))
        return target

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('archive'); parser.add_argument('--match', default='')
    parser.add_argument('--out'); args = parser.parse_args()
    vpk = Vpk(args.archive)
    for key in vpk.entries:
        if args.match.lower() in key.lower():
            print(key)
            if args.out: vpk.extract(key, args.out)
