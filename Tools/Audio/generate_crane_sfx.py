"""Synthesizes overhead crane sound effects as 44.1 kHz mono WAVs (standard library only, fixed seeds).
Re-run to regenerate: python3 Tools/Audio/generate_crane_sfx.py  ->  Assets/Audio/Crane/*.wav
Loops are seamless: every tone completes whole cycles over the loop and noise beds are cross-faded around the seam."""
import math, os, random, struct, wave

SR = 44100
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Assets", "Audio", "Crane")


def zeros(sec):
    return [0.0] * int(SR * sec)


def tone(b, f, amp, start=0, length=None, fm_rate=0.0, fm_depth=0.0, env=None):
    """Sine at f Hz (optionally vibrato), mixed into b. env(i, n) -> gain."""
    n = len(b) - start if length is None else min(length, len(b) - start)
    ph = random.random() * 2 * math.pi
    for i in range(n):
        t = i / SR
        fi = f + fm_depth * math.sin(2 * math.pi * fm_rate * t)
        ph += 2 * math.pi * fi / SR
        g = env(i, n) if env else 1.0
        b[start + i] += amp * g * math.sin(ph)


def noise_bed(sec, lp=0.05, hp=0.0, seed=0):
    """Filtered noise (one-pole low-pass, optional one-pole high-pass), cross-faded so it loops."""
    rnd = random.Random(seed)
    n = int(SR * sec)
    fade = int(0.25 * SR)
    raw = []
    y = 0.0
    hy = hx = 0.0
    for _ in range(n + fade):
        x = rnd.uniform(-1, 1)
        y += lp * (x - y)
        out = y
        if hp > 0:
            hy = hp * (hy + out - hx)
            hx = out
            out = hy
        raw.append(out)
    # cross-fade the extra tail into the head -> seamless loop
    for i in range(fade):
        a = i / fade
        raw[i] = raw[i] * a + raw[n + i] * (1 - a)
    return raw[:n]


def add(b, src, amp, start=0):
    for i, v in enumerate(src):
        j = start + i
        if j >= len(b):
            break
        b[j] += amp * v


def thump(b, start, f, decay, amp):
    for i in range(int(decay * 6 * SR)):
        if start + i >= len(b):
            break
        t = i / SR
        b[start + i] += amp * math.exp(-t / decay) * math.sin(2 * math.pi * f * (1 + 0.5 * math.exp(-t / 0.015)) * t)


def click(b, start, amp, dur=0.004, seed=1):
    rnd = random.Random(seed)
    n = int(dur * SR)
    px = py = 0.0
    for i in range(n):
        x = rnd.uniform(-1, 1)
        y = 0.9 * (py + x - px)
        px, py = x, y
        if start + i < len(b):
            b[start + i] += amp * y * math.exp(-i / (n / 3))


def clank(b, start, base, amp, decay):
    for j, r in enumerate([1.0, 2.32, 3.9, 5.6, 7.7]):
        tone(b, base * r * random.uniform(0.98, 1.02), amp / (1 + 0.7 * j), start, int(decay / (1 + 0.4 * j) * 6 * SR),
             env=lambda i, n, d=decay / (1 + 0.4 * j): math.exp(-i / (d * SR)))


def periodic_ticks(b, rate_hz, amp, seed):
    n = len(b)
    k = 0
    period = SR / rate_hz
    while k * period < n:
        click(b, int(k * period), amp, 0.003, seed + k)
        k += 1


def write(name, b, peak):
    os.makedirs(OUT, exist_ok=True)
    m = max(1e-9, max(abs(v) for v in b))
    b = [v * peak / m for v in b]
    with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, v)) * 32767)) for v in b))
    print("wrote", name, f"{len(b) / SR:.2f}s")


def main():
    # Bridge travel: heavy motor hum, gearbox whine, wheels rumbling on the runway rail, rail-joint clacks.
    random.seed(1)
    L = 4.0
    b = zeros(L)
    for f, a in [(50, 0.5), (100, 0.35), (150, 0.18), (300, 0.08)]:
        tone(b, f, a)
    tone(b, 380, 0.12, fm_rate=0.5, fm_depth=4)
    tone(b, 760, 0.04, fm_rate=0.5, fm_depth=8)
    add(b, noise_bed(L, lp=0.02, seed=11), 2.2)        # low rail rumble
    add(b, noise_bed(L, lp=0.25, hp=0.95, seed=12), 0.12)  # wheel hiss
    for t in (0.6, 2.6):                                 # rail joints
        thump(b, int(t * SR), 70, 0.05, 0.6)
        click(b, int(t * SR), 0.25, 0.006, 3)
    write("SFX_Crane_Bridge_Loop", b, 0.7)

    # Trolley travel: lighter motor, higher whine, light rumble.
    random.seed(2)
    L = 3.0
    b = zeros(L)
    for f, a in [(200 / 3, 0.35), (400 / 3, 0.25), (200, 0.12)]:
        tone(b, f, a)
    tone(b, 560, 0.14, fm_rate=1 / 3, fm_depth=5)
    tone(b, 1120, 0.04)
    add(b, noise_bed(L, lp=0.04, seed=21), 1.2)
    add(b, noise_bed(L, lp=0.3, hp=0.96, seed=22), 0.08)
    write("SFX_Crane_Trolley_Loop", b, 0.65)

    # Hoist: electric hoist motor + cooling fan whir + drum gear ticks + wire rope hiss.
    random.seed(3)
    L = 3.0
    b = zeros(L)
    for f, a in [(100, 0.4), (200, 0.3), (300, 0.15), (400, 0.06)]:
        tone(b, f, a)
    tone(b, 760, 0.16, fm_rate=2 / 3, fm_depth=6)
    tone(b, 1140, 0.07)
    tone(b, 2280, 0.025)
    periodic_ticks(b, 12, 0.12, 300)
    add(b, noise_bed(L, lp=0.35, hp=0.97, seed=31), 0.1)
    write("SFX_Crane_Hoist_Loop", b, 0.65)

    # Motor start: contactor click, brake release thunk, whine spooling up.
    random.seed(4)
    b = zeros(0.7)
    click(b, 0, 0.9, 0.004, 41)
    thump(b, int(0.02 * SR), 75, 0.06, 0.8)
    click(b, int(0.03 * SR), 0.4, 0.008, 42)
    n = int(0.6 * SR)
    tone(b, 380, 0.25, int(0.05 * SR), n, env=lambda i, n: (i / n) ** 0.7 * (1 - i / n) * 2.2)
    tone(b, 100, 0.3, int(0.05 * SR), n, env=lambda i, n: (i / n) * (1 - i / n) * 3)
    write("SFX_Crane_Motor_Start", b, 0.8)

    # Motor stop: whine spooling down, brake clamping.
    random.seed(5)
    b = zeros(0.8)
    n = int(0.45 * SR)
    tone(b, 380, 0.25, 0, n, env=lambda i, n: (1 - i / n) ** 1.5)
    tone(b, 100, 0.3, 0, n, env=lambda i, n: (1 - i / n) ** 1.5)
    thump(b, int(0.12 * SR), 65, 0.07, 0.9)
    clank(b, int(0.12 * SR), 310, 0.35, 0.08)
    click(b, int(0.12 * SR), 0.5, 0.006, 51)
    write("SFX_Crane_Motor_Stop", b, 0.8)

    # Reaching the end of travel / buffer stop.
    random.seed(6)
    b = zeros(1.0)
    thump(b, 0, 55, 0.12, 1.0)
    clank(b, 0, 180, 0.8, 0.35)
    clank(b, int(0.03 * SR), 260, 0.35, 0.2)
    click(b, 0, 0.6, 0.01, 61)
    write("SFX_Crane_Limit_Hit", b, 0.9)

    # Travel warning beeper (one beep per second, loopable).
    random.seed(7)
    b = zeros(1.0)
    n = int(0.28 * SR)
    for f, a in [(2700, 0.6), (5400, 0.15), (8100, 0.06)]:
        tone(b, f, a, 0, n, env=lambda i, n: min(1, i / 300) * min(1, (n - i) / 600))
    write("SFX_Crane_Warning_Beep_Loop", b, 0.6)


if __name__ == "__main__":
    main()
