"""Synthesizes the chain sling sound effects (metal link clinks, rattle loop, clunks, clanks) as 44.1 kHz mono WAVs.
Standard library only. Re-run to regenerate: python3 Tools/Audio/generate_chain_sfx.py
Output: Assets/Audio/Chain/*.wav (deterministic: fixed random seeds)."""
import math, os, random, struct, wave

SR = 44100
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Assets", "Audio", "Chain")


def buf(seconds):
    return [0.0] * int(SR * seconds)


def add_modes(b, start, modes, amp):
    """modes: list of (freq_hz, decay_s, rel_amp). Exponentially decaying sines from sample index `start`."""
    n = len(b)
    for f, decay, a in modes:
        phase = random.random() * 2 * math.pi
        k = 2 * math.pi * f / SR
        length = min(n - start, int(decay * 7 * SR))
        for i in range(max(0, length)):
            b[start + i] += amp * a * math.exp(-i / (decay * SR)) * math.sin(k * i + phase)


def add_noise_burst(b, start, dur, amp, hp=0.92, decay=None):
    """High-passed noise transient (the 'tick' of steel on steel)."""
    n = int(dur * SR)
    prev_in = prev_out = 0.0
    decay = decay or dur / 4
    for i in range(n):
        if start + i >= len(b):
            break
        x = random.uniform(-1, 1)
        y = hp * (prev_out + x - prev_in)   # one-pole high-pass
        prev_in, prev_out = x, y
        b[start + i] += amp * y * math.exp(-i / (decay * SR))


def add_thump(b, start, f, decay, amp):
    for i in range(int(decay * 6 * SR)):
        if start + i >= len(b):
            break
        t = i / SR
        fi = f * (1 + 0.6 * math.exp(-t / 0.01))   # quick pitch drop
        b[start + i] += amp * math.exp(-t / decay) * math.sin(2 * math.pi * fi * t)


def clink(b, t, amp=1.0, bright=1.0, wrap=False):
    """One steel link striking another: 4 inharmonic modes + a noise tick."""
    start = int(t * SR) % len(b) if wrap else int(t * SR)
    base = random.uniform(1700, 2600) * bright
    ratios = [1.0, random.uniform(1.55, 1.8), random.uniform(2.5, 2.9), random.uniform(3.6, 4.2)]
    modes = [(base * r, random.uniform(0.018, 0.06) / (1 + 0.35 * j), 1.0 / (1 + 0.8 * j)) for j, r in enumerate(ratios)]
    if wrap and start + int(0.4 * SR) > len(b):
        tmp = buf(0.4)
        add_modes(tmp, 0, modes, amp * 0.55)
        add_noise_burst(tmp, 0, 0.006, amp * 0.35)
        for i, v in enumerate(tmp):
            b[(start + i) % len(b)] += v
        return
    add_modes(b, start, modes, amp * 0.55)
    add_noise_burst(b, start, 0.006, amp * 0.35)


def clank(b, t, base, amp=1.0, decay=0.35):
    """Heavier ring / hook strike: lower, longer modes."""
    s = int(t * SR)
    ratios = [1.0, 2.44, 4.47, 6.73, 9.1]
    modes = [(base * r * random.uniform(0.98, 1.02), decay / (1 + 0.5 * j), 1.0 / (1 + 0.7 * j)) for j, r in enumerate(ratios)]
    add_modes(b, s, modes, amp * 0.5)
    add_noise_burst(b, s, 0.012, amp * 0.4, hp=0.85, decay=0.004)


def normalize(b, peak):
    m = max(1e-9, max(abs(v) for v in b))
    g = peak / m
    fade = int(0.004 * SR)
    out = [v * g for v in b]
    for i in range(min(fade, len(out))):
        out[-1 - i] *= i / fade
    return out


def write(name, b, peak=0.8):
    os.makedirs(OUT, exist_ok=True)
    b = normalize(b, peak)
    with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, v)) * 32767)) for v in b))
    print("wrote", name, f"{len(b) / SR:.2f}s")


def main():
    # Single link clinks (played per link contact, pitch-randomised in Unity).
    for i in range(6):
        random.seed(100 + i)
        b = buf(0.22)
        clink(b, 0.0, 1.0, bright=random.uniform(0.85, 1.2))
        if random.random() < 0.6:   # a second link answering a few ms later
            clink(b, random.uniform(0.012, 0.03), random.uniform(0.3, 0.6))
        write(f"SFX_Chain_Clink_{i + 1:02d}", b, 0.85)

    # Seamless rattle loop for a moving / swinging chain.
    random.seed(7)
    b = buf(2.0)
    for _ in range(95):
        clink(b, random.uniform(0, 2.0), random.uniform(0.15, 0.7), bright=random.uniform(0.8, 1.25), wrap=True)
    write("SFX_Chain_Rattle_Loop", b, 0.6)

    # Chain dropped / landing in a heap.
    random.seed(11)
    b = buf(1.0)
    add_thump(b, 0, 95, 0.09, 0.9)
    t = 0.0
    while t < 0.75:
        clink(b, t, max(0.15, 1.0 - t * 1.1) * random.uniform(0.6, 1.0), bright=random.uniform(0.8, 1.15))
        t += random.expovariate(1 / (0.012 + t * 0.05))
    write("SFX_Chain_Drop", b, 0.85)

    # Chain bight snapping into the mould ledge socket.
    random.seed(21)
    b = buf(0.5)
    add_thump(b, 0, 120, 0.05, 1.0)
    clank(b, 0.0, 420, 0.9, decay=0.09)
    for k in range(4):
        clink(b, 0.02 + k * random.uniform(0.025, 0.05), random.uniform(0.3, 0.6))
    write("SFX_Chain_Socket_In", b, 0.9)

    # Chain pulled out of the socket.
    random.seed(23)
    b = buf(0.45)
    clank(b, 0.0, 520, 0.5, decay=0.05)
    for k in range(7):
        clink(b, 0.015 + k * random.uniform(0.02, 0.04), random.uniform(0.25, 0.65))
    write("SFX_Chain_Socket_Out", b, 0.85)

    # Ring hung on the crane hook.
    random.seed(31)
    b = buf(1.2)
    add_thump(b, 0, 140, 0.04, 0.7)
    clank(b, 0.0, 610, 1.0, decay=0.45)
    clank(b, 0.035, 640, 0.35, decay=0.25)
    for k in range(5):
        clink(b, 0.06 + k * random.uniform(0.03, 0.07), random.uniform(0.2, 0.45))
    write("SFX_Ring_Hook", b, 0.9)

    # Ring lifted off the hook (short clank + scrape).
    random.seed(33)
    b = buf(0.7)
    clank(b, 0.0, 600, 0.6, decay=0.2)
    add_noise_burst(b, int(0.02 * SR), 0.18, 0.25, hp=0.97, decay=0.06)
    for k in range(4):
        clink(b, 0.05 + k * random.uniform(0.03, 0.06), random.uniform(0.2, 0.4))
    write("SFX_Ring_Unhook", b, 0.8)

    # Free Ring hitting something.
    for i in range(3):
        random.seed(41 + i)
        b = buf(0.6)
        clank(b, 0.0, random.uniform(560, 700), 1.0, decay=random.uniform(0.18, 0.3))
        add_thump(b, 0, random.uniform(110, 160), 0.03, 0.5)
        write(f"SFX_Ring_Impact_{i + 1:02d}", b, 0.85)

    # Chains pulling taut as the mould leaves the ground.
    random.seed(51)
    b = buf(0.9)
    n = int(0.45 * SR)
    phase = 0.0
    for i in range(n):  # low strained creak
        f = 62 + 9 * math.sin(i / SR * 23) + random.uniform(-3, 3)
        phase += f / SR
        env = math.sin(math.pi * i / n) ** 2
        b[i] += 0.35 * env * ((phase % 1.0) * 2 - 1)
    clank(b, 0.05, 380, 0.9, decay=0.25)
    clank(b, 0.08, 410, 0.6, decay=0.2)
    for k in range(6):
        clink(b, 0.07 + k * random.uniform(0.02, 0.05), random.uniform(0.3, 0.6))
    write("SFX_Chain_Taut", b, 0.9)


if __name__ == "__main__":
    main()
