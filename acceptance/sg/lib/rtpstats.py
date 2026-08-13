"""
RFC 3550 stream statistics, and the mouth-to-ear delay the E-model is actually fed.

The arithmetic that makes this component's headline number meaningful is in
`relayed_call_delay_ms`, and it is worth stating plainly before any of the code.  Write L for the
one-way Colombo-Singapore network latency, so the ordinary round trip to Singapore is 2L.

    THE CALL.  Two handsets in Colombo, relayed through a TURN server in Singapore.  Audio from
    A to B goes Colombo -> Singapore -> Colombo.  The one-way, mouth-to-ear NETWORK delay of that
    call is therefore 2L — a full Singapore round trip's worth of geography, for a call between
    two people in the same city.  That doubling is the entire Colombo-TURN question.

    THE PROBE.  The probe sends RTP from A through the relay to B, and B echoes it back through
    the relay to A: A -> relay -> B -> relay -> A, which is 4L.  That path is EXACTLY TWO
    traversals of the call's one-way path, so

        one-way call network delay = probe RTT / 2

    The halving is structural rather than the usual symmetry assumption — the two halves are the
    same two hops walked in opposite directions, and each contains one relay traversal.  What it
    still cannot see is routing asymmetry, where the forward and return paths differ in length;
    that is genuinely unmeasurable from one clock and is stated as a limit in report.md rather
    than corrected for.

Getting this factor wrong in either direction moves the Colombo-TURN recommendation across its own
threshold: treating the probe RTT as the one-way delay would overstate every relayed call by 2x,
and treating it as a conventional network RTT would understate it by 2x.  It is written here
rather than inferred at the call site for that reason.
"""

from __future__ import annotations

import statistics
from dataclasses import dataclass, field

#: RFC 3550 §6.4.1's smoothing constant for the interarrival jitter estimate.
JITTER_GAIN = 16.0

#: Opus in LiveKit is 20 ms per packet (`enabled_codecs: [audio/opus]`, and 20 ms is WebRTC's
#: default ptime).  Both the packetisation delay and the RTP clock below follow from it.
PACKET_MS = 20.0

#: Opus's algorithmic delay at 20 ms frames: the frame itself plus ~5 ms of look-ahead.
CODEC_DELAY_MS = 25.0

#: The 48 kHz RTP clock Opus uses.  Only needed to express jitter in RFC 3550's units; every
#: figure this module reports is in milliseconds, because a jitter in clock ticks is a number
#: nobody can read against a delay budget.
RTP_CLOCK_HZ = 48000.0


@dataclass
class StreamStats:
    """What one probe stream observed, in the units the report prints."""

    sent: int = 0
    received: int = 0
    lost: int = 0
    duplicated: int = 0
    reordered: int = 0
    rtt_ms: list[float] = field(default_factory=list)
    jitter_ms: float = 0.0
    loss_bursts: list[int] = field(default_factory=list)

    # ---- derived -----------------------------------------------------------------------

    @property
    def loss_percent(self) -> float:
        return 100.0 * self.lost / self.sent if self.sent else 0.0

    @property
    def rtt_mean_ms(self) -> float:
        return statistics.fmean(self.rtt_ms) if self.rtt_ms else 0.0

    def rtt_percentile_ms(self, p: float) -> float:
        """
        Nearest-rank percentile.

        Deliberately not `statistics.quantiles`, which interpolates: an interpolated p99 over a
        few hundred samples invents a value between two real ones, and every latency figure in
        this repository's other suites is a rank over observed samples.
        """
        if not self.rtt_ms:
            return 0.0

        ordered = sorted(self.rtt_ms)
        rank = max(1, min(len(ordered), int(round(p / 100.0 * len(ordered) + 0.5))))

        return ordered[rank - 1]

    @property
    def burst_ratio(self) -> float:
        """
        G.107's BurstR: the mean observed loss-burst length over the mean expected under random
        loss.

        Under Bernoulli loss at probability p the expected burst length is 1/(1-p), so
        BurstR = mean_observed * (1 - p).  It is 1.0 for scattered loss and larger when losses
        clump — which matters because packet-loss concealment hides one missing 20 ms frame well
        and ten consecutive ones not at all.  `ie_effective` divides by it, so reporting a flat
        1.0 here would make bursty loss read as benign.
        """
        if not self.loss_bursts:
            return 1.0

        p = self.lost / self.sent if self.sent else 0.0
        expected = 1.0 / (1.0 - p) if p < 1.0 else 1.0

        return max(1.0, statistics.fmean(self.loss_bursts) / expected)


class StreamAccumulator:
    """
    Folds one probe stream's send and receive events into `StreamStats`.

    Sequence numbers are the probe's own 32-bit counters rather than RTP's 16-bit field, because
    a 16-bit sequence wraps every 65536 packets — 22 minutes at 50 pps — and a concurrency run
    long enough to matter would wrap mid-measurement.  The RTP header this rides in still carries
    a conformant 16-bit sequence (`turn.py` writes it); this is the counter the statistics use.
    """

    def __init__(self) -> None:
        self.stats = StreamStats()
        self._sent_at: dict[int, float] = {}
        self._seen: set[int] = set()
        self._highest_received = -1
        self._jitter_ticks = 0.0
        self._last_transit: float | None = None

    def on_send(self, seq: int, at_monotonic: float) -> None:
        self._sent_at[seq] = at_monotonic
        self.stats.sent += 1

    def on_receive(self, seq: int, at_monotonic: float) -> None:
        sent_at = self._sent_at.get(seq)

        if sent_at is None:
            # A packet we never sent, or one whose send record has already been reaped.  Counted
            # rather than dropped: on a relay it means the relay emitted something we did not put
            # in, which is a fact about the relay.
            self.stats.duplicated += 1
            return

        if seq in self._seen:
            self.stats.duplicated += 1
            return

        self._seen.add(seq)
        self.stats.received += 1

        rtt = (at_monotonic - sent_at) * 1000.0
        self.stats.rtt_ms.append(rtt)

        if seq < self._highest_received:
            self.stats.reordered += 1
        else:
            self._highest_received = seq

        # RFC 3550 §6.4.1.  D is the difference in transit time between consecutive packets;
        # because both timestamps come off one clock here, the transit is simply the RTT and the
        # clock-offset term the RFC carries cancels.
        transit = rtt
        if self._last_transit is not None:
            d = abs(transit - self._last_transit)
            self._jitter_ticks += (d - self._jitter_ticks) / JITTER_GAIN
        self._last_transit = transit

    def finish(self) -> StreamStats:
        """
        Closes the accumulation: everything sent and never seen is lost, and the loss pattern is
        reduced to the burst lengths `burst_ratio` needs.
        """
        self.stats.jitter_ms = self._jitter_ticks

        missing = sorted(seq for seq in self._sent_at if seq not in self._seen)
        self.stats.lost = len(missing)

        bursts: list[int] = []
        run = 0
        previous: int | None = None

        for seq in missing:
            if previous is not None and seq == previous + 1:
                run += 1
            else:
                if run:
                    bursts.append(run)
                run = 1
            previous = seq

        if run:
            bursts.append(run)

        self.stats.loss_bursts = bursts

        return self.stats


def jitter_buffer_ms(jitter_ms: float, *, floor_ms: float = 20.0, cap_ms: float = 200.0) -> float:
    """
    The playout buffer an adaptive jitter buffer would settle at, for a measured jitter.

    **This is a model, not a measurement**, and it is the one term in the delay budget that this
    harness cannot observe: the buffer lives inside the handset's WebRTC stack and is not on the
    wire.  `mean + 4 * jitter`, floored at one packet and capped at 200 ms, is the conventional
    target and is what WebRTC's NetEq converges to for a stationary jitter distribution.  It is
    reported as its own line in the delay budget so a reader can substitute their own figure
    rather than having it buried inside a MOS.
    """
    return min(cap_ms, max(floor_ms, PACKET_MS + 4.0 * jitter_ms))


def relayed_call_delay_ms(
    probe_rtt_ms: float,
    jitter_ms: float,
    *,
    codec_delay_ms: float = CODEC_DELAY_MS,
) -> tuple[float, dict[str, float]]:
    """
    Mouth-to-ear one-way delay for a relayed call, and the terms it is made of.

    `probe_rtt_ms` is the probe's echo round trip A -> relay -> B -> relay -> A.  That is two
    traversals of the call's one-way path, so the network term is **half** of it — see the module
    docstring, which is where that factor is argued.

    Returns the total and a breakdown, because a single Ta figure hides which term the Colombo
    decision would actually move: relocating the relay changes the network line and nothing else.
    """
    buffer_ms = jitter_buffer_ms(jitter_ms)

    terms = {
        "network_one_way_ms": probe_rtt_ms / 2.0,
        "jitter_buffer_ms": buffer_ms,
        "codec_and_packetisation_ms": codec_delay_ms,
    }

    return sum(terms.values()), terms
