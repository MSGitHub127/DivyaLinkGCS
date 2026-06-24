/**
 * rtk_throughput.js
 * Place in: wwwroot/js/rtk_throughput.js
 * Load AFTER rtk.js in _Layout.cshtml / index.html.
 *
 * Adds window.rtk.updateThroughputChart() and window.rtk.resetThroughputHistory().
 * Maintains a 60-second rolling history and draws a canvas line chart.
 *
 * Called from RtkDrawer.razor OnRtkStateChanged when _activeTab == 2 (RTCM Stream):
 *
 *   await JS.InvokeVoidAsync("rtk.updateThroughputChart",
 *       "rtk-throughput-canvas",
 *       (double)state.Stream.RxBps / 8.0,   // bps → B/s
 *       (double)state.Stream.TxBps / 8.0);
 *
 * Call window.rtk.resetThroughputHistory() on disconnect / restart to clear stale data.
 */
(function () {
    'use strict';

    // Guard: ensure window.rtk exists (rtk.js must be loaded first).
    if (!window.rtk) window.rtk = {};

    // ── Ring buffer ───────────────────────────────────────────────────────────
    const MAX_SLOTS = 60;   // 60 seconds of history (1 sample per state update)
    const MIN_Y_CEIL = 300;  // minimum Y-axis ceiling in B/s (keeps chart readable at low throughput)

    const _history = {
        rx: new Array(MAX_SLOTS).fill(0),   // bytes/sec from receiver → DivyaLink
        tx: new Array(MAX_SLOTS).fill(0),   // bytes/sec DivyaLink     → drone (injected RTCM)
    };

    /** Push one sample and evict the oldest. */
    function pushSample(rxBps, txBps) {
        _history.rx.push(Math.max(0, rxBps));
        _history.tx.push(Math.max(0, txBps));
        if (_history.rx.length > MAX_SLOTS) { _history.rx.shift(); _history.tx.shift(); }
    }

    /** Compute the Y-axis ceiling from the current window. */
    function yCeil() {
        return Math.max(MIN_Y_CEIL, ...(_history.rx), ...(_history.tx)) * 1.1;
    }

    // ── Canvas drawing ────────────────────────────────────────────────────────
    const PAD = { t: 10, r: 10, b: 22, l: 46 };

    const COLORS = {
        rx: '#58a6ff',
        rxFill: 'rgba(88,166,255,0.10)',
        tx: '#3fb950',
        txFill: 'rgba(63,185,80,0.10)',
        grid: 'rgba(255,255,255,0.05)',
        label: 'rgba(255,255,255,0.30)',
        bg: 'transparent',
    };

    /** Format bytes/s for Y-axis labels — keeps them short. */
    function fmtBps(v) {
        if (v >= 1000) return (v / 1000).toFixed(1) + ' kB/s';
        return Math.round(v) + ' B/s';
    }

    function drawChart(canvas, ceil) {
        const dpr = window.devicePixelRatio || 1;
        const cssW = canvas.offsetWidth || 640;
        const cssH = canvas.offsetHeight || 90;

        // Resize backing store only when needed (avoids layout thrash).
        const wPx = Math.round(cssW * dpr);
        const hPx = Math.round(cssH * dpr);
        if (canvas.width !== wPx || canvas.height !== hPx) {
            canvas.width = wPx;
            canvas.height = hPx;
        }

        const ctx = canvas.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, cssW, cssH);

        const cW = cssW - PAD.l - PAD.r;
        const cH = cssH - PAD.t - PAD.b;

        // ── Grid lines + Y labels ──────────────────────────────────────────
        ctx.font = `9px 'JetBrains Mono', 'Courier New', monospace`;
        ctx.textAlign = 'right';
        ctx.textBaseline = 'middle';

        [0, 0.25, 0.50, 0.75, 1.00].forEach(frac => {
            const y = PAD.t + cH * (1 - frac);
            ctx.strokeStyle = COLORS.grid;
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(PAD.l, y);
            ctx.lineTo(cssW - PAD.r, y);
            ctx.stroke();
            ctx.fillStyle = COLORS.label;
            ctx.fillText(fmtBps(ceil * frac), PAD.l - 4, y);
        });

        // ── Draw one series ────────────────────────────────────────────────
        function drawSeries(data, stroke, fill) {
            const n = data.length;
            if (n < 2) return;

            ctx.beginPath();
            ctx.strokeStyle = stroke;
            ctx.lineWidth = 1.5;
            ctx.lineJoin = 'round';
            ctx.lineCap = 'round';

            data.forEach((v, i) => {
                const x = PAD.l + (i / (n - 1)) * cW;
                const y = PAD.t + cH * (1 - Math.min(v, ceil) / ceil);
                i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
            });
            ctx.stroke();

            // Filled area under the line.
            ctx.lineTo(PAD.l + cW, PAD.t + cH);
            ctx.lineTo(PAD.l, PAD.t + cH);
            ctx.closePath();
            ctx.fillStyle = fill;
            ctx.fill();
        }

        drawSeries(_history.rx, COLORS.rx, COLORS.rxFill);
        drawSeries(_history.tx, COLORS.tx, COLORS.txFill);

        // ── X-axis time labels ("60s ago" … "now") ─────────────────────────
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillStyle = COLORS.label;
        ctx.font = `9px 'JetBrains Mono', 'Courier New', monospace`;
        ctx.fillText('60s', PAD.l, cssH - PAD.b + 4);
        ctx.textAlign = 'right';
        ctx.fillText('now', cssW - PAD.r, cssH - PAD.b + 4);

        // ── Legend (bottom-centre) ─────────────────────────────────────────
        const latestRx = _history.rx[_history.rx.length - 1];
        const latestTx = _history.tx[_history.tx.length - 1];
        const midX = PAD.l + cW / 2;

        ctx.textAlign = 'center';
        ctx.textBaseline = 'top';

        ctx.fillStyle = COLORS.rx;
        ctx.fillText(`\u25ac RX  ${fmtBps(latestRx)}`, midX - 42, cssH - PAD.b + 4);
        ctx.fillStyle = COLORS.tx;
        ctx.fillText(`\u25ac TX  ${fmtBps(latestTx)}`, midX + 42, cssH - PAD.b + 4);

        // ── Mission Planner reference band (150–250 B/s) ──────────────────
        // Shaded region shows the expected RTCM throughput range so operators
        // can see at a glance whether the link is healthy.
        const yHi = PAD.t + cH * (1 - Math.min(250, ceil) / ceil);
        const yLo = PAD.t + cH * (1 - Math.min(150, ceil) / ceil);
        if (yHi < yLo) {  // only draw if target range fits in the visible Y window
            ctx.fillStyle = 'rgba(63,185,80,0.06)';
            ctx.fillRect(PAD.l, yHi, cW, yLo - yHi);
            ctx.strokeStyle = 'rgba(63,185,80,0.20)';
            ctx.lineWidth = 0.75;
            ctx.setLineDash([3, 3]);
            ctx.beginPath(); ctx.moveTo(PAD.l, yLo); ctx.lineTo(PAD.l + cW, yLo); ctx.stroke(); // 150
            ctx.beginPath(); ctx.moveTo(PAD.l, yHi); ctx.lineTo(PAD.l + cW, yHi); ctx.stroke(); // 250
            ctx.setLineDash([]);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Push one B/s sample and redraw the chart.
     *
     * @param {string} canvasId       id of the <canvas> element in the RTCM tab
     * @param {number} rxBytesPerSec  bytes/s received from the M8P receiver
     * @param {number} txBytesPerSec  bytes/s injected to the drone via MAVLink
     */
    window.rtk.updateThroughputChart = function (canvasId, rxBytesPerSec, txBytesPerSec) {
        pushSample(rxBytesPerSec, txBytesPerSec);
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;
        drawChart(canvas, yCeil());
    };

    /**
     * Clear the rolling history.
     * Call on disconnect or survey restart so stale samples don't pollute
     * the new session's chart.
     */
    window.rtk.resetThroughputHistory = function () {
        _history.rx.fill(0);
        _history.tx.fill(0);
    };

})();