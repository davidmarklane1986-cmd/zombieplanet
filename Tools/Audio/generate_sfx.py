#!/usr/bin/env python3
"""
Stargrave procedural SFX generator.

Synthesizes the sound effects the Kenney packs lack (sci-fi blaster shots and
zombie vocalisations) and writes them as 16-bit PCM mono .wav files.

Run from the repository root:
    python Tools/Audio/generate_sfx.py

Output: Assets/Stargrave/Audio/Generated/*.wav

The .wav files are committed, but this script lets anyone regenerate / retune
them. Pure standard library (math, struct, wave, random) - no numpy required.
"""

import math
import os
import random
import struct
import wave

SAMPLE_RATE = 44100
OUT_DIR = os.path.join("Assets", "Stargrave", "Audio", "Generated")


def _write_wav(name, samples):
    """samples: iterable of floats in [-1, 1]. Writes 16-bit mono PCM."""
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, name)
    frames = bytearray()
    for s in samples:
        if s > 1.0:
            s = 1.0
        elif s < -1.0:
            s = -1.0
        frames += struct.pack("<h", int(s * 32767.0))
    with wave.open(path, "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        w.writeframes(bytes(frames))
    print(f"  wrote {path} ({len(frames) // 2} samples, {len(frames) / SAMPLE_RATE / 2:.2f}s)")


def _n(duration):
    return int(SAMPLE_RATE * duration)


def _soft_clip(x):
    # gentle tanh-ish saturation to add body without harsh digital clipping
    return math.tanh(x * 1.4)


def blaster_shoot(seed, base_start=1400.0, base_end=240.0, duration=0.26):
    """Classic 'pew': descending pitch sweep (square+sine mix) with fast decay."""
    rng = random.Random(seed)
    start = base_start * rng.uniform(0.9, 1.12)
    end = base_end * rng.uniform(0.85, 1.15)
    dur = duration * rng.uniform(0.92, 1.1)
    total = _n(dur)
    phase = 0.0
    samples = []
    for i in range(total):
        t = i / total  # 0..1
        # exponential-ish frequency descent (snappier at the start)
        freq = end + (start - end) * ((1.0 - t) ** 2.2)
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        sine = math.sin(phase)
        square = 1.0 if sine >= 0.0 else -1.0
        # blend square (buzzy laser) with sine (tone), squares fade as it decays
        tone = sine * 0.55 + square * 0.45 * (1.0 - t)
        # quick percussive attack + exponential decay envelope
        attack = min(1.0, i / max(1, _n(0.004)))
        env = attack * math.exp(-5.5 * t)
        samples.append(_soft_clip(tone * env) * 0.85)
    # short tail of filtered noise for a sci-fi 'zap' sparkle
    return samples


def zombie_groan(seed, duration=1.15, pitch=62.0):
    """
    Deep, bass-heavy groan: a dedicated sub-bass sine (~50-90 Hz) under a low fundamental, with the
    upper harmonics and breath-noise dialled back so the low end dominates. Slow amplitude/pitch wobble
    keeps it organic and growly.
    """
    rng = random.Random(seed)
    dur = duration * rng.uniform(0.92, 1.12)
    f0 = pitch * rng.uniform(0.85, 1.12)
    # Sub-bass layer kept inside the audible-on-small-speakers band (~50-90 Hz) for chest-thump weight.
    sub_freq = max(50.0, min(90.0, f0 * 0.92))
    total = _n(dur)
    phase = 0.0
    sub_phase = 0.0
    noise_lp = 0.0
    wobble_rate = rng.uniform(3.0, 4.8)
    samples = []
    for i in range(total):
        t = i / total
        # slow vibrato on the fundamental for an organic, sickly waver
        vibrato = 1.0 + 0.05 * math.sin(2.0 * math.pi * wobble_rate * (i / SAMPLE_RATE))
        freq = f0 * vibrato
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        sub_phase += 2.0 * math.pi * sub_freq / SAMPLE_RATE
        # Bass-dominant mix: heavy sub + fundamental, only a whisper of 2nd harmonic, no bright 3rd.
        sub = math.sin(sub_phase)
        voice = (sub * 0.85
                 + math.sin(phase) * 0.55
                 + math.sin(phase * 2.0) * 0.08)
        # low-passed white noise = breath/gravel, reduced and filtered harder so highs don't thin the bass
        white = rng.uniform(-1.0, 1.0)
        noise_lp += (white - noise_lp) * 0.02
        gravel = noise_lp * 0.28
        # slow amplitude wobble + smooth fade in/out
        amp_wobble = 0.72 + 0.28 * math.sin(2.0 * math.pi * (wobble_rate * 0.4) * (i / SAMPLE_RATE))
        fade = math.sin(math.pi * t) ** 0.55  # rounded attack and release
        s = (voice * 0.7 + gravel) * amp_wobble * fade
        samples.append(_soft_clip(s) * 0.9)
    return samples


def zombie_attack(seed, duration=0.55, pitch=120.0):
    """Short aggressive rising snarl/lunge (deepened: lower pitch + a sub layer for weight)."""
    rng = random.Random(seed)
    total = _n(duration)
    phase = 0.0
    sub_phase = 0.0
    noise_lp = 0.0
    samples = []
    for i in range(total):
        t = i / total
        # pitch rises then snaps - a lunge
        freq = pitch * (0.8 + 1.4 * t)
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        sub_phase += 2.0 * math.pi * (freq * 0.5) / SAMPLE_RATE
        voice = math.sin(sub_phase) * 0.4 + math.sin(phase) * 0.6 + math.sin(phase * 2.0) * 0.2
        white = rng.uniform(-1.0, 1.0)
        noise_lp += (white - noise_lp) * 0.1
        gravel = noise_lp * 0.6
        env = (t ** 0.5) * math.exp(-2.0 * max(0.0, t - 0.55))
        s = (voice * 0.6 + gravel) * env
        samples.append(_soft_clip(s * 1.3) * 0.85)
    return samples


def zombie_death(seed, duration=0.95, pitch=100.0):
    """Descending dying groan that trails off (deepened with a sub layer)."""
    rng = random.Random(seed)
    total = _n(duration)
    phase = 0.0
    sub_phase = 0.0
    noise_lp = 0.0
    samples = []
    for i in range(total):
        t = i / total
        freq = pitch * (1.0 - 0.55 * t)  # sags as it dies
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        sub_phase += 2.0 * math.pi * max(45.0, freq * 0.6) / SAMPLE_RATE
        voice = math.sin(sub_phase) * 0.5 + math.sin(phase) * 0.6 + math.sin(phase * 2.0) * 0.1
        white = rng.uniform(-1.0, 1.0)
        noise_lp += (white - noise_lp) * 0.04
        gravel = noise_lp * 0.35
        env = math.exp(-2.6 * t) * (1.0 - t) ** 0.4
        s = (voice * 0.7 + gravel) * env
        samples.append(_soft_clip(s) * 0.85)
    return samples


def hit(seed, duration=0.2):
    """
    Punchy impact thud: a fast low sine 'thump' (pitch drops ~140 -> ~55 Hz) with a snappy decay,
    layered with a very short filtered-noise transient at the attack for the 'smack'. ~0.15-0.25s.
    """
    rng = random.Random(seed)
    dur = duration * rng.uniform(0.9, 1.15)
    total = _n(dur)
    f_start = 140.0 * rng.uniform(0.9, 1.15)
    f_end = 55.0 * rng.uniform(0.9, 1.1)
    phase = 0.0
    noise_lp = 0.0
    samples = []
    for i in range(total):
        t = i / total
        # quick exponential pitch drop = body of the thump
        freq = f_end + (f_start - f_end) * math.exp(-7.0 * t)
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        thump = math.sin(phase)
        # noise transient: loud for the first ~15% then gone (the 'smack')
        white = rng.uniform(-1.0, 1.0)
        noise_lp += (white - noise_lp) * 0.45
        transient = noise_lp * math.exp(-30.0 * t)
        body_env = math.exp(-9.0 * t)
        s = thump * body_env * 0.9 + transient * 0.6
        samples.append(_soft_clip(s * 1.2) * 0.9)
    return samples


# --------------------------------------------------------------------------------------------------
# Footsteps: short filtered-noise impacts, one timbre per surface category. Each call seeds its own RNG
# and jitters params slightly, so calling it with different seeds yields natural variations.
# --------------------------------------------------------------------------------------------------

def footstep_grass(seed):
    """Soft broadband swish/rustle: gently low-passed noise, soft attack, quick decay."""
    rng = random.Random(seed)
    total = _n(0.16 * rng.uniform(0.9, 1.18))
    lp = 0.0
    lp_coeff = rng.uniform(0.18, 0.30)
    decay = rng.uniform(22.0, 30.0)
    atk = max(1, _n(0.009))
    out = []
    for i in range(total):
        t = i / total
        white = rng.uniform(-1.0, 1.0)
        lp += (white - lp) * lp_coeff
        sig = lp * 0.82 + white * 0.12  # mostly soft, a hint of rustle on top
        env = min(1.0, i / atk) * math.exp(-decay * t)
        out.append(_soft_clip(sig * env) * 0.5)
    return out


def footstep_sand(seed):
    """Grittier, dry crunch: band-limited noise with granular amplitude + a slightly longer grainy tail."""
    rng = random.Random(seed)
    total = _n(0.20 * rng.uniform(0.9, 1.15))
    lp = 0.0
    lp_coeff = rng.uniform(0.30, 0.45)
    decay = rng.uniform(14.0, 20.0)
    atk = max(1, _n(0.006))
    out = []
    for i in range(total):
        t = i / total
        white = rng.uniform(-1.0, 1.0)
        lp += (white - lp) * lp_coeff
        hp = white - lp  # high band for grit
        grain = 0.55 + 0.45 * abs(rng.uniform(-1.0, 1.0))  # granular crunch
        sig = (lp * 0.45 + hp * 0.5) * grain
        env = min(1.0, i / atk) * math.exp(-decay * t)
        out.append(_soft_clip(sig * env) * 0.55)
    return out


def footstep_snow(seed):
    """High-frequency squeak/crunch: high-passed noise + a tiny pitched squeak."""
    rng = random.Random(seed)
    total = _n(0.15 * rng.uniform(0.9, 1.12))
    lp = 0.0
    lp_coeff = rng.uniform(0.45, 0.6)
    decay = rng.uniform(24.0, 32.0)
    squeak_f = rng.uniform(1900.0, 2600.0)
    squeak_phase = 0.0
    atk = max(1, _n(0.004))
    out = []
    for i in range(total):
        t = i / total
        white = rng.uniform(-1.0, 1.0)
        lp += (white - lp) * lp_coeff
        hp = white - lp  # crunchy high band
        # tiny squeak with a fast vibrato, decays quicker than the crunch
        squeak_phase += 2.0 * math.pi * (squeak_f * (1.0 + 0.04 * math.sin(60.0 * t))) / SAMPLE_RATE
        squeak = math.sin(squeak_phase) * math.exp(-34.0 * t) * 0.22
        sig = hp * 0.5 + squeak
        env = min(1.0, i / atk) * math.exp(-decay * t)
        out.append(_soft_clip(sig * env) * 0.5)
    return out


def footstep_rock(seed):
    """Hard, sharp tap: snappy noise transient + a quick mid tonal click, fast decay."""
    rng = random.Random(seed)
    total = _n(0.12 * rng.uniform(0.9, 1.15))
    click_f = rng.uniform(430.0, 820.0)
    click_phase = 0.0
    lp = 0.0
    out = []
    for i in range(total):
        t = i / total
        white = rng.uniform(-1.0, 1.0)
        lp += (white - lp) * 0.55
        hp = white - lp
        transient = hp * math.exp(-48.0 * t)        # sharp smack
        click_phase += 2.0 * math.pi * click_f / SAMPLE_RATE
        click = math.sin(click_phase) * math.exp(-42.0 * t) * 0.4  # tiny tonal tick
        sig = transient * 0.75 + click
        out.append(_soft_clip(sig * 1.1) * 0.55)
    return out


def footstep_water(seed):
    """Shallow splash: wet low-mid noise + a resonant downward chirp, longer wet tail."""
    rng = random.Random(seed)
    total = _n(0.24 * rng.uniform(0.9, 1.15))
    lp = 0.0
    lp_coeff = rng.uniform(0.22, 0.32)
    decay = rng.uniform(10.0, 15.0)
    f_start = rng.uniform(820.0, 1100.0)
    f_end = rng.uniform(260.0, 360.0)
    phase = 0.0
    atk = max(1, _n(0.005))
    out = []
    for i in range(total):
        t = i / total
        white = rng.uniform(-1.0, 1.0)
        lp += (white - lp) * lp_coeff
        freq = f_end + (f_start - f_end) * math.exp(-6.0 * t)  # resonant 'bloop' downward
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        chirp = math.sin(phase) * math.exp(-9.0 * t) * 0.35
        sig = lp * 0.55 + chirp
        env = min(1.0, i / atk) * math.exp(-decay * t)
        out.append(_soft_clip(sig * env) * 0.55)
    return out


FOOTSTEP_GENERATORS = {
    "grass": footstep_grass,
    "sand": footstep_sand,
    "snow": footstep_snow,
    "rock": footstep_rock,
    "water": footstep_water,
}


def write_footsteps(variations=4):
    for name, gen in FOOTSTEP_GENERATORS.items():
        for v in range(1, variations + 1):
            seed = hash((name, v)) & 0x7FFFFFFF
            _write_wav(f"footstep_{name}_{v}.wav", gen(seed))


def main():
    print("Generating Stargrave procedural SFX ->", OUT_DIR)
    _write_wav("blaster_shoot_1.wav", blaster_shoot(1, base_start=1500, base_end=240))
    _write_wav("blaster_shoot_2.wav", blaster_shoot(2, base_start=1250, base_end=200))
    _write_wav("blaster_shoot_3.wav", blaster_shoot(3, base_start=1750, base_end=300))
    _write_wav("zombie_groan_1.wav", zombie_groan(11, duration=1.25, pitch=60))
    _write_wav("zombie_groan_2.wav", zombie_groan(12, duration=1.1, pitch=72))
    _write_wav("zombie_attack_1.wav", zombie_attack(21))
    _write_wav("zombie_death_1.wav", zombie_death(31))
    _write_wav("hit_1.wav", hit(41))
    _write_wav("hit_2.wav", hit(42))
    write_footsteps(variations=4)
    print("Done.")


if __name__ == "__main__":
    main()
