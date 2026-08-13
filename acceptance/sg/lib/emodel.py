"""
ITU-T G.107 E-model — the transmission rating R, and the MOS derived from it.

WHY THIS FILE EXISTS AT ALL
---------------------------
C131's definition of done says "VoIP concurrency and quality targets are measured in-region".
The quality target the specs give is a *concurrency* number (ADD §3.2: 500 concurrent calls in
Phase 1) and a *setup latency* number (ADD §13.3 row 4: signalling to first audio frame, p95
< 4 s).  **Neither document gives a MOS floor, a jitter budget or a loss budget** — the words
"MOS", "jitter" and "packet loss" appear in this component's prompt and in no specification in
`specs/`.  So the numbers this file computes have to be traceable to a published standard rather
than to a threshold somebody picked, and G.107 is the one the industry uses for exactly this:
turning measured delay, jitter and loss on a path into a single planning figure.

**A computed MOS is not a subjective test.**  G.107 is a planning model; it predicts what a
listening panel would say about a connection with these transmission parameters.  It is the right
instrument for "is a Colombo-to-Singapore relayed call good enough", and it is the wrong
instrument for "do users like the call quality".  Every figure this module produces is labelled
MOS-CQE (conversational quality, estimated) in the report for that reason.

WHAT IS APPROXIMATED, AND WHERE THAT SHOWS
------------------------------------------
- **The codec is Opus and the impairment parameters are G.711's.**  `livekit.yaml` pins
  `enabled_codecs: [audio/opus]`.  G.113 Appendix I publishes Ie/Bpl for the G.7xx family and
  **has no Opus row**; there is no ITU-published equipment impairment for it.  The values used
  here are G.711 with PLC (Ie = 0, Bpl = 25.1), which is the closest published parameterisation
  of a codec in Opus's class — Opus at 64 kbit/s wideband is generally rated at or above G.711
  narrowband, so a MOS computed this way is conservative on the codec axis and says nothing
  flattering that Opus would not earn.  `CODEC_NOTE` carries this into the report; it is not a
  footnote to be dropped.
- **No echo terms.**  Idte and Idle are zero: both handsets are running LiveKit's WebRTC stack
  with AEC on, and this harness measures a relay path rather than a terminal.  A real one-way-audio
  or echo fault does not show up as a lower MOS here — it shows up as loss, or as nothing at all,
  which is why `media_probe.py` reports the directional counters separately.
- **The advantage factor A defaults to 0.**  G.107 §B.2 allows 10 for "mobile in a moving
  vehicle", which is literally every call on this platform.  It is left at 0 because A is the one
  term in the model that exists to *excuse* a bad connection, and a harness that grants itself
  ten points of R before it starts is not measuring.  The report states both.

Reference: ITU-T G.107 (06/2015), §3 (the rating), §4 (default values), Annex B (Idd and the
R-to-MOS transform).
"""

from __future__ import annotations

import math
from dataclasses import dataclass

# ---------------------------------------------------------------------------------------------
# G.107 default values (§4, Table 1).  These are the "default connection" the model is defined
# against: no noise beyond the defaults, no loudness deviation, no echo.
# ---------------------------------------------------------------------------------------------

#: The basic signal-to-noise ratio with every G.107 Table 1 default in place.  G.107 §4 states
#: this outright — it is the value the whole model is anchored on, and computing it from the
#: noise terms would be re-deriving a constant the standard prints.
R0_DEFAULT = 93.2

#: Simultaneous impairment factor.  Zero under Table 1 defaults (no quantisation distortion, no
#: sidetone deviation, no loudness deviation).
IS_DEFAULT = 0.0

#: Equipment impairment at zero loss, and the packet-loss robustness factor.  G.113 Appendix I,
#: G.711 with the Appendix I packet-loss concealment.  See CODEC_NOTE.
IE_G711_PLC = 0.0
BPL_G711_PLC = 25.1

CODEC_NOTE = (
    "Ie=0 / Bpl=25.1 (G.113 App. I, G.711+PLC). G.113 publishes no Opus row; Opus at 64 kbit/s "
    "wideband is rated at or above G.711 narrowband, so this parameterisation is conservative "
    "on the codec axis."
)

#: Advantage factor.  See the module docstring — deliberately 0.
A_DEFAULT = 0.0

#: G.107 §B.2's advantage factor for "mobile, in a moving vehicle", reported alongside but never
#: used as the headline.
A_MOBILE_VEHICLE = 10.0


@dataclass(frozen=True)
class Rating:
    """One evaluation of the model, with every term kept so the report can show the arithmetic."""

    r: float
    mos: float
    r0: float
    i_s: float
    i_d: float
    ie_eff: float
    a: float
    ta_ms: float
    ppl_percent: float
    burst_ratio: float

    def as_dict(self) -> dict:
        return {
            "R": round(self.r, 2),
            "MOS_CQE": round(self.mos, 3),
            "R0": self.r0,
            "Is": self.i_s,
            "Id": round(self.i_d, 3),
            "Ie_eff": round(self.ie_eff, 3),
            "A": self.a,
            "Ta_ms": round(self.ta_ms, 2),
            "Ppl_percent": round(self.ppl_percent, 4),
            "BurstR": round(self.burst_ratio, 3),
        }


def idd(ta_ms: float) -> float:
    """
    G.107 Annex B's delay impairment factor Idd, for absolute one-way delay `ta_ms`.

    Zero below 100 ms and continuous there: the standard's own piecewise definition, and the
    continuity at 100 ms is one of the fixed points `selftest.py` pins.  Above it the term grows
    faster than linearly, which is the entire reason the Colombo-TURN question has an answer —
    see `delay_budget_ms` and report.md §5.
    """
    if ta_ms < 100.0:
        return 0.0

    x = math.log(ta_ms / 100.0) / math.log(2.0)

    return 25.0 * (
        (1.0 + x**6) ** (1.0 / 6.0)
        - 3.0 * (1.0 + (x / 3.0) ** 6) ** (1.0 / 6.0)
        + 2.0
    )


def ie_effective(
    ppl_percent: float,
    burst_ratio: float = 1.0,
    ie: float = IE_G711_PLC,
    bpl: float = BPL_G711_PLC,
) -> float:
    """
    G.107 §3's effective equipment impairment under packet loss.

        Ie_eff = Ie + (95 - Ie) * Ppl / (Ppl / BurstR + Bpl)

    `burst_ratio` is 1 for random (Bernoulli) loss and > 1 when losses arrive in bursts.  It
    matters more than it looks: the same 2 % loss arriving in bursts of ten is markedly worse
    than 2 % scattered, because PLC conceals one missing frame well and ten consecutive ones not
    at all.  `rtpstats.py` measures it from the actual gap pattern rather than assuming 1.
    """
    if ppl_percent <= 0.0:
        return ie

    burst_ratio = max(burst_ratio, 1e-9)

    return ie + (95.0 - ie) * ppl_percent / (ppl_percent / burst_ratio + bpl)


def r_to_mos(r: float) -> float:
    """
    G.107 Annex B's transform from the rating R to MOS-CQE.

    Clamped at both ends by the standard itself: R <= 0 is MOS 1, R >= 100 is MOS 4.5.  The
    cubic in between is what produces the familiar 4.41 for an unimpaired G.711 connection.
    """
    if r <= 0.0:
        return 1.0
    if r >= 100.0:
        return 4.5

    return 1.0 + 0.035 * r + r * (r - 60.0) * (100.0 - r) * 7.0e-6


def rate(
    ta_ms: float,
    ppl_percent: float,
    burst_ratio: float = 1.0,
    *,
    r0: float = R0_DEFAULT,
    i_s: float = IS_DEFAULT,
    a: float = A_DEFAULT,
    ie: float = IE_G711_PLC,
    bpl: float = BPL_G711_PLC,
) -> Rating:
    """
    The whole model: R = R0 - Is - Id - Ie_eff + A, then MOS.

    `ta_ms` is **absolute one-way delay**, mouth to ear — not RTT, and not network latency alone.
    `media_probe.py` builds it from the measured one-way network delay plus the jitter-buffer and
    codec terms, and says so at the line, because feeding a round-trip figure in here silently
    doubles the only term the Colombo-TURN decision turns on.
    """
    i_d = idd(ta_ms)
    ie_eff = ie_effective(ppl_percent, burst_ratio, ie=ie, bpl=bpl)
    r = r0 - i_s - i_d - ie_eff + a

    return Rating(
        r=r,
        mos=r_to_mos(r),
        r0=r0,
        i_s=i_s,
        i_d=i_d,
        ie_eff=ie_eff,
        a=a,
        ta_ms=ta_ms,
        ppl_percent=ppl_percent,
        burst_ratio=burst_ratio,
    )


def delay_budget_ms(
    mos_floor: float,
    ppl_percent: float = 0.0,
    burst_ratio: float = 1.0,
    *,
    a: float = A_DEFAULT,
) -> float:
    """
    The largest one-way delay that still rates at or above `mos_floor`, at a given loss.

    **This is the function the Colombo-TURN recommendation is built on.**  The question "should
    there be a TURN relay in Colombo" is not a matter of taste once the model is fixed: a relayed
    call between two Colombo handsets through a Singapore TURN carries the Colombo-Singapore leg
    twice in each direction, and this returns the point at which that stops fitting.  The
    in-region run measures the leg; this says what the leg is allowed to be.

    Bisection rather than an inversion of Idd, because Idd has no closed-form inverse and the
    monotonicity that makes bisection valid is a property of the standard's own curve.
    """
    if r_to_mos(rate(0.0, ppl_percent, burst_ratio, a=a).r) < mos_floor:
        return 0.0

    low, high = 0.0, 2000.0

    if rate(high, ppl_percent, burst_ratio, a=a).mos >= mos_floor:
        return high

    for _ in range(200):
        mid = (low + high) / 2.0
        if rate(mid, ppl_percent, burst_ratio, a=a).mos >= mos_floor:
            low = mid
        else:
            high = mid

    return low
