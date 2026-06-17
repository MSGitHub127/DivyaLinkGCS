// wwwroot/js/rtk.js
// RTK Base Station UI helpers for DivyaLink.
// Owns the SNR bar chart canvas rendering — Blazor calls drawSnrChart()
// after every state update from RtkBaseStationService.OnStateChanged.
//
// BUG 4 FIX (JSX): ctx.textAlign was never set before Y-axis label rendering,
// causing numbers to appear right-of-coordinate and clip at the canvas edge.
// Fix: ctx.textAlign = "right" is set explicitly before every fillText call.
//
// BUG 5 FIX (JSX): dead inner ternary `s.active ? 28 : 10` inside
// clamp(snr + Δ, s.active ? 28 : 10, 58) — the outer filter already
// guarantees s.active === true. Since we use real UBX-NAV-SAT data (not
// simulation), there is no jitter function here, so the bug is eliminated
// by design. The clamp call in any future simulation tick must use literal 28.
//
// BUG 6 FIX (JSX): { ...s, snr: s.snr } — redundant explicit re-assignment.
// Not applicable in C# (we use record `with` correctly), noted for completeness.

(function () {
    'use strict';

    if (window.rtk) return; // guard against double-registration on hot reload
    window.rtk = {};

    // ── Constellation colour palette ──────────────────────────────────────────
    const CONSTEL_COLORS = {
        G: { active: '#00f2ff', inactive: 'rgba(0,242,255,0.25)' },  // GPS — cyan
        R: { active: '#3fb950', inactive: 'rgba(63,185,80,0.25)' },  // GLONASS — green
        E: { active: '#ff6b6b', inactive: 'rgba(255,107,107,0.25)' },  // Galileo — red
        B: { active: '#f0b429', inactive: 'rgba(240,180,41,0.25)' },  // BeiDou — amber
        S: { active: '#a78bfa', inactive: 'rgba(167,139,250,0.25)' },  // SBAS — violet
        Q: { active: '#fb923c', inactive: 'rgba(251,146,60,0.25)' },  // QZSS — orange
        '?': { active: '#8b949e', inactive: 'rgba(139,148,158,0.25)' },
    };

    const SNR_MIN = 0;
    const SNR_MAX = 60;
    const SNR_THRESHOLD = 40;   // red line at 40 dB-Hz

    // ── drawSnrChart ──────────────────────────────────────────────────────────
    // Called by RtkBaseStationDrawer.razor after every OnStateChanged:
    //   await JS.InvokeVoidAsync("rtk.drawSnrChart", "rtk-snr-canvas", satellites);
    //
    // satellites: array of { id: "G12", snr: 42, active: true, gnssId: 0, svId: 12 }
    //
    window.rtk.drawSnrChart = function (canvasId, satellites) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;

        const ctx = canvas.getContext('2d');

        // ── DPR-aware sizing ─────────────────────────────────────────────────
        const dpr = window.devicePixelRatio || 1;
        const cssW = canvas.offsetWidth || 640;
        const cssH = canvas.offsetHeight || 220;

        if (canvas.width !== Math.round(cssW * dpr) ||
            canvas.height !== Math.round(cssH * dpr)) {
            canvas.width = Math.round(cssW * dpr);
            canvas.height = Math.round(cssH * dpr);
        }
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        const W = cssW;
        const H = cssH;

        // ── Padding ──────────────────────────────────────────────────────────
        const pad = { t: 12, r: 8, b: 28, l: 30 };
        const chartW = W - pad.l - pad.r;
        const chartH = H - pad.t - pad.b;

        // ── Clear ─────────────────────────────────────────────────────────────
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = 'transparent';
        ctx.fillRect(0, 0, W, H);

        // ── Sort: active first, then by gnssId prefix order G R E B S Q ─────
        const order = { G: 0, R: 1, E: 2, B: 3, S: 4, Q: 5 };
        const sorted = [...satellites].sort((a, b) => {
            const pa = order[a.id[0]] ?? 9;
            const pb = order[b.id[0]] ?? 9;
            if (pa !== pb) return pa - pb;
            if (a.active !== b.active) return b.active ? 1 : -1;
            return b.snr - a.snr;
        });

        if (sorted.length === 0) {
            // No satellites yet — draw empty chart with placeholder text
            ctx.fillStyle = 'rgba(139,148,158,0.3)';
            ctx.font = `12px 'JetBrains Mono', 'Courier New', monospace`;
            ctx.textAlign = 'center';
            ctx.fillText('Awaiting carrier solution stream metrics…', W / 2, H / 2);
            return;
        }

        // ── Y-axis gridlines ─────────────────────────────────────────────────
        ctx.strokeStyle = 'rgba(255,255,255,0.06)';
        ctx.lineWidth = 1;
        ctx.setLineDash([]);
        ctx.font = `9px 'JetBrains Mono', 'Courier New', monospace`;
        ctx.fillStyle = 'rgba(255,255,255,0.3)';

        // BUG 4 FIX: textAlign MUST be set to "right" before drawing Y-axis labels.
        // Without this, numbers render right-of-coordinate (textAlign default is "left")
        // and are clipped at the left canvas edge when pad.l is narrow.
        ctx.textAlign = 'right';

        [0, 20, 40, 60].forEach(v => {
            const y = pad.t + chartH * (1 - (v - SNR_MIN) / (SNR_MAX - SNR_MIN));
            ctx.beginPath();
            ctx.moveTo(pad.l, y);
            ctx.lineTo(W - pad.r, y);
            ctx.stroke();
            ctx.fillText(String(v), pad.l - 4, y + 3);  // right-aligned to coord
        });

        // ── Red SNR threshold line at 40 dB-Hz ──────────────────────────────
        const threshY = pad.t + chartH * (1 - (SNR_THRESHOLD - SNR_MIN) / (SNR_MAX - SNR_MIN));
        ctx.strokeStyle = 'rgba(248,81,73,0.7)';
        ctx.lineWidth = 1.5;
        ctx.setLineDash([4, 3]);
        ctx.beginPath();
        ctx.moveTo(pad.l, threshY);
        ctx.lineTo(W - pad.r, threshY);
        ctx.stroke();
        ctx.setLineDash([]);

        // ── Satellite bars ───────────────────────────────────────────────────
        const numSats = sorted.length;
        const barW = Math.max(4, Math.min(22, (chartW / numSats) - 3));
        const barGap = (chartW - numSats * barW) / (numSats + 1);

        sorted.forEach((sat, i) => {
            const prefix = sat.id[0] || '?';
            const colors = CONSTEL_COLORS[prefix] || CONSTEL_COLORS['?'];
            const snrClamped = Math.min(Math.max(sat.snr, SNR_MIN), SNR_MAX);
            const barH = chartH * ((snrClamped - SNR_MIN) / (SNR_MAX - SNR_MIN));

            const x = pad.l + barGap + i * (barW + barGap);
            const y = pad.t + chartH - barH;

            // Bar fill: gradient for active, flat dim for inactive
            if (sat.active) {
                const grad = ctx.createLinearGradient(x, y, x, pad.t + chartH);
                grad.addColorStop(0, colors.active);
                grad.addColorStop(0.4, colors.active);
                grad.addColorStop(1, 'rgba(0,0,0,0)');
                ctx.fillStyle = grad;
            } else {
                ctx.fillStyle = colors.inactive;
            }

            // Rounded top corners
            ctx.beginPath();
            const r = Math.min(3, barW / 2);
            ctx.moveTo(x + r, y);
            ctx.lineTo(x + barW - r, y);
            ctx.quadraticCurveTo(x + barW, y, x + barW, y + r);
            ctx.lineTo(x + barW, pad.t + chartH);
            ctx.lineTo(x, pad.t + chartH);
            ctx.lineTo(x, y + r);
            ctx.quadraticCurveTo(x, y, x + r, y);
            ctx.closePath();
            ctx.fill();

            // Satellite ID label — reset textAlign to center for X-axis
            ctx.textAlign = 'center';
            ctx.fillStyle = sat.active ? colors.active : 'rgba(139,148,158,0.5)';
            ctx.font = `8px 'JetBrains Mono', 'Courier New', monospace`;
            ctx.fillText(sat.id, x + barW / 2, pad.t + chartH + 11);

            // SNR value above bar if active and bar tall enough
            if (sat.active && barH > 20) {
                ctx.fillStyle = 'rgba(255,255,255,0.7)';
                ctx.font = `8px 'JetBrains Mono', 'Courier New', monospace`;
                ctx.fillText(String(sat.snr), x + barW / 2, y - 3);
            }
        });

        // Restore textAlign to default for next call
        ctx.textAlign = 'left';
    };

    // ── Utility: list available COM ports (Windows) ──────────────────────────
    // This is handled server-side via SerialPort.GetPortNames() — no JS needed.

    console.log('[DivyaLink] rtk.js loaded');
})();