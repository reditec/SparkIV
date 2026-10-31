# Reads GTA IV's compiled shaders (common/shaders/win32_30/*.fxc, format "rgxa") and writes, per shader, its techniques
# with their passes' programs and render states, its parameters and its programs disassembled (docs/GtaIV/Pc/formats/fxc.md).
#   python tools/fxc-dump.py <...\GTAIV\common\shaders\win32_30> <output folder>
#   python tools/fxc-dump.py --states <...\win32_30>     every technique's pass states, grouped, with the shaders using them
# Needs Windows (d3dcompiler_47.dll for the disassembly) and Python 3, nothing else.
import ctypes, collections, os, struct, sys

# GTAIV.exe's table of the passes' state numbers (file offset 0xD10980): state i of a pass is D3DRENDERSTATETYPE STATES[i].
STATES = ['ZENABLE', 'FILLMODE', 'ZWRITEENABLE', 'ALPHATESTENABLE', 'SRCBLEND', 'DESTBLEND', 'CULLMODE', 'ZFUNC',
          'ALPHAREF', 'ALPHAFUNC', 'ALPHABLENDENABLE', 'STENCILENABLE', 'STENCILFAIL', 'STENCILZFAIL', 'STENCILPASS',
          'STENCILFUNC', 'STENCILREF', 'STENCILMASK', 'STENCILWRITEMASK', 'COLORWRITEENABLE', 'COLORWRITEENABLE1',
          'COLORWRITEENABLE2', 'COLORWRITEENABLE3', 'BLENDOP', 'BLENDOPALPHA', 'SEPARATEALPHABLENDENABLE',
          'SRCBLENDALPHA', 'DESTBLENDALPHA']
BLEND = {1: 'ZERO', 2: 'ONE', 3: 'SRCCOLOR', 4: 'INVSRCCOLOR', 5: 'SRCALPHA', 6: 'INVSRCALPHA', 7: 'DESTALPHA',
         8: 'INVDESTALPHA', 9: 'DESTCOLOR', 10: 'INVDESTCOLOR'}
CMP = {1: 'NEVER', 2: 'LESS', 3: 'EQUAL', 4: 'LESSEQUAL', 5: 'GREATER', 6: 'NOTEQUAL', 7: 'GREATEREQUAL', 8: 'ALWAYS'}
CULL = {1: 'NONE', 2: 'CW', 3: 'CCW'}


class Reader:
    def __init__(self, data): self.b, self.p = data, 0
    def u8(self): v = self.b[self.p]; self.p += 1; return v
    def u16(self): v = struct.unpack_from('<H', self.b, self.p)[0]; self.p += 2; return v
    def u32(self): v = struct.unpack_from('<I', self.b, self.p)[0]; self.p += 4; return v
    def raw(self, n): v = self.b[self.p:self.p + n]; self.p += n; return v
    def text(self): return self.raw(self.u8()).split(b'\0')[0].decode('latin1')


def read_programs(r):
    # count, then per program: its constants (type, ?, register, name), its size twice and its D3D9 bytecode
    out = []
    for _ in range(r.u8()):
        params = []
        for _ in range(r.u8()):
            t = r.u8(); r.u8(); reg = r.u16()
            params.append({'type': t, 'reg': reg, 'name': r.text()})
        size = r.u16(); r.u16()
        out.append({'params': params, 'code': r.raw(size)})
    return out


def read_annotations(r):
    out = []
    for _ in range(r.u8()):
        name = r.text(); t = r.u8()
        out.append((name, r.u32() if t == 0 else struct.unpack('<f', r.raw(4))[0] if t == 1 else r.text()))
    return out


def read_variables(r):
    # count, then per variable: type, array count, name, semantic, annotations, its default (32-bit words; a
    # sampler's are its sampler states as pairs)
    out = []
    for _ in range(r.u8()):
        t = r.u8(); n = r.u8(); name = r.text(); sem = r.text(); ann = read_annotations(r)
        out.append({'type': t, 'count': n, 'name': name, 'semantic': sem, 'ann': ann, 'values': [r.u32() for _ in range(r.u8())]})
    return out


def parse(data):
    r = Reader(data)
    if r.raw(4) != b'rgxa': raise ValueError('not an rgxa shader')
    f = {'vs': read_programs(r), 'ps': read_programs(r), 'globals': read_variables(r), 'locals': read_variables(r)}
    techniques = []
    for _ in range(r.u8()):
        name = r.text(); passes = []
        for _ in range(r.u8()):
            vs = r.u8(); ps = r.u8()
            passes.append({'vs': vs, 'ps': ps, 'states': [(r.u32(), r.u32()) for _ in range(r.u8())]})
        techniques.append({'name': name, 'passes': passes})
    f['techniques'] = techniques
    if r.p != len(data): raise ValueError('%d bytes left over' % (len(data) - r.p))
    return f


def state_text(k, v):
    n = STATES[k] if k < len(STATES) else 'state%d' % k
    if n in ('SRCBLEND', 'DESTBLEND', 'SRCBLENDALPHA', 'DESTBLENDALPHA'): v = BLEND.get(v, v)
    elif n in ('ZFUNC', 'ALPHAFUNC', 'STENCILFUNC'): v = CMP.get(v, v)
    elif n == 'CULLMODE': v = CULL.get(v, v)
    elif n.startswith('COLORWRITE'): v = hex(v)
    return '%s=%s' % (n, v)


def disassemble(code):
    if not code: return '(none)'
    d3d = ctypes.WinDLL('d3dcompiler_47.dll')
    blob = ctypes.c_void_p()
    if d3d.D3DDisassemble(ctypes.c_char_p(bytes(code)), ctypes.c_size_t(len(code)), 0, None, ctypes.byref(blob)) != 0:
        return '(disassembly failed)'
    vtable = ctypes.cast(ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])
    pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtable[3])
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtable[4])
    text = ctypes.string_at(pointer(blob), size(blob)).decode('latin1').rstrip('\0')
    release(blob)
    return '\n'.join(l for l in text.splitlines() if l.strip() and not l.lstrip().startswith('//'))


def write_dump(path, out):
    f = parse(open(path, 'rb').read())
    w = out.write
    w('# %s\n\n## Techniques\n\n' % os.path.basename(path))
    for t in f['techniques']:
        for i, p in enumerate(t['passes']):
            w('%-34s pass %d  vs %-2d ps %-2d %s\n' % (t['name'], i, p['vs'], p['ps'], ' '.join(state_text(k, v) for k, v in p['states'])))
    w('\n## Parameters\n\n')
    for v in f['locals']:
        w('%-24s type %d x%d  %s  %s  %s\n' % (v['name'], v['type'], v['count'], v['semantic'], v['ann'], v['values'][:16]))
    for kind in ('ps', 'vs'):
        for i, p in enumerate(f[kind]):
            w('\n## %s %d\n\n' % (kind, i))
            w(''.join('// %s: register %d (type %d)\n' % (q['name'], q['reg'], q['type']) for q in p['params']))
            w(disassemble(p['code']) + '\n')


def states_summary(folder):
    groups = collections.defaultdict(list)
    for fn in sorted(os.listdir(folder)):
        if not fn.endswith('.fxc'): continue
        for t in parse(open(os.path.join(folder, fn), 'rb').read())['techniques']:
            for i, p in enumerate(t['passes']):
                groups[(t['name'], i, ' '.join(state_text(k, v) for k, v in p['states']) or '-')].append(fn[:-4])
    for (t, i, st), shaders in sorted(groups.items()):
        print('%s pass %d | %s | %s' % (t, i, st, ', '.join(shaders)))


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--states':
        states_summary(sys.argv[2])
    elif len(sys.argv) == 3:
        os.makedirs(sys.argv[2], exist_ok=True)
        for fn in sorted(os.listdir(sys.argv[1])):
            if fn.endswith('.fxc'):
                with open(os.path.join(sys.argv[2], fn[:-4] + '.txt'), 'w', encoding='utf-8') as out:
                    write_dump(os.path.join(sys.argv[1], fn), out)
    else:
        print(__doc__ or 'python tools/fxc-dump.py <win32_30 folder> <output folder> | --states <win32_30 folder>')
