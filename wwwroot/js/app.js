// wwwroot/js/app.js
// DivyaLink GCS — complete JavaScript bridge
//
// ARCHITECTURE
// ──────────────────────────────────────────────────────────────────────
//  Map Registry    — _mapRegistry (Map) keyed by containerId.
//                    Home.razor   → initMap(lat, lng, 'map-element')
//                    FlightPlan   → initMap(lat, lng, 'fp-map')
//                    window._gcsMap always points to the last-active map
//                    so that flightplan.js (window.divya.*) can hook in.
//
//  Tile layers     — Google Sat/Hybrid, Bing Sat, Esri Clarity/Hybrid/Topo, OSM
//  Mission engine  — drawMission(), _missionMarkers, drag-to-update JSInterop
//  Video           — initHLSStreamWithFallback(), initSkydroidStream(), initWebRTCStream()
//  HUD spring      — 60fps animateHud() spring interpolation
//  Trail + ring    — _appendTrail(), _updateAccuracyRing()
//
// LOADING ORDER (App.razor):
//   1. leaflet.css  (in <head>)
//   2. leaflet.js
//   3. hls.js
//   4. app.js          ← this file
//   5. flightplan.js   ← depends on window._gcsMap
//
// ── ASSEMBLY NAME ────────────────────────────────────────────────────
//   Every DotNet.invokeMethodAsync call uses 'BlazorApp3'.
//   If your project DLL is named differently, do a project-wide search
//   for 'BlazorApp3' in this file and replace it with your actual name.
// ────────────────────────────────────────────────────────────────────

'use strict';

// ═══════════════════════════════════════════════════════════════════════
// GLOBAL STATE
// ═══════════════════════════════════════════════════════════════════════

// Per-container map registry — never destroy a map on Blazor navigation;
// just call invalidateSize() if the same container is re-used.
const _mapRegistry = new Map(); // containerId → Leaflet map instance

// Convenience global — flightplan.js reads this to hook into the active map
window._gcsMap = null;
window._gcsMarker = null;
window._missionMarkers = [];
window._missionPath = null;
window._trailPoints = [];
window._trailLine = null;
window._trailEnabled = true;
window._accuracyCircle = null;
window._MAX_TRAIL_PTS = 400;
window._pc = null; // WebRTC peer connection

// ═══════════════════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════════════════

function _validGps(lat, lng) {
    return typeof lat === 'number' && typeof lng === 'number'
        && !Number.isNaN(lat) && !Number.isNaN(lng)
        && (Math.abs(lat) > 0.000001 || Math.abs(lng) > 0.000001);
}

function _updateCoordLog(lat, lng) {
    const el = document.getElementById('map-coord-log');
    if (el) el.innerHTML = `LAT <b style="color:#fff">${lat.toFixed(7)}</b><br>LNG <b style="color:#fff">${lng.toFixed(7)}</b>`;
}

function _droneIcon(yawDeg) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="36" height="36" viewBox="0 0 36 36">
        <g transform="rotate(${yawDeg},18,18)">
            <polygon points="18,4 28,30 18,24 8,30"
                     fill="#00f2ff" stroke="#0a0b0d" stroke-width="1.5"
                     stroke-linejoin="round"/>
        </g></svg>`;
    return L.divIcon({ html: svg, className: '', iconSize: [36, 36], iconAnchor: [18, 18] });
}

function _createBingLayer(imagerySet, opts) {
    const layer = L.tileLayer('', opts);
    layer.getTileUrl = function (coords) {
        let quad = '';
        for (let i = coords.z; i > 0; i--) {
            let digit = 0;
            const mask = 1 << (i - 1);
            if ((coords.x & mask) !== 0) digit++;
            if ((coords.y & mask) !== 0) digit += 2;
            quad += digit;
        }
        return `https://ecn.t${Math.floor(Math.random() * 4)}.tiles.virtualearth.net/tiles/${imagerySet}${quad}.jpeg?g=1`;
    };
    return layer;
}

function _droneTooltip(lat, lng, yaw, alt, spd, bat) {
    return `<div style="font-family:monospace;font-size:0.65rem;line-height:1.6;color:#c9d1d9;min-width:130px;">
        <div style="font-weight:900;color:#00f2ff;letter-spacing:1px;margin-bottom:3px;">DRONE</div>
        <div>HDG <b style="color:#fff">${yaw.toFixed(0)}°</b></div>
        <div>ALT <b style="color:#fff">${alt.toFixed(1)} m</b></div>
        <div>SPD <b style="color:#fff">${spd.toFixed(1)} m/s</b></div>
        <div>BAT <b style="color:${bat < 20 ? '#ff6b6b' : '#3fb950'}">${bat}%</b></div>
        <div style="color:#555;font-size:0.55rem;margin-top:2px;">${lat.toFixed(7)}, ${lng.toFixed(7)}</div>
    </div>`;
}

function _updateAccuracyRing(lat, lng, hdop) {
    if (!window._gcsMap) return;
    const radius = Math.max(1, (hdop || 1) * 3);
    if (!window._accuracyCircle) {
        window._accuracyCircle = L.circle([lat, lng], {
            radius, color: '#00f2ff', fillColor: '#00f2ff',
            fillOpacity: 0.06, weight: 1, opacity: 0.35, interactive: false
        }).addTo(window._gcsMap);
    } else {
        window._accuracyCircle.setLatLng([lat, lng]);
        window._accuracyCircle.setRadius(radius);
    }
}

// Minimum movement (~1m at equator) before appending a trail point —
// prevents redundant polyline SVG rebuilds when the drone is stationary.
const _TRAIL_MIN_DIST_DEG = 0.00001;

function _appendTrail(lat, lng) {
    if (!window._trailEnabled || !window._gcsMap) return;
    const pts = window._trailPoints;
    if (pts.length > 0) {
        const last = pts[pts.length - 1];
        const dLat = lat - last[0], dLng = lng - last[1];
        if (dLat * dLat + dLng * dLng < _TRAIL_MIN_DIST_DEG * _TRAIL_MIN_DIST_DEG) return;
    }
    pts.push([lat, lng]);
    if (pts.length > window._MAX_TRAIL_PTS) pts.shift();
    if (!window._trailLine) {
        window._trailLine = L.polyline(pts, {
            color: '#f0b429', weight: 2, opacity: 0.7, smoothFactor: 1, interactive: false
        }).addTo(window._gcsMap);
    } else {
        window._trailLine.setLatLngs(pts);
    }
}

function _ensureDroneMarker(lat, lng, yaw, extra) {
    if (!window._gcsMap) return;
    const alt = extra?.alt ?? 0, spd = extra?.spd ?? 0,
        bat = extra?.bat ?? 0, hdop = extra?.hdop ?? 1;

    if (!window._gcsMarker) {
        window._gcsMarker = L.marker([lat, lng], {
            icon: _droneIcon(yaw), draggable: true, zIndexOffset: 1000
        }).addTo(window._gcsMap);

        window._gcsMarker.bindTooltip(
            _droneTooltip(lat, lng, yaw, alt, spd, bat),
            { permanent: false, direction: 'right', className: 'drone-tooltip', offset: [20, 0] }
        );
        window._gcsMarker.on('drag', (e) => { const p = e.target.getLatLng(); _updateCoordLog(p.lat, p.lng); });
        window._gcsMarker.on('dragend', (e) => { const p = e.target.getLatLng(); _updateCoordLog(p.lat, p.lng); });
    } else {
        window._gcsMarker.setLatLng([lat, lng]);
        window._gcsMarker.setIcon(_droneIcon(yaw));
        if (window._gcsMarker.getTooltip()) {
            window._gcsMarker.getTooltip().setContent(_droneTooltip(lat, lng, yaw, alt, spd, bat));
        }
        _updateCoordLog(lat, lng);
    }
    _appendTrail(lat, lng);
    _updateAccuracyRing(lat, lng, hdop);
}

// ═══════════════════════════════════════════════════════════════════════
// MAP INITIALISATION — Map Registry
// ═══════════════════════════════════════════════════════════════════════
//
// Blazor call:
//   Home.razor      → await JS.InvokeVoidAsync("initMap", lat, lng)
//   FlightPlan.razor → await JS.InvokeVoidAsync("initMap", lat, lng, "fp-map")
//
// The containerId parameter defaults to "map-element" for full backward
// compatibility with all existing Home.razor call sites.

window.initMap = function (lat, lng, containerId = 'map-element') {
    if (typeof L === 'undefined') {
        // Leaflet CDN not loaded yet — retry in 100ms
        setTimeout(() => window.initMap(lat, lng, containerId), 100);
        return;
    }

    const el = document.getElementById(containerId);
    if (!el) {
        console.error(`[initMap] #${containerId} not found in DOM`);
        return;
    }

    if (_mapRegistry.has(containerId)) {
        const existing = _mapRegistry.get(containerId);
        if (document.body.contains(existing.getContainer())) {
            window._gcsMap = existing;
            existing.invalidateSize();
            return;
        } else {
            try {
                existing.remove();
            } catch (e) {
                console.warn('Error removing old map:', e);
            }
            _mapRegistry.delete(containerId);
            if (window._gcsMap === existing) {
                window._gcsMap = null;
                window._gcsMarker = null;
                window._accuracyCircle = null;
                window._trailLine = null;
                window._trailPoints = [];
                window._missionMarkers = [];
                window._missionPath = null;
            }
        }
    }

    const isGpsValid = _validGps(lat, lng);
    const startLatLng = isGpsValid ? [lat, lng] : [20.5937, 78.9629]; // India fallback
    const startZoom = isGpsValid ? 19 : 5;

    const map = L.map(el, {
        center: startLatLng,
        zoom: startZoom,
        minZoom: 3,
        maxZoom: 22,
        zoomControl: true,
        attributionControl: true,
        tap: false,
        worldCopyJump: true
    });

    // Constrain panning to valid lat bounds; allow unlimited longitude panning
    map.setMaxBounds(L.latLngBounds(L.latLng(-85.05, -999999), L.latLng(85.05, 999999)));

    // Auto-invalidate when the container element is resized (CSS transitions, PiP swap)
    if (window.ResizeObserver) {
        new ResizeObserver(() => { if (map) map.invalidateSize(); }).observe(el);
    }

    // ── Tile layers ──────────────────────────────────────────────────────────

    const BLANK = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

    // tileOpts: shared defaults applied to every layer.
    // noWrap: true — prevents the world from repeating when zoomed out.
    const tileOpts = (maxNative, extra) => Object.assign({
        maxNativeZoom: maxNative,
        maxZoom: 22,
        minZoom: 3,
        keepBuffer: 6,
        updateWhenIdle: false,
        updateWhenZooming: false,
        errorTileUrl: BLANK,
        crossOrigin: true,
        noWrap: true
    }, extra || {});

    const googleSat = L.tileLayer('https://mt{s}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}',
        tileOpts(20, { subdomains: '0123', attribution: 'Map data &copy; Google' }));

    const googleHybrid = L.tileLayer('https://mt{s}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}',
        tileOpts(20, { subdomains: '0123', attribution: 'Map data &copy; Google' }));

    const bingSat = _createBingLayer('a',
        tileOpts(19, { attribution: 'Map data &copy; Microsoft Bing' }));

    const esriClarity = L.tileLayer(
        'https://clarity.maptiles.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
        tileOpts(19, { attribution: 'Tiles &copy; Esri' }));

    const esriHybrid = L.layerGroup([
        L.tileLayer(
            'https://clarity.maptiles.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
            tileOpts(19, { attribution: 'Tiles &copy; Esri' })),
        L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
            tileOpts(19, { opacity: 0.85 }))
    ]);

    const osm = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
        tileOpts(19, { subdomains: 'abc', attribution: '&copy; <a href="https://openstreetmap.org">OpenStreetMap</a>' }));

    const esriTopo = L.tileLayer(
        'https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',
        tileOpts(19, { attribution: 'Tiles &copy; Esri' }));

    // Default layer
    googleSat.addTo(map);

    // Layer switcher — topleft so it doesn't overlap the header
    L.control.layers({
        '🛰️ Google Satellite': googleSat,
        '🛰️ Google Hybrid': googleHybrid,
        '🛰️ Bing Satellite': bingSat,
        '🛰️ Esri Clarity': esriClarity,
        '🛰️ Esri Hybrid': esriHybrid,
        '🗺️ Street (OSM)': osm,
        '🏔️ Terrain (Esri)': esriTopo
    }, {}, { position: 'topleft', collapsed: true }).addTo(map);

    // Scale bar
    L.control.scale({ position: 'bottomright', imperial: false }).addTo(map);

    // ── Map click → C# AddWaypointFromMap ───────────────────────────────────
    // ASSEMBLY NAME: change 'BlazorApp3' if your project DLL differs.
    try {
        map.on('click', function (e) {
            if (typeof DotNet !== 'undefined') {
                DotNet.invokeMethodAsync('BlazorApp3', 'AddWaypointFromMap', e.latlng.lat, e.latlng.lng)
                    .catch(err => console.error('[initMap] AddWaypointFromMap failed', err));
            }
        });
    } catch (ex) {
        console.warn('[initMap] Failed to attach map click handler', ex);
    }

    // ── Register ─────────────────────────────────────────────────────────────
    _mapRegistry.set(containerId, map);

    // flightplan.js reads window._gcsMap — always point to the most-recently-initialised map
    window._gcsMap = map;

    console.log(`[initMap] Ready on #${containerId} @ [${startLatLng}] z${startZoom}`);
};

window.preserveMap = () => { /* Leaflet: no-op */ };

// ═══════════════════════════════════════════════════════════════════════
// DRONE MARKER & TELEMETRY OVERLAY
// ═══════════════════════════════════════════════════════════════════════

window.jumpToDrone = function (lat, lng, yawDeg, extra) {
    if (!window._gcsMap || !_validGps(lat, lng)) return;
    window._gcsMap.setView([lat, lng], Math.max(window._gcsMap.getZoom(), 19));
    _ensureDroneMarker(lat, lng, yawDeg ?? 0, extra);
};

window.recentreToDrone = function () {
    if (!window._gcsMap || !window._gcsMarker) return;
    window._gcsMap.panTo(window._gcsMarker.getLatLng());
};

window.updateMapPos = function (lat, lng, yawDeg, extra) {
    if (!window._gcsMap || !_validGps(lat, lng)) return;
    _ensureDroneMarker(lat, lng, yawDeg ?? 0, extra);
};

window.setTrailEnabled = function (enabled) {
    window._trailEnabled = enabled;
    if (!enabled && window._trailLine) {
        window._gcsMap?.removeLayer(window._trailLine);
        window._trailLine = null;
        window._trailPoints = [];
    }
};

window.clearTrail = function () {
    if (window._trailLine) {
        window._gcsMap?.removeLayer(window._trailLine);
        window._trailLine = null;
    }
    window._trailPoints = [];
};

// ═══════════════════════════════════════════════════════════════════════
// MISSION WAYPOINT ENGINE
// ═══════════════════════════════════════════════════════════════════════
// DO NOT MODIFY — drag-to-update JSInterop contract with FlightPlan.razor.
// ASSEMBLY NAME: change 'BlazorApp3' below if your project DLL differs.

window.drawMission = function (waypoints) {
    if (!window._gcsMap) return;

    window._missionMarkers.forEach(m => window._gcsMap.removeLayer(m));
    window._missionMarkers = [];
    if (window._missionPath) {
        window._gcsMap.removeLayer(window._missionPath);
        window._missionPath = null;
    }
    if (!waypoints || !waypoints.length) return;

    const coords = [];
    waypoints.forEach(wp => {
        const lat = wp.lat ?? wp.Lat;
        const lng = wp.lng ?? wp.Lng;
        const idx = wp.index ?? wp.Index;
        const home = wp.IsHome || wp.isHome || idx === 0;
        if (lat === undefined || lng === undefined) return;

        coords.push([lat, lng]);

        const sz = home ? 22 : 18, bg = home ? '#ff9900' : '#00f2ff';
        const icon = L.divIcon({
            html: `<div style="width:${sz}px;height:${sz}px;background:${bg};border:2px solid #fff;
                         border-radius:50%;display:flex;align-items:center;justify-content:center;
                         font-size:9px;font-weight:bold;color:#000;">${home ? 'H' : idx}</div>`,
            className: '', iconSize: [sz, sz], iconAnchor: [sz / 2, sz / 2]
        });

        const m = L.marker([lat, lng], { icon, draggable: true }).addTo(window._gcsMap);
        m.bindTooltip(`WP${idx}: ${lat.toFixed(6)}, ${lng.toFixed(6)}`, { direction: 'top' });
        m.on('dragend', e => {
            const p = e.target.getLatLng();
            if (typeof DotNet !== 'undefined') {
                // ASSEMBLY NAME: change 'DivyaLink' if your project DLL differs.
                DotNet.invokeMethodAsync('BlazorApp3', 'UpdateWaypointPosition', idx, p.lat, p.lng)
                    .catch(console.error);
            }
        });
        window._missionMarkers.push(m);
    });

    if (coords.length > 1) {
        window._missionPath = L.polyline(coords, {
            color: '#2b58ff', weight: 3, opacity: 0.85
        }).addTo(window._gcsMap);
    }
};

// ═══════════════════════════════════════════════════════════════════════
// MAP RESIZE & FULLSCREEN TOGGLE
// ═══════════════════════════════════════════════════════════════════════

const _animateMapResize = () => {
    // 35 ticks × 10ms = 350ms of continuous invalidateSize calls.
    // This matches CSS transition durations so tiles fill in smoothly.
    let ticks = 0;
    const timer = setInterval(() => {
        _mapRegistry.forEach(map => map.invalidateSize(false));
        ticks++;
        if (ticks > 35) clearInterval(timer);
    }, 10);
};

window.expandMap = function () {
    document.querySelector('.gcs-wrapper')?.classList.add('map-fullscreen');
    document.querySelector('.gcs-drawer')?.classList.add('map-fullscreen-drawer');
    _animateMapResize();
};

window.collapseMap = function () {
    document.querySelector('.gcs-wrapper')?.classList.remove('map-fullscreen');
    document.querySelector('.gcs-drawer')?.classList.remove('map-fullscreen-drawer');
    _animateMapResize();
};

// ═══════════════════════════════════════════════════════════════════════
// UTILITIES
// ═══════════════════════════════════════════════════════════════════════

window.scrollToBottom = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
};

window.scrollToBottomInstant = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.scrollTop = el.scrollHeight;
};

window.downloadFile = function (fileName, content) {
    const blob = new Blob([content], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = fileName; a.click();
    URL.revokeObjectURL(url);
};

window.triggerFilePicker = function (id) {
    const el = document.getElementById(id);
    if (el) el.click();
};

window.setToastUplift = function (heightPx) {
    document.documentElement.style.setProperty('--toast-pip-uplift', heightPx + 'px');
};

// ═══════════════════════════════════════════════════════════════════════
// SKYDROID WEBRTC STREAM
// ═══════════════════════════════════════════════════════════════════════

window.initSkydroidStream = async function () {
    const videoElement = document.getElementById('skydroid-video');
    if (!videoElement) { console.error('[Skydroid] Video element not found'); return; }

    const pc = new RTCPeerConnection();
    pc.ontrack = (event) => { videoElement.srcObject = event.streams[0]; };
    pc.addTransceiver('video', { direction: 'recvonly' });

    try {
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        const response = await fetch(webrtcUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/sdp' },
            body: offer.sdp
        });

        if (!response.ok) throw new Error('MediaMTX server not responding');

        const answer = await response.json();
        await pc.setRemoteDescription(new RTCSessionDescription(answer));
        console.log('[Skydroid] WebRTC stream established.');
    } catch (err) {
        console.error('[Skydroid] Failed to connect. Is MediaMTX running?', err);
    }
};

// ═══════════════════════════════════════════════════════════════════════
// HLS VIDEO STREAMING WITH AUTOMATIC FALLBACK
// ═══════════════════════════════════════════════════════════════════════

window.initHLSStreamWithFallback = async function (urls) {
    console.log('[HLS] Initializing with fallback strategy');
    console.log('[HLS] Will try', urls.length, 'URLs');

    const video = document.getElementById('hls-video');
    if (!video) { console.error('[HLS] Video element not found'); return; }

    for (let i = 0; i < urls.length; i++) {
        const url = urls[i];
        console.log(`[HLS] Trying URL ${i + 1}/${urls.length}: ${url}`);

        const success = await _tryHLSUrl(url, video);
        if (success) {
            console.log(`[HLS] ✓ Success with URL: ${url}`);
            if (typeof DotNet !== 'undefined') {
                // ASSEMBLY NAME: change 'BlazorApp3' if your project DLL differs.
                DotNet.invokeMethodAsync('BlazorApp3', 'OnVideoStatusChanged', 'Connected')
                    .catch(e => console.log('[HLS] Blazor callback unavailable', e));
            }
            return;
        }

        console.log('[HLS] ✗ Failed, trying next URL...');
        await new Promise(resolve => setTimeout(resolve, 1000));
    }

    console.error('[HLS] ✗ All URLs failed');
    if (typeof DotNet !== 'undefined') {
        // ASSEMBLY NAME: change 'BlazorApp3' if your project DLL differs.
        DotNet.invokeMethodAsync('BlazorApp3', 'OnVideoStatusChanged', 'Error')
            .catch(() => { });
    }

    console.log('[HLS] Retrying entire sequence in 10 seconds...');
    setTimeout(() => window.initHLSStreamWithFallback(urls), 10000);
};

async function _tryHLSUrl(hlsUrl, video) {
    return new Promise((resolve) => {
        console.log('[HLS] Attempting URL:', hlsUrl);

        let resolved = false;
        const cleanup = () => { if (!resolved) { resolved = true; clearTimeout(timeout); } };
        const timeout = setTimeout(() => {
            console.log('[HLS] Timeout — no response in 10s');
            cleanup(); resolve(false);
        }, 10000);

        // Safari: native HLS support
        if (video.canPlayType('application/vnd.apple.mpegurl')) {
            console.log('[HLS] Using native HLS');
            video.src = hlsUrl;
            video.load();
            video.onloadedmetadata = () => {
                video.play().then(() => { cleanup(); resolve(true); });
            };
            video.onerror = (e) => { console.error('[HLS] ✗ Native error:', e); cleanup(); resolve(false); };
            return;
        }

        // Chrome / Firefox: hls.js
        if (typeof Hls === 'undefined' || !Hls.isSupported()) {
            console.error('[HLS] hls.js not supported'); cleanup(); resolve(false); return;
        }

        if (window.hls) { window.hls.destroy(); }

        window.hls = new Hls({
            debug: false,
            autoStartLoad: true,
            startPosition: -1,
            backBufferLength: 90,
            maxBufferLength: 30,
            maxMaxBufferLength: 60,
            lowLatencyMode: true,
            liveSyncDurationCount: 3,
            liveMaxLatencyDurationCount: 10,
            maxFragLookUpTolerance: 0.25,
            enableWorker: true,
            progressive: true,
            manifestLoadingTimeOut: 10000,
            manifestLoadingMaxRetry: 3,
            manifestLoadingRetryDelay: 500,
            fragLoadingTimeOut: 20000,
            fragLoadingMaxRetry: 6,
            fragLoadingRetryDelay: 500,
            levelLoadingTimeOut: 10000,
            levelLoadingMaxRetry: 3,
            levelLoadingRetryDelay: 500
        });

        window.hls.on(Hls.Events.MANIFEST_PARSED, () => {
            console.log('[HLS] ✓ Manifest parsed');
            video.play()
                .then(() => { cleanup(); resolve(true); })
                .catch(() => { cleanup(); resolve(true); }); // autoplay blocked — still ok
        });

        // Stall detection: warn if no fragment arrives for >5s
        let _lastFragTime = Date.now();
        window.hls.on(Hls.Events.FRAG_LOADED, (evt, data) => {
            _lastFragTime = Date.now();
            console.log(`[HLS] ✓ Fragment ${data.frag.sn} loaded (${data.frag.duration.toFixed(2)}s)`);
        });

        const healthCheck = setInterval(() => {
            const stale = Date.now() - _lastFragTime;
            if (stale > 5000 && window.hls) {
                console.warn(`[HLS] ⚠ No fragment for ${(stale / 1000).toFixed(1)}s — recovering`);
                window.hls.startLoad();
            }
        }, 5000);

        window.hls.on(Hls.Events.ERROR, (evt, data) => {
            console.error('[HLS] Error:', data.type, data.details, data.fatal);
            if (data.fatal) {
                clearInterval(healthCheck);
                switch (data.type) {
                    case Hls.ErrorTypes.NETWORK_ERROR:
                        setTimeout(() => window.hls.startLoad(), 1000); break;
                    case Hls.ErrorTypes.MEDIA_ERROR:
                        window.hls.recoverMediaError(); break;
                    default:
                        window.hls.destroy(); cleanup(); resolve(false); break;
                }
            }
        });

        // Video element diagnostics
        video.addEventListener('playing', () => console.log('[VIDEO] ▶ Playing'));
        video.addEventListener('waiting', () => console.warn('[VIDEO] ⏸ Buffering'));
        video.addEventListener('stalled', () => console.error('[VIDEO] ⚠ Stalled'));
        video.addEventListener('timeupdate', function logTime() {
            if (this.currentTime % 2 < 0.1)
                console.log(`[VIDEO] t=${this.currentTime.toFixed(1)}s`);
        });

        window.hls.loadSource(hlsUrl);
        window.hls.attachMedia(video);
        console.log('[HLS] Source attached, waiting for manifest...');
    });
}

// Backward-compat shim — one URL wrapped as single-element array
window.initHLSStream = async function (hlsUrl) {
    await window.initHLSStreamWithFallback([hlsUrl]);
};

window.addEventListener('beforeunload', () => { if (window.hls) window.hls.destroy(); });

// ═══════════════════════════════════════════════════════════════════════
// ULTRA-LOW LATENCY WEBRTC (WHEP) STREAMING
// ═══════════════════════════════════════════════════════════════════════

window.initWebRTCStream = async function (webrtcUrl) {
    console.log('[WebRTC] Initiating WHEP handshake:', webrtcUrl);

    const video = document.getElementById('webrtc-video');
    if (!video) return;

    if (window._pc) { window._pc.close(); }

    window._pc = new RTCPeerConnection();

    window._pc.ontrack = (event) => {
        console.log('[WebRTC] ✓ Video track received');
        video.srcObject = (event.streams && event.streams[0])
            ? event.streams[0]
            : new MediaStream([event.track]);
        video.play().catch(e => console.warn('[WebRTC] Autoplay blocked:', e));
    };

    window._pc.addTransceiver('video', { direction: 'recvonly' });

    try {
        const offer = await window._pc.createOffer();
        await window._pc.setLocalDescription(offer);

        const response = await fetch(webrtcUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/sdp' },
            body: offer.sdp
        });

        if (!response.ok) throw new Error(`MediaMTX: ${response.status} ${response.statusText}`);

        const answerSdp = await response.text();
        await window._pc.setRemoteDescription(
            new RTCSessionDescription({ type: 'answer', sdp: answerSdp })
        );

        console.log('[WebRTC] ✓ Handshake complete — zero-latency stream live.');

        if (typeof DotNet !== 'undefined') {
            // ASSEMBLY NAME: change 'BlazorApp3' if your project DLL differs.
            DotNet.invokeMethodAsync('BlazorApp3', 'OnVideoStatusChanged', 'Connected')
                .catch(e => console.warn('[WebRTC] Blazor callback failed', e));
        }

    } catch (err) {
        console.warn('[WebRTC] Camera not detected — entering Standby mode.', err.message);
        if (typeof DotNet !== 'undefined') {
            // ASSEMBLY NAME: change 'BlazorApp3' if your project DLL differs.
            DotNet.invokeMethodAsync('BlazorApp3', 'OnVideoStatusChanged', 'Standby')
                .catch(e => console.warn('[WebRTC] Blazor callback failed', e));
        }
    }
};

// ═══════════════════════════════════════════════════════════════════════
// TIME-DELTA SPRING INTERPOLATION ENGINE (HUD)
// ═══════════════════════════════════════════════════════════════════════
// Blazor pushes attitude at ~15Hz via updateHudTarget().
// The RAF loop runs at 60fps independently — this is what eliminates the
// 1-2s HUD lag you get when the transform is set inside the Blazor render.

window._hudTarget = { roll: 0, pitch: 0 };
window._hudCurrent = { roll: 0, pitch: 0 };
window._lastFrameTime = performance.now();

window.updateHudTarget = function (roll, pitch) {
    window._hudTarget.roll = roll;
    window._hudTarget.pitch = pitch;
};

const animateHud = (currentTime) => {
    // Cap dt to 50ms: prevents a huge snap if the tab was hidden
    const dt = Math.min(currentTime - window._lastFrameTime, 50) / 1000;
    window._lastFrameTime = currentTime;

    // Exponential spring — k=8: snappy but smooth
    // alpha ≈ 0.125 at 60fps → ~80% of gap closed per second
    const k = 8.0;
    const alpha = 1 - Math.exp(-k * dt);

    window._hudCurrent.roll += (window._hudTarget.roll - window._hudCurrent.roll) * alpha;
    window._hudCurrent.pitch += (window._hudTarget.pitch - window._hudCurrent.pitch) * alpha;

    const el = document.getElementById('hud-horizon');
    if (el) {
        const r = window._hudCurrent.roll;
        const p = window._hudCurrent.pitch;
        // roll  → negative: right-roll tilts horizon LEFT (correct avionics convention)
        // pitch → 3px per degree gives good visual scaling in the HUD widget size
        el.style.transform = `rotate(${-r}deg) translateY(${p * 3}px)`;
    }

    requestAnimationFrame(animateHud);
};

// Start HUD engine immediately
requestAnimationFrame(animateHud);

console.log('[DivyaLink] app.js loaded');
